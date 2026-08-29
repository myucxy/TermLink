package api

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/commerce"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

type commerceIdentityVerifier struct{ identity auth.Identity }

func (v commerceIdentityVerifier) Verify(context.Context, string) (auth.Identity, error) {
	return v.identity, nil
}

type memoryCommerceSecurityStore struct {
	admin      bool
	roleChecks int
	audits     []commerceAuditRecord
	events     []store.SecurityEvent
}

type commerceAuditRecord struct {
	action, targetType, result, traceID, metadata string
	targetID                                      uuid.UUID
}

func (s *memoryCommerceSecurityStore) IsAdmin(context.Context, uuid.UUID) (bool, error) {
	s.roleChecks++
	return s.admin, nil
}

func (s *memoryCommerceSecurityStore) Audit(_ context.Context, _, _ uuid.UUID, action, targetType string, targetID uuid.UUID, result, traceID, metadata string) error {
	s.audits = append(s.audits, commerceAuditRecord{action: action, targetType: targetType, targetID: targetID, result: result, traceID: traceID, metadata: metadata})
	return nil
}

func (s *memoryCommerceSecurityStore) RecordSecurityEvent(_ context.Context, event store.SecurityEvent) error {
	s.events = append(s.events, event)
	return nil
}

func TestCommerceRoutesKeepWebhooksPublicAndAccountRoutesAuthenticated(t *testing.T) {
	service := &API{}
	handler := service.Handler()

	webhook := httptest.NewRequest(http.MethodPost, "/v1/billing/webhooks/alipay", strings.NewReader("notification"))
	webhookResponse := httptest.NewRecorder()
	handler.ServeHTTP(webhookResponse, webhook)
	if webhookResponse.Code != http.StatusOK || webhookResponse.Body.String() != "failure" {
		t.Fatalf("public webhook status/body = %d/%q", webhookResponse.Code, webhookResponse.Body.String())
	}
	if _, err := uuid.Parse(webhookResponse.Header().Get("X-Trace-ID")); err != nil {
		t.Fatalf("public webhook did not pass trace middleware: %q", webhookResponse.Header().Get("X-Trace-ID"))
	}

	privateRequest := httptest.NewRequest(http.MethodGet, "/v1/billing/orders", nil)
	privateResponse := httptest.NewRecorder()
	handler.ServeHTTP(privateResponse, privateRequest)
	if privateResponse.Code != http.StatusUnauthorized {
		t.Fatalf("private commerce route status = %d, want 401", privateResponse.Code)
	}

	adminRequest := httptest.NewRequest(http.MethodGet, "/v1/admin/payment-channels", nil)
	adminResponse := httptest.NewRecorder()
	handler.ServeHTTP(adminResponse, adminRequest)
	if adminResponse.Code != http.StatusUnauthorized {
		t.Fatalf("admin commerce route status = %d, want 401", adminResponse.Code)
	}

	for _, target := range []string{
		"/v1/account/redemptions",
		"/v1/billing/redemptions/preview",
		"/v1/billing/orders/" + uuid.NewString() + "/query",
		"/v1/admin/invitation-redemptions",
		"/v1/admin/redemption-claims",
		"/v1/admin/billing/orders/" + uuid.NewString() + "/retry-fulfillment",
		"/v1/admin/billing/refunds/" + uuid.NewString() + "/confirm",
	} {
		method := http.MethodGet
		if strings.Contains(target, "/preview") || strings.Contains(target, "/query") || strings.Contains(target, "/retry-fulfillment") {
			method = http.MethodPost
		}
		request := httptest.NewRequest(method, target, strings.NewReader(`{}`))
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		if response.Code != http.StatusUnauthorized {
			t.Fatalf("authenticated route %s status = %d, want 401", target, response.Code)
		}
	}
}

func TestCommerceRateLimitIsBoundedSeparatedAndSecretIndependent(t *testing.T) {
	redisClient := redis.NewClient(&redis.Options{Addr: "unused.invalid:6379"})
	hook := &memoryCommerceRateHook{counts: make(map[string]int64)}
	redisClient.AddHook(hook)
	t.Cleanup(func() { _ = redisClient.Close() })
	service := &API{Valkey: &kv.Valkey{Client: redisClient}}

	first := httptest.NewRequest(http.MethodPost, "/v1/auth/invitations/validate", strings.NewReader(`{"invitation_code":"TLI-SECRET-A","email":"first@example.test"}`))
	first.RemoteAddr = "203.0.113.44:43120"
	second := httptest.NewRequest(http.MethodPost, "/v1/auth/invitations/validate", strings.NewReader(`{"invitation_code":"TLI-SECRET-B","email":"second@example.test"}`))
	second.RemoteAddr = first.RemoteAddr
	firstScope, err := commerceRateLimitScope(first, commerceRateInvitationValidate)
	if err != nil {
		t.Fatal(err)
	}
	secondScope, err := commerceRateLimitScope(second, commerceRateInvitationValidate)
	if err != nil {
		t.Fatal(err)
	}
	if firstScope != secondScope {
		t.Fatal("rate scope depended on submitted invitation code or email")
	}
	if strings.Contains(firstScope, "SECRET") || strings.Contains(firstScope, "example.test") || len(firstScope) > 128 {
		t.Fatalf("rate scope leaked sensitive or unbounded input: %q", firstScope)
	}
	separateDomain, err := commerceRateLimitScope(first, commerceRateRedemptionClaim)
	if err != nil {
		t.Fatal(err)
	}
	if separateDomain == firstScope {
		t.Fatal("invitation and redemption limits shared a domain")
	}

	for attempt := 0; attempt < 12; attempt++ {
		allowed, err := service.consumeCommerceRateLimit(first, commerceRateInvitationValidate)
		if err != nil || !allowed {
			t.Fatalf("attempt %d allowed=%t err=%v", attempt, allowed, err)
		}
	}
	allowed, err := service.consumeCommerceRateLimit(first, commerceRateInvitationValidate)
	if err != nil || allowed {
		t.Fatalf("over-limit request allowed=%t err=%v", allowed, err)
	}
	allowed, err = service.consumeCommerceRateLimit(first, commerceRateRedemptionClaim)
	if err != nil || !allowed {
		t.Fatalf("separate domain did not retain capacity: allowed=%t err=%v", allowed, err)
	}
}

