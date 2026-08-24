package api

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/history"
	"github.com/termlink/termlink/server/internal/store"
)

func TestHistoryPageTokensRoundTripAndBindAuthority(t *testing.T) {
	api := &API{TokenKey: []byte("0123456789abcdef0123456789abcdef")}
	userID, deviceID, sessionID := uuid.New(), uuid.New(), uuid.New()
	asOf, err := api.encodeHistoryAsOf(userID, deviceID, sessionID, 41)
	if err != nil {
		t.Fatal(err)
	}
	request := httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history?kind=snapshot&format_name=TERMLINK_TERMINAL_SNAPSHOT&limit=1&as_of="+asOf, nil)
	page, cursorRequest, queryErr := api.parseHistoryPageRequest(request, userID, deviceID, sessionID)
	if queryErr != nil || cursorRequest || page.Kind != store.HistoryKindSnapshot || page.Limit != 1 ||
		page.FormatName != store.HistoryFormatTerminalSnapshot ||
		page.AsOfRevision == nil || *page.AsOfRevision != 41 {
		t.Fatalf("parsed first page=%+v cursor=%t err=%v", page, cursorRequest, queryErr)
	}

	itemID := uuid.New()
	cursor, err := api.encodeHistoryCursor(userID, deviceID, sessionID, store.HistoryKindSnapshot, store.HistoryFormatTerminalSnapshot, 41, store.HistoryItem{
		Kind: string(store.HistoryKindSnapshot), ID: itemID, SnapshotSequence: 17,
	})
	if err != nil {
		t.Fatal(err)
	}
	request = httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history?cursor="+cursor+"&limit=7", nil)
	page, cursorRequest, queryErr = api.parseHistoryPageRequest(request, userID, deviceID, sessionID)
	if queryErr != nil || !cursorRequest || page.Kind != store.HistoryKindSnapshot || page.Limit != 7 ||
		page.FormatName != store.HistoryFormatTerminalSnapshot ||
		page.AsOfRevision == nil || *page.AsOfRevision != 41 || page.Before == nil ||
		page.Before.Sequence != 17 || page.Before.Kind != store.HistoryKindSnapshot || page.Before.ID != itemID {
		t.Fatalf("parsed cursor page=%+v cursor=%t err=%v", page, cursorRequest, queryErr)
	}

	for name, request := range map[string]*http.Request{
		"tampered":     httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history?cursor="+cursor+"x", nil),
		"cross-user":   httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history?cursor="+cursor, nil),
		"cross-device": httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history?cursor="+cursor, nil),
		"cursor-kind":  httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history?cursor="+cursor+"&kind=snapshot", nil),
	} {
		t.Run(name, func(t *testing.T) {
			actor, device := userID, deviceID
			if name == "cross-user" {
				actor = uuid.New()
			}
			if name == "cross-device" {
				device = uuid.New()
			}
			_, _, got := api.parseHistoryPageRequest(request, actor, device, sessionID)
			if got == nil || got.Code != "INVALID_HISTORY_CURSOR" {
				t.Fatalf("error=%v, want INVALID_HISTORY_CURSOR", got)
			}
		})
	}
}

func TestHistoryPageQueryValidationAndLegacyDetection(t *testing.T) {
	api := &API{TokenKey: []byte("0123456789abcdef0123456789abcdef")}
	userID, deviceID, sessionID := uuid.New(), uuid.New(), uuid.New()
	legacy := httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history?legacy=true", nil)
	if historyPaginationRequested(legacy) {
		t.Fatal("unknown legacy parameter unexpectedly enabled pagination")
	}
	page, cursorRequest, queryErr := api.parseHistoryPageRequest(legacy, userID, deviceID, sessionID)
	if queryErr != nil || cursorRequest || page.Kind != store.HistoryKindAll || page.Limit != 50 {
		t.Fatalf("legacy compatibility page=%+v cursor=%t err=%v", page, cursorRequest, queryErr)
	}

	tests := []struct {
		query string
		code  string
	}{
		{"?limit=0", "INVALID_HISTORY_LIMIT"},
		{"?limit=101", "INVALID_HISTORY_LIMIT"},
		{"?limit=1&limit=2", "INVALID_HISTORY_LIMIT"},
		{"?kind=other", "INVALID_HISTORY_KIND"},
		{"?kind=chunk&format_name=TERMLINK_TERMINAL_SNAPSHOT", "INVALID_HISTORY_FORMAT"},
		{"?kind=chunk&unknown=true", "INVALID_HISTORY_PAGE"},
		{"?legacy=true&limit=1", "INVALID_HISTORY_PAGE"},
		{"?as_of=broken", "INVALID_HISTORY_AS_OF"},
	}
	for _, test := range tests {
		request := httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history"+test.query, nil)
		_, _, err := api.parseHistoryPageRequest(request, userID, deviceID, sessionID)
		if err == nil || err.Code != test.code {
			t.Errorf("query %q error=%v, want %s", test.query, err, test.code)
		}
	}
}

