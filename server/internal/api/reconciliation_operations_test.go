package api

import (
	"context"
	"crypto/ed25519"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/reconciliation"
	"github.com/termlink/termlink/server/internal/store"
)

type recordingReconciliationRepository struct {
	lists         []reconciliation.DiscrepancyListRequest
	transitions   []reconciliation.DiscrepancyTransitionCommand
	page          reconciliation.DiscrepancyPage
	item          reconciliation.OperationalDiscrepancy
	listErr       error
	transitionErr error
}

func (r *recordingReconciliationRepository) ListDiscrepancies(_ context.Context, request reconciliation.DiscrepancyListRequest) (reconciliation.DiscrepancyPage, error) {
	r.lists = append(r.lists, request)
	return r.page, r.listErr
}

func (r *recordingReconciliationRepository) TransitionDiscrepancy(_ context.Context, command reconciliation.DiscrepancyTransitionCommand) (reconciliation.OperationalDiscrepancy, error) {
	r.transitions = append(r.transitions, command)
	return r.item, r.transitionErr
}

type failingReconciliationOperations struct{ err error }

func (f failingReconciliationOperations) List(context.Context, reconciliation.DiscrepancyListRequest) (reconciliation.DiscrepancyPage, error) {
	return reconciliation.DiscrepancyPage{}, f.err
}

func (f failingReconciliationOperations) Transition(context.Context, reconciliation.DiscrepancyTransitionRequest) (reconciliation.OperationalDiscrepancy, error) {
	return reconciliation.OperationalDiscrepancy{}, f.err
}

type reconciliationIdentityVerifier struct{ identity auth.Identity }

func (v reconciliationIdentityVerifier) Verify(context.Context, string) (auth.Identity, error) {
	return v.identity, nil
}

type reconciliationTestEndpoint struct {
	handler    http.Handler
	privateKey ed25519.PrivateKey
	deviceID   uuid.UUID
	now        time.Time
}

func TestReconciliationAdminRoutesRequireAuthentication(t *testing.T) {
	service := &API{ReconciliationOperations: failingReconciliationOperations{}}
	handler := service.Handler()
	for _, request := range []*http.Request{
		httptest.NewRequest(http.MethodGet, "/v1/admin/billing/reconciliation-discrepancies", nil),
		httptest.NewRequest(http.MethodPost, "/v1/admin/billing/reconciliation-discrepancies/"+uuid.NewString()+"/transition", strings.NewReader(`{}`)),
	} {
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		if response.Code != http.StatusUnauthorized {
			t.Fatalf("%s %s status = %d, want 401", request.Method, request.URL.Path, response.Code)
		}
	}
}

func TestReconciliationListPassesBoundedFiltersAndReturnsOnlyOperationalDTO(t *testing.T) {
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	accountID, runID, afterID := uuid.New(), uuid.New(), uuid.New()
	item := validReconciliationOperationalDiscrepancy(now)
	repository := &recordingReconciliationRepository{page: reconciliation.DiscrepancyPage{
		Items: []reconciliation.OperationalDiscrepancy{item},
		Next:  &reconciliation.DiscrepancyCursor{LastSeenAt: item.LastSeenAt, ID: item.ID},
	}}
	operations := newReconciliationOperations(t, repository)
	endpoint := newReconciliationTestEndpoint(t, accountID, now, auth.Identity{}, operations)
	query := url.Values{
		"provider":           {" alipay "},
		"status":             {" acknowledged "},
		"run_id":             {runID.String()},
		"after_last_seen_at": {now.Add(-time.Hour).Format(time.RFC3339Nano)},
		"after_id":           {afterID.String()},
		"limit":              {"25"},
	}
	request := endpoint.request(t, http.MethodGet, "/v1/admin/billing/reconciliation-discrepancies?"+query.Encode(), "", false)
	response := httptest.NewRecorder()
	endpoint.handler.ServeHTTP(response, request)
	if response.Code != http.StatusOK || len(repository.lists) != 1 {
		t.Fatalf("status/list calls/body = %d/%d/%s", response.Code, len(repository.lists), response.Body.String())
	}
	got := repository.lists[0]
	if got.ActorID != accountID || got.Provider != "ALIPAY" || got.Status != reconciliation.DiscrepancyStatusAcknowledged ||
		got.RunID != runID || got.Limit != 25 || got.After == nil || got.After.ID != afterID ||
		!got.After.LastSeenAt.Equal(now.Add(-time.Hour)) {
		t.Fatalf("list filter was not normalized/bounded: %#v", got)
	}
	body := strings.ToLower(response.Body.String())
	for _, forbidden := range []string{"discrepancy_key", "external_reference", "provider_reference", "statement_row", "raw_provider"} {
		if strings.Contains(body, forbidden) {
			t.Fatalf("redacted response exposed %q: %s", forbidden, response.Body.String())
		}
	}
	if !strings.Contains(body, `"next_cursor"`) || !strings.Contains(body, item.ID.String()) {
		t.Fatalf("response omitted redacted item or cursor: %s", response.Body.String())
	}
}

