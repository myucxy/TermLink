package main

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/api"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/config"
	"github.com/termlink/termlink/server/internal/history"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/lanreplay"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/observability"
	"github.com/termlink/termlink/server/internal/outbox"
	"github.com/termlink/termlink/server/internal/phase1test"
	"github.com/termlink/termlink/server/internal/realtime"
	serverruntime "github.com/termlink/termlink/server/internal/runtime"
	"github.com/termlink/termlink/server/internal/store"
	"github.com/termlink/termlink/server/internal/webportal"
)

func main() {
	command := "lan-test"
	if len(os.Args) > 1 {
		command = os.Args[1]
	}
	var err error
	switch command {
	case "serve":
		err = runProduction()
	case "migrate":
		err = runMigrate()
	case "healthcheck":
		os.Exit(runHealthcheck())
	case "lan-test":
		err = runLANTest()
	default:
		err = fmt.Errorf("unknown command %q (use serve, migrate, healthcheck, or lan-test)", command)
	}
	if err != nil {
		slog.Error("server stopped", "error", err)
		os.Exit(1)
	}
}

func optionalHistoryStore(store *history.Store) api.HistoryObjectStore {
	if store == nil {
		return nil
	}
	return store
}

func runProduction() error {
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	cfg, err := config.Load()
	if err != nil {
		return fmt.Errorf("load production config: %w", err)
	}
	repository, err := store.Open(ctx, cfg.DatabaseURL.Value())
	if err != nil {
		return err
	}
	defer repository.Close()
	// First-version product policy: signing in is sufficient to use the
	// account's own devices. Legacy trust/grant tables remain for compatibility
	// and future policy work, but are not a user-facing enrollment step.
	repository.AutoTrustDevices = true
	if err := repository.VerifySchema(ctx); err != nil {
		return err
	}
	valkey, err := kv.Open(ctx, cfg.RedisURL.Value())
	if err != nil {
		return err
	}
	defer valkey.Close()
	verifier, err := auth.NewOIDCVerifier(ctx, cfg.OIDCIssuer, cfg.OIDCClientID, cfg.OIDCAllowedParties)
	if err != nil {
		return err
	}
	keyMaster := cfg.TokenSigningKey.Value()
	subjectKey := deriveServerKey(keyMaster, "oidc-subject-hash")
	tokenKey := deriveServerKey(keyMaster, "capability-token-signing")
	cookieKey := deriveServerKey(keyMaster, "web-cookie-master")
	authentication := auth.Middleware{Tokens: verifier, Devices: repository, Nonces: valkey, SubjectKey: subjectKey}
	registration, err := auth.NewDirectRegistrationService(auth.DirectRegistrationConfig{
		Issuer: cfg.OIDCIssuer, ClientID: cfg.AndroidOIDCClientID, RedirectURI: cfg.AndroidOIDCRedirectURI,
	})
	if err != nil {
		return fmt.Errorf("initialize direct registration: %w", err)
	}
	var historyStore *history.Store
	if cfg.HistoryEnabled {
		historyStore, err = history.Open(ctx, cfg.S3Endpoint, cfg.S3PublicEndpoint, cfg.S3Region, cfg.S3Bucket, cfg.S3AccessKey.Value(), cfg.S3SecretKey.Value())
		if err != nil {
			return fmt.Errorf("open encrypted history object store: %w", err)
		}
	}
	productionAPI := &api.API{
		Store: repository, Valkey: valkey, Tokens: verifier, Auth: authentication, Registration: registration,
		SubjectKey: subjectKey, TokenKey: tokenKey, History: optionalHistoryStore(historyStore),
		ClientConfig: api.ClientConfiguration{
			OIDCIssuer: cfg.OIDCIssuer, OIDCClientID: cfg.AndroidOIDCClientID, RedirectURI: cfg.AndroidOIDCRedirectURI,
			WindowsClientID: cfg.WindowsOIDCClientID, WindowsRegistrationParties: cfg.WindowsRegistrationOIDCParties,
			WindowsRedirectURI: cfg.WindowsOIDCRedirectURI,
			WebClientID:        cfg.WebOIDCClientID, WebRedirectURI: cfg.WebOIDCRedirectURI,
		},
	}
	metrics := observability.New(repository.Pool)
	hub := realtime.NewHubWithObserver(repository, valkey, slog.Default(), metrics)
	defer func() {
		shutdown, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = hub.Close(shutdown)
	}()
	portal, err := webportal.New(ctx, webportal.Config{
		PublicURL:   cfg.PublicURL,
		Issuer:      cfg.OIDCIssuer,
		ClientID:    cfg.WebOIDCClientID,
		RedirectURI: cfg.WebOIDCRedirectURI,
		CookieKey:   cookieKey,
		SubjectKey:  subjectKey,
		Repository:  repository,
		AccountStatusChanged: func(ctx context.Context, accountID uuid.UUID, status string) error {
			return valkey.Client.Publish(ctx, "account-status-changed", accountID.String()).Err()
		},
	})
	if err != nil {
		return fmt.Errorf("initialize web portal: %w", err)
	}

	mux := http.NewServeMux()
	mux.Handle("/v1/realtime", authentication.Authenticate(hub.Handler()))
	mux.Handle("/v1/", productionAPI.Handler())
	mux.Handle("/v1", productionAPI.Handler())
	mux.HandleFunc("GET /readyz", readinessHandler(repository, valkey))
	mux.Handle("/", portal.Handler())
	authorityReady := make(chan struct{})
	go consumeAuthorityChanges(ctx, valkey, hub, authorityReady)
	select {
	case <-authorityReady:
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(5 * time.Second):
		return errors.New("realtime authority subscription did not become ready")
	}
	go (outbox.Dispatcher{Store: repository, Valkey: valkey, Logger: slog.Default()}).Run(ctx)
	slog.Info("TermLink production server starting", "address", cfg.ListenAddr, "public_url", cfg.PublicURL, "history_enabled", cfg.HistoryEnabled, "metrics_enabled", cfg.MetricsListenAddr != "")
	return serveProduction(ctx, serverruntime.New(cfg, metrics.HTTPMiddleware(mux)), metrics, cfg.MetricsListenAddr)
}