type memoryCommerceRateHook struct {
	mu     sync.Mutex
	counts map[string]int64
}

func (h *memoryCommerceRateHook) DialHook(next redis.DialHook) redis.DialHook { return next }

func (h *memoryCommerceRateHook) ProcessHook(next redis.ProcessHook) redis.ProcessHook {
	return func(ctx context.Context, command redis.Cmder) error {
		if command.Name() != "evalsha" && command.Name() != "eval" {
			return next(ctx, command)
		}
		var key string
		var limit int64
		arguments := command.Args()
		for index, argument := range arguments {
			candidate := fmt.Sprint(argument)
			if strings.HasPrefix(candidate, "rate:commerce:") && index+1 < len(arguments) {
				key = candidate
				limit, _ = strconv.ParseInt(fmt.Sprint(arguments[index+1]), 10, 64)
				break
			}
		}
		result, ok := command.(*redis.Cmd)
		if !ok || key == "" || limit < 1 {
			return next(ctx, command)
		}
		h.mu.Lock()
		allowed := h.counts[key] < limit
		if allowed {
			h.counts[key]++
		}
		h.mu.Unlock()
		if allowed {
			result.SetVal(int64(1))
		} else {
			result.SetVal(int64(0))
		}
		return nil
	}
}

func (h *memoryCommerceRateHook) ProcessPipelineHook(next redis.ProcessPipelineHook) redis.ProcessPipelineHook {
	return next
}

func TestCommerceWebhookFailureResponsesAreProviderExact(t *testing.T) {
	tests := []struct {
		provider    string
		status      int
		contentType string
		body        string
	}{
		{"ALIPAY", http.StatusOK, "text/plain; charset=utf-8", "failure"},
		{"WECHAT_PAY", http.StatusInternalServerError, "application/json; charset=utf-8", `{"code":"FAIL","message":"processing failed"}`},
	}
	for _, test := range tests {
		t.Run(test.provider, func(t *testing.T) {
			response := httptest.NewRecorder()
			writeCommerceWebhookAcknowledgement(response, commerceWebhookFailure(test.provider))
			if response.Code != test.status || response.Header().Get("Content-Type") != test.contentType || response.Body.String() != test.body {
				t.Fatalf("ack = status %d, type %q, body %q", response.Code, response.Header().Get("Content-Type"), response.Body.String())
			}
		})
	}
}

func TestCommerceWebhookFailureClassificationIsStableAndLowCardinality(t *testing.T) {
	tests := []struct {
		err  error
		want string
	}{
		{payment.ErrInvalidSignature, "INVALID_SIGNATURE"},
		{payment.ErrReplay, "REPLAY_WINDOW"},
		{payment.ErrPayloadTooLarge, "PAYLOAD_TOO_LARGE"},
		{payment.ErrMalformedPayload, "MALFORMED_PAYLOAD"},
		{commerce.ErrPaymentResultMismatch, "PAYMENT_MISMATCH"},
		{commerce.ErrPaymentStateNotProgressable, "STATE_REJECTED"},
		{commerce.ErrPaymentChannelUnavailable, "CHANNEL_UNAVAILABLE"},
		{fmt.Errorf("external-trade-id-should-not-be-a-label: %w", store.ErrConflict), "PROCESSING_FAILED"},
	}
	for _, test := range tests {
		classification := commerceWebhookFailureClass(test.err)
		if classification != test.want {
			t.Fatalf("classification for %v = %q, want %q", test.err, classification, test.want)
		}
		if strings.Contains(classification, "external-trade") || len(classification) > 32 {
			t.Fatalf("unbounded webhook classification: %q", classification)
		}
	}
}

