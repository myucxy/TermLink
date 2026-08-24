package store

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"os"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestFileOperationClaimAuthorizationRetryUploadCancellationAndExpiry(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the file-operation integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()

	accountID, otherAccountID := uuid.New(), uuid.New()
	agentID, requesterID, wrongRequesterID := uuid.New(), uuid.New(), uuid.New()
	otherAgentID, otherRequesterID := uuid.New(), uuid.New()
	pcID, otherPCID := uuid.New(), uuid.New()
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id IN ($1,$2)`, accountID, otherAccountID)
	})
	insertFileOperationFixture(t, ctx, repository, accountID, agentID, requesterID, wrongRequesterID, pcID, 1)
	insertFileOperationFixture(t, ctx, repository, otherAccountID, otherAgentID, otherRequesterID, uuid.Nil, otherPCID, 20)

	operation, err := repository.CreateFileOperation(ctx, accountID, requesterID, pcID, FileOperationCreateInput{
		Operation: FileOperationList, Path: `C:\`, ExpiresAt: time.Now().UTC().Add(time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := repository.CreateFileOperation(ctx, otherAccountID, otherRequesterID, pcID, FileOperationCreateInput{
		Operation: FileOperationList, Path: `C:\`, ExpiresAt: time.Now().UTC().Add(time.Hour),
	}); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account create error = %v, want ErrDenied", err)
	}
	if _, _, err := repository.ClaimFileOperation(ctx, accountID, requesterID, pcID, time.Minute); !errors.Is(err, ErrDenied) {
		t.Fatalf("non-Agent claim error = %v, want ErrDenied", err)
	}

	type claimResult struct {
		item  *FileOperationRecord
		token string
		err   error
	}
	results := make(chan claimResult, 2)
	var claims sync.WaitGroup
	for range 2 {
		claims.Add(1)
		go func() {
			defer claims.Done()
			item, token, claimErr := repository.ClaimFileOperation(context.Background(), accountID, agentID, pcID, time.Minute)
			results <- claimResult{item: item, token: token, err: claimErr}
		}()
	}
	claims.Wait()
	close(results)
	var claimed claimResult
	claimCount := 0
	for result := range results {
		if result.err != nil {
			t.Fatal(result.err)
		}
		if result.item != nil {
			claimCount++
			claimed = result
		}
	}
	if claimCount != 1 || claimed.item.ID != operation.ID {
		t.Fatalf("atomic claim count=%d item=%+v", claimCount, claimed.item)
	}
	claimedHash := fileOperationLeaseHash(t, claimed.token)
	renewed, err := repository.RenewFileOperation(ctx, accountID, agentID, pcID, operation.ID, claimedHash, time.Hour)
	if err != nil || renewed.LeaseExpiresAt == nil || renewed.LeaseExpiresAt.After(renewed.ExpiresAt) {
		t.Fatalf("renewed item=%+v err=%v", renewed, err)
	}
	wrongLeaseHash := sha256.Sum256([]byte("wrong lease"))
	if _, err := repository.RenewFileOperation(ctx, accountID, agentID, pcID, operation.ID, wrongLeaseHash, time.Hour); !errors.Is(err, ErrConflict) {
		t.Fatalf("wrong-token renew error=%v, want ErrConflict", err)
	}
	if _, err := repository.GetFileOperation(ctx, accountID, wrongRequesterID, pcID, operation.ID); !errors.Is(err, ErrNotFound) {
		t.Fatalf("wrong requester read error = %v, want ErrNotFound", err)
	}

	if _, err := repository.Pool.Exec(ctx, `UPDATE pc_file_operations SET lease_expires_at=now()-interval '1 second' WHERE id=$1`, operation.ID); err != nil {
		t.Fatal(err)
	}
	reclaimed, retryToken, err := repository.ClaimFileOperation(ctx, accountID, agentID, pcID, time.Minute)
	if err != nil || reclaimed == nil || reclaimed.ID != operation.ID || reclaimed.AttemptCount != 2 || retryToken == claimed.token {
		t.Fatalf("expired lease reclaim item=%+v tokenChanged=%t err=%v", reclaimed, retryToken != claimed.token, err)
	}
	retryHash := fileOperationLeaseHash(t, retryToken)
	result := json.RawMessage(`{"items":[{"name":"file.txt","type":"file"}],"next_offset":null}`)
	completed, err := repository.CompleteFileOperation(ctx, accountID, agentID, pcID, operation.ID, retryHash, FileOperationCompleteInput{
		Status: FileOperationSucceeded, Result: result,
	})
	if err != nil || completed.Status != FileOperationSucceeded {
		t.Fatalf("complete item=%+v err=%v", completed, err)
	}
	if replay, err := repository.CompleteFileOperation(ctx, accountID, agentID, pcID, operation.ID, retryHash, FileOperationCompleteInput{
		Status: FileOperationSucceeded, Result: json.RawMessage(" { \"items\" : [ { \"type\":\"file\", \"name\":\"file.txt\" } ], \"next_offset\": null } "),
	}); err != nil || replay.Status != FileOperationSucceeded {
		t.Fatalf("idempotent completion item=%+v err=%v", replay, err)
	}

	shortLived, err := repository.CreateFileOperation(ctx, accountID, requesterID, pcID, FileOperationCreateInput{
		Operation: FileOperationDownload, Path: `C:\short-lived.txt`, ExpiresAt: time.Now().UTC().Add(2 * time.Minute),
	})
	if err != nil {
		t.Fatal(err)
	}
	shortClaim, shortToken, err := repository.ClaimFileOperation(ctx, accountID, agentID, pcID, time.Hour)
	if err != nil || shortClaim == nil || shortClaim.ID != shortLived.ID || shortClaim.LeaseExpiresAt == nil || shortClaim.LeaseExpiresAt.After(shortClaim.ExpiresAt) {
		t.Fatalf("short-lived claim=%+v err=%v", shortClaim, err)
	}
	shortHash := fileOperationLeaseHash(t, shortToken)
	if _, err := repository.Pool.Exec(ctx, `
		UPDATE pc_file_operations SET created_at=now()-interval '2 hours',expires_at=now()-interval '1 second',lease_expires_at=now()-interval '1 second'
		WHERE id=$1`, shortLived.ID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.RenewFileOperation(ctx, accountID, agentID, pcID, shortLived.ID, shortHash, time.Hour); !errors.Is(err, ErrConflict) {
		t.Fatalf("expired operation renew error=%v, want ErrConflict", err)
	}
	emptyDigest := sha256.Sum256(nil)
	if _, err := repository.RecordFileOperationUpload(ctx, accountID, agentID, pcID, shortLived.ID, shortHash, FileOperationUploadInput{
		Filename: "short-lived.txt", SizeBytes: 0, SHA256: emptyDigest[:], DirectoryZIP: false,
	}); !errors.Is(err, ErrConflict) {
		t.Fatalf("expired operation upload error=%v, want ErrConflict", err)
	}
	if _, err := repository.CompleteFileOperation(ctx, accountID, agentID, pcID, shortLived.ID, shortHash, FileOperationCompleteInput{
		Status: FileOperationFailed, ErrorMessage: stringPointer("expired"),
	}); !errors.Is(err, ErrConflict) {
		t.Fatalf("expired operation complete error=%v, want ErrConflict", err)
	}

	download, err := repository.CreateFileOperation(ctx, accountID, requesterID, pcID, FileOperationCreateInput{
		Operation: FileOperationDownload, Path: `C:\folder`, ExpiresAt: time.Now().UTC().Add(time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	downloadClaim, downloadToken, err := repository.ClaimFileOperation(ctx, accountID, agentID, pcID, time.Minute)
	if err != nil || downloadClaim == nil || downloadClaim.ID != download.ID {
		t.Fatalf("download claim=%+v err=%v", downloadClaim, err)
	}
	downloadHash := fileOperationLeaseHash(t, downloadToken)
	digest := sha256.Sum256(nil)
	upload := FileOperationUploadInput{Filename: "empty.txt", SizeBytes: 0, SHA256: digest[:], DirectoryZIP: false}
	recorded, err := repository.RecordFileOperationUpload(ctx, accountID, agentID, pcID, download.ID, downloadHash, upload)
	if err != nil || recorded.ObjectKey != "accounts/"+accountID.String()+"/file-transfers/"+pcID.String()+"/"+download.ID.String()+".bin" {
		t.Fatalf("recorded upload=%+v err=%v", recorded, err)
	}
	if replay, err := repository.RecordFileOperationUpload(ctx, accountID, agentID, pcID, download.ID, downloadHash, upload); err != nil || replay.ObjectKey != recorded.ObjectKey {
		t.Fatalf("upload metadata retry=%+v err=%v", replay, err)
	}
	changed := upload
	changed.SizeBytes++
	if _, err := repository.RecordFileOperationUpload(ctx, accountID, agentID, pcID, download.ID, downloadHash, changed); !errors.Is(err, ErrConflict) {
		t.Fatalf("changed upload retry error=%v, want ErrConflict", err)
	}
	if _, err := repository.AgentFileOperation(ctx, accountID, otherAgentID, pcID, download.ID, downloadHash); !errors.Is(err, ErrNotFound) {
		t.Fatalf("wrong PC read error=%v, want ErrNotFound", err)
	}
	cancelledDownload, err := repository.CancelFileOperation(ctx, accountID, requesterID, pcID, download.ID)
	if err != nil || cancelledDownload.Status != FileOperationCancelled {
		t.Fatalf("cancel uploaded download=%+v err=%v", cancelledDownload, err)
	}
	assertFileTransferCleanupScheduled(t, ctx, repository, download.ID, recorded.ObjectKey)

	failedDownload, err := repository.CreateFileOperation(ctx, accountID, requesterID, pcID, FileOperationCreateInput{
		Operation: FileOperationDownload, Path: `C:\failed.txt`, ExpiresAt: time.Now().UTC().Add(time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	failedClaim, failedToken, err := repository.ClaimFileOperation(ctx, accountID, agentID, pcID, time.Hour)
	if err != nil || failedClaim == nil || failedClaim.ID != failedDownload.ID {
		t.Fatalf("failed-download claim=%+v err=%v", failedClaim, err)
	}
	failedHash := fileOperationLeaseHash(t, failedToken)
	failedUpload, err := repository.RecordFileOperationUpload(ctx, accountID, agentID, pcID, failedDownload.ID, failedHash, FileOperationUploadInput{
		Filename: "failed.txt", SizeBytes: 0, SHA256: emptyDigest[:], DirectoryZIP: false,
	})
	if err != nil {
		t.Fatal(err)
	}
	failedMessage := "source changed"
	failedTerminal, err := repository.CompleteFileOperation(ctx, accountID, agentID, pcID, failedDownload.ID, failedHash, FileOperationCompleteInput{
		Status: FileOperationFailed, ErrorMessage: &failedMessage,
	})
	if err != nil || failedTerminal.Status != FileOperationFailed {
		t.Fatalf("failed terminal=%+v err=%v", failedTerminal, err)
	}
	assertFileTransferCleanupScheduled(t, ctx, repository, failedDownload.ID, failedUpload.ObjectKey)

	stale, err := repository.CreateFileOperation(ctx, accountID, requesterID, pcID, FileOperationCreateInput{
		Operation: FileOperationDownload, Path: `C:\stale.txt`, ExpiresAt: time.Now().UTC().Add(time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	staleClaim, staleToken, err := repository.ClaimFileOperation(ctx, accountID, agentID, pcID, time.Minute)
	if err != nil || staleClaim == nil || staleClaim.ID != stale.ID {
		t.Fatalf("stale claim=%+v err=%v", staleClaim, err)
	}
	staleHash := fileOperationLeaseHash(t, staleToken)
	staleDigest := sha256.Sum256(nil)
	staleUpload, err := repository.RecordFileOperationUpload(ctx, accountID, agentID, pcID, stale.ID, staleHash, FileOperationUploadInput{
		Filename: "stale.txt", SizeBytes: 0, SHA256: staleDigest[:], DirectoryZIP: false,
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		UPDATE pc_file_operations
		SET created_at=now()-interval '2 hours',expires_at=now()-interval '1 hour',
		    lease_expires_at=now()-interval '1 hour'
		WHERE id=$1`, stale.ID); err != nil {
		t.Fatal(err)
	}
	expiredStale, err := repository.GetFileOperation(ctx, accountID, requesterID, pcID, stale.ID)
	if err != nil || expiredStale.Status != FileOperationExpired {
		t.Fatalf("expired stale upload=%+v err=%v", expiredStale, err)
	}
	assertFileTransferCleanupScheduled(t, ctx, repository, stale.ID, staleUpload.ObjectKey)

	cancellable, err := repository.CreateFileOperation(ctx, accountID, requesterID, pcID, FileOperationCreateInput{
		Operation: FileOperationOpenTerminal, Path: `C:\folder`, ShellType: stringPointer("windows-powershell"), ExpiresAt: time.Now().UTC().Add(time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	cancellableClaim, cancellableToken, err := repository.ClaimFileOperation(ctx, accountID, agentID, pcID, time.Hour)
	if err != nil || cancellableClaim == nil || cancellableClaim.ID != cancellable.ID {
		t.Fatalf("cancellable claim=%+v err=%v", cancellableClaim, err)
	}
	cancellableHash := fileOperationLeaseHash(t, cancellableToken)
	cancelled, err := repository.CancelFileOperation(ctx, accountID, requesterID, pcID, cancellable.ID)
	if err != nil || cancelled.Status != FileOperationCancelled {
		t.Fatalf("cancelled=%+v err=%v", cancelled, err)
	}
	if retry, err := repository.CancelFileOperation(ctx, accountID, requesterID, pcID, cancellable.ID); err != nil || retry.Status != FileOperationCancelled {
		t.Fatalf("cancel retry=%+v err=%v", retry, err)
	}
	if _, err := repository.RenewFileOperation(ctx, accountID, agentID, pcID, cancellable.ID, cancellableHash, time.Hour); !errors.Is(err, ErrConflict) {
		t.Fatalf("cancelled operation renew error=%v, want ErrConflict", err)
	}

	terminalPath := `C:\terminal`
	terminalShell := "windows-powershell"
	terminal, err := repository.CreateFileOperation(ctx, accountID, requesterID, pcID, FileOperationCreateInput{
		Operation: FileOperationOpenTerminal, Path: terminalPath, ShellType: &terminalShell, ExpiresAt: time.Now().UTC().Add(time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	terminalClaim, terminalToken, err := repository.ClaimFileOperation(ctx, accountID, agentID, pcID, time.Hour)
	if err != nil || terminalClaim == nil || terminalClaim.ID != terminal.ID {
		t.Fatalf("terminal claim=%+v err=%v", terminalClaim, err)
	}
	terminalHash := fileOperationLeaseHash(t, terminalToken)
	invalidTerminalResult, _ := json.Marshal(map[string]string{
		"session_id": uuid.NewString(), "shell_type": "command-prompt", "cwd": terminalPath,
	})
	if _, err := repository.CompleteFileOperation(ctx, accountID, agentID, pcID, terminal.ID, terminalHash, FileOperationCompleteInput{
		Status: FileOperationSucceeded, Result: invalidTerminalResult,
	}); err == nil {
		t.Fatal("database accepted OPEN_TERMINAL completion with mismatched shell")
	}
	terminalSessionID := uuid.NewString()
	validTerminalResult, _ := json.Marshal(map[string]string{
		"session_id": terminalSessionID, "shell_type": terminalShell, "cwd": terminalPath,
	})
	completedTerminal, err := repository.CompleteFileOperation(ctx, accountID, agentID, pcID, terminal.ID, terminalHash, FileOperationCompleteInput{
		Status: FileOperationSucceeded, Result: validTerminalResult,
	})
	if err != nil || completedTerminal.Status != FileOperationSucceeded {
		t.Fatalf("valid terminal completion=%+v err=%v", completedTerminal, err)
	}

	expiring, err := repository.CreateFileOperation(ctx, accountID, requesterID, pcID, FileOperationCreateInput{
		Operation: FileOperationList, Path: "", ExpiresAt: time.Now().UTC().Add(time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `UPDATE pc_file_operations SET created_at=now()-interval '2 hours',expires_at=now()-interval '1 hour' WHERE id=$1`, expiring.ID); err != nil {
		t.Fatal(err)
	}
	expired, err := repository.GetFileOperation(ctx, accountID, requesterID, pcID, expiring.ID)
	if err != nil || expired.Status != FileOperationExpired {
		t.Fatalf("expired=%+v err=%v", expired, err)
	}
}

func insertFileOperationFixture(t *testing.T, ctx context.Context, repository *Postgres, accountID, agentID, requesterID, extraRequesterID, pcID uuid.UUID, seed byte) {
	t.Helper()
	subject := sha256.Sum256([]byte("file-operation-account-" + accountID.String()))
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, accountID, subject[:]); err != nil {
		t.Fatal(err)
	}
	insertDevice := func(id uuid.UUID, deviceType, name string, keySeed byte, approvedBy *uuid.UUID) {
		key := make([]byte, 32)
		for index := range key {
			key[index] = keySeed
		}
		exchange := append([]byte(nil), key...)
		exchange[0]++
		if _, err := repository.Pool.Exec(ctx, `
			INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_by,approved_at)
			VALUES($1,$2,$3,$4,$5,$6,$7,'TRUSTED',$8,now())`,
			id, accountID, deviceType, name, key, exchange, "key-"+id.String(), approvedBy); err != nil {
			t.Fatal(err)
		}
	}
	insertDevice(agentID, "WINDOWS_PC", "Agent", seed, nil)
	insertDevice(requesterID, "ANDROID", "Requester", seed+1, &agentID)
	if extraRequesterID != uuid.Nil {
		insertDevice(extraRequesterID, "ANDROID", "Other requester", seed+2, &agentID)
	}
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch) VALUES($1,$2,$3,'PC','test',1)`, pcID, accountID, agentID); err != nil {
		t.Fatal(err)
	}
}

func fileOperationLeaseHash(t *testing.T, token string) [32]byte {
	t.Helper()
	decoded, err := base64.RawURLEncoding.DecodeString(token)
	if err != nil || len(decoded) != 32 {
		t.Fatalf("invalid lease token %q: %v", token, err)
	}
	return sha256.Sum256(decoded)
}

func stringPointer(value string) *string { return &value }

func assertFileTransferCleanupScheduled(t *testing.T, ctx context.Context, repository *Postgres, operationID uuid.UUID, objectKey string) {
	t.Helper()
	var payloadKey string
	var delayed bool
	err := repository.Pool.QueryRow(ctx, `
		SELECT payload->>'object_key',available_at>now()+interval '55 minutes'
		FROM outbox
		WHERE event_type='history.object.delete' AND aggregate_id=$1 AND payload->>'object_key'=$2`,
		operationID, objectKey).Scan(&payloadKey, &delayed)
	if err != nil || payloadKey != objectKey || !delayed {
		t.Fatalf("cleanup event operation=%s key=%q delayed=%t err=%v", operationID, payloadKey, delayed, err)
	}
}