func TestDefaultAndLegacyHistoryRequestsUseBoundedFirstPage(t *testing.T) {
	for _, query := range []string{"", "?legacy=true"} {
		t.Run(query, func(t *testing.T) {
			items := make([]store.HistoryItem, 51)
			for index := range items {
				items[index] = store.HistoryItem{
					Kind: string(store.HistoryKindChunk), ID: uuid.New(), StartSequence: uint64(index + 1),
					EndSequence: uint64(index + 1), ObjectKey: fmt.Sprintf("history-%02d", index),
					KeyID: "history-key", CiphertextSHA256: make([]byte, sha256.Size), SizeBytes: 1,
					CreatedAt: time.Unix(int64(index+1), 0).UTC(), HistoryRevision: int64(index + 1),
				}
			}
			pageStore := &recordingHistoryPageLister{page: store.HistoryPage{
				Items: items, AsOfRevision: 51,
			}}
			objectStore := &recordingHistoryObjectStore{}
			service := &API{
				TokenKey: []byte("bounded-default-history-page-key"), History: objectStore, historyPages: pageStore,
			}
			sessionID := uuid.New()
			request := httptest.NewRequest(http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history"+query, nil)
			request.SetPathValue("id", sessionID.String())
			response := httptest.NewRecorder()

			service.listHistory(response, request)

			if response.Code != http.StatusOK {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
			if len(pageStore.requests) != 1 {
				t.Fatalf("page requests=%d, want 1", len(pageStore.requests))
			}
			pageRequest := pageStore.requests[0]
			if pageRequest.Kind != store.HistoryKindAll || pageRequest.Limit != 50 || pageRequest.Before != nil || pageRequest.AsOfRevision != nil {
				t.Fatalf("default page request=%+v", pageRequest)
			}
			if len(objectStore.gets) != 50 {
				t.Fatalf("presigned URLs=%d, want bounded 50", len(objectStore.gets))
			}
			var body struct {
				Items          []map[string]any `json:"items"`
				PlaintextIndex bool             `json:"plaintext_index"`
				HasMore        bool             `json:"has_more"`
				AsOf           string           `json:"as_of"`
				NextCursor     string           `json:"next_cursor"`
			}
			if err := json.Unmarshal(response.Body.Bytes(), &body); err != nil {
				t.Fatal(err)
			}
			if len(body.Items) != 50 || body.PlaintextIndex || !body.HasMore || body.AsOf == "" || body.NextCursor == "" {
				t.Fatalf("bounded response items=%d plaintext=%t has_more=%t as_of=%q next=%q", len(body.Items), body.PlaintextIndex, body.HasMore, body.AsOf, body.NextCursor)
			}
			if _, present := body.Items[0]["snapshot_seq"]; !present {
				t.Fatalf("default compatibility item omitted legacy metadata: %v", body.Items[0])
			}
		})
	}
}

func TestHistoryResponseItemsExposeSnapshotMetadataAndOnlyPresignItems(t *testing.T) {
	objectStore := &recordingHistoryObjectStore{}
	api := &API{History: objectStore}
	digest := sha256.Sum256([]byte("snapshot"))
	item := store.HistoryItem{
		Kind: string(store.HistoryKindSnapshot), ID: uuid.New(), SnapshotSequence: 0,
		Rows: 35, Columns: 120, ObjectKey: "snapshot-object", KeyID: "key-0001",
		CiphertextSHA256: digest[:], SizeBytes: 512, FormatName: "BOUNDED_EVENT_REPLAY",
		FormatVersion: 1, CreatedAt: time.Unix(1, 0).UTC(),
	}
	response, err := api.historyResponseItems(context.Background(), []store.HistoryItem{item}, true)
	if err != nil {
		t.Fatal(err)
	}
	if len(response) != 1 || len(objectStore.gets) != 1 || objectStore.gets[0] != item.ObjectKey {
		t.Fatalf("items=%d presigned=%v", len(response), objectStore.gets)
	}
	got := response[0]
	if got["snapshot_seq"] != uint64(0) || got["rows"] != 35 || got["columns"] != 120 ||
		got["format_name"] != "BOUNDED_EVENT_REPLAY" || got["format_version"] != 1 {
		t.Fatalf("snapshot metadata=%v", got)
	}
	if _, exists := got["start_seq"]; exists {
		t.Fatalf("paged snapshot leaked chunk metadata: %v", got)
	}

	empty, err := (&API{}).historyResponseItems(context.Background(), nil, true)
	if err != nil || len(empty) != 0 {
		t.Fatalf("empty history response=%v err=%v", empty, err)
	}
}

func TestHistorySnapshotMetadataSupportsPortableSnapshotAndLegacyReplay(t *testing.T) {
	tests := []struct {
		name          string
		sequence      uint64
		sizeBytes     int64
		rows, columns int
		format        string
		version       int
		wantError     bool
	}{
		{name: "portable-minimum", sequence: 1, sizeBytes: 1, rows: 1, columns: 1, format: terminalSnapshotFormat, version: 1},
		{name: "portable-maximum", sequence: 99, sizeBytes: maxTerminalSnapshotCiphertextBytes, rows: 256, columns: 512, format: terminalSnapshotFormat, version: 1},
		{name: "legacy-sequence-zero", sequence: 0, sizeBytes: maxTerminalSnapshotCiphertextBytes, rows: 256, columns: 512, format: boundedEventReplayFormat, version: 1},
		{name: "legacy-too-large", sequence: 0, sizeBytes: maxTerminalSnapshotCiphertextBytes + 1, rows: 24, columns: 80, format: boundedEventReplayFormat, version: 1, wantError: true},
		{name: "portable-sequence-zero", sequence: 0, sizeBytes: 1, rows: 24, columns: 80, format: terminalSnapshotFormat, version: 1, wantError: true},
		{name: "portable-too-large", sequence: 1, sizeBytes: maxTerminalSnapshotCiphertextBytes + 1, rows: 24, columns: 80, format: terminalSnapshotFormat, version: 1, wantError: true},
		{name: "portable-too-many-rows", sequence: 1, sizeBytes: 1, rows: 257, columns: 80, format: terminalSnapshotFormat, version: 1, wantError: true},
		{name: "portable-too-many-columns", sequence: 1, sizeBytes: 1, rows: 24, columns: 513, format: terminalSnapshotFormat, version: 1, wantError: true},
		{name: "portable-zero-dimension", sequence: 1, sizeBytes: 1, rows: 0, columns: 80, format: terminalSnapshotFormat, version: 1, wantError: true},
		{name: "unknown-format", sequence: 1, sizeBytes: 1, rows: 24, columns: 80, format: "PRIVATE_ENGINE_DUMP", version: 1, wantError: true},
		{name: "unsupported-version", sequence: 1, sizeBytes: 1, rows: 24, columns: 80, format: terminalSnapshotFormat, version: 2, wantError: true},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			err := validateHistorySnapshotMetadata(test.sequence, test.sizeBytes, test.rows, test.columns, test.format, test.version)
			if (err != nil) != test.wantError {
				t.Fatalf("validation error=%v, wantError=%t", err, test.wantError)
			}
		})
	}
}

