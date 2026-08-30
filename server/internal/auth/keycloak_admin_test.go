package auth

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/uuid"
)

var (
	keycloakTestAttempt  = uuid.MustParse("10000000-0000-4000-8000-000000000001")
	keycloakTestUserID   = "20000000-0000-4000-8000-000000000002"
	keycloakTestProofKey = bytes.Repeat([]byte{0x6b}, 32)
)

func keycloakUserJSON(t *testing.T, id string, attempts, proofs []string) map[string]any {
	t.Helper()
	return map[string]any{
		"id":       id,
		"username": "must-not-be-needed",
		"email":    "must-not-be-needed@example.test",
		"attributes": map[string][]string{
			RegistrationAttemptIDAttribute:    attempts,
			RegistrationAttemptProofAttribute: proofs,
		},
	}
}

func validKeycloakUserJSON(t *testing.T, id string) map[string]any {
	t.Helper()
	proof, err := registrationAttemptProof(keycloakTestProofKey, keycloakTestAttempt)
	if err != nil {
		t.Fatal(err)
	}
	return keycloakUserJSON(t, id, []string{keycloakTestAttempt.String()}, []string{proof})
}

func newKeycloakAdminTestClient(t *testing.T, handler http.Handler, timeout time.Duration) (*KeycloakAdminClient, *httptest.Server) {
	t.Helper()
	server := httptest.NewTLSServer(handler)
	if timeout == 0 {
		timeout = time.Second
	}
	client, err := NewKeycloakAdminClient(KeycloakAdminConfig{
		Issuer: server.URL + "/auth/realms/termlink", ClientID: "registration-recovery",
		ClientSecret: "provider-client-secret", RegistrationProofKey: keycloakTestProofKey,
		RequestTimeout: timeout, HTTPClient: server.Client(),
	})
	if err != nil {
		server.Close()
		t.Fatalf("NewKeycloakAdminClient() error = %v", err)
	}
	return client, server
}

func tokenResponse(w http.ResponseWriter, token string) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"access_token": token, "token_type": "Bearer", "expires_in": 300,
	})
}

func TestKeycloakAdminCreatesAccountsThroughBoundedAdminEndpoint(t *testing.T) {
	tests := []struct {
		name          string
		emailVerified bool
		attemptID     uuid.UUID
		register      func(*KeycloakAdminClient, DirectRegistrationInput) error
	}{
		{
			name: "ordinary registration",
			register: func(client *KeycloakAdminClient, input DirectRegistrationInput) error {
				return client.Register(context.Background(), input)
			},
		},
		{
			name: "verified email registration", emailVerified: true,
			register: func(client *KeycloakAdminClient, input DirectRegistrationInput) error {
				return client.RegisterVerifiedEmail(context.Background(), input)
			},
		},
		{
			name: "invitation registration", attemptID: keycloakTestAttempt,
			register: func(client *KeycloakAdminClient, input DirectRegistrationInput) error {
				return client.RegisterAttempt(context.Background(), keycloakTestAttempt, input)
			},
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var payload map[string]any
			client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch r.URL.Path {
				case "/auth/realms/termlink/protocol/openid-connect/token":
					tokenResponse(w, "registration-token")
				case "/auth/admin/realms/termlink/users":
					if r.Method != http.MethodPost || r.Header.Get("Authorization") != "Bearer registration-token" {
						t.Fatalf("unexpected account creation request: %s authorization=%q", r.Method, r.Header.Get("Authorization"))
					}
					if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
						t.Fatal(err)
					}
					w.WriteHeader(http.StatusCreated)
				default:
					http.NotFound(w, r)
				}
			}), 0)
			defer server.Close()

			input := DirectRegistrationInput{
				Username: "new-user", Nickname: "新用户", Email: "new-user@example.test",
				Password: "correct horse battery staple", Confirm: "correct horse battery staple",
			}
			if err := tt.register(client, input); err != nil {
				t.Fatalf("registration error = %v", err)
			}
			if payload["username"] != input.Username || payload["email"] != input.Email ||
				payload["emailVerified"] != tt.emailVerified || payload["enabled"] != true {
				t.Fatalf("unexpected account payload: %#v", payload)
			}
			attributes, ok := payload["attributes"].(map[string]any)
			if !ok || firstJSONText(attributes["nickname"]) != input.Nickname {
				t.Fatalf("nickname attributes = %#v", payload["attributes"])
			}
			if tt.attemptID == uuid.Nil {
				if _, exists := attributes[RegistrationAttemptIDAttribute]; exists {
					t.Fatal("ordinary account unexpectedly contains an invitation correlation")
				}
			} else {
				proof, err := registrationAttemptProof(keycloakTestProofKey, tt.attemptID)
				if err != nil {
					t.Fatal(err)
				}
				if firstJSONText(attributes[RegistrationAttemptIDAttribute]) != tt.attemptID.String() ||
					firstJSONText(attributes[RegistrationAttemptProofAttribute]) != proof {
					t.Fatalf("invitation recovery attributes = %#v", attributes)
				}
			}
			credentials, ok := payload["credentials"].([]any)
			if !ok || len(credentials) != 1 {
				t.Fatalf("password credentials = %#v", payload["credentials"])
			}
			credential, ok := credentials[0].(map[string]any)
			if !ok || credential["type"] != "password" || credential["value"] != input.Password || credential["temporary"] != false {
				t.Fatalf("password credential = %#v", credentials[0])
			}
		})
	}
}

