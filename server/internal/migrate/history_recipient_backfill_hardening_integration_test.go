package migrate

import (
	"bytes"
	"context"
	"crypto/sha256"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgtype"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestHistoryRecipientBackfillHardeningPreservesCiphertextAndClearsInvalidAssociations(t *testing.T) {
	t.Skip("legacy server terminal-history tables were retired by migration 39")
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the migration integration test")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	if err := UpPool(ctx, pool); err != nil {
		t.Fatal(err)
	}

	userID, otherUserID := uuid.New(), uuid.New()
	ownerID, androidID := uuid.New(), uuid.New()
	otherAndroidID := uuid.New()
	pcID, sessionID := uuid.New(), uuid.New()
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = pool.Exec(cleanupCtx, `DELETE FROM users WHERE id IN ($1,$2)`, userID, otherUserID)
	}()

	userSubject := sha256.Sum256([]byte("history-recipient-hardening-user:" + userID.String()))
	otherSubject := sha256.Sum256([]byte("history-recipient-hardening-user:" + otherUserID.String()))
	if _, err := pool.Exec(ctx, `
		INSERT INTO users(id,oidc_subject_hash)
		VALUES($1,$2),($3,$4)`, userID, userSubject[:], otherUserID, otherSubject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES
		  ($1,$2,'WINDOWS_PC','Migration 13 owner',decode(repeat('41',32),'hex'),decode(repeat('42',32),'hex'),$3,'TRUSTED'),
		  ($4,$2,'ANDROID','Migration 13 Android',decode(repeat('43',32),'hex'),decode(repeat('44',32),'hex'),$5,'TRUSTED'),
		  ($6,$7,'ANDROID','Migration 13 cross-account Android',decode(repeat('45',32),'hex'),decode(repeat('46',32),'hex'),$8,'TRUSTED')`,
		ownerID, userID, "migration13-owner-"+ownerID.String(),
		androidID, "migration13-android-"+androidID.String(),
		otherAndroidID, otherUserID, "migration13-other-"+otherAndroidID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch)
		VALUES($1,$2,$3,'Migration 13 PC','integration-test',1)`, pcID, userID, ownerID); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,last_output_seq)
		VALUES($1,$2,'powershell','RUNNING',120,40,100)`, sessionID, pcID); err != nil {
		t.Fatal(err)
	}

	digest := sha256.Sum256([]byte("migration 13 ciphertext remains intact"))
	keyID := func(prefix string, recipient uuid.UUID, epoch string) string {
		return prefix + ":" + strings.ReplaceAll(recipient.String(), "-", "") + ":" + epoch
	}
	objectKey := func(table string, id uuid.UUID) string {
		return "integration/migration-13/" + table + "/" + id.String() + ".bin"
	}

	historyCanonical := uuid.New()
	historyLeadingZero := uuid.New()
	historySnapshotNine := uuid.New()
	historyCrossAccount := uuid.New()
	historyWindows := uuid.New()
	insertHistoryChunk := func(id uuid.UUID, key string, recipient uuid.UUID, epoch, sequence int64) {
		t.Helper()
		if _, err := pool.Exec(ctx, `
			INSERT INTO history_uploads(
			  id,user_id,actor_device_id,session_id,kind,object_key,key_id,
			  ciphertext_sha256,size_bytes,start_seq,end_seq,
			  recipient_device_id,recipient_key_epoch,expires_at)
			VALUES($1,$2,$3,$4,'CHUNK',$5,$6,$7,1,$8,$8,$9,$10,now()+interval '1 hour')`,
			id, userID, ownerID, sessionID, objectKey("history-upload", id), key,
			digest[:], sequence, recipient, epoch); err != nil {
			t.Fatalf("insert history upload %s: %v", id, err)
		}
	}
	insertHistorySnapshot := func(id uuid.UUID, key string, recipient uuid.UUID, epoch, sequence int64) {
		t.Helper()
		if _, err := pool.Exec(ctx, `
			INSERT INTO history_uploads(
			  id,user_id,actor_device_id,session_id,kind,object_key,key_id,
			  ciphertext_sha256,size_bytes,snapshot_seq,rows,columns,format_name,format_version,
			  recipient_device_id,recipient_key_epoch,expires_at)
			VALUES($1,$2,$3,$4,'SNAPSHOT',$5,$6,$7,1,$8,40,120,
			  'TERMLINK_TERMINAL_SNAPSHOT',1,$9,$10,now()+interval '1 hour')`,
			id, userID, ownerID, sessionID, objectKey("history-upload", id), key,
			digest[:], sequence, recipient, epoch); err != nil {
			t.Fatalf("insert snapshot upload %s: %v", id, err)
		}
	}
	insertHistoryChunk(historyCanonical, keyID("history-v1", androidID, "00000001"), androidID, 1, 1)
	insertHistoryChunk(historyLeadingZero, keyID("history-v1", androidID, "012345678"), androidID, 12345678, 2)
	insertHistorySnapshot(historySnapshotNine, keyID("snapshot-v1", androidID, "123456789"), androidID, 123456789, 3)
	insertHistoryChunk(historyCrossAccount, keyID("history-v1", otherAndroidID, "00000001"), otherAndroidID, 1, 4)
	insertHistoryChunk(historyWindows, keyID("history-v1", ownerID, "00000001"), ownerID, 1, 5)

	eventCanonical := uuid.New()
	eventLeadingZero := uuid.New()
	eventCrossAccount := uuid.New()
	eventWindows := uuid.New()
	insertEvent := func(id uuid.UUID, key string, recipient uuid.UUID, sequence int64) {
		t.Helper()
		if _, err := pool.Exec(ctx, `
			INSERT INTO event_chunks(
			  id,session_id,start_seq,end_seq,object_key,key_id,ciphertext_sha256,size_bytes,recipient_device_id)
			VALUES($1,$2,$3,$3,$4,$5,$6,1,$7)`,
			id, sessionID, sequence, objectKey("event-chunk", id), key, digest[:], recipient); err != nil {
			t.Fatalf("insert event chunk %s: %v", id, err)
		}
	}
	insertEvent(eventCanonical, keyID("history-v1", androidID, "00000001"), androidID, 11)
	insertEvent(eventLeadingZero, keyID("history-v1", androidID, "012345678"), androidID, 12)
	insertEvent(eventCrossAccount, keyID("history-v1", otherAndroidID, "00000001"), otherAndroidID, 13)
	insertEvent(eventWindows, keyID("history-v1", ownerID, "00000001"), ownerID, 14)

	snapshotCanonical := uuid.New()
	snapshotLeadingZero := uuid.New()
	snapshotNine := uuid.New()
	snapshotCrossAccount := uuid.New()
	snapshotWindows := uuid.New()
	insertSnapshot := func(id uuid.UUID, format, key string, recipient uuid.UUID, sequence int64) {
		t.Helper()
		if _, err := pool.Exec(ctx, `
			INSERT INTO snapshots(
			  id,session_id,snapshot_seq,rows,columns,object_key,key_id,ciphertext_sha256,
			  size_bytes,format_name,format_version,recipient_device_id)
			VALUES($1,$2,$3,40,120,$4,$5,$6,1,$7,1,$8)`,
			id, sessionID, sequence, objectKey("snapshot", id), key, digest[:], format, recipient); err != nil {
			t.Fatalf("insert snapshot %s: %v", id, err)
		}
	}
	insertSnapshot(snapshotCanonical, "TERMLINK_TERMINAL_SNAPSHOT", keyID("snapshot-v1", androidID, "00000001"), androidID, 21)
	insertSnapshot(snapshotLeadingZero, "BOUNDED_EVENT_REPLAY", keyID("history-v1", androidID, "012345678"), androidID, 22)
	insertSnapshot(snapshotNine, "TERMLINK_TERMINAL_SNAPSHOT", keyID("snapshot-v1", androidID, "123456789"), androidID, 23)
	insertSnapshot(snapshotCrossAccount, "TERMLINK_TERMINAL_SNAPSHOT", keyID("snapshot-v1", otherAndroidID, "00000001"), otherAndroidID, 24)
	insertSnapshot(snapshotWindows, "BOUNDED_EVENT_REPLAY", keyID("history-v1", ownerID, "00000001"), ownerID, 25)

	migrationSQL, err := migrations.Files.ReadFile("000013_history_recipient_backfill_hardening.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	tx, err := pool.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := tx.Exec(ctx, string(migrationSQL)); err != nil {
		_ = tx.Rollback(ctx)
		t.Fatalf("reapply migration 13: %v", err)
	}
	if err := tx.Commit(ctx); err != nil {
		t.Fatal(err)
	}

	type expectation struct {
		source        string
		wantRecipient uuid.UUID
	}
	expected := map[uuid.UUID]expectation{
		historyCanonical:     {source: "history_uploads", wantRecipient: androidID},
		historyLeadingZero:   {source: "history_uploads"},
		historySnapshotNine:  {source: "history_uploads"},
		historyCrossAccount:  {source: "history_uploads"},
		historyWindows:       {source: "history_uploads"},
		eventCanonical:       {source: "event_chunks", wantRecipient: androidID},
		eventLeadingZero:     {source: "event_chunks"},
		eventCrossAccount:    {source: "event_chunks"},
		eventWindows:         {source: "event_chunks"},
		snapshotCanonical:    {source: "snapshots", wantRecipient: androidID},
		snapshotLeadingZero:  {source: "snapshots"},
		snapshotNine:         {source: "snapshots"},
		snapshotCrossAccount: {source: "snapshots"},
		snapshotWindows:      {source: "snapshots"},
	}
	rows, err := pool.Query(ctx, `
		SELECT 'history_uploads',id,recipient_device_id,ciphertext_sha256
		FROM history_uploads WHERE session_id=$1
		UNION ALL
		SELECT 'event_chunks',id,recipient_device_id,ciphertext_sha256
		FROM event_chunks WHERE session_id=$1
		UNION ALL
		SELECT 'snapshots',id,recipient_device_id,ciphertext_sha256
		FROM snapshots WHERE session_id=$1`, sessionID)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	seen := make(map[uuid.UUID]bool, len(expected))
	for rows.Next() {
		var source string
		var id uuid.UUID
		var recipient pgtype.UUID
		var ciphertextSHA256 []byte
		if err := rows.Scan(&source, &id, &recipient, &ciphertextSHA256); err != nil {
			t.Fatal(err)
		}
		want, ok := expected[id]
		if !ok {
			t.Fatalf("unexpected %s row %s for migration fixture", source, id)
		}
		if seen[id] {
			t.Fatalf("duplicate result for %s row %s", source, id)
		}
		seen[id] = true
		if source != want.source {
			t.Errorf("row %s source=%q, want %q", id, source, want.source)
		}
		if !bytes.Equal(ciphertextSHA256, digest[:]) {
			t.Errorf("%s row %s ciphertext digest changed", source, id)
		}
		if want.wantRecipient == uuid.Nil {
			if recipient.Valid {
				t.Errorf("%s row %s recipient=%s, want NULL", source, id, uuid.UUID(recipient.Bytes))
			}
		} else if !recipient.Valid || uuid.UUID(recipient.Bytes) != want.wantRecipient {
			t.Errorf("%s row %s recipient=%v, want %s", source, id, recipient, want.wantRecipient)
		}
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	if len(seen) != len(expected) {
		for id, want := range expected {
			if !seen[id] {
				t.Errorf("migration deleted %s ciphertext row %s", want.source, id)
			}
		}
	}

	for id, wantEpoch := range map[uuid.UUID]int64{
		historyCanonical:    1,
		historyLeadingZero:  0,
		historySnapshotNine: 0,
		historyCrossAccount: 0,
		historyWindows:      0,
	} {
		var epoch pgtype.Int8
		if err := pool.QueryRow(ctx,
			`SELECT recipient_key_epoch FROM history_uploads WHERE id=$1`, id).Scan(&epoch); err != nil {
			t.Fatal(err)
		}
		if wantEpoch == 0 {
			if epoch.Valid {
				t.Errorf("history upload %s recipient_key_epoch=%d, want NULL", id, epoch.Int64)
			}
		} else if !epoch.Valid || epoch.Int64 != wantEpoch {
			t.Errorf("history upload %s recipient_key_epoch=%v, want %d", id, epoch, wantEpoch)
		}
	}
}
