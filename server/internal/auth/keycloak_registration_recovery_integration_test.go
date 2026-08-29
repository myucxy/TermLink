package auth

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"io"
	"net/http"
	"net/url"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
)

// TestKeycloakRegistrationRecoveryUserProfile is an opt-in acceptance test
// against a real Keycloak realm. It proves that the registration context can
// persist the two controlled attributes while account/token surfaces cannot
// read or alter them. The test creates one disposable user and deletes it via
// the least-privilege recovery service account.
func TestKeycloakRegistrationRecoveryUserProfile(t *testing.T) {
	issuer := strings.TrimRight(os.Getenv("TERMLINK_TEST_KEYCLOAK_RECOVERY_ISSUER"), "/")
	clientID := os.Getenv("TERMLINK_TEST_KEYCLOAK_REGISTRATION_CLIENT_ID")
	adminClientID := os.Getenv("TERMLINK_TEST_KEYCLOAK_ADMIN_CLIENT_ID")
	adminSecret := os.Getenv("TERMLINK_TEST_KEYCLOAK_ADMIN_CLIENT_SECRET")
	adminBaseURL := strings.TrimRight(os.Getenv("TERMLINK_TEST_KEYCLOAK_ADMIN_BASE_URL"), "/")
	if issuer == "" || clientID == "" || adminClientID == "" || adminSecret == "" {
		t.Skip("real Keycloak registration recovery environment is not configured")
	}
	proofKey := bytes.Repeat([]byte{0x72}, 32)
	transport := http.DefaultTransport.(*http.Transport).Clone()
	if caFile := strings.TrimSpace(os.Getenv("TERMLINK_TEST_KEYCLOAK_CA_FILE")); caFile != "" {
		roots, rootsErr := x509.SystemCertPool()
		if rootsErr != nil || roots == nil {
			roots = x509.NewCertPool()
		}
		certificate, readErr := os.ReadFile(caFile)
		if readErr != nil || !roots.AppendCertsFromPEM(certificate) {
			t.Fatal("real Keycloak acceptance CA could not be loaded")
		}
		if transport.TLSClientConfig != nil {
			transport.TLSClientConfig = transport.TLSClientConfig.Clone()
		} else {
			transport.TLSClientConfig = &tls.Config{}
		}
		transport.TLSClientConfig.RootCAs = roots
	}
	client := &http.Client{
		Transport:     transport,
		Timeout:       10 * time.Second,
		CheckRedirect: func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse },
	}
	registration, err := NewDirectRegistrationService(DirectRegistrationConfig{
		Issuer: issuer, ClientID: clientID, RedirectURI: "dev.termlink:/oauth2redirect",
		RegistrationProofKey: proofKey, HTTPClient: client,
	})
	if err != nil {
		t.Fatal(err)
	}
	admin, err := NewKeycloakAdminClient(KeycloakAdminConfig{
		Issuer: issuer, AdminBaseURL: adminBaseURL, ClientID: adminClientID, ClientSecret: adminSecret,
		RegistrationProofKey: proofKey, HTTPClient: client,
	})
	if err != nil {
		t.Fatal(err)
	}
	attemptID := uuid.New()
	username := "recovery-" + strings.ReplaceAll(uuid.NewString(), "-", "")
	email := username + "@example.test"
	password := "Tl!" + strings.ReplaceAll(uuid.NewString(), "-", "")
	if err := registration.RegisterAttempt(context.Background(), attemptID, DirectRegistrationInput{
		Username: username, Email: email, Password: password, Confirm: password,
		FirstName: "Recovery", LastName: "Acceptance",
	}); err != nil {
		t.Fatalf("real Keycloak registration failed: %v", err)
	}
	lookup, err := admin.LookupRegistrationAttempt(context.Background(), attemptID)
	if err != nil || lookup.Status != RegistrationIdentityExact || lookup.UserID == "" {
		t.Fatalf("real Keycloak lookup=(%s,%v), want EXACT", lookup.Status, err)
	}
	defer func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cleanupCancel()
		if cleanupErr := admin.DeleteRegistrationIdentity(cleanupContext, lookup.UserID); cleanupErr != nil {
			t.Errorf("delete disposable Keycloak identity: %v", cleanupErr)
		}
	}()

	accessToken := keycloakPasswordToken(t, client, issuer, clientID, username, password, "openid profile")
	assertKeycloakUserSurfaceRedacted(t, client, issuer+"/protocol/openid-connect/userinfo", accessToken)
	accountURL := issuer + "/account/"
	assertKeycloakUserSurfaceRedacted(t, client, accountURL, accessToken)
	// The registration-only scope is attached to a public client, so an
	// ordinary caller can ask for its name. Keycloak must not place it in the
	// token scope claim or use it to reveal the controlled attributes after
	// registration.
	requestedRegistrationScopeToken := keycloakPasswordToken(t, client, issuer, clientID, username, password, "openid profile "+RegistrationAttemptScope)
	assertKeycloakUserSurfaceRedacted(t, client, issuer+"/protocol/openid-connect/userinfo", requestedRegistrationScopeToken)
	assertKeycloakUserSurfaceRedacted(t, client, accountURL, requestedRegistrationScopeToken)

	// A normal access token must not authorize the Admin Users API.
	request, err := http.NewRequest(http.MethodPut, admin.usersURL+"/"+url.PathEscape(lookup.UserID), strings.NewReader(`{"attributes":{"`+RegistrationAttemptIDAttribute+`":"tampered"}}`))
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Authorization", "Bearer "+accessToken)
	request.Header.Set("Content-Type", "application/json")
	response, err := client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 4096))
	response.Body.Close()
	if response.StatusCode != http.StatusForbidden {
		t.Fatalf("ordinary token Admin API status=%d, want 403", response.StatusCode)
	}

	// Account Console update attempts also cannot replace the controlled
	// values. Keycloak may either reject the uneditable fields or ignore them;
	// the authoritative postcondition is that the HMAC-proven lookup stays exact.
	accountUpdate := map[string]any{
		"username": username, "email": email, "firstName": "Recovery", "lastName": "Acceptance",
		"attributes": map[string]any{
			RegistrationAttemptIDAttribute:    []string{uuid.NewString()},
			RegistrationAttemptProofAttribute: []string{"tampered"},
		},
	}
	encoded, err := json.Marshal(accountUpdate)
	if err != nil {
		t.Fatal(err)
	}
	request, err = http.NewRequest(http.MethodPost, accountURL, bytes.NewReader(encoded))
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Authorization", "Bearer "+accessToken)
	request.Header.Set("Content-Type", "application/json")
	response, err = client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 4096))
	response.Body.Close()
	if response.StatusCode >= 500 {
		t.Fatalf("account update returned server status %d", response.StatusCode)
	}
	lookupAfter, err := admin.LookupRegistrationAttempt(context.Background(), attemptID)
	if err != nil || lookupAfter.Status != RegistrationIdentityExact || lookupAfter.UserID != lookup.UserID {
		t.Fatalf("ordinary account update changed controlled recovery attributes: status=%s error=%v", lookupAfter.Status, err)
	}
}