func TestCommerceStepUpHandlerRequiresDatabaseAdminAndRecentMFA(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID := uuid.New(), uuid.New()
	tests := []struct {
		name       string
		admin      bool
		identity   auth.Identity
		reason     string
		wantStatus int
		wantCode   string
		wantEvents int
		wantAction string
	}{
		{
			name: "database role is required before step-up", admin: false,
			identity:   auth.Identity{MFA: true, AuthenticatedAt: now},
			wantStatus: http.StatusForbidden, wantCode: "ADMIN_REQUIRED",
		},
		{
			name: "mfa claim is required", admin: true,
			identity:   auth.Identity{MFA: false, AuthenticatedAt: now, ACR: "sensitive-provider-acr"},
			wantStatus: http.StatusForbidden, wantCode: "STEP_UP_REQUIRED", wantEvents: 1, wantAction: "commerce.admin.step_up.denied",
		},
		{
			name: "auth time is required", admin: true,
			identity:   auth.Identity{MFA: true},
			wantStatus: http.StatusForbidden, wantCode: "STEP_UP_REQUIRED", wantEvents: 1, wantAction: "commerce.admin.step_up.denied",
		},
		{
			name: "stale mfa is rejected", admin: true,
			identity:   auth.Identity{MFA: true, AuthenticatedAt: now.Add(-commerceStepUpMaximumAge - time.Second)},
			wantStatus: http.StatusForbidden, wantCode: "STEP_UP_REQUIRED", wantEvents: 1, wantAction: "commerce.admin.step_up.denied",
		},
		{
			name: "future auth time is rejected", admin: true,
			identity:   auth.Identity{MFA: true, AuthenticatedAt: now.Add(time.Second)},
			wantStatus: http.StatusForbidden, wantCode: "STEP_UP_REQUIRED", wantEvents: 1, wantAction: "commerce.admin.step_up.denied",
		},
		{
			name: "operation reason is required after mfa", admin: true,
			identity:   auth.Identity{MFA: true, AuthenticatedAt: now},
			wantStatus: http.StatusBadRequest, wantCode: "OPERATION_REASON_REQUIRED", wantEvents: 1, wantAction: "commerce.admin.operation_reason.denied",
		},
		{
			name: "short operation reason is rejected", admin: true,
			identity: auth.Identity{MFA: true, AuthenticatedAt: now}, reason: "ab",
			wantStatus: http.StatusBadRequest, wantCode: "OPERATION_REASON_REQUIRED", wantEvents: 1, wantAction: "commerce.admin.operation_reason.denied",
		},
		{
			name: "oversized unicode operation reason is rejected", admin: true,
			identity: auth.Identity{MFA: true, AuthenticatedAt: now}, reason: strings.Repeat("界", 501),
			wantStatus: http.StatusBadRequest, wantCode: "OPERATION_REASON_REQUIRED", wantEvents: 1, wantAction: "commerce.admin.operation_reason.denied",
		},
		{
			name: "recent mfa is accepted", admin: true,
			identity: auth.Identity{MFA: true, AuthenticatedAt: now.Add(-commerceStepUpMaximumAge)}, reason: "Rotate payment credentials",
			wantStatus: http.StatusNoContent,
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			repository := &memoryCommerceSecurityStore{admin: test.admin}
			service := &API{Clock: func() time.Time { return now }, commerceSecurity: repository}
			authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
			authentication.Tokens = commerceIdentityVerifier{identity: test.identity}
			handler := traceMiddleware(authentication.Authenticate(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if _, ok := service.requireCommerceStepUp(w, r); ok {
					w.WriteHeader(http.StatusNoContent)
				}
			})))
			request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodPost, "/v1/admin/step-up-test", nil)
			if test.reason != "" {
				request.Header.Set(commerceOperationReasonHeader, test.reason)
			}
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != test.wantStatus || (test.wantCode != "" && !strings.Contains(response.Body.String(), `"code":"`+test.wantCode+`"`)) {
				t.Fatalf("status/body = %d/%s, want %d/%s", response.Code, response.Body.String(), test.wantStatus, test.wantCode)
			}
			if repository.roleChecks != 1 || len(repository.events) != test.wantEvents {
				t.Fatalf("role checks/events = %d/%d, want 1/%d", repository.roleChecks, len(repository.events), test.wantEvents)
			}
			if strings.Contains(response.Body.String(), "sensitive-provider-acr") {
				t.Fatal("step-up response exposed provider ACR")
			}
			for _, event := range repository.events {
				if event.Action != test.wantAction || event.Result != "DENIED" || len(event.SourceHash) != sha256.Size {
					t.Fatalf("unexpected step-up security event: %+v", event)
				}
				if bytes.Contains(event.Metadata, []byte("sensitive-provider-acr")) {
					t.Fatal("step-up event exposed provider ACR")
				}
			}
		})
	}
}

