package api_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/api"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/store"
)

// assertDirectInputLeaseAuthorizationHTTP runs against the production API
// router and authentication middleware. The surrounding same-account E2E
// fixture supplies real PostgreSQL/Valkey state and two Android devices that
// have joined the same shared lease.
func assertDirectInputLeaseAuthorizationHTTP(
	t *testing.T,
	root string,
	service *api.API,
	authentication auth.Middleware,
	repository *store.Postgres,
	coordinator kv.Coordinator,
	pcID, sessionID uuid.UUID,
	windows, nonOwningWindows, holder, participant, crossAccount testDevice,
	initialPolicyVersion int64,
) {
	t.Helper()

	// The route remains authenticated even though Windows supplies the Android
	// sender as a query parameter. A sender identifier can never stand in for
	// the calling PC's signed device proof.
	directPath := "/v1/sessions/" + sessionID.String() +
		"/lease?sender_device_id=" + holder.id.String()
	request, err := http.NewRequest(http.MethodGet, root+directPath, nil)
	if err != nil {
		t.Fatal(err)
	}
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	assertDirectInputErrorResponse(t, response, http.StatusUnauthorized, "UNAUTHENTICATED", "auth.unauthenticated")

	legacyPath := strings.Split(directPath, "?")[0]
	legacy := sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, legacyPath, nil, "", http.StatusOK)
	if _, exists := legacy["authorized_sender_device_id"]; exists {
		t.Fatalf("legacy lease response unexpectedly gained sender authorization fields: %v", legacy)
	}

	assertDirectInputAuthorizationResponse(
		t,
		sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, directPath, nil, "", http.StatusOK),
		holder.id,
		"COLLABORATIVE",
		initialPolicyVersion,
	)
	participantPath := legacyPath + "?sender_device_id=" + participant.id.String()
	assertDirectInputAuthorizationResponse(
		t,
		sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, participantPath, nil, "", http.StatusOK),
		participant.id,
		"COLLABORATIVE",
		initialPolicyVersion,
	)

	invalidSender := sameAccountSignedJSON(
		t, root, windows, "same-account-windows", http.MethodGet,
		legacyPath+"?sender_device_id="+strings.ToUpper(holder.id.String()), nil, "", http.StatusBadRequest,
	)
	assertDirectInputErrorPayload(t, invalidSender, "INVALID_DEVICE_ID", "errors.invalid_device_id")

	unknownSender := sameAccountSignedJSON(
		t, root, windows, "same-account-windows", http.MethodGet,
		legacyPath+"?sender_device_id="+uuid.NewString(), nil, "", http.StatusForbidden,
	)
	assertDirectInputErrorPayload(t, unknownSender, "CONTROL_NOT_GRANTED", "errors.control_not_granted")

	crossSender := sameAccountSignedJSON(
		t, root, windows, "same-account-windows", http.MethodGet,
		legacyPath+"?sender_device_id="+crossAccount.id.String(), nil, "", http.StatusForbidden,
	)
	assertDirectInputErrorPayload(t, crossSender, "CONTROL_NOT_GRANTED", "errors.control_not_granted")

	crossCaller := sameAccountSignedJSON(
		t, root, crossAccount, "cross-account-android", http.MethodGet,
		legacyPath+"?sender_device_id="+crossAccount.id.String(), nil, "", http.StatusForbidden,
	)
	assertDirectInputErrorPayload(t, crossCaller, "FORBIDDEN", "errors.forbidden")

	nonPCCaller := sameAccountSignedJSON(
		t, root, participant, "same-account-android-two", http.MethodGet,
		directPath, nil, "", http.StatusForbidden,
	)
	assertDirectInputErrorPayload(t, nonPCCaller, "CONTROL_NOT_GRANTED", "errors.control_not_granted")

	nonOwningPCCaller := sameAccountSignedJSON(
		t, root, nonOwningWindows, "same-account-windows-two", http.MethodGet,
		directPath, nil, "", http.StatusForbidden,
	)
	assertDirectInputErrorPayload(t, nonOwningPCCaller, "CONTROL_NOT_GRANTED", "errors.control_not_granted")

	policyVersion := setDirectInputControlMode(
		t, root, holder, "same-account-android", pcID,
		"SINGLE", initialPolicyVersion,
	)
	assertDirectInputAuthorizationResponse(
		t,
		sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, directPath, nil, "", http.StatusOK),
		holder.id,
		"SINGLE",
		policyVersion,
	)

	nonHolder := sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, participantPath, nil, "", http.StatusForbidden)
	assertDirectInputErrorPayload(t, nonHolder, "CONTROL_NOT_GRANTED", "errors.control_not_granted")

	policyVersion = setDirectInputControlMode(
		t, root, holder, "same-account-android", pcID,
		"TAKEOVER", policyVersion,
	)
	assertDirectInputAuthorizationResponse(
		t,
		sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, directPath, nil, "", http.StatusOK),
		holder.id,
		"TAKEOVER",
		policyVersion,
	)
	nonHolder = sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, participantPath, nil, "", http.StatusForbidden)
	assertDirectInputErrorPayload(t, nonHolder, "CONTROL_NOT_GRANTED", "errors.control_not_granted")

	policyVersion = setDirectInputControlMode(
		t, root, holder, "same-account-android", pcID,
		"COLLABORATIVE", policyVersion,
	)
	assertDirectInputAuthorizationResponse(
		t,
		sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, participantPath, nil, "", http.StatusOK),
		participant.id,
		"COLLABORATIVE",
		policyVersion,
	)

	currentLease, err := coordinator.GetLease(context.Background(), sessionID)
	if err != nil {
		t.Fatalf("load shared lease before participant release: %v", err)
	}
	accepted, released, err := coordinator.ReleaseForDevice(context.Background(), currentLease, participant.id)
	if err != nil || !accepted || released {
		t.Fatalf("release Direct INPUT participant accepted/released/error=%t/%t/%v", accepted, released, err)
	}
	releasedParticipant := sameAccountSignedJSON(
		t, root, windows, "same-account-windows", http.MethodGet,
		participantPath, nil, "", http.StatusForbidden,
	)
	assertDirectInputErrorPayload(t, releasedParticipant, "CONTROL_NOT_GRANTED", "errors.control_not_granted")
	rejoined, err := coordinator.Acquire(context.Background(), sessionID, participant.id, 30*time.Second)
	if err != nil || rejoined.ID != currentLease.ID || rejoined.Version != currentLease.Version {
		t.Fatalf("rejoin Direct INPUT participant lease=%+v error=%v, want fence %+v", rejoined, err, currentLease)
	}
	assertDirectInputAuthorizationResponse(
		t,
		sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, participantPath, nil, "", http.StatusOK),
		participant.id,
		"COLLABORATIVE",
		policyVersion,
	)

	// A coordinator outage must not turn into an optimistic authorization.
	// Authentication retains the healthy coordinator; only the handler's lease
	// read is failed so this exercises the intended dependency boundary.
	failingService := &api.API{
		Store: repository,
		Valkey: directInputParticipantFailureCoordinator{
			Coordinator: coordinator,
		},
		Tokens:       service.Tokens,
		Auth:         authentication,
		SubjectKey:   service.SubjectKey,
		TokenKey:     service.TokenKey,
		ClientConfig: service.ClientConfig,
	}
	failingServer := httptest.NewServer(failingService.Handler())
	defer failingServer.Close()
	failedDependency := sameAccountSignedJSON(
		t, failingServer.URL, windows, "same-account-windows", http.MethodGet,
		directPath, nil, "", http.StatusServiceUnavailable,
	)
	assertDirectInputErrorPayload(t, failedDependency, "LEASE_UNAVAILABLE", "errors.lease_unavailable")
}

