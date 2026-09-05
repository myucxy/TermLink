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

func TestTranscriptHistoryCommitPagingIsolationAndDeletion(t *testing.T) {
	t.Skip("legacy server terminal-history transcripts were retired by migration 39")
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
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
	subject := sha256.Sum256([]byte("transcript-history:" + userID.String()))
	digest := sha256.Sum256([]byte("transcript ciphertext"))
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=$1`, sessionID)
	})
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES
		  ($1,$2,'WINDOWS_PC','Transcript owner',decode(repeat('11',32),'hex'),decode(repeat('12',32),'hex'),$3,'TRUSTED'),
		  ($4,$2,'ANDROID','Transcript A',decode(repeat('21',32),'hex'),decode(repeat('22',32),'hex'),$5,'TRUSTED'),
		  ($6,$2,'ANDROID','Transcript B',decode(repeat('31',32),'hex'),decode(repeat('32',32),'hex'),$7,'TRUSTED')`,
		ownerID, userID, "owner-"+ownerID.String(), androidA, "android-"+androidA.String(), androidB,
		"android-"+androidB.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch)
		VALUES($1,$2,$3,'Transcript PC','integration-test',1)`, pcID, userID, ownerID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,last_output_seq)
		VALUES($1,$2,'powershell','RUNNING',120,40,220)`, sessionID, pcID); err != nil {
		t.Fatal(err)
	}

	keyID := func(prefix string, recipient uuid.UUID) string {
		return fmt.Sprintf("%s:%s:%08d", prefix, strings.ReplaceAll(recipient.String(), "-", ""), 1)
	}
	commit := func(upload HistoryUploadRecord) (HistoryItem, error) {
		t.Helper()
		if err := repository.CreateHistoryUpload(ctx, userID, ownerID, upload); err != nil {
			return HistoryItem{}, err
		}
		return repository.CommitHistoryUpload(ctx, userID, ownerID, sessionID, upload.ID)
	}
	snapshot := func(recipient uuid.UUID, sequence int64) HistoryUploadRecord {
		id := uuid.New()
		rows, columns, version := 40, 120, 1
		format := HistoryFormatTerminalSnapshot
		return HistoryUploadRecord{
			ID: id, SessionID: sessionID, Kind: "SNAPSHOT",
			ObjectKey: "accounts/" + userID.String() + "/sessions/" + sessionID.String() + "/snapshot/" + id.String() + ".bin",
			KeyID:     keyID("snapshot-v1", recipient), CiphertextSHA256: digest[:], SizeBytes: 128,
			SnapshotSequence: &sequence, Rows: &rows, Columns: &columns,
			FormatName: &format, FormatVersion: &version, RecipientDeviceID: recipient,
			RecipientKeyEpoch: 1, ExpiresAt: time.Now().Add(10 * time.Minute),
		}
	}
	snapshotWithPublication := func(recipient uuid.UUID, sequence, initialBefore int64, pageCount int) HistoryUploadRecord {
		upload := snapshot(recipient, sequence)
		upload.TranscriptInitialBeforeLineID = &initialBefore
		upload.TranscriptPageCount = &pageCount
		return upload
	}
	transcript := func(recipient uuid.UUID, asOf, first, last int64) HistoryUploadRecord {
		id := uuid.New()
		version := 1
		format := HistoryFormatTerminalTranscript
		return HistoryUploadRecord{
			ID: id, SessionID: sessionID, Kind: "TRANSCRIPT",
			ObjectKey: "accounts/" + userID.String() + "/sessions/" + sessionID.String() + "/transcript/" + id.String() + ".bin",
			KeyID:     keyID("transcript-v1", recipient), CiphertextSHA256: digest[:], SizeBytes: 256,
			AsOfSnapshotSequence: &asOf, FirstLineID: &first, LastLineID: &last,
			FormatName: &format, FormatVersion: &version, RecipientDeviceID: recipient,
			RecipientKeyEpoch: 1, ExpiresAt: time.Now().Add(10 * time.Minute),
		}
	}

	preSnapshotUpload := transcript(androidA, 220, 201, 220)
	preSnapshot, err := commit(preSnapshotUpload)
	if err != nil {
		t.Fatalf("pre-snapshot transcript commit: %v", err)
	}
	preSnapshotPage, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: 220, Limit: 1,
	})
	if err != nil || preSnapshotPage.Ready || len(preSnapshotPage.Items) != 0 {
		t.Fatalf("pre-snapshot transcript visible: page=%+v err=%v", preSnapshotPage, err)
	}
	for _, recipient := range []uuid.UUID{androidA, androidB} {
		if _, err := commit(snapshot(recipient, 220)); err != nil {
			t.Fatalf("commit snapshot for %s: %v", recipient, err)
		}
	}
	visibleAfterSnapshot, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: 220, Limit: 1,
	})
	if err != nil || !visibleAfterSnapshot.Ready || len(visibleAfterSnapshot.Items) != 1 || visibleAfterSnapshot.Items[0].ID != preSnapshot.ID {
		t.Fatalf("post-snapshot transcript page=%+v err=%v", visibleAfterSnapshot, err)
	}
	high := preSnapshot
	middle, err := commit(transcript(androidA, 220, 181, 200))
	if err != nil {
		t.Fatal(err)
	}
	otherRecipient, err := commit(transcript(androidB, 220, 201, 220))
	if err != nil {
		t.Fatal(err)
	}
	retryUpload := transcript(androidA, 220, 201, 220)
	retry, err := commit(retryUpload)
	if err != nil || retry.ID != high.ID || retry.ObjectKey != high.ObjectKey {
		t.Fatalf("canonical retry=%+v err=%v, want %+v", retry, err, high)
	}
	if _, err := commit(transcript(androidA, 220, 190, 210)); !errors.Is(err, ErrConflict) {
		t.Fatalf("overlap error=%v, want ErrConflict", err)
	}

	firstPage, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: 220, Limit: 1,
	})
	if err != nil || !firstPage.Ready || !firstPage.HasMore || len(firstPage.Items) != 1 || firstPage.Items[0].ID != high.ID {
		t.Fatalf("first transcript page=%+v err=%v", firstPage, err)
	}
	if _, err := commit(transcript(androidA, 220, 161, 180)); err != nil {
		t.Fatal(err)
	}
	before := int64(high.FirstLineID)
	pinnedPage, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: 220, BeforeLineID: &before, AsOfRevision: &firstPage.AsOfRevision, Limit: 1,
	})
	if err != nil || !pinnedPage.Ready || pinnedPage.HasMore || len(pinnedPage.Items) != 1 || pinnedPage.Items[0].ID != middle.ID {
		t.Fatalf("pinned transcript page=%+v err=%v", pinnedPage, err)
	}
	freshPage, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: 220, BeforeLineID: &before, Limit: 1,
	})
	if err != nil || !freshPage.Ready || !freshPage.HasMore || len(freshPage.Items) != 1 || freshPage.Items[0].ID != middle.ID {
		t.Fatalf("fresh transcript page=%+v err=%v", freshPage, err)
	}
	recipientPage, err := repository.ListTranscriptPage(ctx, userID, androidB, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: 220, Limit: 1,
	})
	if err != nil || !recipientPage.Ready || recipientPage.HasMore || len(recipientPage.Items) != 1 || recipientPage.Items[0].ID != otherRecipient.ID {
		t.Fatalf("other recipient page=%+v err=%v", recipientPage, err)
	}

	// A publication-aware snapshot declares the complete transcript batch before
	// Windows starts its background upload. Partial batches remain invisible even
	// when line IDs have gaps and cannot be matched with cursor-1 arithmetic.
	const tailFirstSnapshotSequence int64 = 219
	initialBefore := int64(1500)
	ranges := [][2]int64{{25, 224}, {250, 449}, {500, 699}, {725, 924}, {1000, 1199}}
	if _, err := commit(snapshotWithPublication(androidA, tailFirstSnapshotSequence, initialBefore, len(ranges))); err != nil {
		t.Fatalf("commit tail-first snapshot: %v", err)
	}
	pendingEmpty, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: tailFirstSnapshotSequence, BeforeLineID: &initialBefore, Limit: 1,
	})
	if err != nil || pendingEmpty.Ready || len(pendingEmpty.Items) != 0 || pendingEmpty.HasMore {
		t.Fatalf("empty pending transcript publication page=%+v err=%v", pendingEmpty, err)
	}
	committedRanges := make([]HistoryItem, 0, len(ranges))
	for index, lineRange := range ranges {
		item, err := commit(transcript(androidA, tailFirstSnapshotSequence, lineRange[0], lineRange[1]))
		if err != nil {
			t.Fatalf("commit tail-first transcript range %d-%d: %v", lineRange[0], lineRange[1], err)
		}
		committedRanges = append(committedRanges, item)
		if index == len(ranges)-1 {
			continue
		}
		partial, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
			AsOfSnapshotSequence: tailFirstSnapshotSequence, BeforeLineID: &initialBefore, Limit: 1,
		})
		if err != nil || partial.Ready || len(partial.Items) != 0 || partial.HasMore {
			t.Fatalf("partial transcript after range %d-%d page=%+v err=%v", lineRange[0], lineRange[1], partial, err)
		}
	}

	firstTailPage, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: tailFirstSnapshotSequence, BeforeLineID: &initialBefore, Limit: 1,
	})
	if err != nil || !firstTailPage.Ready || !firstTailPage.HasMore || len(firstTailPage.Items) != 1 ||
		firstTailPage.Items[0].ID != committedRanges[4].ID ||
		firstTailPage.Items[0].FirstLineID != 1000 || firstTailPage.Items[0].LastLineID != 1199 {
		t.Fatalf("ready tail-first transcript page=%+v err=%v", firstTailPage, err)
	}
	topRetryUpload := transcript(androidA, tailFirstSnapshotSequence, ranges[4][0], ranges[4][1])
	topRetry, err := commit(topRetryUpload)
	if err != nil || topRetry.ID != committedRanges[4].ID || topRetry.ObjectKey != committedRanges[4].ObjectKey {
		t.Fatalf("completed publication canonical retry=%+v err=%v", topRetry, err)
	}
	if _, err := commit(transcript(androidA, tailFirstSnapshotSequence, 1, 24)); !errors.Is(err, ErrConflict) {
		t.Fatalf("completed publication accepted additional page: %v", err)
	}
	// Unrelated history can advance the session revision without changing the
	// publication's frozen traversal revision.
	if _, err := commit(snapshot(androidB, 216)); err != nil {
		t.Fatalf("commit unrelated post-publication snapshot: %v", err)
	}
	wrongRevision := firstTailPage.AsOfRevision - 1
	wrongBefore := int64(firstTailPage.Items[0].FirstLineID)
	if _, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: tailFirstSnapshotSequence, BeforeLineID: &wrongBefore,
		AsOfRevision: &wrongRevision, Limit: 1,
	}); !errors.Is(err, ErrHistoryAnchorAhead) {
		t.Fatalf("publication accepted mismatched cursor revision: %v", err)
	}

	gotRanges := [][2]uint64{{firstTailPage.Items[0].FirstLineID, firstTailPage.Items[0].LastLineID}}
	page := firstTailPage
	for page.HasMore {
		before := int64(page.Items[0].FirstLineID)
		page, err = repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
			AsOfSnapshotSequence: tailFirstSnapshotSequence,
			BeforeLineID:         &before,
			AsOfRevision:         &firstTailPage.AsOfRevision,
			Limit:                1,
		})
		if err != nil || !page.Ready || len(page.Items) != 1 || page.AsOfRevision != firstTailPage.AsOfRevision {
			t.Fatalf("pinned tail-first transcript before %d page=%+v err=%v", before, page, err)
		}
		gotRanges = append(gotRanges, [2]uint64{page.Items[0].FirstLineID, page.Items[0].LastLineID})
	}
	wantRanges := [][2]uint64{{1000, 1199}, {725, 924}, {500, 699}, {250, 449}, {25, 224}}
	if len(gotRanges) != len(wantRanges) {
		t.Fatalf("pinned tail-first ranges=%v, want %v", gotRanges, wantRanges)
	}
	for index := range wantRanges {
		if gotRanges[index] != wantRanges[index] {
			t.Fatalf("pinned tail-first ranges=%v, want %v", gotRanges, wantRanges)
		}
	}

	// Older Android releases do not retain the opaque response cursor. They
	// request every page again with the Snapshot sequence and the prior page's
	// first line ID. A completed publication must still pin each of those fresh
	// requests to its completed revision rather than rejecting or drifting it.
	const explicitPagingSnapshotSequence int64 = 215
	explicitBefore := int64(1001)
	explicitRanges := [][2]int64{{25, 224}, {225, 424}, {425, 624}, {625, 824}, {825, 1000}}
	if _, err := commit(snapshotWithPublication(androidA, explicitPagingSnapshotSequence, explicitBefore, len(explicitRanges))); err != nil {
		t.Fatalf("commit explicit-paging snapshot: %v", err)
	}
	for _, lineRange := range explicitRanges {
		if _, err := commit(transcript(androidA, explicitPagingSnapshotSequence, lineRange[0], lineRange[1])); err != nil {
			t.Fatalf("commit explicit-paging transcript range %d-%d: %v", lineRange[0], lineRange[1], err)
		}
	}
	wantExplicitRanges := [][2]uint64{{825, 1000}, {625, 824}, {425, 624}, {225, 424}, {25, 224}}
	var explicitRevision int64
	for index, wantRange := range wantExplicitRanges {
		page, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
			AsOfSnapshotSequence: explicitPagingSnapshotSequence,
			BeforeLineID:         &explicitBefore,
			Limit:                1,
		})
		if err != nil || !page.Ready || len(page.Items) != 1 ||
			page.Items[0].FirstLineID != wantRange[0] || page.Items[0].LastLineID != wantRange[1] {
			t.Fatalf("explicit transcript before %d page=%+v err=%v, want %v", explicitBefore, page, err, wantRange)
		}
		if index == 0 {
			explicitRevision = page.AsOfRevision
		} else if page.AsOfRevision != explicitRevision {
			t.Fatalf("explicit transcript revision drifted from %d to %d", explicitRevision, page.AsOfRevision)
		}
		if page.HasMore != (index < len(wantExplicitRanges)-1) {
			t.Fatalf("explicit transcript before %d has_more=%t", explicitBefore, page.HasMore)
		}
		explicitBefore = int64(page.Items[0].FirstLineID)
	}

	// Zero-page publications complete with the snapshot revision and distinguish
	// "no scrollback" from a publication that is still uploading.
	const zeroPageSnapshotSequence int64 = 218
	zeroBefore := int64(77)
	if _, err := commit(snapshotWithPublication(androidA, zeroPageSnapshotSequence, zeroBefore, 0)); err != nil {
		t.Fatalf("commit zero-page transcript publication: %v", err)
	}
	zeroPage, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: zeroPageSnapshotSequence, BeforeLineID: &zeroBefore, Limit: 1,
	})
	if err != nil || !zeroPage.Ready || zeroPage.HasMore || len(zeroPage.Items) != 0 || zeroPage.AsOfRevision < 1 {
		t.Fatalf("zero-page transcript publication=%+v err=%v", zeroPage, err)
	}
	if _, err := commit(transcript(androidA, zeroPageSnapshotSequence, 1, 50)); !errors.Is(err, ErrConflict) {
		t.Fatalf("zero-page publication accepted transcript page: %v", err)
	}

	// Publication readiness and counts are scoped to a single Android recipient.
	const recipientSnapshotSequence int64 = 217
	androidABefore, androidBBefore := int64(5000), int64(6000)
	if _, err := commit(snapshotWithPublication(androidA, recipientSnapshotSequence, androidABefore, 2)); err != nil {
		t.Fatalf("commit recipient A publication: %v", err)
	}
	if _, err := commit(snapshotWithPublication(androidB, recipientSnapshotSequence, androidBBefore, 1)); err != nil {
		t.Fatalf("commit recipient B publication: %v", err)
	}
	if _, err := commit(transcript(androidA, recipientSnapshotSequence, 100, 199)); err != nil {
		t.Fatalf("commit recipient A partial page: %v", err)
	}
	recipientBItem, err := commit(transcript(androidB, recipientSnapshotSequence, 1000, 1199))
	if err != nil {
		t.Fatalf("commit recipient B complete page: %v", err)
	}
	androidAPending, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: recipientSnapshotSequence, BeforeLineID: &androidABefore, Limit: 1,
	})
	if err != nil || androidAPending.Ready || len(androidAPending.Items) != 0 {
		t.Fatalf("recipient A partial publication=%+v err=%v", androidAPending, err)
	}
	androidBReady, err := repository.ListTranscriptPage(ctx, userID, androidB, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: recipientSnapshotSequence, BeforeLineID: &androidBBefore, Limit: 1,
	})
	if err != nil || !androidBReady.Ready || len(androidBReady.Items) != 1 || androidBReady.Items[0].ID != recipientBItem.ID {
		t.Fatalf("recipient B complete publication=%+v err=%v", androidBReady, err)
	}
	if _, err := commit(transcript(androidA, recipientSnapshotSequence, 4900, 5000)); !errors.Is(err, ErrConflict) {
		t.Fatalf("publication accepted page at/above initial cursor: %v", err)
	}
	androidATop, err := commit(transcript(androidA, recipientSnapshotSequence, 300, 399))
	if err != nil {
		t.Fatalf("complete recipient A publication: %v", err)
	}
	androidAReady, err := repository.ListTranscriptPage(ctx, userID, androidA, sessionID, TranscriptPageRequest{
		AsOfSnapshotSequence: recipientSnapshotSequence, BeforeLineID: &androidABefore, Limit: 1,
	})
	if err != nil || !androidAReady.Ready || len(androidAReady.Items) != 1 || androidAReady.Items[0].ID != androidATop.ID {
		t.Fatalf("recipient A completed publication=%+v err=%v", androidAReady, err)
	}

	legacy, err := repository.ListHistory(ctx, userID, androidA, sessionID)
	if err != nil {
		t.Fatal(err)
	}
	for _, item := range legacy {
		if item.Kind == string(HistoryKindTranscript) {
			t.Fatalf("legacy history leaked transcript: %+v", item)
		}
	}
	all, err := repository.ListHistoryPage(ctx, userID, androidA, sessionID, HistoryPageRequest{Kind: HistoryKindAll, Limit: 100})
	if err != nil {
		t.Fatal(err)
	}
	for _, item := range all.Items {
		if item.Kind == string(HistoryKindTranscript) {
			t.Fatalf("all history leaked transcript: %+v", item)
		}
	}

	if _, err := repository.Pool.Exec(ctx, `UPDATE sessions SET state='EXITED',ended_at=now() WHERE id=$1`, sessionID); err != nil {
		t.Fatal(err)
	}
	keys, err := repository.SessionHistoryObjectKeys(ctx, userID, ownerID, sessionID)
	if err != nil {
		t.Fatal(err)
	}
	keySet := make(map[string]bool, len(keys))
	for _, key := range keys {
		keySet[key] = true
	}
	if !keySet[high.ObjectKey] || !keySet[retryUpload.ObjectKey] {
		t.Fatalf("delete keys omit canonical or retry transcript: %v", keySet)
	}
	if err := repository.MarkSessionDeleted(ctx, userID, ownerID, sessionID, "transcript-delete"); err != nil {
		t.Fatal(err)
	}
	assertHistoryDeleteOutbox(t, ctx, repository, sessionID, keys...)
	var livePages, uploads int
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM transcript_pages WHERE session_id=$1 AND deleted_at IS NULL`, sessionID).Scan(&livePages); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM history_uploads WHERE session_id=$1`, sessionID).Scan(&uploads); err != nil {
		t.Fatal(err)
	}
	if livePages != 0 || uploads != 0 {
		t.Fatalf("deleted transcript live pages/uploads=%d/%d", livePages, uploads)
	}
}