func TestRefundConfirmationExactEndpointSecurityBoundary(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID, refundID := uuid.New(), uuid.New(), uuid.New()
	tests := []struct {
		name           string
		identity       auth.Identity
		reason         string
		body           []byte
		useFullHandler bool
		wantStatus     int
		wantCode       string
	}{
		{
			name: "mfa is required", identity: auth.Identity{AuthenticatedAt: now},
			reason: "Confirm requested refund", body: []byte(`{"expected_version":1}`),
			wantStatus: http.StatusForbidden, wantCode: "STEP_UP_REQUIRED",
		},
		{
			name: "operation reason is required", identity: auth.Identity{MFA: true, AuthenticatedAt: now},
			body:       []byte(`{"expected_version":1}`),
			wantStatus: http.StatusBadRequest, wantCode: "OPERATION_REASON_REQUIRED",
		},
		{
			name: "positive expected version is required", identity: auth.Identity{MFA: true, AuthenticatedAt: now},
			reason: "Confirm requested refund", body: []byte(`{"expected_version":0}`),
			wantStatus: http.StatusBadRequest, wantCode: "INVALID_REFUND_CONFIRMATION",
		},
		{
			name: "idempotency key is required", identity: auth.Identity{MFA: true, AuthenticatedAt: now},
			reason: "Confirm requested refund", body: []byte(`{"expected_version":1}`), useFullHandler: true,
			wantStatus: http.StatusBadRequest, wantCode: "IDEMPOTENCY_KEY_REQUIRED",
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			security := &memoryCommerceSecurityStore{admin: true}
			authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
			authentication.Tokens = commerceIdentityVerifier{identity: test.identity}
			service := &API{
				Store: &store.Postgres{}, Commerce: &commerce.Service{}, Auth: authentication,
				Clock: func() time.Time { return now }, commerceSecurity: security,
			}
			var handler http.Handler
			if test.useFullHandler {
				handler = service.Handler()
			} else {
				mux := http.NewServeMux()
				mux.HandleFunc("POST /v1/admin/billing/refunds/{refundId}/confirm", service.confirmCommerceRefund)
				handler = traceMiddleware(authentication.Authenticate(mux))
			}
			target := "/v1/admin/billing/refunds/" + refundID.String() + "/confirm"
			request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodPost, target, test.body)
			if test.reason != "" {
				request.Header.Set(commerceOperationReasonHeader, test.reason)
			}
			if !test.useFullHandler {
				request.Header.Set("Idempotency-Key", "refund-confirm-security-test")
			}
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != test.wantStatus || !strings.Contains(response.Body.String(), `"code":"`+test.wantCode+`"`) {
				t.Fatalf("status/body = %d/%s, want %d/%s", response.Code, response.Body.String(), test.wantStatus, test.wantCode)
			}
		})
	}
}

func TestStepUpSuccessAuditStoresReasonAndOnlyIdempotencyDigest(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID := uuid.New(), uuid.New()
	repository := &memoryCommerceSecurityStore{admin: true}
	service := &API{
		Clock: func() time.Time { return now }, commerceSecurity: repository,
		CommerceKey: []byte("commerce-audit-key-0123456789abcdef"),
	}
	authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
	authentication.Tokens = commerceIdentityVerifier{identity: auth.Identity{MFA: true, AuthenticatedAt: now}}
	targetID := uuid.New()
	handler := traceMiddleware(authentication.Authenticate(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		actorID, ok := service.requireCommerceStepUp(w, r)
		if !ok {
			return
		}
		service.auditCommerceOperationSuccess(r, actorID, "commerce.test.context", "payment_channel", targetID)
		w.WriteHeader(http.StatusNoContent)
	})))
	request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodPost, "/v1/admin/audit-test", nil)
	reason := "Rotate provider credentials after scheduled review"
	rawIdempotencyKey := "raw-idempotency-key-must-not-enter-audit"
	request.Header.Set(commerceOperationReasonHeader, reason)
	request.Header.Set("Idempotency-Key", rawIdempotencyKey)
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusNoContent || len(repository.audits) != 1 {
		t.Fatalf("status/audits = %d/%d", response.Code, len(repository.audits))
	}
	audit := repository.audits[0]
	if audit.action != "commerce.test.context" || audit.targetID != targetID || audit.result != "SUCCEEDED" {
		t.Fatalf("unexpected success audit: %+v", audit)
	}
	if strings.Contains(audit.metadata, rawIdempotencyKey) || strings.Contains(response.Body.String(), reason) {
		t.Fatal("raw idempotency key or audit-only reason escaped its intended boundary")
	}
	var metadata map[string]string
	if err := json.Unmarshal([]byte(audit.metadata), &metadata); err != nil {
		t.Fatal(err)
	}
	if metadata["operation_reason"] != reason || metadata["idempotency_key_hash"] != service.commerceIdempotencyAuditHash(rawIdempotencyKey) || len(metadata["idempotency_key_hash"]) != sha256.Size*2 {
		t.Fatalf("unexpected sanitized audit metadata: %#v", metadata)
	}
}