func assertExpiredDirectInputLeaseDenied(
	t *testing.T,
	root string,
	coordinator kv.Coordinator,
	windows, sender testDevice,
	sessionID uuid.UUID,
) {
	t.Helper()
	if _, err := coordinator.Acquire(context.Background(), sessionID, sender.id, 500*time.Millisecond); err != nil {
		t.Fatalf("arrange expiring Direct INPUT lease: %v", err)
	}
	path := "/v1/sessions/" + sessionID.String() + "/lease?sender_device_id=" + sender.id.String()
	sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, path, nil, "", http.StatusOK)
	time.Sleep(750 * time.Millisecond)
	expired := sameAccountSignedJSON(t, root, windows, "same-account-windows", http.MethodGet, path, nil, "", http.StatusNotFound)
	assertDirectInputErrorPayload(t, expired, "LEASE_NOT_FOUND", "errors.lease_not_found")
}

type directInputParticipantFailureCoordinator struct {
	kv.Coordinator
}

func (directInputParticipantFailureCoordinator) IsLeaseParticipantActive(context.Context, kv.Lease, uuid.UUID) (bool, error) {
	return false, errors.New("injected Direct INPUT participant lookup failure")
}

func setDirectInputControlMode(
	t *testing.T,
	root string,
	actor testDevice,
	bearer string,
	pcID uuid.UUID,
	mode string,
	expectedVersion int64,
) int64 {
	t.Helper()
	response := sameAccountSignedJSONWithHeaders(
		t, root, actor, bearer, http.MethodPut, "/v1/account/pcs/"+pcID.String()+"/control-policy",
		map[string]any{
			"remote_routing_enabled": true,
			"mode":                   mode,
			"local_priority":         true,
			"expected_version":       expectedVersion,
		},
		"direct-input-policy-"+strings.ToLower(mode),
		map[string]string{"X-TermLink-Operation-Reason": "verify Direct INPUT " + mode + " authorization"},
		http.StatusOK,
	)
	return mustNestedResponseInt64(t, response, "policy", "policy_version")
}