func TestReconciliationListRejectsInvalidFiltersAndCursorBeforeCore(t *testing.T) {
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	accountID := uuid.New()
	tests := []struct {
		name  string
		query string
	}{
		{"partial cursor time", "after_last_seen_at=" + url.QueryEscape(now.Format(time.RFC3339Nano))},
		{"partial cursor id", "after_id=" + uuid.NewString()},
		{"over limit", "limit=201"},
		{"unknown provider", "provider=STRIPE"},
		{"unknown filter", "secret_reference=value"},
		{"duplicate filter", "limit=1&limit=2"},
		{"oversized query", "provider=" + strings.Repeat("A", maxReconciliationQueryBytes+1)},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			repository := &recordingReconciliationRepository{}
			endpoint := newReconciliationTestEndpoint(t, accountID, now, auth.Identity{}, newReconciliationOperations(t, repository))
			request := endpoint.request(t, http.MethodGet, "/v1/admin/billing/reconciliation-discrepancies?"+test.query, "", false)
			response := httptest.NewRecorder()
			endpoint.handler.ServeHTTP(response, request)
			if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), `"code":"INVALID_RECONCILIATION_OPERATION"`) {
				t.Fatalf("status/body = %d/%s, want stable 400", response.Code, response.Body.String())
			}
			if len(repository.lists) != 0 {
				t.Fatal("invalid filter reached reconciliation repository")
			}
		})
	}
}

func TestReconciliationTransitionRequiresMFAReasonVersionAndIdempotency(t *testing.T) {
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	accountID, discrepancyID := uuid.New(), uuid.New()
	validBody := `{"target_status":"ACKNOWLEDGED","expected_version":1,"reason_code":"MANUAL_REVIEW"}`
	tests := []struct {
		name       string
		identity   auth.Identity
		path, body string
		key        bool
		wantStatus int
		wantCode   string
	}{
		{"idempotency required", auth.Identity{MFA: true, AuthenticatedAt: now}, discrepancyID.String(), validBody, false, 400, "IDEMPOTENCY_KEY_REQUIRED"},
		{"MFA required", auth.Identity{AuthenticatedAt: now}, discrepancyID.String(), validBody, true, 403, "STEP_UP_REQUIRED"},
		{"recent MFA required", auth.Identity{MFA: true, AuthenticatedAt: now.Add(-commerceStepUpMaximumAge - time.Second)}, discrepancyID.String(), validBody, true, 403, "STEP_UP_REQUIRED"},
		{"future auth rejected", auth.Identity{MFA: true, AuthenticatedAt: now.Add(time.Second)}, discrepancyID.String(), validBody, true, 403, "STEP_UP_REQUIRED"},
		{"reason code validated", auth.Identity{MFA: true, AuthenticatedAt: now}, discrepancyID.String(), `{"target_status":"ACKNOWLEDGED","expected_version":1,"reason_code":"contains spaces"}`, true, 400, "INVALID_RECONCILIATION_OPERATION"},
		{"expected version required", auth.Identity{MFA: true, AuthenticatedAt: now}, discrepancyID.String(), `{"target_status":"ACKNOWLEDGED","expected_version":0,"reason_code":"MANUAL_REVIEW"}`, true, 400, "INVALID_RECONCILIATION_OPERATION"},
		{"target transition validated", auth.Identity{MFA: true, AuthenticatedAt: now}, discrepancyID.String(), `{"target_status":"OPEN","expected_version":1,"reason_code":"MANUAL_REVIEW"}`, true, 400, "INVALID_RECONCILIATION_OPERATION"},
		{"canonical ID required", auth.Identity{MFA: true, AuthenticatedAt: now}, "not-a-uuid", validBody, true, 400, "INVALID_RECONCILIATION_DISCREPANCY_ID"},
		{"unknown body field rejected", auth.Identity{MFA: true, AuthenticatedAt: now}, discrepancyID.String(), `{"target_status":"ACKNOWLEDGED","expected_version":1,"reason_code":"MANUAL_REVIEW","provider_reference":"secret"}`, true, 400, "INVALID_RECONCILIATION_OPERATION"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			repository := &recordingReconciliationRepository{}
			endpoint := newReconciliationTestEndpoint(t, accountID, now, test.identity, newReconciliationOperations(t, repository))
			target := "/v1/admin/billing/reconciliation-discrepancies/" + test.path + "/transition"
			request := endpoint.request(t, http.MethodPost, target, test.body, test.key)
			response := httptest.NewRecorder()
			endpoint.handler.ServeHTTP(response, request)
			if response.Code != test.wantStatus || !strings.Contains(response.Body.String(), `"code":"`+test.wantCode+`"`) {
				t.Fatalf("status/body = %d/%s, want %d/%s", response.Code, response.Body.String(), test.wantStatus, test.wantCode)
			}
			if len(repository.transitions) != 0 {
				t.Fatal("rejected transition reached reconciliation repository")
			}
		})
	}
}