func TestGenericIdempotencyMiddlewareCoversPatch(t *testing.T) {
	nextCalled := false
	handler := (&API{}).idempotency(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		nextCalled = true
	}))
	request := httptest.NewRequest(http.MethodPatch, "/v1/admin/registration-policy", strings.NewReader(`{"mode":"INVITE_REQUIRED"}`))
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || nextCalled || !strings.Contains(response.Body.String(), `"code":"IDEMPOTENCY_KEY_REQUIRED"`) {
		t.Fatalf("PATCH idempotency status/body/next = %d/%s/%t", response.Code, response.Body.String(), nextCalled)
	}

	withReason := httptest.NewRequest(http.MethodPatch, "/v1/admin/registration-policy", strings.NewReader(`{"mode":"INVITE_REQUIRED"}`))
	withReason.Header.Set(commerceOperationReasonHeader, "Change registration policy for launch")
	withoutReason := httptest.NewRequest(http.MethodPatch, "/v1/admin/registration-policy", strings.NewReader(`{"mode":"INVITE_REQUIRED"}`))
	body := []byte(`{"mode":"INVITE_REQUIRED"}`)
	if idempotencyRequestDigest(withReason, body) == idempotencyRequestDigest(withoutReason, body) {
		t.Fatal("operation reason was omitted from the idempotency request digest")
	}
}

func TestRefundRequestAndConfirmationUseIndependentIdempotencyDomains(t *testing.T) {
	orderID, refundID := uuid.New(), uuid.New()
	requestBody := []byte(`{"amount_minor":990,"reason":"customer request"}`)
	confirmBody := []byte(`{"expected_version":1}`)
	request := httptest.NewRequest(http.MethodPost, "/v1/admin/billing/orders/"+orderID.String()+"/refunds", bytes.NewReader(requestBody))
	confirm := httptest.NewRequest(http.MethodPost, "/v1/admin/billing/refunds/"+refundID.String()+"/confirm", bytes.NewReader(confirmBody))
	for _, item := range []*http.Request{request, confirm} {
		item.Header.Set("Idempotency-Key", "shared-client-key-is-still-path-scoped")
		item.Header.Set(commerceOperationReasonHeader, "Refund requested by support")
	}
	if idempotencyRequestDigest(request, requestBody) == idempotencyRequestDigest(confirm, confirmBody) {
		t.Fatal("refund request and confirmation shared one idempotency digest domain")
	}
	source, err := os.ReadFile("api.go")
	if err != nil {
		t.Fatal(err)
	}
	for _, route := range []string{
		`trusted.HandleFunc("POST /v1/admin/billing/orders/{orderId}/refunds", a.createCommerceRefund)`,
		`trusted.HandleFunc("POST /v1/admin/billing/refunds/{refundId}/confirm", a.confirmCommerceRefund)`,
	} {
		if !bytes.Contains(source, []byte(route)) {
			t.Fatalf("exact authenticated refund route is missing: %s", route)
		}
	}
}

func TestHighRiskAdminHandlersInvokeStepUpBoundary(t *testing.T) {
	commerceSource, err := os.ReadFile("commerce_routes.go")
	if err != nil {
		t.Fatal(err)
	}
	for _, handler := range []string{
		"adminUpdateRegistrationPolicy",
		"generateInvitationCodes",
		"generateRedemptionCodes",
		"createCommerceRefund",
		"confirmCommerceRefund",
		"createPaymentChannel",
		"queryAdminCommerceOrder",
		"retryAdminOrderFulfillment",
		"createReconciliationRun",
		"createPaymentChannelVersion",
		"validatePaymentChannelVersion",
		"activatePaymentChannelVersion",
		"disablePaymentChannel",
	} {
		pattern := regexp.MustCompile(`(?s)func \(a \*API\) ` + handler + `\(.{0,1000}?requireCommerceStepUp\(w, r\)`)
		if !pattern.Match(commerceSource) {
			t.Fatalf("high-risk handler %s does not invoke the database-role plus recent-MFA boundary", handler)
		}
	}
	apiSource, err := os.ReadFile("api.go")
	if err != nil {
		t.Fatal(err)
	}
	if !regexp.MustCompile(`(?s)func \(a \*API\) publishCatalog\(.{0,500}?requireAdmin\(w, r\).{0,300}?requireRecentStepUp\(w, r, p.UserID\)`).Match(apiSource) {
		t.Fatal("catalog publication does not require database admin role plus recent MFA")
	}
}

func TestHighRiskAdminHandlersWriteSanitizedSuccessAudit(t *testing.T) {
	commerceSource, err := os.ReadFile("commerce_routes.go")
	if err != nil {
		t.Fatal(err)
	}
	for _, handler := range []string{
		"adminUpdateRegistrationPolicy",
		"generateInvitationCodes",
		"generateRedemptionCodes",
		"createCommerceRefund",
		"confirmCommerceRefund",
		"createPaymentChannel",
		"createPaymentChannelVersion",
		"validatePaymentChannelVersion",
		"activatePaymentChannelVersion",
		"disablePaymentChannel",
		"queryAdminCommerceOrder",
		"retryAdminOrderFulfillment",
		"createReconciliationRun",
	} {
		if !bytes.Contains(sourceFunctionBody(t, commerceSource, handler), []byte("auditCommerceOperationSuccess(r,")) {
			t.Fatalf("high-risk handler %s does not write sanitized success audit context", handler)
		}
	}
	apiSource, err := os.ReadFile("api.go")
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(sourceFunctionBody(t, apiSource, "publishCatalog"), []byte("auditCommerceOperationSuccess(r,")) {
		t.Fatal("catalog publication does not write sanitized success audit context")
	}
}

