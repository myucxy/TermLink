package store

import (
	"bytes"
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestCleanupFileOperationsBoundsAccountsAndWaitsForObjectDeletion(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the file-operation cleanup integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	defer cancel()
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "file_operations_cleanup_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err = admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cleanupCancel()
		if _, cleanupErr := admin.Exec(cleanupCtx, "DROP SCHEMA "+quotedSchema+" CASCADE"); cleanupErr != nil {
			t.Errorf("drop isolated file-operation cleanup schema: %v", cleanupErr)
		}
	}()

	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	config.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	if err := migrate.UpPool(ctx, pool); err != nil {
		t.Fatal(err)
	}
	repository := &Postgres{Pool: pool}

	cleanupAccountID, cappedAccountID := uuid.New(), uuid.New()
	cleanupAgentID, cleanupRequesterID, cleanupPCID := uuid.New(), uuid.New(), uuid.New()
	cappedAgentID, cappedRequesterID, cappedPCID := uuid.New(), uuid.New(), uuid.New()
	insertFileOperationFixture(t, ctx, repository, cleanupAccountID, cleanupAgentID, cleanupRequesterID, uuid.Nil, cleanupPCID, 40)
	insertFileOperationFixture(t, ctx, repository, cappedAccountID, cappedAgentID, cappedRequesterID, uuid.Nil, cappedPCID, 50)
	operationIDs := make([]uuid.UUID, 0, MaxRetainedFileOperationsPerAccount+8)
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=ANY($1::uuid[])`, operationIDs)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=ANY($1::uuid[])`, []uuid.UUID{cleanupAccountID, cappedAccountID})
	})

	cappedIDs := make([]uuid.UUID, MaxRetainedFileOperationsPerAccount)
	for index := range cappedIDs {
		cappedIDs[index] = uuid.New()
	}
	operationIDs = append(operationIDs, cappedIDs...)
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pc_file_operations(id,account_id,pc_id,requester_device_id,operation,path,expires_at)
		SELECT id,$2,$3,$4,'LIST','',now()+interval '1 hour'
		FROM unnest($1::uuid[]) AS ids(id)`, cappedIDs, cappedAccountID, cappedPCID, cappedRequesterID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.CreateFileOperation(ctx, cappedAccountID, cappedRequesterID, cappedPCID, FileOperationCreateInput{
		Operation: FileOperationList, ExpiresAt: time.Now().UTC().Add(time.Hour),
	}); !errors.Is(err, ErrConflict) {
		t.Fatalf("account operation bound error=%v, want ErrConflict", err)
	}
	if _, err := repository.Pool.Exec(ctx, `DELETE FROM pc_file_operations WHERE id=$1`, cappedIDs[0]); err != nil {
		t.Fatal(err)
	}
	type createResult struct {
		item FileOperationRecord
		err  error
	}
	createResults := make(chan createResult, 2)
	var creates sync.WaitGroup
	for range 2 {
		creates.Add(1)
		go func() {
			defer creates.Done()
			item, createErr := repository.CreateFileOperation(context.Background(), cappedAccountID, cappedRequesterID, cappedPCID, FileOperationCreateInput{
				Operation: FileOperationList, ExpiresAt: time.Now().UTC().Add(time.Hour),
			})
			createResults <- createResult{item: item, err: createErr}
		}()
	}
	creates.Wait()
	close(createResults)
	created, conflicted := 0, 0
	for result := range createResults {
		switch {
		case result.err == nil:
			created++
			operationIDs = append(operationIDs, result.item.ID)
		case errors.Is(result.err, ErrConflict):
			conflicted++
		default:
			t.Fatalf("concurrent bounded create error=%v", result.err)
		}
	}
	if created != 1 || conflicted != 1 {
		t.Fatalf("concurrent bounded creates succeeded/conflicted=%d/%d", created, conflicted)
	}

	create := func(operation, path string) FileOperationRecord {
		item, createErr := repository.CreateFileOperation(ctx, cleanupAccountID, cleanupRequesterID, cleanupPCID, FileOperationCreateInput{
			Operation: operation, Path: path, ExpiresAt: time.Now().UTC().Add(time.Hour),
		})
		if createErr != nil {
			t.Fatal(createErr)
		}
		operationIDs = append(operationIDs, item.ID)
		return item
	}
	now := time.Now().UTC()
	expiredPending := create(FileOperationList, "")
	expiredDownload := create(FileOperationDownload, `C:\expired.bin`)
	freshPending := create(FileOperationList, `C:\fresh`)
	oldList := create(FileOperationList, `C:\old-list`)
	recentFailure := create(FileOperationList, `C:\recent`)
	gatedDownload := create(FileOperationDownload, `C:\gated.bin`)
	deliveredDownload := create(FileOperationDownload, `C:\delivered.bin`)
	noObjectDownload := create(FileOperationDownload, `C:\missing.bin`)

	if _, err := repository.Pool.Exec(ctx, `
		UPDATE pc_file_operations
		SET created_at=$2::timestamptz-interval '3 hours',expires_at=$2::timestamptz-interval '2 hours',
		    updated_at=$2::timestamptz-interval '3 hours'
		WHERE id=$1`, expiredPending.ID, now); err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256([]byte("opaque transfer"))
	expiredObjectKey := "accounts/" + cleanupAccountID.String() + "/file-transfers/" + cleanupPCID.String() + "/" + expiredDownload.ID.String() + ".bin"
	if _, err := repository.Pool.Exec(ctx, `
		UPDATE pc_file_operations
		SET status='PROCESSING',attempt_count=1,lease_token_hash=$2,lease_expires_at=$3::timestamptz-interval '2 hours',
		    object_key=$4,filename='expired.bin',size_bytes=15,sha256=$5,directory_zip=false,
		    created_at=$3::timestamptz-interval '3 hours',expires_at=$3::timestamptz-interval '2 hours',
		    updated_at=$3::timestamptz-interval '3 hours'
		WHERE id=$1`, expiredDownload.ID, bytes.Repeat([]byte{7}, 32), now, expiredObjectKey, digest[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		UPDATE pc_file_operations
		SET status='SUCCEEDED',attempt_count=1,lease_token_hash=$2,lease_expires_at=$3::timestamptz-interval '47 hours',
		    result='{}'::jsonb,completed_at=$3::timestamptz-interval '47 hours',created_at=$3::timestamptz-interval '48 hours',
		    expires_at=$3::timestamptz-interval '46 hours',updated_at=$3::timestamptz-interval '47 hours'
		WHERE id=$1`, oldList.ID, bytes.Repeat([]byte{8}, 32), now); err != nil {
		t.Fatal(err)
	}
	recentError := "recent failure"
	if _, err := repository.Pool.Exec(ctx, `
		UPDATE pc_file_operations
		SET status='FAILED',attempt_count=1,lease_token_hash=$2,lease_expires_at=$3::timestamptz-interval '1 hour',
		    error_message=$4,completed_at=$3::timestamptz-interval '1 hour',created_at=$3::timestamptz-interval '2 hours',
		    expires_at=$3::timestamptz+interval '1 hour',updated_at=$3::timestamptz-interval '1 hour'
		WHERE id=$1`, recentFailure.ID, bytes.Repeat([]byte{9}, 32), now, recentError); err != nil {
		t.Fatal(err)
	}

	setOldDownload := func(item FileOperationRecord, filename string, withObject, processed bool) string {
		t.Helper()
		objectKey := ""
		if withObject {
			objectKey = "accounts/" + cleanupAccountID.String() + "/file-transfers/" + cleanupPCID.String() + "/" + item.ID.String() + ".bin"
			if _, updateErr := repository.Pool.Exec(ctx, `
				UPDATE pc_file_operations
				SET status='FAILED',attempt_count=1,lease_token_hash=$2,lease_expires_at=$3::timestamptz-interval '47 hours',
				    error_message='terminal failure',object_key=$4,filename=$5,size_bytes=15,sha256=$6,directory_zip=false,
				    completed_at=$3::timestamptz-interval '47 hours',created_at=$3::timestamptz-interval '48 hours',
				    expires_at=$3::timestamptz-interval '46 hours',updated_at=$3::timestamptz-interval '47 hours'
				WHERE id=$1`, item.ID, bytes.Repeat([]byte{10}, 32), now, objectKey, filename, digest[:]); updateErr != nil {
				t.Fatal(updateErr)
			}
			if _, insertErr := repository.Pool.Exec(ctx, `
				INSERT INTO outbox(event_type,aggregate_id,payload,available_at,processed_at)
				VALUES('history.object.delete',$1,jsonb_build_object('object_key',$2::text),$3::timestamptz-interval '46 hours',
				       CASE WHEN $4 THEN $3::timestamptz-interval '46 hours' END)`, item.ID, objectKey, now, processed); insertErr != nil {
				t.Fatal(insertErr)
			}
			return objectKey
		}
		if _, updateErr := repository.Pool.Exec(ctx, `
			UPDATE pc_file_operations
			SET status='FAILED',attempt_count=1,lease_token_hash=$2,lease_expires_at=$3::timestamptz-interval '47 hours',
			    error_message='terminal failure',completed_at=$3::timestamptz-interval '47 hours',
			    created_at=$3::timestamptz-interval '48 hours',expires_at=$3::timestamptz-interval '46 hours',
			    updated_at=$3::timestamptz-interval '47 hours'
			WHERE id=$1`, item.ID, bytes.Repeat([]byte{11}, 32), now); updateErr != nil {
			t.Fatal(updateErr)
		}
		return objectKey
	}
	_ = setOldDownload(gatedDownload, "gated.bin", true, false)
	_ = setOldDownload(deliveredDownload, "delivered.bin", true, true)
	_ = setOldDownload(noObjectDownload, "missing.bin", false, false)

	results := make(chan FileOperationCleanupResult, 2)
	errorsChannel := make(chan error, 2)
	var workers sync.WaitGroup
	for range 2 {
		workers.Add(1)
		go func() {
			defer workers.Done()
			result, cleanupErr := repository.CleanupFileOperations(context.Background(), now, now.Add(-24*time.Hour), 3)
			results <- result
			errorsChannel <- cleanupErr
		}()
	}
	workers.Wait()
	close(results)
	close(errorsChannel)
	total := FileOperationCleanupResult{}
	for cleanupErr := range errorsChannel {
		if cleanupErr != nil {
			t.Fatal(cleanupErr)
		}
	}
	for result := range results {
		total.Expired += result.Expired
		total.Deleted += result.Deleted
	}
	if total.Expired != 2 || total.Deleted != 3 {
		t.Fatalf("concurrent cleanup result=%+v, want expired=2 deleted=3", total)
	}

	var expiredStatus string
	if err := repository.Pool.QueryRow(ctx, `SELECT status FROM pc_file_operations WHERE id=$1`, expiredPending.ID).Scan(&expiredStatus); err != nil || expiredStatus != FileOperationExpired {
		t.Fatalf("expired pending status=%q err=%v", expiredStatus, err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT status FROM pc_file_operations WHERE id=$1`, expiredDownload.ID).Scan(&expiredStatus); err != nil || expiredStatus != FileOperationExpired {
		t.Fatalf("expired download status=%q err=%v", expiredStatus, err)
	}
	var cleanupEventCount int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM outbox
		WHERE event_type='history.object.delete' AND aggregate_id=$1
		  AND payload->>'object_key'=$2 AND processed_at IS NULL`, expiredDownload.ID, expiredObjectKey).Scan(&cleanupEventCount); err != nil || cleanupEventCount != 1 {
		t.Fatalf("expired download cleanup event count=%d err=%v", cleanupEventCount, err)
	}
	var retainedIDs []uuid.UUID
	rows, err := repository.Pool.Query(ctx, `SELECT id FROM pc_file_operations WHERE account_id=$1`, cleanupAccountID)
	if err != nil {
		t.Fatal(err)
	}
	for rows.Next() {
		var id uuid.UUID
		if err := rows.Scan(&id); err != nil {
			rows.Close()
			t.Fatal(err)
		}
		retainedIDs = append(retainedIDs, id)
	}
	rows.Close()
	for _, expected := range []uuid.UUID{expiredPending.ID, expiredDownload.ID, freshPending.ID, recentFailure.ID, gatedDownload.ID} {
		if !containsUUID(retainedIDs, expected) {
			t.Fatalf("required retained operation %s missing from %v", expected, retainedIDs)
		}
	}
	for _, removed := range []uuid.UUID{oldList.ID, deliveredDownload.ID, noObjectDownload.ID} {
		if containsUUID(retainedIDs, removed) {
			t.Fatalf("eligible terminal operation %s was retained", removed)
		}
	}
}

func containsUUID(values []uuid.UUID, target uuid.UUID) bool {
	for _, value := range values {
		if value == target {
			return true
		}
	}
	return false
}
