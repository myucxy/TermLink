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
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestValidateHistorySnapshotMetadata(t *testing.T) {
	tests := []struct {
		name          string
		sequence      int64
		sizeBytes     int64
		rows, columns int
		format        string
		wantError     bool
	}{
		{name: "portable", sequence: 1, sizeBytes: MaxSnapshotCiphertextBytes, rows: 256, columns: 512, format: HistoryFormatTerminalSnapshot},
		{name: "legacy-zero", sequence: 0, sizeBytes: 1, rows: 1, columns: 1, format: HistoryFormatBoundedEventReplay},
		{name: "portable-zero", sequence: 0, sizeBytes: 1, rows: 24, columns: 80, format: HistoryFormatTerminalSnapshot, wantError: true},
		{name: "negative-sequence", sequence: -1, sizeBytes: 1, rows: 24, columns: 80, format: HistoryFormatBoundedEventReplay, wantError: true},
		{name: "too-large", sequence: 1, sizeBytes: MaxSnapshotCiphertextBytes + 1, rows: 24, columns: 80, format: HistoryFormatTerminalSnapshot, wantError: true},
		{name: "too-tall", sequence: 1, sizeBytes: 1, rows: 257, columns: 80, format: HistoryFormatTerminalSnapshot, wantError: true},
		{name: "too-wide", sequence: 1, sizeBytes: 1, rows: 24, columns: 513, format: HistoryFormatTerminalSnapshot, wantError: true},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			err := ValidateHistorySnapshotMetadata(test.sequence, test.sizeBytes, test.rows, test.columns, test.format, 1)
			if (err != nil) != test.wantError {
				t.Fatalf("validation error=%v, wantError=%t", err, test.wantError)
			}
		})
	}
}

func TestParseHistoryRecipientKeyIDIsCanonicalAndFormatBound(t *testing.T) {
	recipient := uuid.MustParse("10213243-5465-7687-98a9-bacbdcedfe0f")
	recipientN := strings.ReplaceAll(recipient.String(), "-", "")
	for _, test := range []struct {
		kind, format, key string
	}{
		{kind: "CHUNK", key: "history-v1:" + recipientN + ":00000007"},
		{kind: "SNAPSHOT", format: HistoryFormatBoundedEventReplay, key: "history-v1:" + recipientN + ":00000007"},
		{kind: "SNAPSHOT", format: HistoryFormatTerminalSnapshot, key: "snapshot-v1:" + recipientN + ":00000007"},
	} {
		gotRecipient, gotEpoch, err := ParseHistoryRecipientKeyID(test.kind, test.format, test.key)
		if err != nil || gotRecipient != recipient || gotEpoch != 7 {
			t.Fatalf("ParseHistoryRecipientKeyID(%q)=%s/%d/%v", test.key, gotRecipient, gotEpoch, err)
		}
	}
	for _, key := range []string{
		"snapshot-v1:" + recipientN + ":00000007",
		"history-v1:" + strings.ToUpper(recipientN) + ":00000007",
		"history-v1:" + recipientN + ":7",
		"history-v1:" + recipientN + ":00000000",
		"history-v1:" + recipientN + ":9223372036854775808",
	} {
		if _, _, err := ParseHistoryRecipientKeyID("CHUNK", "", key); !errors.Is(err, ErrInvalidHistoryRecipient) {
			t.Fatalf("ParseHistoryRecipientKeyID(%q) error=%v", key, err)
		}
	}
}

func TestCreateHistoryUploadRejectsInvalidSnapshotBeforeDatabase(t *testing.T) {
	sequence := int64(0)
	rows, columns, version := 24, 80, 1
	format := HistoryFormatTerminalSnapshot
	err := (&Postgres{}).CreateHistoryUpload(context.Background(), uuid.New(), uuid.New(), HistoryUploadRecord{
		Kind: "SNAPSHOT", SnapshotSequence: &sequence, Rows: &rows, Columns: &columns,
		FormatName: &format, FormatVersion: &version, SizeBytes: 1,
	})
	if !errors.Is(err, ErrInvalidHistorySnapshot) {
		t.Fatalf("CreateHistoryUpload() error=%v, want ErrInvalidHistorySnapshot", err)
	}
}

