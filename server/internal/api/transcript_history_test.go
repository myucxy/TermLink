package api

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"math"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type recordingHistoryUploadStore struct {
	created    []store.HistoryUploadRecord
	pending    store.HistoryUploadRecord
	pendingErr error
	committed  store.HistoryItem
	commitErr  error
}

func (s *recordingHistoryUploadStore) CreateHistoryUpload(_ context.Context, _, _ uuid.UUID, item store.HistoryUploadRecord) error {
	s.created = append(s.created, item)
	return nil
}

func (s *recordingHistoryUploadStore) PendingHistoryUpload(_ context.Context, _, _, _, _ uuid.UUID) (store.HistoryUploadRecord, error) {
	return s.pending, s.pendingErr
}

func (s *recordingHistoryUploadStore) CommitHistoryUpload(_ context.Context, _, _, _, _ uuid.UUID) (store.HistoryItem, error) {
	return s.committed, s.commitErr
}

type recordingTranscriptPageLister struct {
	requests []store.TranscriptPageRequest
	page     store.TranscriptPage
	err      error
}

func (s *recordingTranscriptPageLister) ListTranscriptPage(_ context.Context, _, _, _ uuid.UUID, request store.TranscriptPageRequest) (store.TranscriptPage, error) {
	s.requests = append(s.requests, request)
	return s.page, s.err
}

func TestTranscriptPageRequestPinsAnchorAndExclusiveCursor(t *testing.T) {
	api := &API{TokenKey: []byte("transcript-history-test-signing-key-32-bytes")}
	userID, deviceID, sessionID := uuid.New(), uuid.New(), uuid.New()
	request := httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history?kind=transcript&as_of_snapshot_seq=77&before_line_id=401&limit=1", nil)
	page, cursorRequest, queryErr := api.parseTranscriptPageRequest(request, userID, deviceID, sessionID)
	if queryErr != nil || cursorRequest || page.AsOfSnapshotSequence != 77 || page.BeforeLineID == nil || *page.BeforeLineID != 401 || page.Limit != 1 {
		t.Fatalf("initial transcript page=%+v cursor=%t err=%v", page, cursorRequest, queryErr)
	}

	cursor, err := api.encodeTranscriptCursor(userID, deviceID, sessionID, 19, 77, 321)
	if err != nil {
		t.Fatal(err)
	}
	request = httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history?cursor="+cursor+"&limit=1", nil)
	page, cursorRequest, queryErr = api.parseTranscriptPageRequest(request, userID, deviceID, sessionID)
	if queryErr != nil || !cursorRequest || page.AsOfRevision == nil || *page.AsOfRevision != 19 ||
		page.AsOfSnapshotSequence != 77 || page.BeforeLineID == nil || *page.BeforeLineID != 321 || page.Limit != 1 {
		t.Fatalf("cursor transcript page=%+v cursor=%t err=%v", page, cursorRequest, queryErr)
	}

	for name, identity := range map[string][3]uuid.UUID{
		"cross-user":    {uuid.New(), deviceID, sessionID},
		"cross-device":  {userID, uuid.New(), sessionID},
		"cross-session": {userID, deviceID, uuid.New()},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := api.decodeTranscriptCursor(cursor, identity[0], identity[1], identity[2]); err == nil {
				t.Fatal("cross-identity transcript cursor accepted")
			}
		})
	}
	if _, err := api.decodeHistoryCursor(cursor, userID, deviceID, sessionID); err == nil {
		t.Fatal("transcript cursor accepted by legacy history cursor domain")
	}
}

func TestTranscriptPageRequestRejectsInvalidSelectors(t *testing.T) {
	api := &API{TokenKey: []byte("transcript-history-test-signing-key-32-bytes")}
	userID, deviceID, sessionID := uuid.New(), uuid.New(), uuid.New()
	queries := []string{
		"?kind=transcript",
		"?kind=transcript&as_of_snapshot_seq=0",
		"?kind=transcript&as_of_snapshot_seq=" + "9223372036854775808",
		"?kind=transcript&as_of_snapshot_seq=1&before_line_id=0",
		"?kind=transcript&as_of_snapshot_seq=1&before_line_id=9223372036854775808",
		"?kind=transcript&as_of_snapshot_seq=1&limit=2",
		"?kind=transcript&as_of_snapshot_seq=1&format_name=" + terminalTranscriptFormat,
	}
	for _, query := range queries {
		request := httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history"+query, nil)
		if _, _, err := api.parseTranscriptPageRequest(request, userID, deviceID, sessionID); err == nil {
			t.Fatalf("invalid transcript selector accepted: %s", query)
		}
	}
}