func TestReconciliationTransitionMapsAllSupportedTargets(t *testing.T) {
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	accountID := uuid.New()
	for _, targetStatus := range []string{
		reconciliation.DiscrepancyStatusAcknowledged,
		reconciliation.DiscrepancyStatusResolved,
		reconciliation.DiscrepancyStatusIgnored,
	} {
		t.Run(targetStatus, func(t *testing.T) {
			item := validReconciliationOperationalDiscrepancy(now)
			item.Status = targetStatus
			if targetStatus == reconciliation.DiscrepancyStatusAcknowledged {
				item.AcknowledgedAt = &now
			} else {
				acknowledged := now.Add(-time.Minute)
				item.AcknowledgedAt, item.ResolvedAt = &acknowledged, &now
				item.ResolvedBy = &accountID
				reason := "MANUAL_REVIEW"
				item.ResolutionCode = &reason
			}
			repository := &recordingReconciliationRepository{item: item}
			endpoint := newReconciliationTestEndpoint(t, accountID, now,
				auth.Identity{MFA: true, AuthenticatedAt: now}, newReconciliationOperations(t, repository))
			body := `{"target_status":"` + strings.ToLower(targetStatus) + `","expected_version":1,"reason_code":"manual_review"}`
			request := endpoint.request(t, http.MethodPost,
				"/v1/admin/billing/reconciliation-discrepancies/"+item.ID.String()+"/transition", body, true)
			response := httptest.NewRecorder()
			endpoint.handler.ServeHTTP(response, request)
			if response.Code != http.StatusOK || len(repository.transitions) != 1 {
				t.Fatalf("status/calls/body = %d/%d/%s", response.Code, len(repository.transitions), response.Body.String())
			}
			command := repository.transitions[0]
			if command.ActorID != accountID || command.DiscrepancyID != item.ID || command.TargetStatus != targetStatus ||
				command.ReasonCode != "MANUAL_REVIEW" || command.ExpectedVersion != 1 || command.IdempotencyStorageKey == "reconciliation-idempotency-key" {
				t.Fatalf("transition command was not canonicalized/HMACed: %#v", command)
			}
			if strings.Contains(response.Body.String(), "reconciliation-idempotency-key") {
				t.Fatal("response exposed raw idempotency key")
			}
		})
	}
}