func TestKeycloakAdminRegistrationRejectsNilInvitationAttempt(t *testing.T) {
	client, server := newKeycloakAdminTestClient(t, http.NotFoundHandler(), 0)
	defer server.Close()
	input := DirectRegistrationInput{
		Username: "new-user", Nickname: "New user", Email: "new-user@example.test",
		Password: "correct horse battery staple", Confirm: "correct horse battery staple",
	}
	if err := client.RegisterAttempt(context.Background(), uuid.Nil, input); !errors.Is(err, ErrKeycloakAdminRejected) {
		t.Fatalf("nil registration attempt error = %v", err)
	}
}

func TestKeycloakAdminRegistrationDistinguishesUnavailableFromUnknownOutcome(t *testing.T) {
	input := DirectRegistrationInput{
		Username: "new-user", Nickname: "New user", Email: "new-user@example.test",
		Password: "correct horse battery staple", Confirm: "correct horse battery staple",
	}
	t.Run("token unavailable before create", func(t *testing.T) {
		client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			w.WriteHeader(http.StatusServiceUnavailable)
		}), 0)
		defer server.Close()
		if err := client.Register(context.Background(), input); !errors.Is(err, ErrKeycloakAdminUnavailable) {
			t.Fatalf("pre-create identity failure = %v", err)
		}
	})
	t.Run("transport timeout after create starts", func(t *testing.T) {
		client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if strings.HasSuffix(r.URL.Path, "/token") {
				tokenResponse(w, "registration-token")
				return
			}
			time.Sleep(250 * time.Millisecond)
			w.WriteHeader(http.StatusCreated)
		}), 100*time.Millisecond)
		defer server.Close()
		if err := client.Register(context.Background(), input); !errors.Is(err, ErrRegistrationOutcomeUnknown) {
			t.Fatalf("post-create transport failure = %v", err)
		}
	})
}