func TestTranscriptMetadataBoundsAndResponse(t *testing.T) {
	if err := validateHistoryTranscriptMetadata(1, 1, 1, maxTerminalTranscriptCiphertextBytes, terminalTranscriptFormat, 1); err != nil {
		t.Fatalf("maximum valid transcript rejected: %v", err)
	}
	for _, input := range []struct {
		asOf, first, last uint64
		size              int64
	}{
		{0, 1, 1, 1},
		{1, 0, 1, 1},
		{1, 2, 1, 1},
		{math.MaxUint64, 1, 1, 1},
		{1, math.MaxUint64, math.MaxUint64, 1},
		{1, 1, math.MaxUint64, 1},
		{1, 1, 1, maxTerminalTranscriptCiphertextBytes + 1},
	} {
		if err := validateHistoryTranscriptMetadata(input.asOf, input.first, input.last, input.size, terminalTranscriptFormat, 1); err == nil {
			t.Fatalf("invalid transcript metadata accepted: %+v", input)
		}
	}

	objectStore := &recordingHistoryObjectStore{}
	digest := sha256.Sum256([]byte("transcript ciphertext"))
	item := store.HistoryItem{
		Kind: string(store.HistoryKindTranscript), ID: uuid.New(), AsOfSnapshotSequence: 77,
		FirstLineID: 201, LastLineID: 220, ObjectKey: "transcript-object", KeyID: "transcript-key",
		CiphertextSHA256: digest[:], SizeBytes: 512, FormatName: terminalTranscriptFormat,
		FormatVersion: 1, CreatedAt: time.Unix(1, 0).UTC(),
	}
	response, err := (&API{History: objectStore}).historyResponseItems(context.Background(), []store.HistoryItem{item}, true)
	if err != nil {
		t.Fatal(err)
	}
	got := response[0]
	if got["as_of_snapshot_seq"] != uint64(77) || got["first_line_id"] != uint64(201) ||
		got["last_line_id"] != uint64(220) || got["format_name"] != terminalTranscriptFormat {
		t.Fatalf("transcript response=%v", got)
	}
	for _, forbidden := range []string{"start_seq", "end_seq", "snapshot_seq", "rows", "columns"} {
		if _, present := got[forbidden]; present {
			t.Fatalf("transcript response leaked %s: %v", forbidden, got)
		}
	}
}

func TestCreateTranscriptRejectsOversizeBeforeStoreAccess(t *testing.T) {
	digest := sha256.Sum256([]byte("transcript ciphertext"))
	body := `{"kind":"TRANSCRIPT","key_id":"transcript-key-0001","ciphertext_sha256":"` +
		base64.RawURLEncoding.EncodeToString(digest[:]) + `","size_bytes":524289,"as_of_snapshot_seq":1,"first_line_id":1,"last_line_id":2,"format_name":"` +
		terminalTranscriptFormat + `","format_version":1}`
	request := httptest.NewRequest(http.MethodPost, "/v1/sessions/session/history/uploads", strings.NewReader(body))
	request.SetPathValue("id", uuid.New().String())
	response := httptest.NewRecorder()
	(&API{History: &recordingHistoryObjectStore{}}).createHistoryUpload(response, request)
	if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), `"code":"INVALID_HISTORY_TRANSCRIPT"`) {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestCreateSnapshotAcceptsPairedTranscriptPublicationMetadataAndLegacyShape(t *testing.T) {
	digest := sha256.Sum256([]byte("snapshot ciphertext"))
	recipientID := uuid.New()
	keyID := "snapshot-v1:" + strings.ReplaceAll(recipientID.String(), "-", "") + ":00000001"
	tests := []struct {
		name        string
		metadata    string
		wantInitial *int64
		wantCount   *int
	}{
		{name: "legacy-metadata-absent"},
		{name: "publication-metadata-paired", metadata: `,"transcript_initial_before_line_id":1001,"transcript_page_count":5`, wantInitial: int64Pointer(1001), wantCount: intPointer(5)},
		{name: "publication-zero-pages", metadata: `,"transcript_initial_before_line_id":1001,"transcript_page_count":0`, wantInitial: int64Pointer(1001), wantCount: intPointer(0)},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			uploads := &recordingHistoryUploadStore{}
			body := fmt.Sprintf(`{"kind":"SNAPSHOT","key_id":%q,"ciphertext_sha256":%q,"size_bytes":512,"snapshot_seq":50,"rows":25,"columns":80,"format_name":%q,"format_version":1%s}`,
				keyID, base64.RawURLEncoding.EncodeToString(digest[:]), terminalSnapshotFormat, test.metadata)
			request := httptest.NewRequest(http.MethodPost, "/v1/sessions/session/history/uploads", strings.NewReader(body))
			request.SetPathValue("id", uuid.New().String())
			response := httptest.NewRecorder()
			(&API{History: &recordingHistoryObjectStore{}, historyUploads: uploads}).createHistoryUpload(response, request)
			if response.Code != http.StatusCreated {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
			if len(uploads.created) != 1 {
				t.Fatalf("created uploads=%d", len(uploads.created))
			}
			got := uploads.created[0]
			if !equalOptionalInt64(got.TranscriptInitialBeforeLineID, test.wantInitial) ||
				!equalOptionalInt(got.TranscriptPageCount, test.wantCount) {
				t.Fatalf("publication metadata initial=%v count=%v", got.TranscriptInitialBeforeLineID, got.TranscriptPageCount)
			}
		})
	}
}

