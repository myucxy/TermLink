package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestCommitHistoryUploadMakesIdenticalEventChunkRetriesIdempotent(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()
	repository.AutoTrustDevices = true
	if err := repository.VerifySchema(ctx); err != nil {
		t.Fatal(err)
	}

	userID, deviceID, recipientID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New(), uuid.New()
	subjectHash := sha256.Sum256([]byte("history-chunk-idempotency-" + userID.String()))
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupContext, `DELETE FROM users WHERE id=$1`, userID)
	})
	if _, err := repository.Pool.Exec(ctx,
		`INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subjectHash[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES
		  ($1,$2,'WINDOWS_PC','History Retry PC',decode(repeat('01',32),'hex'),decode(repeat('02',32),'hex'),$3,'TRUSTED'),
		  ($4,$2,'ANDROID','History Retry Android',decode(repeat('03',32),'hex'),decode(repeat('04',32),'hex'),$5,'TRUSTED')`,
		deviceID, userID, "history-"+deviceID.String(), recipientID, "history-"+recipientID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx,
		`INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version) VALUES($1,$2,$3,'History Retry PC','integration-test')`,
		pcID, userID, deviceID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,last_output_seq)
		VALUES($1,$2,'powershell','RUNNING',120,40,9)`, sessionID, pcID); err != nil {
		t.Fatal(err)
	}

	startSequence, endSequence := int64(4), int64(9)
	digest := sha256.Sum256([]byte("same encrypted event chunk"))
	keyID := fmt.Sprintf("history-v1:%s:%08d", strings.ReplaceAll(recipientID.String(), "-", ""), 1)
	newUpload := func(keyID string, ciphertextSHA256 []byte, sizeBytes int64) HistoryUploadRecord {
		uploadID := uuid.New()
		return HistoryUploadRecord{
			ID:                uploadID,
			SessionID:         sessionID,
			Kind:              "CHUNK",
			ObjectKey:         "accounts/" + userID.String() + "/sessions/" + sessionID.String() + "/chunk/" + uploadID.String() + ".bin",
			KeyID:             keyID,
			RecipientDeviceID: recipientID,
			RecipientKeyEpoch: 1,
			CiphertextSHA256:  append([]byte(nil), ciphertextSHA256...),
			SizeBytes:         sizeBytes,
			StartSequence:     &startSequence,
			EndSequence:       &endSequence,
			ExpiresAt:         time.Now().Add(10 * time.Minute),
		}
	}
	createAndCommit := func(upload HistoryUploadRecord) (HistoryItem, error) {
		t.Helper()
		if err := repository.CreateHistoryUpload(ctx, userID, deviceID, upload); err != nil {
			t.Fatalf("create history upload %s: %v", upload.ID, err)
		}
		return repository.CommitHistoryUpload(ctx, userID, deviceID, sessionID, upload.ID)
	}

	firstUpload := newUpload(keyID, digest[:], int64(len("same encrypted event chunk")))
	first, err := createAndCommit(firstUpload)
	if err != nil {
		t.Fatalf("commit initial chunk: %v", err)
	}
	retryUpload := newUpload(firstUpload.KeyID, firstUpload.CiphertextSHA256, firstUpload.SizeBytes)
	retry, err := createAndCommit(retryUpload)
	if err != nil {
		t.Fatalf("commit identical retry: %v", err)
	}
	if retry.ID != first.ID || retry.ObjectKey != first.ObjectKey {
		t.Fatalf("identical retry returned chunk %s at %q, want canonical chunk %s at %q", retry.ID, retry.ObjectKey, first.ID, first.ObjectKey)
	}
	var chunkCount, committedRetryCount int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT (SELECT count(*) FROM event_chunks WHERE session_id=$1 AND start_seq=$2 AND end_seq=$3),
		       (SELECT count(*) FROM history_uploads WHERE id=$4 AND committed_at IS NOT NULL)`,
		sessionID, startSequence, endSequence, retryUpload.ID).Scan(&chunkCount, &committedRetryCount); err != nil {
		t.Fatal(err)
	}
	if chunkCount != 1 || committedRetryCount != 1 {
		t.Fatalf("after identical retry chunk count=%d committed retry count=%d, want 1/1", chunkCount, committedRetryCount)
	}

	differentDigest := sha256.Sum256([]byte("different encrypted event chunk"))
	conflicts := []HistoryUploadRecord{
		newUpload(firstUpload.KeyID, differentDigest[:], firstUpload.SizeBytes),
		newUpload(firstUpload.KeyID, firstUpload.CiphertextSHA256, firstUpload.SizeBytes+1),
	}
	for _, conflictUpload := range conflicts {
		if _, err := createAndCommit(conflictUpload); !errors.Is(err, ErrConflict) {
			t.Fatalf("commit mismatched retry %s error=%v, want ErrConflict", conflictUpload.ID, err)
		}
		var committed bool
		if err := repository.Pool.QueryRow(ctx,
			`SELECT committed_at IS NOT NULL FROM history_uploads WHERE id=$1`, conflictUpload.ID).Scan(&committed); err != nil {
			t.Fatal(err)
		}
		if committed {
			t.Fatalf("mismatched retry %s was marked committed", conflictUpload.ID)
		}
	}
	if err := repository.Pool.QueryRow(ctx,
		`SELECT count(*) FROM event_chunks WHERE session_id=$1 AND start_seq=$2 AND end_seq=$3`,
		sessionID, startSequence, endSequence).Scan(&chunkCount); err != nil {
		t.Fatal(err)
	}
	if chunkCount != 1 {
		t.Fatalf("after mismatched retries chunk count=%d, want 1", chunkCount)
	}
}