func TestKeycloakAdminReadsOnlyConfiguredOTPStatusForExactSubject(t *testing.T) {
	for _, configured := range []bool{false, true} {
		t.Run(fmt.Sprintf("configured-%t", configured), func(t *testing.T) {
			client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch r.URL.Path {
				case "/auth/realms/termlink/protocol/openid-connect/token":
					tokenResponse(w, "mfa-status-token")
				case "/auth/admin/realms/termlink/users/" + keycloakTestUserID:
					if r.Header.Get("Authorization") != "Bearer mfa-status-token" {
						t.Error("MFA status lookup omitted bearer authorization")
					}
					_ = json.NewEncoder(w).Encode(map[string]any{
						"id": keycloakTestUserID, "totp": configured, "username": "must-not-be-returned",
					})
				default:
					http.NotFound(w, r)
				}
			}), 0)
			defer server.Close()

			actual, err := client.HasConfiguredOTP(context.Background(), keycloakTestUserID)
			if err != nil || actual != configured {
				t.Fatalf("HasConfiguredOTP()=%t error=%v, want %t", actual, err, configured)
			}
		})
	}
	client, server := newKeycloakAdminTestClient(t, http.NotFoundHandler(), 0)
	defer server.Close()
	if _, err := client.HasConfiguredOTP(context.Background(), "../other-user"); !errors.Is(err, ErrKeycloakAdminRejected) {
		t.Fatalf("unsafe MFA subject error=%v, want rejected", err)
	}
}

func TestKeycloakAdminLookupClassifiesOnlyUniqueProvenIdentityAsExact(t *testing.T) {
	proof, err := registrationAttemptProof(keycloakTestProofKey, keycloakTestAttempt)
	if err != nil {
		t.Fatal(err)
	}
	tests := []struct {
		name   string
		users  []map[string]any
		status RegistrationIdentityLookupStatus
	}{
		{name: "absent", users: []map[string]any{}, status: RegistrationIdentityAbsent},
		{name: "unique exact", users: []map[string]any{validKeycloakUserJSON(t, keycloakTestUserID)}, status: RegistrationIdentityExact},
		{name: "two matches", users: []map[string]any{validKeycloakUserJSON(t, keycloakTestUserID), validKeycloakUserJSON(t, "30000000-0000-4000-8000-000000000003")}, status: RegistrationIdentityAmbiguous},
		{name: "truncated boundary", users: []map[string]any{validKeycloakUserJSON(t, keycloakTestUserID), validKeycloakUserJSON(t, "30000000-0000-4000-8000-000000000003"), validKeycloakUserJSON(t, "40000000-0000-4000-8000-000000000004")}, status: RegistrationIdentityAmbiguous},
		{name: "wrong proof", users: []map[string]any{keycloakUserJSON(t, keycloakTestUserID, []string{keycloakTestAttempt.String()}, []string{"invalid-proof"})}, status: RegistrationIdentityAmbiguous},
		{name: "duplicate attempt attribute", users: []map[string]any{keycloakUserJSON(t, keycloakTestUserID, []string{keycloakTestAttempt.String(), keycloakTestAttempt.String()}, []string{proof})}, status: RegistrationIdentityAmbiguous},
		{name: "duplicate proof attribute", users: []map[string]any{keycloakUserJSON(t, keycloakTestUserID, []string{keycloakTestAttempt.String()}, []string{proof, proof})}, status: RegistrationIdentityAmbiguous},
		{name: "non canonical identity ID", users: []map[string]any{keycloakUserJSON(t, "{"+keycloakTestUserID+"}", []string{keycloakTestAttempt.String()}, []string{proof})}, status: RegistrationIdentityAmbiguous},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var query url.Values
			client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch r.URL.Path {
				case "/auth/realms/termlink/protocol/openid-connect/token":
					if err := r.ParseForm(); err != nil {
						t.Error(err)
					}
					if r.PostForm.Get("grant_type") != "client_credentials" || r.PostForm.Get("client_id") != "registration-recovery" || r.PostForm.Get("client_secret") != "provider-client-secret" {
						t.Errorf("unexpected token form keys")
					}
					tokenResponse(w, "access-token")
				case "/auth/admin/realms/termlink/users":
					query = r.URL.Query()
					if r.Header.Get("Authorization") != "Bearer access-token" {
						t.Error("admin request lacks bearer token")
					}
					w.Header().Set("Content-Type", "application/json")
					_ = json.NewEncoder(w).Encode(tt.users)
				default:
					http.NotFound(w, r)
				}
			}), 0)
			defer server.Close()

			lookup, err := client.LookupRegistrationAttempt(context.Background(), keycloakTestAttempt)
			if err != nil || lookup.Status != tt.status {
				t.Fatalf("LookupRegistrationAttempt() = %+v, %v; want %s", lookup, err, tt.status)
			}
			if tt.status == RegistrationIdentityExact && lookup.UserID != keycloakTestUserID {
				t.Fatalf("exact identity = %q", lookup.UserID)
			}
			if tt.status != RegistrationIdentityExact && lookup.UserID != "" {
				t.Fatalf("non-exact lookup exposed identity ID %q", lookup.UserID)
			}
			if query.Get("q") != RegistrationAttemptIDAttribute+":"+keycloakTestAttempt.String() ||
				query.Get("first") != "0" || query.Get("max") != "3" || query.Get("briefRepresentation") != "false" {
				t.Fatalf("unsafe or unbounded users query: %v", query)
			}
		})
	}
}