func keycloakPasswordToken(t *testing.T, client *http.Client, issuer, clientID, username, password, scope string) string {
	t.Helper()
	form := url.Values{
		"grant_type": {"password"}, "client_id": {clientID},
		"username": {username}, "password": {password}, "scope": {scope},
	}
	request, err := http.NewRequest(http.MethodPost, issuer+"/protocol/openid-connect/token", strings.NewReader(form.Encode()))
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response, err := client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 4096))
		t.Fatalf("password token status=%d", response.StatusCode)
	}
	var payload struct {
		AccessToken string `json:"access_token"`
	}
	if err := json.NewDecoder(io.LimitReader(response.Body, 64<<10)).Decode(&payload); err != nil || payload.AccessToken == "" {
		t.Fatal("password token response was invalid")
	}
	return payload.AccessToken
}

func assertKeycloakUserSurfaceRedacted(t *testing.T, client *http.Client, target, accessToken string) {
	t.Helper()
	request, err := http.NewRequest(http.MethodGet, target, nil)
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Authorization", "Bearer "+accessToken)
	request.Header.Set("Accept", "application/json")
	response, err := client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(io.LimitReader(response.Body, 256<<10))
	if err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != http.StatusOK {
		t.Fatalf("ordinary user surface status=%d", response.StatusCode)
	}
	if bytes.Contains(body, []byte(RegistrationAttemptIDAttribute)) || bytes.Contains(body, []byte(RegistrationAttemptProofAttribute)) {
		t.Fatal("ordinary user surface exposed controlled registration attributes")
	}
}