func TestCreateHistoryUploadRejectsInvalidTranscriptPublicationMetadata(t *testing.T) {
	digest := base64.RawURLEncoding.EncodeToString(sha256Bytes("history ciphertext"))
	recipientID := strings.ReplaceAll(uuid.New().String(), "-", "")
	snapshotBase := fmt.Sprintf(`{"kind":"SNAPSHOT","key_id":"snapshot-v1:%s:00000001","ciphertext_sha256":%q,"size_bytes":512,"snapshot_seq":50,"rows":25,"columns":80,"format_name":%q,"format_version":1`, recipientID, digest, terminalSnapshotFormat)
	boundedBase := fmt.Sprintf(`{"kind":"SNAPSHOT","key_id":"history-v1:%s:00000001","ciphertext_sha256":%q,"size_bytes":512,"snapshot_seq":50,"rows":25,"columns":80,"format_name":%q,"format_version":1`, recipientID, digest, boundedEventReplayFormat)
	chunkBase := fmt.Sprintf(`{"kind":"CHUNK","key_id":"history-v1:%s:00000001","ciphertext_sha256":%q,"size_bytes":512,"start_seq":1,"end_seq":2`, recipientID, digest)
	transcriptBase := fmt.Sprintf(`{"kind":"TRANSCRIPT","key_id":"transcript-v1:%s:00000001","ciphertext_sha256":%q,"size_bytes":512,"as_of_snapshot_seq":50,"first_line_id":1,"last_line_id":2,"format_name":%q,"format_version":1`, recipientID, digest, terminalTranscriptFormat)
	tests := []struct {
		name string
		body string
		code string
	}{
		{name: "initial-only", body: snapshotBase + `,"transcript_initial_before_line_id":1001`, code: "INVALID_HISTORY_SNAPSHOT"},
		{name: "count-only", body: snapshotBase + `,"transcript_page_count":5`, code: "INVALID_HISTORY_SNAPSHOT"},
		{name: "zero-initial", body: snapshotBase + `,"transcript_initial_before_line_id":0,"transcript_page_count":0`, code: "INVALID_HISTORY_SNAPSHOT"},
		{name: "initial-over-int64", body: snapshotBase + `,"transcript_initial_before_line_id":9223372036854775808,"transcript_page_count":1`, code: "INVALID_HISTORY_SNAPSHOT"},
		{name: "negative-page-count", body: snapshotBase + `,"transcript_initial_before_line_id":1,"transcript_page_count":-1`, code: "INVALID_HISTORY_SNAPSHOT"},
		{name: "excessive-page-count", body: snapshotBase + fmt.Sprintf(`,"transcript_initial_before_line_id":1,"transcript_page_count":%d`, maxTranscriptPublicationPageCount+1), code: "INVALID_HISTORY_SNAPSHOT"},
		{name: "bounded-replay", body: boundedBase + `,"transcript_initial_before_line_id":1001,"transcript_page_count":5`, code: "INVALID_HISTORY_SNAPSHOT"},
		{name: "chunk", body: chunkBase + `,"transcript_initial_before_line_id":1001,"transcript_page_count":5`, code: "INVALID_HISTORY_RANGE"},
		{name: "transcript", body: transcriptBase + `,"transcript_initial_before_line_id":1001,"transcript_page_count":5`, code: "INVALID_HISTORY_TRANSCRIPT"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			request := httptest.NewRequest(http.MethodPost, "/v1/sessions/session/history/uploads", strings.NewReader(test.body+`}`))
			request.SetPathValue("id", uuid.New().String())
			response := httptest.NewRecorder()
			(&API{History: &recordingHistoryObjectStore{}}).createHistoryUpload(response, request)
			if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), `"code":"`+test.code+`"`) {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
		})
	}
}