func TestKeycloakAdminLookupRejectsOversizeAndTrailingJSON(t *testing.T) {
	tests := []struct {
		name string
		body func() string
	}{
		{name: "oversize", body: func() string { return `[{"id":"` + strings.Repeat("a", maxKeycloakAdminBody) + `"}]` }},
		{name: "trailing JSON", body: func() string { return `[] {"secret":"must-not-leak"}` }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if strings.HasSuffix(r.URL.Path, "/token") {
					tokenResponse(w, "access-token")
					return
				}
				w.Header().Set("Content-Type", "application/json")
				_, _ = w.Write([]byte(tt.body()))
			}), 0)
			defer server.Close()
			_, err := client.LookupRegistrationAttempt(context.Background(), keycloakTestAttempt)
			if !errors.Is(err, ErrKeycloakAdminUnavailable) || strings.Contains(err.Error(), "must-not-leak") {
				t.Fatalf("LookupRegistrationAttempt() error = %v", err)
			}
		})
	}
}

func TestKeycloakAdminRefreshesTokenOnceAfterUnauthorized(t *testing.T) {
	var tokenCalls atomic.Int32
	var userCalls atomic.Int32
	client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/auth/realms/termlink/protocol/openid-connect/token":
			call := tokenCalls.Add(1)
			tokenResponse(w, fmt.Sprintf("access-token-%d", call))
		case "/auth/admin/realms/termlink/users":
			userCalls.Add(1)
			if r.Header.Get("Authorization") == "Bearer access-token-1" {
				w.WriteHeader(http.StatusUnauthorized)
				return
			}
			_ = json.NewEncoder(w).Encode([]map[string]any{validKeycloakUserJSON(t, keycloakTestUserID)})
		default:
			http.NotFound(w, r)
		}
	}), 0)
	defer server.Close()

	lookup, err := client.LookupRegistrationAttempt(context.Background(), keycloakTestAttempt)
	if err != nil || lookup.Status != RegistrationIdentityExact || tokenCalls.Load() != 2 || userCalls.Load() != 2 {
		t.Fatalf("lookup=%+v err=%v token_calls=%d user_calls=%d", lookup, err, tokenCalls.Load(), userCalls.Load())
	}
}

