package api

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

func TestAdminCloseOrderRequestHashBindsEverySecurityDimension(t *testing.T) {
	actorID, orderID := uuid.New(), uuid.New()
	base := adminCloseOrderRequestHash(actorID, orderID, "CUSTOMER_REQUEST", "Support confirmed cancellation")
	variants := [][4]string{
		{uuid.NewString(), orderID.String(), "CUSTOMER_REQUEST", "Support confirmed cancellation"},
		{actorID.String(), uuid.NewString(), "CUSTOMER_REQUEST", "Support confirmed cancellation"},
		{actorID.String(), orderID.String(), "RISK_REVIEW", "Support confirmed cancellation"},
		{actorID.String(), orderID.String(), "CUSTOMER_REQUEST", "Different operation reason"},
	}
	for _, variant := range variants {
		got := adminCloseOrderRequestHash(uuid.MustParse(variant[0]), uuid.MustParse(variant[1]), variant[2], variant[3])
		if got == base {
			t.Fatalf("request hash did not bind variant %#v", variant)
		}
	}
}

func TestAdminCloseOrderHandlerEnforcesStepUpReasonAndIdempotencyBeforeStore(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID, orderID := uuid.New(), uuid.New(), uuid.New()
	tests := []struct {
		name       string
		identity   auth.Identity
		reason     string
		key        string
		body       string
		wantStatus int
		wantCode   string
	}{
		{"mfa required", auth.Identity{AuthenticatedAt: now}, "Close abandoned order", "admin-close-key-01", `{"reason_code":"CUSTOMER_REQUEST"}`, http.StatusForbidden, "STEP_UP_REQUIRED"},
		{"operation reason required", auth.Identity{MFA: true, AuthenticatedAt: now}, "", "admin-close-key-02", `{"reason_code":"CUSTOMER_REQUEST"}`, http.StatusBadRequest, "OPERATION_REASON_REQUIRED"},
		{"idempotency required", auth.Identity{MFA: true, AuthenticatedAt: now}, "Close abandoned order", "", `{"reason_code":"CUSTOMER_REQUEST"}`, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED"},
		{"reason code required", auth.Identity{MFA: true, AuthenticatedAt: now}, "Close abandoned order", "admin-close-key-03", `{"reason_code":"free form secret"}`, http.StatusBadRequest, "INVALID_ADMIN_ORDER_CLOSE_REASON"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			security := &memoryCommerceSecurityStore{admin: true}
			authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
			authentication.Tokens = commerceIdentityVerifier{identity: test.identity}
			service := &API{Store: &store.Postgres{}, Clock: func() time.Time { return now }, commerceSecurity: security}
			mux := http.NewServeMux()
			mux.HandleFunc("POST /v1/admin/billing/orders/{orderId}/close", service.closeAdminCommerceOrder)
			handler := traceMiddleware(authentication.Authenticate(mux))
			target := "/v1/admin/billing/orders/" + orderID.String() + "/close"
			request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodPost, target, []byte(test.body))
			request.Header.Set(commerceOperationReasonHeader, test.reason)
			request.Header.Set("Idempotency-Key", test.key)
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != test.wantStatus || !strings.Contains(response.Body.String(), `"code":"`+test.wantCode+`"`) {
				t.Fatalf("status/body=%d/%s want=%d/%s", response.Code, response.Body.String(), test.wantStatus, test.wantCode)
			}
		})
	}
}

func TestAdminCloseHandlerMustUseExactAuthenticationOutsideGenericIdempotency(t *testing.T) {
	source, err := os.ReadFile("commerce_admin_close.go")
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range [][]byte{
		[]byte("requireCommerceStepUp(w, r)"), []byte("commerceIdempotencyKey(r)"),
		[]byte("AdminCloseUnpaidOrder"), []byte("TRUSTED_DEVICE_REQUIRED"),
	} {
		if !bytes.Contains(source, required) {
			t.Fatalf("admin close handler is missing security boundary %q", required)
		}
	}
	if bytes.Contains(source, []byte("CancelOrder(")) {
		t.Fatal("admin close handler reused account-scoped cancellation")
	}
	routes, err := os.ReadFile("api.go")
	if err != nil {
		t.Fatal(err)
	}
	want := []byte(`root.Handle("POST /v1/admin/billing/orders/{orderId}/close", a.Auth.Authenticate(http.HandlerFunc(a.closeAdminCommerceOrder)))`)
	if !bytes.Contains(routes, want) {
		t.Fatal("admin close is not an exact authenticated route outside generic idempotency")
	}
}

func TestAdminCloseOrderResponseViewOmitsInternalPaymentMaterial(t *testing.T) {
	encoded, err := json.Marshal(commerceOrderView(store.Order{
		ID: uuid.New(), AccountID: uuid.New(), OrderNo: "TL-safe-order", Status: "CLOSED",
		Provider: "ALIPAY", PaymentScene: "NATIVE", IdempotencyKey: "must-not-escape",
	}))
	if err != nil {
		t.Fatal(err)
	}
	for _, forbidden := range []string{"must-not-escape", "idempotency_key", "payment_payload", "provider_trade", "credential", "secret"} {
		if bytes.Contains(bytes.ToLower(encoded), []byte(forbidden)) {
			t.Fatalf("admin close response exposed %q: %s", forbidden, encoded)
		}
	}
}