func TestCreateHistoryUploadRejectsInvalidPortableSnapshotMetadata(t *testing.T) {
	digest := sha256.Sum256([]byte("portable-snapshot-ciphertext"))
	tests := []struct {
		name          string
		sequence      uint64
		sizeBytes     int64
		rows, columns int
		format        string
	}{
		{name: "zero-sequence", sequence: 0, sizeBytes: 1, rows: 24, columns: 80, format: terminalSnapshotFormat},
		{name: "too-large", sequence: 1, sizeBytes: maxTerminalSnapshotCiphertextBytes + 1, rows: 24, columns: 80, format: terminalSnapshotFormat},
		{name: "too-tall", sequence: 1, sizeBytes: 1, rows: 257, columns: 80, format: terminalSnapshotFormat},
		{name: "too-wide", sequence: 1, sizeBytes: 1, rows: 24, columns: 513, format: terminalSnapshotFormat},
		{name: "unknown-format", sequence: 1, sizeBytes: 1, rows: 24, columns: 80, format: "PRIVATE_ENGINE_DUMP"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			body := fmt.Sprintf(`{"kind":"SNAPSHOT","key_id":"snapshot-key-0001","ciphertext_sha256":"%s","size_bytes":%d,"snapshot_seq":%d,"rows":%d,"columns":%d,"format_name":"%s","format_version":1}`,
				base64.RawURLEncoding.EncodeToString(digest[:]), test.sizeBytes, test.sequence, test.rows, test.columns, test.format)
			request := httptest.NewRequest(http.MethodPost, "/v1/sessions/"+uuid.New().String()+"/history/uploads", strings.NewReader(body))
			request.SetPathValue("id", uuid.New().String())
			response := httptest.NewRecorder()
			(&API{History: &recordingHistoryObjectStore{}}).createHistoryUpload(response, request)
			if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), `"code":"INVALID_HISTORY_SNAPSHOT"`) {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
		})
	}
}