func TestKeycloakAdminErrorsAreStatusOnlyAndTimeoutBounded(t *testing.T) {
	t.Run("redirect not followed", func(t *testing.T) {
		var redirectedCalls atomic.Int32
		redirectTarget := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			redirectedCalls.Add(1)
			w.WriteHeader(http.StatusOK)
		}))
		defer redirectTarget.Close()
		client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if strings.HasSuffix(r.URL.Path, "/token") {
				tokenResponse(w, "access-token")
				return
			}
			http.Redirect(w, r, redirectTarget.URL, http.StatusTemporaryRedirect)
		}), 0)
		defer server.Close()
		_, err := client.LookupRegistrationAttempt(context.Background(), keycloakTestAttempt)
		if !errors.Is(err, ErrKeycloakAdminUnavailable) || redirectedCalls.Load() != 0 {
			t.Fatalf("redirect result: error=%v target_calls=%d", err, redirectedCalls.Load())
		}
	})

	t.Run("forbidden body redacted", func(t *testing.T) {
		client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if strings.HasSuffix(r.URL.Path, "/token") {
				tokenResponse(w, "access-token")
				return
			}
			w.WriteHeader(http.StatusForbidden)
			_, _ = w.Write([]byte("alice@example.test bearer-secret " + keycloakTestUserID))
		}), 0)
		defer server.Close()
		_, err := client.LookupRegistrationAttempt(context.Background(), keycloakTestAttempt)
		if !errors.Is(err, ErrKeycloakAdminRejected) {
			t.Fatalf("lookup error = %v", err)
		}
		for _, forbidden := range []string{"alice@example.test", "bearer-secret", keycloakTestUserID} {
			if strings.Contains(err.Error(), forbidden) {
				t.Fatalf("error exposed %q: %v", forbidden, err)
			}
		}
	})

	t.Run("timeout", func(t *testing.T) {
		client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if strings.HasSuffix(r.URL.Path, "/token") {
				tokenResponse(w, "access-token")
				return
			}
			<-r.Context().Done()
		}), 100*time.Millisecond)
		defer server.Close()
		started := time.Now()
		_, err := client.LookupRegistrationAttempt(context.Background(), keycloakTestAttempt)
		if !errors.Is(err, ErrKeycloakAdminUnavailable) || time.Since(started) > time.Second {
			t.Fatalf("timeout result: error=%v elapsed=%v", err, time.Since(started))
		}
	})
}

func TestKeycloakAdminDeleteIsIdempotentAndValidatesIdentityID(t *testing.T) {
	for _, status := range []int{http.StatusNoContent, http.StatusNotFound} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			var deleteCalls int
			client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if strings.HasSuffix(r.URL.Path, "/token") {
					tokenResponse(w, "access-token")
					return
				}
				if r.Method != http.MethodDelete || r.URL.Path != "/auth/admin/realms/termlink/users/"+keycloakTestUserID {
					http.NotFound(w, r)
					return
				}
				deleteCalls++
				w.WriteHeader(status)
			}), 0)
			defer server.Close()
			if err := client.DeleteRegistrationIdentity(context.Background(), keycloakTestUserID); err != nil || deleteCalls != 1 {
				t.Fatalf("DeleteRegistrationIdentity() error=%v calls=%d", err, deleteCalls)
			}
		})
	}

	client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		t.Fatal("invalid identity ID reached Keycloak")
	}), 0)
	defer server.Close()
	if err := client.DeleteRegistrationIdentity(context.Background(), "not-a-uuid"); !errors.Is(err, ErrKeycloakAdminRejected) {
		t.Fatalf("invalid identity ID error = %v", err)
	}
}

