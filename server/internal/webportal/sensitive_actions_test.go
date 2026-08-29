package webportal

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type testSensitiveCompletion struct {
	accountID   uuid.UUID
	key         string
	requestHash [32]byte
	status      int
	body        json.RawMessage
}

type testSensitiveActionRepository struct {
	completions []testSensitiveCompletion
	completeErr error
}

func (*testSensitiveActionRepository) ClaimIdempotency(_ context.Context, _ uuid.UUID, _ string, requestHash [32]byte) (store.IdempotencyResult, error) {
	return store.IdempotencyResult{Claimed: true, RequestHash: requestHash}, nil
}

func (r *testSensitiveActionRepository) CompleteIdempotency(_ context.Context, accountID uuid.UUID, key string, requestHash [32]byte, status int, body json.RawMessage) error {
	r.completions = append(r.completions, testSensitiveCompletion{
		accountID: accountID, key: key, requestHash: requestHash, status: status, body: body,
	})
	return r.completeErr
}

func (*testSensitiveActionRepository) RecordSecurityEvent(context.Context, store.SecurityEvent) error {
	return nil
}

func TestSensitiveActionCompletionClassifiesTerminalAndRetryableStatuses(t *testing.T) {
	tests := []struct {
		name       string
		response   int
		completion int
	}{
		{name: "success", response: http.StatusOK, completion: http.StatusOK},
		{name: "redirect", response: http.StatusSeeOther, completion: http.StatusSeeOther},
		{name: "client failure", response: http.StatusConflict, completion: http.StatusInternalServerError},
		{name: "server failure", response: http.StatusServiceUnavailable, completion: http.StatusInternalServerError},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			repository := &testSensitiveActionRepository{}
			accountID := uuid.MustParse("77777777-7777-4777-8777-777777777777")
			requestHash := sensitiveRequestHash("test.action", url.Values{"value": {test.name}})
			handler := (&Portal{}).sensitiveAction("test.action", func(w http.ResponseWriter, r *http.Request) {
				slot := r.Context().Value(sensitiveActionContextKey{}).(*sensitiveActionSlot)
				slot.claim = &sensitiveActionClaim{
					repository: repository, accountID: accountID, key: "same-key-0001", requestHash: requestHash,
				}
				w.WriteHeader(test.response)
			})
			response := httptest.NewRecorder()
			handler(response, httptest.NewRequest(http.MethodPost, "https://termlink.example/test", nil))

			if response.Code != test.response {
				t.Fatalf("response status=%d, want %d", response.Code, test.response)
			}
			if len(repository.completions) != 1 {
				t.Fatalf("completion calls=%+v", repository.completions)
			}
			completion := repository.completions[0]
			if completion.accountID != accountID || completion.key != "same-key-0001" ||
				completion.requestHash != requestHash || completion.status != test.completion || completion.body != nil {
				t.Fatalf("completion=%+v", completion)
			}
		})
	}
}

func TestSensitiveActionDoesNotCompleteBeforeClaim(t *testing.T) {
	repository := &testSensitiveActionRepository{}
	handler := (&Portal{}).sensitiveAction("test.action", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusForbidden)
	})
	response := httptest.NewRecorder()
	handler(response, httptest.NewRequest(http.MethodPost, "https://termlink.example/test", nil))

	if response.Code != http.StatusForbidden || len(repository.completions) != 0 {
		t.Fatalf("pre-claim response=%d completions=%+v", response.Code, repository.completions)
	}
}

func TestSensitiveActionCompletionFailureDoesNotOverwriteCommittedResponse(t *testing.T) {
	repository := &testSensitiveActionRepository{completeErr: errors.New("postgresql://secret@db.internal")}
	hash := sensitiveRequestHash("test.action", url.Values{"value": {"original"}})
	handler := (&Portal{}).sensitiveAction("test.action", func(w http.ResponseWriter, r *http.Request) {
		slot := r.Context().Value(sensitiveActionContextKey{}).(*sensitiveActionSlot)
		slot.claim = &sensitiveActionClaim{repository: repository, accountID: uuid.New(), key: "failure-key-0001", requestHash: hash}
		w.WriteHeader(http.StatusTeapot)
		_, _ = w.Write([]byte("original response"))
	})
	response := httptest.NewRecorder()
	handler(response, httptest.NewRequest(http.MethodPost, "https://termlink.example/test", nil))

	if response.Code != http.StatusTeapot || response.Body.String() != "original response" {
		t.Fatalf("completion failure overwrote response: status=%d body=%q", response.Code, response.Body.String())
	}
	if len(repository.completions) != 1 || repository.completions[0].status != http.StatusInternalServerError {
		t.Fatalf("completion failure calls=%+v", repository.completions)
	}
}

func TestSensitiveRequestHashSeparatesPayloadsAndCompletionPreservesHash(t *testing.T) {
	first := sensitiveRequestHash("test.action", url.Values{
		"csrf": {"csrf-one"}, "idempotency_key": {"shared-key-0001"}, "value": {"first"},
	})
	firstWithRotatedCSRF := sensitiveRequestHash("test.action", url.Values{
		"csrf": {"csrf-two"}, "idempotency_key": {"shared-key-0001"}, "value": {"first"},
	})
	second := sensitiveRequestHash("test.action", url.Values{
		"csrf": {"csrf-one"}, "idempotency_key": {"shared-key-0001"}, "value": {"second"},
	})
	if first != firstWithRotatedCSRF {
		t.Fatal("CSRF rotation changed the idempotency request hash")
	}
	if first == second {
		t.Fatal("different payloads shared an idempotency request hash")
	}

	repository := &testSensitiveActionRepository{}
	handler := (&Portal{}).sensitiveAction("test.action", func(w http.ResponseWriter, r *http.Request) {
		slot := r.Context().Value(sensitiveActionContextKey{}).(*sensitiveActionSlot)
		slot.claim = &sensitiveActionClaim{repository: repository, accountID: uuid.New(), key: "shared-key-0001", requestHash: first}
		w.WriteHeader(http.StatusOK)
	})
	handler(httptest.NewRecorder(), httptest.NewRequest(http.MethodPost, "https://termlink.example/test", nil))
	if len(repository.completions) != 1 || repository.completions[0].requestHash != first {
		t.Fatalf("completion changed request hash: %+v", repository.completions)
	}
}

func TestSensitiveActionReasonDefaultsToStableRouteAuditValue(t *testing.T) {
	values := url.Values{}
	if got := ensureSensitiveActionReason(values, "admin.account.status.update"); got != "TermLink Web auto audit: admin.account.status.update" || values.Get("reason") != got {
		t.Fatalf("automatic reason=%q values=%v", got, values)
	}
	values.Set("reason", "  人工复核说明  ")
	if got := ensureSensitiveActionReason(values, "ignored"); got != "人工复核说明" || values.Get("reason") != "  人工复核说明  " {
		t.Fatalf("explicit reason=%q values=%v", got, values)
	}
	values.Set("reason", "系统自动记录")
	if got := ensureSensitiveActionReason(values, "admin.account.status.update"); got != "TermLink Web auto audit: admin.account.status.update" || values.Get("reason") != got {
		t.Fatalf("generic automatic reason=%q values=%v", got, values)
	}
}
