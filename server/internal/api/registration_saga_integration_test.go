package api

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
	"github.com/termlink/termlink/server/migrations"
)

func TestRegisterAccountInvitationSagaProviderOutcomes(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for registration saga integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	repository, cleanup := newRegistrationSagaRepository(t, ctx, databaseURL)
	defer cleanup()
	if _, err := repository.Pool.Exec(ctx, `UPDATE registration_policies SET mode='INVITE_REQUIRED' WHERE singleton`); err != nil {
		t.Fatal(err)
	}

	proofKey := bytes.Repeat([]byte{0x51}, 32)
	registrationCodeKey := bytes.Repeat([]byte{0x61}, 32)
	var posted sync.Map
	provider := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/realms/test/protocol/openid-connect/auth":
			_, _ = fmt.Fprintf(w, `<a href="/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s">Create</a>`, url.QueryEscape(r.URL.Query().Get("state")))
		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodGet:
			_, _ = fmt.Fprintf(w, `<form id="kc-register-form" action="https://%s/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s" method="post"><input name="user.attributes.%s"/><input name="user.attributes.%s"/></form>`, r.Host, url.QueryEscape(r.URL.Query().Get("flow_state")), auth.RegistrationAttemptIDAttribute, auth.RegistrationAttemptProofAttribute)
		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodPost:
			if err := r.ParseForm(); err != nil {
				t.Error(err)
				w.WriteHeader(http.StatusBadRequest)
				return
			}
			username := r.PostForm.Get("username")
			posted.Store(username, r.PostForm)
			switch username {
			case "success-user":
				w.Header().Set("Location", "dev.termlink:/oauth2redirect?code=created&state="+url.QueryEscape(r.URL.Query().Get("flow_state")))
				w.WriteHeader(http.StatusFound)
			case "rejected-user":
				w.WriteHeader(http.StatusConflict)
				_, _ = w.Write([]byte("provider-secret alice@example.test"))
			case "unknown-user":
				time.Sleep(250 * time.Millisecond)
				w.WriteHeader(http.StatusServiceUnavailable)
			default:
				http.NotFound(w, r)
			}
		default:
			http.NotFound(w, r)
		}
	}))
	defer provider.Close()
	providerHTTP := provider.Client()
	providerHTTP.Timeout = 100 * time.Millisecond
	providerHTTP.CheckRedirect = func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }
	registration, err := auth.NewDirectRegistrationService(auth.DirectRegistrationConfig{
		Issuer: provider.URL + "/realms/test", ClientID: "termlink-android",
		RedirectURI: "dev.termlink:/oauth2redirect", RegistrationProofKey: proofKey,
		HTTPClient: providerHTTP,
	})
	if err != nil {
		t.Fatal(err)
	}
	service := (&API{Store: repository, Registration: registration, RegistrationCodeKey: registrationCodeKey}).Handler()

	tests := []struct {
		name          string
		username      string
		email         string
		code          string
		wantHTTP      int
		wantState     string
		wantUsedCount int
		wantStatus    string
	}{
		{name: "success confirms provider", username: "success-user", email: "success@example.test", code: "TLI-ABCDEFG-HJKLMNP-QRSTUVW", wantHTTP: http.StatusCreated, wantState: "PROVIDER_ACCEPTED", wantUsedCount: 1, wantStatus: "PROVIDER_ACCEPTED"},
		{name: "explicit rejection releases", username: "rejected-user", email: "rejected@example.test", code: "TLI-BCDEFGH-JKLMNPQ-RSTUVWX", wantHTTP: http.StatusUnprocessableEntity, wantState: "RELEASED", wantUsedCount: 0},
		{name: "unknown outcome remains reserved", username: "unknown-user", email: "unknown@example.test", code: "TLI-CDEFGHJ-KLMNPQR-STUVWXY", wantHTTP: http.StatusAccepted, wantState: "RESERVED", wantUsedCount: 1, wantStatus: "PENDING_RECONCILIATION"},
	}
	adminID := uuid.New()
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,is_admin,role) VALUES($1,$2,true,'SUPER_ADMIN')`, adminID, registrationSagaDigest(0x11)); err != nil {
		t.Fatal(err)
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			codeID := seedRegistrationSagaInvitation(t, ctx, repository.Pool, adminID, registrationCodeKey, test.code)
			body, _ := json.Marshal(registerAccountRequest{
				Username: test.username, Nickname: test.username, Email: test.email,
				Password: "correct horse battery staple", Confirm: "correct horse battery staple",
				InvitationCode: test.code,
			})
			request := httptest.NewRequest(http.MethodPost, "/v1/auth/register", bytes.NewReader(body))
			request.Header.Set("Content-Type", "application/json")
			response := httptest.NewRecorder()
			service.ServeHTTP(response, request)
			if response.Code != test.wantHTTP {
				t.Fatalf("status=%d body=%s, want %d", response.Code, response.Body.String(), test.wantHTTP)
			}
			if strings.Contains(response.Body.String(), "provider-secret") || strings.Contains(response.Body.String(), "alice@example.test") {
				t.Fatalf("provider details escaped to public response: %s", response.Body.String())
			}
			var payload map[string]any
			if err := json.Unmarshal(response.Body.Bytes(), &payload); err != nil {
				t.Fatal(err)
			}
			if test.wantStatus != "" {
				status := payload["invitation_link_status"]
				if test.wantStatus == "PENDING_RECONCILIATION" {
					if payload["status"] != test.wantStatus || payload["message_key"] != test.wantStatus {
						t.Fatalf("pending response lacks stable status/message: %v", payload)
					}
				}
				if status != test.wantStatus {
					t.Fatalf("invitation_link_status=%v, want %s", status, test.wantStatus)
				}
			}

			var attemptID uuid.UUID
			var state string
			if err := repository.Pool.QueryRow(ctx, `SELECT registration_attempt_id,state FROM invitation_redemptions WHERE code_id=$1`, codeID).Scan(&attemptID, &state); err != nil {
				t.Fatal(err)
			}
			if state != test.wantState {
				t.Fatalf("redemption state=%s, want %s", state, test.wantState)
			}
			var usedCount int
			if err := repository.Pool.QueryRow(ctx, `SELECT used_count FROM invitation_codes WHERE id=$1`, codeID).Scan(&usedCount); err != nil || usedCount != test.wantUsedCount {
				t.Fatalf("used_count=(%d,%v), want %d", usedCount, err, test.wantUsedCount)
			}
			postedValue, ok := posted.Load(test.username)
			if !ok {
				t.Fatal("registration was not submitted to provider")
			}
			form := postedValue.(url.Values)
			if form.Get("user.attributes."+auth.RegistrationAttemptIDAttribute) != attemptID.String() ||
				form.Get("user.attributes."+auth.RegistrationAttemptProofAttribute) == "" {
				t.Fatalf("provider form lacks controlled recovery attributes: %v", form)
			}
		})
	}
}

func newRegistrationSagaRepository(t *testing.T, ctx context.Context, databaseURL string) (*store.Postgres, func()) {
	t.Helper()
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	schema := "api_registration_saga_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err := admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		admin.Close()
		t.Fatal(err)
	}
	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		admin.Close()
		t.Fatal(err)
	}
	config.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		admin.Close()
		t.Fatal(err)
	}
	entries, err := migrations.Files.ReadDir(".")
	if err != nil {
		pool.Close()
		admin.Close()
		t.Fatal(err)
	}
	for version := 1; version <= int(migrations.LatestVersion); version++ {
		prefix := fmt.Sprintf("%06d_", version)
		name := ""
		for _, entry := range entries {
			if strings.HasPrefix(entry.Name(), prefix) && strings.HasSuffix(entry.Name(), ".up.sql") {
				name = entry.Name()
				break
			}
		}
		contents, readErr := migrations.Files.ReadFile(name)
		if readErr != nil {
			pool.Close()
			admin.Close()
			t.Fatal(readErr)
		}
		if _, execErr := pool.Exec(ctx, string(contents)); execErr != nil {
			pool.Close()
			admin.Close()
			t.Fatalf("apply %s: %v", name, execErr)
		}
	}
	cleanup := func() {
		pool.Close()
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cleanupCancel()
		if _, cleanupErr := admin.Exec(cleanupContext, "DROP SCHEMA "+quotedSchema+" CASCADE"); cleanupErr != nil {
			t.Errorf("drop registration saga schema: %v", cleanupErr)
		}
		admin.Close()
	}
	return &store.Postgres{Pool: pool}, cleanup
}

func seedRegistrationSagaInvitation(t *testing.T, ctx context.Context, pool *pgxpool.Pool, adminID uuid.UUID, registrationCodeKey []byte, displayCode string) uuid.UUID {
	t.Helper()
	batchID, codeID := uuid.New(), uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO invitation_batches(id,name,status,default_max_uses,created_by) VALUES($1,$2,'ACTIVE',1,$3)`, batchID, "registration-saga-"+codeID.String(), adminID); err != nil {
		t.Fatal(err)
	}
	normalized := normalizeRegistrationCode(displayCode)
	digest := keyedRegistrationDigest(registrationCodeKey, "invitation-code", normalized)
	if _, err := pool.Exec(ctx, `INSERT INTO invitation_codes(id,batch_id,code_hash,code_prefix,max_uses,used_count) VALUES($1,$2,$3,$4,1,0)`, codeID, batchID, digest[:], normalized[:8]); err != nil {
		t.Fatal(err)
	}
	return codeID
}

func registrationSagaDigest(value byte) []byte {
	result := make([]byte, 32)
	for index := range result {
		result[index] = value
	}
	return result
}