func sourceFunctionBody(t *testing.T, source []byte, functionName string) []byte {
	t.Helper()
	startMarker := []byte("func (a *API) " + functionName + "(")
	start := bytes.Index(source, startMarker)
	if start < 0 {
		t.Fatalf("function %s is missing", functionName)
	}
	remainder := source[start+len(startMarker):]
	end := bytes.Index(remainder, []byte("\nfunc "))
	if end < 0 {
		return remainder
	}
	return remainder[:end]
}

func TestEveryAdminWriteHandlerInvokesDatabaseRoleBoundary(t *testing.T) {
	commerceSource, err := os.ReadFile("commerce_routes.go")
	if err != nil {
		t.Fatal(err)
	}
	apiSource, err := os.ReadFile("api.go")
	if err != nil {
		t.Fatal(err)
	}
	tests := []struct {
		handler  string
		source   []byte
		boundary string
	}{
		{"adminUpdateRegistrationPolicy", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"createInvitationBatch", commerceSource, `requireCommerceAdmin\(w, r\)`},
		{"disableInvitationBatch", commerceSource, `requireCommerceAdmin\(w, r\)`},
		{"disableInvitationCode", commerceSource, `requireCommerceAdmin\(w, r\)`},
		{"generateInvitationCodes", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"createRedemptionBatch", commerceSource, `requireCommerceAdmin\(w, r\)`},
		{"disableRedemptionBatch", commerceSource, `requireCommerceAdmin\(w, r\)`},
		{"disableRedemptionCode", commerceSource, `requireCommerceAdmin\(w, r\)`},
		{"generateRedemptionCodes", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"queryAdminCommerceOrder", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"retryAdminOrderFulfillment", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"createCommerceRefund", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"confirmCommerceRefund", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"createReconciliationRun", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"createPaymentChannel", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"createPaymentChannelVersion", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"validatePaymentChannelVersion", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"activatePaymentChannelVersion", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"disablePaymentChannel", commerceSource, `requireCommerceStepUp\(w, r\)`},
		{"createPlanDraft", apiSource, `requireAdmin\(w, r\)`},
		{"updatePlanDraft", apiSource, `requireAdmin\(w, r\)`},
		{"publishCatalog", apiSource, `requireAdmin\(w, r\)`},
	}
	for _, test := range tests {
		pattern := regexp.MustCompile(`(?s)func \(a \*API\) ` + test.handler + `\(.{0,1000}?` + test.boundary)
		if !pattern.Match(test.source) {
			t.Fatalf("admin write handler %s does not invoke its database role boundary", test.handler)
		}
	}
}

func TestCommerceRejectionsPersistBoundedSecurityEvents(t *testing.T) {
	repository := &memoryCommerceSecurityStore{admin: true}
	service := &API{commerceSecurity: repository}
	request := httptest.NewRequest(http.MethodPost, "/v1/billing/webhooks/alipay", strings.NewReader("forged-provider-trade-secret"))
	request.RemoteAddr = "203.0.113.40:43210"
	classification := service.recordCommerceWebhookFailure(request, string(payment.ProviderAlipay), fmt.Errorf("provider-trade-secret: %w", payment.ErrInvalidSignature))
	service.recordCommerceRateLimitFailure(request, commerceRateInvitationValidate, false)
	service.recordCommerceRateLimitFailure(request, commerceRateRedemptionClaim, false)
	service.recordCommerceRateLimitFailure(request, commerceRateOrderQuery, false)
	if classification != "INVALID_SIGNATURE" || len(repository.events) != 4 {
		t.Fatalf("classification/events = %q/%d", classification, len(repository.events))
	}
	wantActions := []string{
		"commerce.webhook.denied",
		"commerce.invitation.rate_limit.denied",
		"commerce.redemption.rate_limit.denied",
		"commerce.payment.rate_limit.denied",
	}
	for index, event := range repository.events {
		encoded, err := json.Marshal(event)
		if err != nil {
			t.Fatal(err)
		}
		if event.Action != wantActions[index] || len(event.SourceHash) != sha256.Size || event.TraceID != "" {
			t.Fatalf("event %d = %+v", index, event)
		}
		for _, forbidden := range []string{"forged-provider-trade-secret", "203.0.113.40"} {
			if bytes.Contains(bytes.ToLower(encoded), bytes.ToLower([]byte(forbidden))) {
				t.Fatalf("security event exposed %q: %s", forbidden, encoded)
			}
		}
	}
	if boundedCommerceProvider("attacker-provider-trade-secret") != "UNKNOWN" {
		t.Fatal("unbounded payment provider was accepted for security metadata")
	}
}