func deriveServerKey(master, purpose string) []byte {
	mac := hmac.New(sha256.New, []byte(master))
	_, _ = mac.Write([]byte("TermLink server key V1\x00"))
	_, _ = mac.Write([]byte(purpose))
	return mac.Sum(nil)
}

func serveProduction(ctx context.Context, application *serverruntime.Runtime, metrics *observability.Metrics, metricsAddr string) error {
	runContext, cancel := context.WithCancel(ctx)
	defer cancel()
	type serverResult struct {
		name string
		err  error
	}
	serverCount := 1
	results := make(chan serverResult, 2)
	go func() { results <- serverResult{name: "application", err: application.Serve(runContext)} }()
	if metricsAddr != "" {
		serverCount++
		go func() { results <- serverResult{name: "metrics", err: metrics.Serve(runContext, metricsAddr)} }()
	}

	var firstError error
	for range serverCount {
		result := <-results
		if result.err != nil && firstError == nil {
			firstError = fmt.Errorf("%s server: %w", result.name, result.err)
		}
		cancel()
	}
	return firstError
}

func runMigrate() error {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	cfg, err := config.Load()
	if err != nil {
		return fmt.Errorf("load production config: %w", err)
	}
	return migrate.Up(ctx, cfg.DatabaseURL.Value())
}

func readinessHandler(repository *store.Postgres, valkey *kv.Valkey) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
		defer cancel()
		if repository.Ping(ctx) != nil || valkey.Ping(ctx) != nil || repository.VerifySchema(ctx) != nil {
			http.Error(w, `{"status":"unavailable"}`, http.StatusServiceUnavailable)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprint(w, `{"status":"ready"}`)
	}
}

func consumeAuthorityChanges(ctx context.Context, valkey *kv.Valkey, hub *realtime.Hub, ready chan<- struct{}) {
	var readyOnce sync.Once
	channels := []string{"device-revoked", "device-authorization-changed", "account-status-changed", "account-entitlement-changed"}
	for ctx.Err() == nil {
		subscription := valkey.Client.Subscribe(ctx, channels...)
		if _, err := subscription.Receive(ctx); err != nil {
			_ = subscription.Close()
			if !waitForRetry(ctx, time.Second) {
				return
			}
			continue
		}
		readyOnce.Do(func() { close(ready) })
		messages := subscription.Channel()
		connected := true
		for connected {
			select {
			case <-ctx.Done():
				_ = subscription.Close()
				return
			case message, ok := <-messages:
				if !ok {
					connected = false
					continue
				}
				id, err := uuid.Parse(message.Payload)
				if err != nil {
					continue
				}
				switch message.Channel {
				case "device-revoked":
					hub.Revoke(id)
				case "device-authorization-changed":
					hub.RefreshDevice(id)
				case "account-status-changed":
					hub.DisableAccount(id)
				case "account-entitlement-changed":
					hub.RefreshAccount(id)
				}
			}
		}
		_ = subscription.Close()
		if !waitForRetry(ctx, time.Second) {
			return
		}
	}
}

func waitForRetry(ctx context.Context, delay time.Duration) bool {
	timer := time.NewTimer(delay)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-timer.C:
		return true
	}
}

// runLANTest retains the explicitly non-production Golden Replay and TLT1
// fixtures. Production containers pass the serve command and never register
// these routes.
func runLANTest() error {
	token := envOrDefault("TERMLINK_TEST_TOKEN", "termlink-lan-test")
	if len(token) < 12 {
		return fmt.Errorf("TERMLINK_TEST_TOKEN must contain at least 12 characters")
	}
	bundleRoot := envOrDefault("TERMLINK_TESTDATA_DIR", "/testdata/terminal-golden-streams")
	server := &http.Server{Addr: envOrDefault("TERMLINK_LISTEN_ADDR", ":8080"), Handler: newLANTestHandler(token, bundleRoot), ReadHeaderTimeout: 5 * time.Second, IdleTimeout: 30 * time.Second}
	shutdownContext, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	go func() {
		<-shutdownContext.Done()
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = server.Shutdown(ctx)
	}()
	slog.Info("TermLink LAN test server listening", "address", server.Addr)
	err := server.ListenAndServe()
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}

func newLANTestHandler(token, bundleRoot string) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", lanreplay.HealthHandler)
	mux.HandleFunc("GET /readyz", lanreplay.HealthHandler)
	mux.Handle("GET /v1/test/replay/", lanreplay.Handler{BundleRoot: bundleRoot, Token: token})
	mux.Handle("GET /v1/test/session/", phase1test.Handler{BundleRoot: bundleRoot, Token: token})
	return mux
}

func runHealthcheck() int {
	client := http.Client{Timeout: 2 * time.Second}
	response, err := client.Get("http://127.0.0.1:8080/readyz")
	if err != nil {
		return 1
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return 1
	}
	return 0
}
func envOrDefault(name, fallback string) string {
	if value := os.Getenv(name); value != "" {
		return value
	}
	return fallback
}