func TestNewKeycloakAdminClientRejectsUnsafeOrIncompleteConfiguration(t *testing.T) {
	valid := KeycloakAdminConfig{
		Issuer: "https://identity.example.test/realms/termlink", ClientID: "recovery",
		ClientSecret: "secret", RegistrationProofKey: keycloakTestProofKey,
	}
	tests := []KeycloakAdminConfig{
		{},
		{Issuer: "http://identity.example.test/realms/termlink", ClientID: valid.ClientID, ClientSecret: valid.ClientSecret, RegistrationProofKey: valid.RegistrationProofKey},
		{Issuer: valid.Issuer, ClientSecret: valid.ClientSecret, RegistrationProofKey: valid.RegistrationProofKey},
		{Issuer: valid.Issuer, ClientID: valid.ClientID, RegistrationProofKey: valid.RegistrationProofKey},
		{Issuer: valid.Issuer, ClientID: valid.ClientID, ClientSecret: valid.ClientSecret, RegistrationProofKey: bytes.Repeat([]byte{1}, 31)},
		{Issuer: valid.Issuer, AdminBaseURL: "file:///var/run/keycloak", ClientID: valid.ClientID, ClientSecret: valid.ClientSecret, RegistrationProofKey: valid.RegistrationProofKey},
		{Issuer: valid.Issuer, AdminBaseURL: "https://user@identity.example.test", ClientID: valid.ClientID, ClientSecret: valid.ClientSecret, RegistrationProofKey: valid.RegistrationProofKey},
		{Issuer: valid.Issuer, AdminBaseURL: "https://identity.example.test?target=other", ClientID: valid.ClientID, ClientSecret: valid.ClientSecret, RegistrationProofKey: valid.RegistrationProofKey},
		{Issuer: valid.Issuer, AdminBaseURL: "https://identity.example.test/base/../other", ClientID: valid.ClientID, ClientSecret: valid.ClientSecret, RegistrationProofKey: valid.RegistrationProofKey},
		{Issuer: valid.Issuer, AdminBaseURL: "http://identity.example.test:8080", ClientID: valid.ClientID, ClientSecret: valid.ClientSecret, RegistrationProofKey: valid.RegistrationProofKey},
		{Issuer: valid.Issuer, AdminBaseURL: "http://keycloak", ClientID: valid.ClientID, ClientSecret: valid.ClientSecret, RegistrationProofKey: valid.RegistrationProofKey},
	}
	for index, config := range tests {
		if _, err := NewKeycloakAdminClient(config); err == nil {
			t.Fatalf("unsafe config %d was accepted", index)
		}
	}
}

func TestKeycloakAdminUsesExplicitPrivateAdminBase(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/auth/realms/termlink/protocol/openid-connect/token":
			tokenResponse(w, "access-token")
		case "/management/admin/realms/termlink/users":
			_ = json.NewEncoder(w).Encode([]map[string]any{validKeycloakUserJSON(t, keycloakTestUserID)})
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	client, err := NewKeycloakAdminClient(KeycloakAdminConfig{
		Issuer: server.URL + "/auth/realms/termlink", AdminBaseURL: server.URL + "/management",
		ClientID: "recovery", ClientSecret: "secret", RegistrationProofKey: keycloakTestProofKey,
		HTTPClient: server.Client(),
	})
	if err != nil {
		t.Fatal(err)
	}
	lookup, err := client.LookupRegistrationAttempt(context.Background(), keycloakTestAttempt)
	if err != nil || lookup.Status != RegistrationIdentityExact || lookup.UserID != keycloakTestUserID {
		t.Fatalf("explicit admin base lookup=%+v error=%v", lookup, err)
	}
	private, err := NewKeycloakAdminClient(KeycloakAdminConfig{
		Issuer: "https://identity.example.test/realms/termlink", AdminBaseURL: "http://keycloak:8080",
		ClientID: "recovery", ClientSecret: "secret", RegistrationProofKey: keycloakTestProofKey,
	})
	if err != nil || private.usersURL != "http://keycloak:8080/admin/realms/termlink/users" {
		t.Fatalf("private Docker admin base was not accepted: url=%q error=%v", private.usersURL, err)
	}
}