func TestCommitSnapshotMirrorsTranscriptPublicationMetadataValidation(t *testing.T) {
	digest := sha256Bytes("snapshot ciphertext")
	sequence, rows, columns, format, version := int64(50), 25, 80, terminalSnapshotFormat, 1
	count := 5
	uploads := &recordingHistoryUploadStore{pending: store.HistoryUploadRecord{
		Kind: "SNAPSHOT", SnapshotSequence: &sequence, Rows: &rows, Columns: &columns,
		FormatName: &format, FormatVersion: &version, SizeBytes: 512, CiphertextSHA256: digest,
		TranscriptPageCount: &count,
	}}
	request := httptest.NewRequest(http.MethodPost, "/v1/sessions/session/history/uploads/upload/commit", nil)
	request.SetPathValue("id", uuid.New().String())
	request.SetPathValue("uploadId", uuid.New().String())
	response := httptest.NewRecorder()
	(&API{History: &recordingHistoryObjectStore{}, historyUploads: uploads}).commitHistoryUpload(response, request)
	if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), `"code":"INVALID_HISTORY_SNAPSHOT"`) {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestTranscriptHistoryResponsePublicationReadinessAndCursorGating(t *testing.T) {
	userID, deviceID, sessionID := uuid.New(), uuid.New(), uuid.New()
	requestURL := "/v1/sessions/" + sessionID.String() + "/history?kind=transcript&as_of_snapshot_seq=50&before_line_id=1001&limit=1"
	tests := []struct {
		name       string
		page       store.TranscriptPage
		wantReady  bool
		wantCursor bool
		wantItems  int
	}{
		{name: "pending-empty", page: store.TranscriptPage{AsOfRevision: 7, Ready: false}, wantReady: false},
		{name: "completed-empty", page: store.TranscriptPage{AsOfRevision: 8, Ready: true}, wantReady: true},
		{name: "pending-does-not-issue-cursor", page: store.TranscriptPage{AsOfRevision: 9, Ready: false, HasMore: true, Items: []store.HistoryItem{transcriptHistoryTestItem()}}, wantReady: false, wantItems: 1},
		{name: "completed-page-issues-cursor", page: store.TranscriptPage{AsOfRevision: 10, Ready: true, HasMore: true, Items: []store.HistoryItem{transcriptHistoryTestItem()}}, wantReady: true, wantCursor: true, wantItems: 1},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			pages := &recordingTranscriptPageLister{page: test.page}
			request := httptest.NewRequest(http.MethodGet, requestURL, nil)
			response := httptest.NewRecorder()
			api := &API{
				TokenKey: []byte("0123456789abcdef0123456789abcdef"), History: &recordingHistoryObjectStore{},
				transcriptPages: pages,
			}
			api.listTranscriptHistory(response, request, userID, deviceID, sessionID)
			if response.Code != http.StatusOK {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
			var body struct {
				Ready                bool              `json:"ready"`
				HasMore              bool              `json:"has_more"`
				NextCursor           *string           `json:"next_cursor"`
				AsOfSnapshotSequence uint64            `json:"as_of_snapshot_seq"`
				Items                []json.RawMessage `json:"items"`
			}
			if err := json.Unmarshal(response.Body.Bytes(), &body); err != nil {
				t.Fatal(err)
			}
			if body.Ready != test.wantReady || (body.NextCursor != nil) != test.wantCursor ||
				len(body.Items) != test.wantItems || body.AsOfSnapshotSequence != 50 {
				t.Fatalf("response=%s", response.Body.String())
			}
		})
	}
}

func transcriptHistoryTestItem() store.HistoryItem {
	return store.HistoryItem{
		Kind: string(store.HistoryKindTranscript), ID: uuid.New(), AsOfSnapshotSequence: 50,
		FirstLineID: 825, LastLineID: 1000, ObjectKey: "transcript-page", KeyID: "transcript-key",
		CiphertextSHA256: sha256Bytes("transcript ciphertext"), SizeBytes: 512,
		FormatName: terminalTranscriptFormat, FormatVersion: 1, CreatedAt: time.Unix(1, 0).UTC(),
	}
}

func sha256Bytes(value string) []byte {
	digest := sha256.Sum256([]byte(value))
	return digest[:]
}

func int64Pointer(value int64) *int64 { return &value }
func intPointer(value int) *int       { return &value }

func equalOptionalInt64(left, right *int64) bool {
	return left == nil && right == nil || left != nil && right != nil && *left == *right
}

func equalOptionalInt(left, right *int) bool {
	return left == nil && right == nil || left != nil && right != nil && *left == *right
}