func assertDirectInputAuthorizationResponse(t *testing.T, response map[string]any, sender uuid.UUID, mode string, policyVersion int64) {
	t.Helper()
	if fmt.Sprint(response["authorized_sender_device_id"]) != sender.String() ||
		fmt.Sprint(response["control_mode"]) != mode ||
		mustResponseInt64(t, response, "policy_version") != policyVersion {
		t.Fatalf("Direct INPUT authorization response=%v, want sender=%s mode=%s policy_version=%d", response, sender, mode, policyVersion)
	}
	if mustResponseUUID(t, response, "lease_id") == uuid.Nil || mustResponseInt64(t, response, "version") < 1 {
		t.Fatalf("Direct INPUT authorization returned an invalid lease fence: %v", response)
	}
}

func assertDirectInputErrorResponse(t *testing.T, response *http.Response, wantStatus int, code, messageKey string) {
	t.Helper()
	defer response.Body.Close()
	if response.StatusCode != wantStatus {
		t.Fatalf("Direct INPUT status=%d, want %d", response.StatusCode, wantStatus)
	}
	var payload map[string]any
	if err := json.NewDecoder(response.Body).Decode(&payload); err != nil {
		t.Fatalf("decode Direct INPUT error: %v", err)
	}
	assertDirectInputErrorPayload(t, payload, code, messageKey)
}

func assertDirectInputErrorPayload(t *testing.T, payload map[string]any, code, messageKey string) {
	t.Helper()
	errorPayload, ok := payload["error"].(map[string]any)
	if !ok {
		t.Fatalf("Direct INPUT error payload=%v", payload)
	}
	if errorPayload["code"] != code || errorPayload["message_key"] != messageKey {
		t.Fatalf("Direct INPUT error=%v, want code=%s message_key=%s", errorPayload, code, messageKey)
	}
	// API responses carry stable localization keys; Android and Windows resolve
	// those keys to Chinese/English. Do not regress to a server-only raw message.
	if _, exists := errorPayload["message"]; exists {
		t.Fatalf("Direct INPUT error leaked a non-localizable raw message: %v", errorPayload)
	}
}

func mustResponseInt64(t *testing.T, response map[string]any, key string) int64 {
	t.Helper()
	value, ok := response[key].(float64)
	if !ok || value < 1 || value != float64(int64(value)) {
		t.Fatalf("response %q is not a positive integer: %v", key, response[key])
	}
	return int64(value)
}

func mustNestedResponseInt64(t *testing.T, response map[string]any, objectKey, valueKey string) int64 {
	t.Helper()
	object, ok := response[objectKey].(map[string]any)
	if !ok {
		t.Fatalf("response %q is not an object: %v", objectKey, response[objectKey])
	}
	return mustResponseInt64(t, object, valueKey)
}
