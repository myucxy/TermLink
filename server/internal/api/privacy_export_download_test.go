package api

import (
	"context"
	"crypto/ed25519"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/privacyexport"
)

type fakePrivacyExportDownloader struct {
	body      []byte
	err       error
	accountID uuid.UUID
	exportID  uuid.UUID
	calls     int
}

func (f *fakePrivacyExportDownloader) Download(_ context.Context, accountID, exportID uuid.UUID) ([]byte, error) {
	f.calls++
	f.accountID = accountID
	f.exportID = exportID
	return append([]byte(nil), f.body...), f.err
}

func TestPrivacyExportDownloadRouteReturnsDecryptedJSONForAuthenticatedAccount(t *testing.T) {
	now := time.Date(2026, time.August, 27, 10, 0, 0, 0, time.UTC)
	accountID, deviceID, exportID := uuid.New(), uuid.New(), uuid.New()
	body := []byte(`{"account":{"status":"ACTIVE"},"devices":[]}`)
	downloader := &fakePrivacyExportDownloader{body: body}
	handler, privateKey := privacyExportTestHandler(t, accountID, deviceID, now, downloader)

	request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodGet,
		"/v1/account/data-exports/"+exportID.String()+"/download", nil)
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)

	if response.Code != http.StatusOK {
		t.Fatalf("status/body = %d/%s, want 200", response.Code, response.Body.String())
	}
	if downloader.calls != 1 || downloader.accountID != accountID || downloader.exportID != exportID {
		t.Fatalf("download calls/account/export = %d/%s/%s, want 1/%s/%s",
			downloader.calls, downloader.accountID, downloader.exportID, accountID, exportID)
	}
	for name, want := range map[string]string{
		"Content-Type":           "application/json",
		"Content-Disposition":    `attachment; filename="termlink-data-export.json"`,
		"Cache-Control":          "no-store",
		"X-Content-Type-Options": "nosniff",
	} {
		if got := response.Header().Get(name); got != want {
			t.Fatalf("%s = %q, want %q", name, got, want)
		}
	}
	if response.Body.String() != string(body) {
		t.Fatalf("body = %q, want decrypted JSON %q", response.Body.String(), body)
	}
	for _, forbidden := range []string{"object_key", "ciphertext", "encrypted"} {
		if strings.Contains(strings.ToLower(response.Body.String()), forbidden) {
			t.Fatalf("download exposed forbidden storage field %q", forbidden)
		}
	}
}

func TestPrivacyExportDownloadUnavailableCasesShareOneNonOracular404(t *testing.T) {
	now := time.Date(2026, time.August, 27, 10, 0, 0, 0, time.UTC)
	accountID, deviceID := uuid.New(), uuid.New()
	trace := uuid.NewString()
	targets := []string{
		"/v1/account/data-exports/" + uuid.NewString() + "/download", // absent
		"/v1/account/data-exports/" + uuid.NewString() + "/download", // another account
		"/v1/account/data-exports/not-a-uuid/download",               // malformed identifier
	}
	responses := make([]*httptest.ResponseRecorder, 0, len(targets))
	for _, target := range targets {
		downloader := &fakePrivacyExportDownloader{err: privacyexport.ErrExportUnavailable}
		handler, privateKey := privacyExportTestHandler(t, accountID, deviceID, now, downloader)
		request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodGet, target, nil)
		request.Header.Set("X-Trace-ID", trace)
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		responses = append(responses, response)
	}

	for i, response := range responses {
		if response.Code != http.StatusNotFound || !strings.Contains(response.Body.String(), `"code":"DATA_EXPORT_NOT_FOUND"`) {
			t.Fatalf("response %d status/body = %d/%s, want stable 404", i, response.Code, response.Body.String())
		}
		if response.Body.String() != responses[0].Body.String() {
			t.Fatalf("response %d differs from non-oracular baseline: %s / %s", i, response.Body.String(), responses[0].Body.String())
		}
	}
}