func TestGeneratedCommerceCodesHavePrefixEntropyAndNoPlaintextRecord(t *testing.T) {
	key := []byte("0123456789abcdef0123456789abcdef")
	shapes := map[string]*regexp.Regexp{
		"TLI": regexp.MustCompile(`^TLI-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$`),
		"TLR": regexp.MustCompile(`^TLR-[A-HJ-NP-Z2-9]{7}-[A-HJ-NP-Z2-9]{7}-[A-HJ-NP-Z2-9]{7}$`),
	}
	lengths := map[string]int{"TLI": 15, "TLR": 24}
	for _, test := range []struct {
		prefix  string
		purpose string
	}{
		{"TLI", "invitation-code"},
		{"TLR", "redemption-code"},
	} {
		plaintext, records, err := generateCommerceCodes(test.prefix, test.purpose, key, generateCodesRequest{Count: 256, MaxUses: 1})
		if err != nil {
			t.Fatal(err)
		}
		seen := make(map[string]struct{}, len(plaintext))
		for index, code := range plaintext {
			if !shapes[test.prefix].MatchString(code) {
				t.Fatalf("generated code has invalid shape: %q", code)
			}
			normalized := normalizeCommerceCode(code)
			if !validCommerceCode(normalized, test.prefix) || len(normalized) != lengths[test.prefix] {
				t.Fatalf("generated normalized code = %q", normalized)
			}
			if _, exists := seen[code]; exists {
				t.Fatalf("duplicate generated code: %q", code)
			}
			seen[code] = struct{}{}
			if records[index].Digest != keyedCommerceDigest(key, test.purpose, normalized) {
				t.Fatal("persisted record did not contain the keyed, purpose-separated digest")
			}
			encoded, err := json.Marshal(records[index])
			if err != nil {
				t.Fatal(err)
			}
			if bytes.Contains(encoded, []byte(code)) || bytes.Contains(encoded, []byte(normalized)) {
				t.Fatal("persisted code record contained plaintext")
			}
		}
	}
	if !validCommerceCode("TLIABCDEFGHJKLMNPQRSTUVWXYZ", "TLI") {
		t.Fatal("legacy long invitation code is no longer accepted")
	}
}

func TestCommerceDigestKeyPrefersDedicatedKey(t *testing.T) {
	dedicated := []byte("commerce-purpose-key-0123456789abcd")
	token := []byte("token-purpose-key----0123456789abcd")
	service := &API{CommerceKey: dedicated, TokenKey: token}
	if !bytes.Equal(service.commerceDigestKey(), dedicated) {
		t.Fatal("commerce digest did not use its dedicated key")
	}
	service.CommerceKey = nil
	if !bytes.Equal(service.commerceDigestKey(), token) {
		t.Fatal("staged compatibility fallback did not use token key")
	}
}

func TestPublicInvitationFailuresDoNotRevealInventoryState(t *testing.T) {
	errorsToHide := []error{
		store.ErrInvitationInvalid,
		store.ErrInvitationExpired,
		store.ErrInvitationExhausted,
		store.ErrInvitationEmailMismatch,
	}
	var expected string
	for _, hidden := range errorsToHide {
		request := httptest.NewRequest(http.MethodPost, "/v1/auth/register", nil)
		response := httptest.NewRecorder()
		if !writeRegistrationAdmissionError(response, request, hidden) {
			t.Fatalf("error was not classified: %v", hidden)
		}
		var payload struct {
			Error struct {
				Code string `json:"code"`
			} `json:"error"`
		}
		if err := json.Unmarshal(response.Body.Bytes(), &payload); err != nil {
			t.Fatal(err)
		}
		if expected == "" {
			expected = payload.Error.Code
		}
		if response.Code != http.StatusUnprocessableEntity || payload.Error.Code != expected || payload.Error.Code != "INVITATION_INVALID" {
			t.Fatalf("public classification = %d/%q", response.Code, payload.Error.Code)
		}
	}
}

func TestPaymentChannelValidationRejectsClientAssertedResult(t *testing.T) {
	request := httptest.NewRequest(http.MethodPost, "/v1/admin/payment-channels/"+uuid.NewString()+"/validate", strings.NewReader(`{"version_id":"`+uuid.NewString()+`","passed":true,"summary":"trust me"}`))
	var input validatePaymentChannelRequest
	if err := decodeJSON(request, &input); err == nil {
		t.Fatal("client-supplied payment validation result was accepted")
	}
}

func TestCommerceRequestsRejectForgedOwnershipAndUnexpectedBodies(t *testing.T) {
	preview := httptest.NewRequest(http.MethodPost, "/v1/billing/redemptions/preview", strings.NewReader(`{"code":"TLR-ABCDEFG-HJKLMNP-QRSTUV2","account_id":"`+uuid.NewString()+`"}`))
	var redemption redemptionClaimRequest
	if err := decodeJSON(preview, &redemption); err == nil {
		t.Fatal("redemption request accepted a client-supplied account owner")
	}

	query := httptest.NewRequest(http.MethodPost, "/v1/billing/orders/"+uuid.NewString()+"/query", strings.NewReader(`{"account_id":"`+uuid.NewString()+`"}`))
	if err := decodeEmptyCommerceJSON(query); err == nil {
		t.Fatal("order query accepted a client-supplied account owner")
	}
	validEmpty := httptest.NewRequest(http.MethodPost, "/v1/billing/orders/"+uuid.NewString()+"/query", strings.NewReader(`{}`))
	if err := decodeEmptyCommerceJSON(validEmpty); err != nil {
		t.Fatalf("empty query object rejected: %v", err)
	}
	validNoBody := httptest.NewRequest(http.MethodPost, "/v1/billing/orders/"+uuid.NewString()+"/query", nil)
	if err := decodeEmptyCommerceJSON(validNoBody); err != nil {
		t.Fatalf("absent query body rejected: %v", err)
	}
}