func TestPortableSnapshotResponseIncludesRestorationMetadata(t *testing.T) {
	objectStore := &recordingHistoryObjectStore{}
	api := &API{History: objectStore}
	digest := sha256.Sum256([]byte("portable-snapshot"))
	item := store.HistoryItem{
		Kind: string(store.HistoryKindSnapshot), ID: uuid.New(), SnapshotSequence: 77,
		Rows: 64, Columns: 180, ObjectKey: "portable-snapshot-object", KeyID: "key-0002",
		CiphertextSHA256: digest[:], SizeBytes: 2048, FormatName: terminalSnapshotFormat,
		FormatVersion: 1, CreatedAt: time.Unix(2, 0).UTC(),
	}
	response, err := api.historyResponseItems(context.Background(), []store.HistoryItem{item}, true)
	if err != nil {
		t.Fatal(err)
	}
	got := response[0]
	if got["snapshot_seq"] != uint64(77) || got["rows"] != 64 || got["columns"] != 180 ||
		got["format_name"] != terminalSnapshotFormat || got["format_version"] != 1 {
		t.Fatalf("portable snapshot metadata=%v", got)
	}
}

type recordingHistoryObjectStore struct {
	gets []string
}

type recordingHistoryPageLister struct {
	requests []store.HistoryPageRequest
	page     store.HistoryPage
}

func (s *recordingHistoryPageLister) ListHistoryPage(_ context.Context, _, _, _ uuid.UUID, request store.HistoryPageRequest) (store.HistoryPage, error) {
	s.requests = append(s.requests, request)
	return s.page, nil
}

func (s *recordingHistoryObjectStore) PresignPut(context.Context, string, int64, []byte, time.Duration) (history.PresignedRequest, error) {
	return history.PresignedRequest{}, nil
}

func (s *recordingHistoryObjectStore) PresignGet(_ context.Context, key string, _ time.Duration) (history.PresignedRequest, error) {
	s.gets = append(s.gets, key)
	return history.PresignedRequest{URL: "https://history.invalid/" + key, Method: http.MethodGet}, nil
}

func (s *recordingHistoryObjectStore) VerifyCiphertext(context.Context, string, int64, []byte) error {
	return nil
}

func (s *recordingHistoryObjectStore) Delete(context.Context, string) error { return nil }