func TestListHistoryPageKeepsStableAnchorAcrossConcurrentUploads(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()
	repository.AutoTrustDevices = true
	if err := repository.VerifySchema(ctx); err != nil {
		t.Fatal(err)
	}

	userID, ownerID, readerID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New(), uuid.New()
	subject := sha256.Sum256([]byte("history-paging:" + userID.String()))
	digest := sha256.Sum256([]byte("history-paging-ciphertext"))
	historyKeyID := fmt.Sprintf("history-v1:%s:%08d", strings.ReplaceAll(readerID.String(), "-", ""), 1)
	snapshotKeyID := fmt.Sprintf("snapshot-v1:%s:%08d", strings.ReplaceAll(readerID.String(), "-", ""), 1)
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
		  ($1,$2,'WINDOWS_PC','History owner',decode(repeat('41',32),'hex'),decode(repeat('42',32),'hex'),$3,'TRUSTED'),
		  ($4,$2,'ANDROID','History reader',decode(repeat('51',32),'hex'),decode(repeat('52',32),'hex'),$5,'TRUSTED')`,
		ownerID, userID, "history-owner-"+ownerID.String(), readerID, "history-reader-"+readerID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version)
		VALUES($1,$2,$3,'History paging PC','integration-test')`, pcID, userID, ownerID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,last_output_seq)
		VALUES($1,$2,'powershell','RUNNING',120,40,30)`, sessionID, pcID); err != nil {
		t.Fatal(err)
	}

	insertChunk := func(start, end int64) uuid.UUID {
		t.Helper()
		id := uuid.New()
		if _, err := repository.Pool.Exec(ctx, `
			INSERT INTO event_chunks(id,session_id,start_seq,end_seq,object_key,key_id,ciphertext_sha256,size_bytes,recipient_device_id)
			VALUES($1,$2,$3,$4,$5,$6,$7,128,$8)`, id, sessionID, start, end,
			"history-paging/"+id.String()+".bin", historyKeyID, digest[:], readerID); err != nil {
			t.Fatal(err)
		}
		return id
	}
	firstChunkID := insertChunk(1, 10)
	snapshotID := uuid.New()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO snapshots(id,session_id,snapshot_seq,rows,columns,object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version,recipient_device_id)
		VALUES($1,$2,10,40,120,$3,$4,$5,256,'BOUNDED_EVENT_REPLAY',1,$6)`,
		snapshotID, sessionID, "history-paging/"+snapshotID.String()+".bin", historyKeyID, digest[:], readerID); err != nil {
		t.Fatal(err)
	}
	secondChunkID := insertChunk(11, 20)

	first, err := repository.ListHistoryPage(ctx, userID, readerID, sessionID, HistoryPageRequest{Kind: HistoryKindAll, Limit: 2})
	if err != nil {
		t.Fatal(err)
	}
	if !first.HasMore || first.AsOfRevision != 3 || len(first.Items) != 2 ||
		first.Items[0].ID != secondChunkID || first.Items[1].ID != snapshotID ||
		first.Items[1].Rows != 40 || first.Items[1].Columns != 120 {
		t.Fatalf("first page=%+v", first)
	}

	newChunkID := insertChunk(21, 30)
	before := HistoryPosition{Sequence: int64(first.Items[1].SnapshotSequence), Kind: HistoryKindSnapshot, ID: snapshotID}
	second, err := repository.ListHistoryPage(ctx, userID, readerID, sessionID, HistoryPageRequest{
		Kind: HistoryKindAll, Limit: 2, AsOfRevision: &first.AsOfRevision, Before: &before,
	})
	if err != nil {
		t.Fatal(err)
	}
	if second.HasMore || second.AsOfRevision != first.AsOfRevision || len(second.Items) != 1 || second.Items[0].ID != firstChunkID {
		t.Fatalf("stable second page=%+v", second)
	}
	for _, item := range second.Items {
		if item.ID == newChunkID {
			t.Fatal("post-anchor upload leaked into a continuation page")
		}
	}

	fresh, err := repository.ListHistoryPage(ctx, userID, readerID, sessionID, HistoryPageRequest{Kind: HistoryKindChunk, Limit: 1})
	if err != nil {
		t.Fatal(err)
	}
	if fresh.AsOfRevision != 4 || len(fresh.Items) != 1 || fresh.Items[0].ID != newChunkID {
		t.Fatalf("fresh page=%+v", fresh)
	}

	// Hold an upload transaction after its trigger has advanced the session
	// revision but before commit. The first-page share lock must wait, then
	// include the object and its committed revision in one atomic view.
	inflightTx, err := repository.Pool.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	inflightID := uuid.New()
	if _, err := inflightTx.Exec(ctx, `
		INSERT INTO event_chunks(id,session_id,start_seq,end_seq,object_key,key_id,ciphertext_sha256,size_bytes,recipient_device_id)
		VALUES($1,$2,31,40,$3,$4,$5,128,$6)`, inflightID, sessionID,
		"history-paging/"+inflightID.String()+".bin", historyKeyID, digest[:], readerID); err != nil {
		_ = inflightTx.Rollback(ctx)
		t.Fatal(err)
	}
	type pageResult struct {
		page HistoryPage
		err  error
	}
	pageReady := make(chan pageResult, 1)
	go func() {
		page, pageErr := repository.ListHistoryPage(ctx, userID, readerID, sessionID, HistoryPageRequest{Kind: HistoryKindChunk, Limit: 1})
		pageReady <- pageResult{page: page, err: pageErr}
	}()
	select {
	case early := <-pageReady:
		_ = inflightTx.Rollback(ctx)
		t.Fatalf("page crossed an uncommitted revision fence: %+v", early)
	case <-time.After(100 * time.Millisecond):
	}
	if err := inflightTx.Commit(ctx); err != nil {
		t.Fatal(err)
	}
	var committed pageResult
	select {
	case committed = <-pageReady:
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	if committed.err != nil || committed.page.AsOfRevision != 5 || len(committed.page.Items) != 1 || committed.page.Items[0].ID != inflightID {
		t.Fatalf("page after concurrent commit=%+v err=%v", committed.page, committed.err)
	}
	invalidPendingID := uuid.New()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO history_uploads(id,user_id,actor_device_id,session_id,kind,object_key,key_id,ciphertext_sha256,size_bytes,snapshot_seq,rows,columns,format_name,format_version,recipient_device_id,recipient_key_epoch,expires_at)
		VALUES($1,$2,$3,$4,'SNAPSHOT',$5,$6,$7,1024,0,24,80,'TERMLINK_TERMINAL_SNAPSHOT',1,$8,1,now()+interval '10 minutes')`,
		invalidPendingID, userID, ownerID, sessionID, "history-paging/"+invalidPendingID.String()+".bin", snapshotKeyID, digest[:], readerID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.CommitHistoryUpload(ctx, userID, ownerID, sessionID, invalidPendingID); !errors.Is(err, ErrInvalidHistorySnapshot) {
		t.Fatalf("commit invalid portable snapshot error=%v, want ErrInvalidHistorySnapshot", err)
	}

	aheadPendingID := uuid.New()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO history_uploads(id,user_id,actor_device_id,session_id,kind,object_key,key_id,ciphertext_sha256,size_bytes,snapshot_seq,rows,columns,format_name,format_version,recipient_device_id,recipient_key_epoch,expires_at)
		VALUES($1,$2,$3,$4,'SNAPSHOT',$5,$6,$7,1024,31,24,80,'TERMLINK_TERMINAL_SNAPSHOT',1,$8,1,now()+interval '10 minutes')`,
		aheadPendingID, userID, ownerID, sessionID, "history-paging/"+aheadPendingID.String()+".bin", snapshotKeyID, digest[:], readerID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.CommitHistoryUpload(ctx, userID, ownerID, sessionID, aheadPendingID); !errors.Is(err, ErrInvalidHistorySnapshot) {
		t.Fatalf("commit snapshot ahead of last output error=%v, want ErrInvalidHistorySnapshot", err)
	}
	var aheadCommitted bool
	if err := repository.Pool.QueryRow(ctx,
		`SELECT committed_at IS NOT NULL FROM history_uploads WHERE id=$1`, aheadPendingID,
	).Scan(&aheadCommitted); err != nil {
		t.Fatal(err)
	}
	if aheadCommitted {
		t.Fatal("snapshot ahead of authoritative last output was committed")
	}

	insertPortableSnapshot := func(sequence int64) uuid.UUID {
		t.Helper()
		id := uuid.New()
		if _, err := repository.Pool.Exec(ctx, `
			INSERT INTO snapshots(id,session_id,snapshot_seq,rows,columns,object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version,recipient_device_id)
			VALUES($1,$2,$3,64,180,$4,$5,$6,2048,'TERMLINK_TERMINAL_SNAPSHOT',1,$7)`,
			id, sessionID, sequence, "history-paging/"+id.String()+".bin", snapshotKeyID, digest[:], readerID); err != nil {
			t.Fatal(err)
		}
		return id
	}
	olderPortableSnapshotID := insertPortableSnapshot(20)
	equalPortableSnapshotID := insertPortableSnapshot(30)
	aheadPortableSnapshotID := insertPortableSnapshot(40)

	latestSnapshot, err := repository.ListHistoryPage(ctx, userID, readerID, sessionID, HistoryPageRequest{
		Kind: HistoryKindSnapshot, FormatName: HistoryFormatTerminalSnapshot, Limit: 1,
	})
	if err != nil {
		t.Fatal(err)
	}
	if !latestSnapshot.HasMore || latestSnapshot.AsOfRevision != 8 || len(latestSnapshot.Items) != 1 ||
		latestSnapshot.Items[0].ID != equalPortableSnapshotID || latestSnapshot.Items[0].SnapshotSequence != 30 ||
		latestSnapshot.Items[0].Rows != 64 || latestSnapshot.Items[0].Columns != 180 ||
		latestSnapshot.Items[0].FormatName != "TERMLINK_TERMINAL_SNAPSHOT" || latestSnapshot.Items[0].FormatVersion != 1 {
		t.Fatalf("latest portable snapshot=%+v", latestSnapshot)
	}
	if latestSnapshot.Items[0].ID == aheadPortableSnapshotID {
		t.Fatal("snapshot ahead of authoritative last output was selected")
	}

	beforePortableSnapshot := HistoryPosition{
		Sequence: int64(latestSnapshot.Items[0].SnapshotSequence),
		Kind:     HistoryKindSnapshot,
		ID:       latestSnapshot.Items[0].ID,
	}
	olderSnapshot, err := repository.ListHistoryPage(ctx, userID, readerID, sessionID, HistoryPageRequest{
		Kind: HistoryKindSnapshot, FormatName: HistoryFormatTerminalSnapshot, Limit: 1,
		AsOfRevision: &latestSnapshot.AsOfRevision, Before: &beforePortableSnapshot,
	})
	if err != nil {
		t.Fatal(err)
	}
	if olderSnapshot.HasMore || olderSnapshot.AsOfRevision != latestSnapshot.AsOfRevision || len(olderSnapshot.Items) != 1 ||
		olderSnapshot.Items[0].ID != olderPortableSnapshotID || olderSnapshot.Items[0].SnapshotSequence != 20 {
		t.Fatalf("older eligible portable snapshot=%+v", olderSnapshot)
	}

	var sessionRevision, uniqueRevisions int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT s.history_revision,
		       (SELECT count(DISTINCT history_revision) FROM (
		          SELECT history_revision FROM event_chunks WHERE session_id=$1
		          UNION ALL
		          SELECT history_revision FROM snapshots WHERE session_id=$1
		       ) revisions)
		FROM sessions s WHERE s.id=$1`, sessionID).Scan(&sessionRevision, &uniqueRevisions); err != nil {
		t.Fatal(err)
	}
	if sessionRevision != 8 || uniqueRevisions != 8 {
		t.Fatalf("session revision=%d unique object revisions=%d, want 8/8", sessionRevision, uniqueRevisions)
	}

	noEligibleSessionID, noEligibleSnapshotID := uuid.New(), uuid.New()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,last_output_seq)
		VALUES($1,$2,'powershell','RUNNING',120,40,30)`, noEligibleSessionID, pcID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO snapshots(id,session_id,snapshot_seq,rows,columns,object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version,recipient_device_id)
		VALUES($1,$2,40,64,180,$3,$4,$5,2048,'TERMLINK_TERMINAL_SNAPSHOT',1,$6)`,
		noEligibleSnapshotID, noEligibleSessionID, "history-paging/"+noEligibleSnapshotID.String()+".bin", snapshotKeyID, digest[:], readerID); err != nil {
		t.Fatal(err)
	}
	noEligibleSnapshot, err := repository.ListHistoryPage(ctx, userID, readerID, noEligibleSessionID, HistoryPageRequest{
		Kind: HistoryKindSnapshot, FormatName: HistoryFormatTerminalSnapshot, Limit: 1,
	})
	if err != nil {
		t.Fatal(err)
	}
	if noEligibleSnapshot.HasMore || noEligibleSnapshot.AsOfRevision != 1 || len(noEligibleSnapshot.Items) != 0 {
		t.Fatalf("snapshot-only-ahead page=%+v, want empty revision-1 page", noEligibleSnapshot)
	}
}