func TestReconciliationErrorsAreStableAndNonLeaking(t *testing.T) {
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	accountID, discrepancyID := uuid.New(), uuid.New()
	tests := []struct {
		name   string
		err    error
		status int
		code   string
	}{
		{"role denied", store.ErrDenied, 403, "RECONCILIATION_ACCESS_DENIED"},
		{"not found", store.ErrNotFound, 404, "RECONCILIATION_DISCREPANCY_NOT_FOUND"},
		{"transition conflict", errors.Join(store.ErrConflict, reconciliation.ErrInvalidTransition), 409, "RECONCILIATION_TRANSITION_CONFLICT"},
		{"idempotency conflict", reconciliation.ErrIdempotencyConflict, 409, "RECONCILIATION_IDEMPOTENCY_CONFLICT"},
		{"in progress", reconciliation.ErrOperationInProgress, 409, "RECONCILIATION_OPERATION_IN_PROGRESS"},
		{"unavailable", reconciliation.ErrOperationsUnavailable, 503, "RECONCILIATION_OPERATIONS_UNAVAILABLE"},
		{"backend", errors.New("provider reference secret-trade-123 database dsn"), 503, "RECONCILIATION_OPERATION_FAILED"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			endpoint := newReconciliationTestEndpoint(t, accountID, now,
				auth.Identity{MFA: true, AuthenticatedAt: now}, failingReconciliationOperations{err: test.err})
			body := `{"target_status":"ACKNOWLEDGED","expected_version":1,"reason_code":"MANUAL_REVIEW"}`
			request := endpoint.request(t, http.MethodPost,
				"/v1/admin/billing/reconciliation-discrepancies/"+discrepancyID.String()+"/transition", body, true)
			response := httptest.NewRecorder()
			endpoint.handler.ServeHTTP(response, request)
			if response.Code != test.status || !strings.Contains(response.Body.String(), `"code":"`+test.code+`"`) {
				t.Fatalf("status/body = %d/%s, want %d/%s", response.Code, response.Body.String(), test.status, test.code)
			}
			lower := strings.ToLower(response.Body.String())
			for _, forbidden := range []string{"secret-trade", "provider reference", "database dsn"} {
				if strings.Contains(lower, forbidden) {
					t.Fatalf("error response leaked %q: %s", forbidden, response.Body.String())
				}
			}
		})
	}
}

func newReconciliationOperations(t *testing.T, repository reconciliation.OperationsRepository) *reconciliation.Operations {
	t.Helper()
	operations, err := reconciliation.NewOperations(repository, reconciliation.OperationsOptions{
		DigestKey: []byte("reconciliation-api-test-key-32-bytes-minimum"),
	})
	if err != nil {
		t.Fatal(err)
	}
	return operations
}

func newReconciliationTestEndpoint(t *testing.T, accountID uuid.UUID, now time.Time, identity auth.Identity, operations ReconciliationOperations) reconciliationTestEndpoint {
	t.Helper()
	deviceID := uuid.New()
	authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
	authentication.Tokens = reconciliationIdentityVerifier{identity: identity}
	service := &API{Auth: authentication, Clock: func() time.Time { return now }, ReconciliationOperations: operations}
	return reconciliationTestEndpoint{handler: service.Handler(), privateKey: privateKey, deviceID: deviceID, now: now}
}

func (e reconciliationTestEndpoint) request(t *testing.T, method, target, body string, idempotent bool) *http.Request {
	t.Helper()
	request := signedFileOperationRequest(t, e.privateKey, e.deviceID, e.now, method, target, []byte(body))
	if idempotent {
		request.Header.Set("Idempotency-Key", "reconciliation-idempotency-key")
	}
	return request
}

func validReconciliationOperationalDiscrepancy(now time.Time) reconciliation.OperationalDiscrepancy {
	return reconciliation.OperationalDiscrepancy{
		ID: uuid.New(), RunID: uuid.New(), Provider: "ALIPAY", Category: "OTHER",
		SummaryCode: "MANUAL_DIFFERENCE", Status: reconciliation.DiscrepancyStatusOpen, Version: 1,
		FirstSeenAt: now.Add(-2 * time.Hour), LastSeenAt: now.Add(-time.Hour),
		CreatedAt: now.Add(-2 * time.Hour), UpdatedAt: now.Add(-time.Hour),
	}
}

var _ reconciliation.OperationsRepository = (*recordingReconciliationRepository)(nil)
var _ ReconciliationOperations = failingReconciliationOperations{}