func TestPrivacyExportDownloadFailuresAreStableAndDoNotLeakBackendDetails(t *testing.T) {
	now := time.Date(2026, time.August, 27, 10, 0, 0, 0, time.UTC)
	accountID, deviceID, exportID := uuid.New(), uuid.New(), uuid.New()
	trace := uuid.NewString()
	tests := []struct {
		name string
		body []byte
		err  error
	}{
		{name: "integrity", err: privacyexport.ErrExportIntegrity},
		{name: "backend", err: errors.New("s3 object key secret/export ciphertext")},
		{name: "invalid downloader JSON", body: []byte("not-json")},
	}
	var baseline string
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			downloader := &fakePrivacyExportDownloader{body: test.body, err: test.err}
			handler, privateKey := privacyExportTestHandler(t, accountID, deviceID, now, downloader)
			target := "/v1/account/data-exports/" + exportID.String() + "/download"
			request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodGet, target, nil)
			request.Header.Set("X-Trace-ID", trace)
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != http.StatusServiceUnavailable || !strings.Contains(response.Body.String(), `"code":"DATA_EXPORT_UNAVAILABLE"`) {
				t.Fatalf("status/body = %d/%s, want stable 503", response.Code, response.Body.String())
			}
			if strings.Contains(response.Body.String(), "s3") || strings.Contains(response.Body.String(), "object key") || strings.Contains(response.Body.String(), "ciphertext") {
				t.Fatalf("response leaked backend details: %s", response.Body.String())
			}
			if baseline == "" {
				baseline = response.Body.String()
			} else if response.Body.String() != baseline {
				t.Fatalf("failure response differs from baseline: %s / %s", response.Body.String(), baseline)
			}
		})
	}
}

func TestPrivacyExportDownloadWithoutDownloaderReturns503(t *testing.T) {
	now := time.Date(2026, time.August, 27, 10, 0, 0, 0, time.UTC)
	accountID, deviceID, exportID := uuid.New(), uuid.New(), uuid.New()
	handler, privateKey := privacyExportTestHandler(t, accountID, deviceID, now, nil)
	target := "/v1/account/data-exports/" + exportID.String() + "/download"
	request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodGet, target, nil)
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusServiceUnavailable || !strings.Contains(response.Body.String(), `"code":"DATA_EXPORT_UNAVAILABLE"`) {
		t.Fatalf("status/body = %d/%s, want stable 503", response.Code, response.Body.String())
	}
}

func TestPrivacyExportRequestWithoutDownloaderDoesNotCreateTask(t *testing.T) {
	now := time.Date(2026, time.August, 27, 10, 0, 0, 0, time.UTC)
	accountID, deviceID := uuid.New(), uuid.New()
	repository := &fakeUserPortalActionStore{now: now}
	service := &API{Clock: func() time.Time { return now }, commerceSecurity: &memoryCommerceSecurityStore{}}
	actions := userPortalActionHandlers{api: service, repository: repository}
	endpoint := userPortalAuthenticatedEndpoint(t, accountID, deviceID, now,
		"POST /v1/account/data-exports", auth.Identity{MFA: true, AuthenticatedAt: now}, actions.requestDataExport)
	request := endpoint.request(t, deviceID, now, http.MethodPost, "/v1/account/data-exports", `{}`, true, true)
	response := httptest.NewRecorder()
	endpoint.handler.ServeHTTP(response, request)

	if response.Code != http.StatusServiceUnavailable || !strings.Contains(response.Body.String(), `"code":"DATA_EXPORT_UNAVAILABLE"`) {
		t.Fatalf("status/body = %d/%s, want stable 503", response.Code, response.Body.String())
	}
	if repository.called != "" {
		t.Fatalf("unavailable export service created a durable task through %q", repository.called)
	}
}

func privacyExportTestHandler(t *testing.T, accountID, deviceID uuid.UUID, now time.Time, downloader PrivacyExportDownloader) (http.Handler, ed25519.PrivateKey) {
	t.Helper()
	authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
	service := &API{Auth: authentication, DataExportDownloader: downloader, Clock: func() time.Time { return now }}
	return service.Handler(), privateKey
}

var _ PrivacyExportDownloader = (*fakePrivacyExportDownloader)(nil)