func TestKeycloakAdminProfileUpdatesPreserveUnrelatedIdentityFields(t *testing.T) {
	tests := []struct {
		name   string
		update func(*KeycloakAdminClient) error
		assert func(*testing.T, map[string]any)
	}{
		{
			name: "nickname",
			update: func(client *KeycloakAdminClient) error {
				return client.UpdateNickname(context.Background(), keycloakTestUserID, "  清风用户  ")
			},
			assert: func(t *testing.T, payload map[string]any) {
				attributes, ok := payload["attributes"].(map[string]any)
				if !ok {
					t.Fatalf("nickname payload omitted attributes: %#v", payload)
				}
				if got := firstJSONText(attributes["nickname"]); got != "清风用户" {
					t.Fatalf("nickname = %q", got)
				}
				if got := firstJSONText(attributes["existing"]); got != "preserved" {
					t.Fatalf("existing attribute = %q", got)
				}
				if payload["email"] != "before@example.test" || payload["emailVerified"] != false {
					t.Fatalf("nickname update changed e-mail fields: %#v", payload)
				}
			},
		},
		{
			name: "verified email",
			update: func(client *KeycloakAdminClient) error {
				return client.UpdateVerifiedEmail(context.Background(), keycloakTestUserID, "new@example.test")
			},
			assert: func(t *testing.T, payload map[string]any) {
				if payload["email"] != "new@example.test" || payload["emailVerified"] != true {
					t.Fatalf("verified email payload = %#v", payload)
				}
				attributes, ok := payload["attributes"].(map[string]any)
				if !ok || firstJSONText(attributes["nickname"]) != "Original" || firstJSONText(attributes["existing"]) != "preserved" {
					t.Fatalf("email update changed attributes: %#v", payload)
				}
			},
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var updated map[string]any
			client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch {
				case strings.HasSuffix(r.URL.Path, "/token"):
					tokenResponse(w, "profile-access-token")
				case r.URL.Path == "/auth/admin/realms/termlink/users/"+keycloakTestUserID && r.Method == http.MethodGet:
					_ = json.NewEncoder(w).Encode(map[string]any{
						"id": keycloakTestUserID, "username": "immutable-user", "email": "before@example.test",
						"emailVerified": false, "enabled": true, "requiredActions": []string{"UPDATE_PASSWORD"},
						"attributes": map[string][]string{"nickname": {"Original"}, "existing": {"preserved"}},
					})
				case r.URL.Path == "/auth/admin/realms/termlink/users/"+keycloakTestUserID && r.Method == http.MethodPut:
					if r.Header.Get("Authorization") != "Bearer profile-access-token" {
						t.Error("profile update omitted bearer authorization")
					}
					if err := json.NewDecoder(r.Body).Decode(&updated); err != nil {
						t.Error(err)
					}
					w.WriteHeader(http.StatusNoContent)
				default:
					http.NotFound(w, r)
				}
			}), 0)
			defer server.Close()

			if err := tt.update(client); err != nil {
				t.Fatalf("profile update error = %v", err)
			}
			if updated["username"] != "immutable-user" || updated["enabled"] != true {
				t.Fatalf("profile update changed immutable or status fields: %#v", updated)
			}
			requiredActions, ok := updated["requiredActions"].([]any)
			if !ok || len(requiredActions) != 1 || requiredActions[0] != "UPDATE_PASSWORD" {
				t.Fatalf("profile update changed required actions: %#v", updated)
			}
			tt.assert(t, updated)
		})
	}
}

func TestKeycloakAdminDuplicateEmailIsReportedWithoutEnumerationDetail(t *testing.T) {
	client, server := newKeycloakAdminTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case strings.HasSuffix(r.URL.Path, "/token"):
			tokenResponse(w, "access-token")
		case r.Method == http.MethodGet:
			_ = json.NewEncoder(w).Encode(map[string]any{
				"id": keycloakTestUserID, "username": "immutable-user", "email": "before@example.test",
			})
		case r.Method == http.MethodPut:
			w.WriteHeader(http.StatusConflict)
			_, _ = w.Write([]byte(`{"errorMessage":"new@example.test already belongs to private-user"}`))
		default:
			http.NotFound(w, r)
		}
	}), 0)
	defer server.Close()

	err := client.UpdateVerifiedEmail(context.Background(), keycloakTestUserID, "new@example.test")
	if !errors.Is(err, ErrKeycloakAdminRejected) {
		t.Fatalf("duplicate e-mail error = %v", err)
	}
	for _, forbidden := range []string{"new@example.test", "private-user", keycloakTestUserID} {
		if strings.Contains(err.Error(), forbidden) {
			t.Fatalf("duplicate e-mail error exposed %q: %v", forbidden, err)
		}
	}
}

func firstJSONText(value any) string {
	values, ok := value.([]any)
	if !ok || len(values) == 0 {
		return ""
	}
	result, _ := values[0].(string)
	return result
}