func TestCommerceErrorMappingIsStableAndAdminDenialIsForbidden(t *testing.T) {
	tests := []struct {
		err    error
		status int
		code   string
	}{
		{store.ErrDenied, http.StatusForbidden, "FORBIDDEN"},
		{store.ErrRedemptionExpired, http.StatusGone, "REDEMPTION_EXPIRED"},
		{store.ErrOrderNotPayable, http.StatusConflict, "ORDER_NOT_PAYABLE"},
	}
	for _, test := range tests {
		request := httptest.NewRequest(http.MethodPost, "/v1/admin/commerce-test", nil)
		response := httptest.NewRecorder()
		writeCommerceError(response, request, test.err)
		if response.Code != test.status || !strings.Contains(response.Body.String(), `"code":"`+test.code+`"`) {
			t.Fatalf("error %v mapped to status/body %d/%s", test.err, response.Code, response.Body.String())
		}
	}
	if _, err := commerceIdempotencyKey(httptest.NewRequest(http.MethodPost, "/", nil)); err == nil {
		t.Fatal("missing idempotency key was accepted")
	}
	request := httptest.NewRequest(http.MethodPost, "/", nil)
	request.Header.Set("Idempotency-Key", strings.Repeat("x", 129))
	if _, err := commerceIdempotencyKey(request); err == nil {
		t.Fatal("oversized idempotency key was accepted")
	}
}

func TestPaymentChannelVersionViewNeverEchoesSecretReferences(t *testing.T) {
	view := paymentChannelVersionView(store.PaymentChannelVersion{
		ID: uuid.New(), ChannelID: uuid.New(), SecretRefs: json.RawMessage(`{"api_v3_key":"docker-secret://merchant-api-key"}`),
	})
	encoded, err := json.Marshal(view)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(encoded, []byte("merchant-api-key")) || bytes.Contains(encoded, []byte("docker-secret://")) {
		t.Fatalf("secret reference echoed by API: %s", encoded)
	}
	if !bytes.Contains(encoded, []byte(`"secret_refs_configured":true`)) {
		t.Fatalf("sanitized configured state missing: %s", encoded)
	}
}

func TestCommerceViewsOmitInternalPaymentAndIdempotencyMaterial(t *testing.T) {
	order := store.Order{
		ID: uuid.New(), AccountID: uuid.New(), OrderNo: "TL-safe-order",
		IdempotencyKey: "must-not-be-returned", PlanKey: "pro", Provider: "ALIPAY",
	}
	encoded, err := json.Marshal(paymentOutcomeView(commerce.PaymentOutcome{Order: order, ProviderStatus: payment.StatusPending}))
	if err != nil {
		t.Fatal(err)
	}
	for _, forbidden := range []string{"must-not-be-returned", "idempotency_key", "payment_payload", "provider_trade_no_hash"} {
		if bytes.Contains(encoded, []byte(forbidden)) {
			t.Fatalf("commerce view exposed internal material %q: %s", forbidden, encoded)
		}
	}

	invitation := store.InvitationRedemption{ID: uuid.New(), CodeID: uuid.New(), AttemptID: uuid.New(), State: "REDEEMED"}
	encoded, err = json.Marshal(invitationRedemptionView(invitation))
	if err != nil {
		t.Fatal(err)
	}
	for _, forbidden := range []string{"email", "code_hash", "invitation_code"} {
		if bytes.Contains(bytes.ToLower(encoded), []byte(forbidden)) {
			t.Fatalf("invitation history exposed sensitive field %q: %s", forbidden, encoded)
		}
	}

	accountID := uuid.New()
	redemption, err := json.Marshal(accountRedemptionView(store.AccountRedemption{ClaimID: uuid.New(), AccountID: accountID}))
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(redemption, []byte(accountID.String())) || bytes.Contains(redemption, []byte("code_hash")) {
		t.Fatalf("admin redemption record lost ownership or exposed a digest: %s", redemption)
	}
}

func TestGeneratedCodeValidationRejectsUnsafeBounds(t *testing.T) {
	key := []byte("0123456789abcdef0123456789abcdef")
	for _, input := range []generateCodesRequest{
		{},
		{Count: maxGeneratedCodeCount + 1, MaxUses: 1},
		{Count: 1, MaxUses: 1_000_001},
	} {
		if _, _, err := generateCommerceCodes("TLI", "invitation-code", key, input); err == nil {
			t.Fatalf("unsafe generation input accepted: %+v", input)
		}
	}
	if _, _, err := generateCommerceCodes("TLI", "invitation-code", []byte("short"), generateCodesRequest{Count: 1, MaxUses: 1}); err == nil {
		t.Fatal("short digest key accepted")
	}
}
