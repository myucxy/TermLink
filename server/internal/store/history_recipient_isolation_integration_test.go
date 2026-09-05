package store

import (
	"context"
	"crypto/sha256"
	"fmt"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestHistoryObjectsAreIsolatedPerRecipient(t *testing.T) {
	t.Skip("legacy server terminal-history objects were retired by migration 39")
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

	userID, ownerID, androidA, androidB := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	pcID, sessionID := uuid.New(), uuid.New()
	subject := sha256.Sum256([]byte("history-recipient-isolation:" + userID.String()))
	digest := sha256.Sum256([]byte("recipient ciphertext"))
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
	})
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES
		  ($1,$2,'WINDOWS_PC','History owner',decode(repeat('11',32),'hex'),decode(repeat('12',32),'hex'),$3,'TRUSTED'),
		  ($4,$2,'ANDROID','Android A',decode(repeat('21',32),'hex'),decode(repeat('22',32),'hex'),$5,'TRUSTED'),
		  ($6,$2,'ANDROID','Android B',decode(repeat('31',32),'hex'),decode(repeat('32',32),'hex'),$7,'TRUSTED')`,
		ownerID, userID, "owner-"+ownerID.String(), androidA, "android-"+androidA.String(), androidB,
		"android-"+androidB.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch)
		VALUES($1,$2,$3,'Recipient PC','integration-test',1)`, pcID, userID, ownerID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,last_output_seq)
		VALUES($1,$2,'powershell','RUNNING',120,40,10)`, sessionID, pcID); err != nil {
		t.Fatal(err)
	}

	keyID := func(prefix string, recipient uuid.UUID) string {
		return fmt.Sprintf("%s:%s:%08d", prefix, strings.ReplaceAll(recipient.String(), "-", ""), 1)
	}
	createAndCommit := func(item HistoryUploadRecord) HistoryItem {
		t.Helper()
		if err := repository.CreateHistoryUpload(ctx, userID, ownerID, item); err != nil {
			t.Fatalf("create %s: %v", item.ID, err)
		}
		committed, err := repository.CommitHistoryUpload(ctx, userID, ownerID, sessionID, item.ID)
		if err != nil {
			t.Fatalf("commit %s: %v", item.ID, err)
		}
		return committed
	}
	chunk := func(recipient uuid.UUID) HistoryUploadRecord {
		id := uuid.New()
		start, end := int64(1), int64(10)
		return HistoryUploadRecord{
			ID: id, SessionID: sessionID, Kind: "CHUNK",
			ObjectKey: "accounts/" + userID.String() + "/sessions/" + sessionID.String() + "/chunk/" + id.String() + ".bin",
			KeyID:     keyID("history-v1", recipient), CiphertextSHA256: digest[:], SizeBytes: 20,
			StartSequence: &start, EndSequence: &end, RecipientDeviceID: recipient, RecipientKeyEpoch: 1,
			ExpiresAt: time.Now().Add(10 * time.Minute),
		}
	}
	snapshot := func(recipient uuid.UUID) HistoryUploadRecord {
		id := uuid.New()
		sequence := int64(10)
		rows, columns, version := 40, 120, 1
		format := HistoryFormatTerminalSnapshot
		return HistoryUploadRecord{
			ID: id, SessionID: sessionID, Kind: "SNAPSHOT",
			ObjectKey: "accounts/" + userID.String() + "/sessions/" + sessionID.String() + "/snapshot/" + id.String() + ".bin",
			KeyID:     keyID("snapshot-v1", recipient), CiphertextSHA256: digest[:], SizeBytes: 30,
			SnapshotSequence: &sequence, Rows: &rows, Columns: &columns, FormatName: &format, FormatVersion: &version,
			RecipientDeviceID: recipient, RecipientKeyEpoch: 1, ExpiresAt: time.Now().Add(10 * time.Minute),
		}
	}

	chunkA, chunkB := createAndCommit(chunk(androidA)), createAndCommit(chunk(androidB))
	snapshotUploadA := snapshot(androidA)
	snapshotA, snapshotB := createAndCommit(snapshotUploadA), createAndCommit(snapshot(androidB))
	retry := snapshot(androidA)
	retry.CiphertextSHA256 = append([]byte(nil), snapshotUploadA.CiphertextSHA256...)
	canonicalRetry := createAndCommit(retry)
	if canonicalRetry.ID != snapshotA.ID || canonicalRetry.ObjectKey != snapshotA.ObjectKey {
		t.Fatalf("snapshot retry=%+v, want canonical %+v", canonicalRetry, snapshotA)
	}

	assertIDs := func(actor uuid.UUID, expected ...uuid.UUID) {
		t.Helper()
		items, err := repository.ListHistory(ctx, userID, actor, sessionID)
		if err != nil {
			t.Fatal(err)
		}
		got := make(map[uuid.UUID]bool, len(items))
		for _, item := range items {
			got[item.ID] = true
		}
		if len(got) != len(expected) {
			t.Fatalf("actor %s history=%v", actor, got)
		}
		for _, id := range expected {
			if !got[id] {
				t.Fatalf("actor %s missing history %s in %v", actor, id, got)
			}
		}
	}
	assertIDs(androidA, chunkA.ID, snapshotA.ID)
	assertIDs(androidB, chunkB.ID, snapshotB.ID)
	assertIDs(ownerID, chunkA.ID, chunkB.ID, snapshotA.ID, snapshotB.ID)

	for actor, expected := range map[uuid.UUID]uuid.UUID{androidA: snapshotA.ID, androidB: snapshotB.ID} {
		page, err := repository.ListHistoryPage(ctx, userID, actor, sessionID, HistoryPageRequest{
			Kind: HistoryKindSnapshot, FormatName: HistoryFormatTerminalSnapshot, Limit: 1,
		})
		if err != nil || len(page.Items) != 1 || page.Items[0].ID != expected {
			t.Fatalf("actor %s latest snapshot=%+v err=%v", actor, page, err)
		}
	}
}
