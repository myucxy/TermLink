package main

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/accountdeletion"
	"github.com/termlink/termlink/server/internal/adminroute"
	"github.com/termlink/termlink/server/internal/agentupdate"
	"github.com/termlink/termlink/server/internal/api"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/buildinfo"
	"github.com/termlink/termlink/server/internal/config"
	"github.com/termlink/termlink/server/internal/fileoperationcleanup"
	"github.com/termlink/termlink/server/internal/history"
	"github.com/termlink/termlink/server/internal/humanverification"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/observability"
	"github.com/termlink/termlink/server/internal/outbox"
	"github.com/termlink/termlink/server/internal/privacyexport"
	"github.com/termlink/termlink/server/internal/realtime"
	"github.com/termlink/termlink/server/internal/registrationrecovery"
	serverruntime "github.com/termlink/termlink/server/internal/runtime"
	"github.com/termlink/termlink/server/internal/serviceemail"
	"github.com/termlink/termlink/server/internal/sessioncleanup"
	"github.com/termlink/termlink/server/internal/store"
	"github.com/termlink/termlink/server/internal/transport"
	"github.com/termlink/termlink/server/internal/webportal"
)

func main() {
	command := "serve"
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
	case "admin-route-reset":
		err = runAdminRouteReset()
	default:
		err = fmt.Errorf("unknown command %q (use serve, migrate, healthcheck, or admin-route-reset)", command)
	}
	if err != nil {
		logProcessFailure(slog.Default(), command, err)
		os.Exit(1)
	}
}

func logProcessFailure(logger *slog.Logger, command string, _ error) {
	errorClass := "invalid_command"
	switch command {
	case "serve":
		errorClass = "production_server_failed"
	case "migrate":
		errorClass = "database_migration_failed"
	case "healthcheck":
		errorClass = "healthcheck_failed"
	case "admin-route-reset":
		errorClass = "admin_route_offline_reset_failed"
	}
	logger.Error("server stopped", "error_class", errorClass)
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
	configureLogging(cfg.LogLevel)
	repository, err := store.OpenWithOptions(ctx, cfg.DatabaseURL.Value(), store.OpenOptions{
		MaxConns: cfg.PostgresMaxConns, MinConns: cfg.PostgresMinConns,
		MaxConnLifetime: cfg.PostgresMaxConnLifetime, MaxConnIdleTime: cfg.PostgresMaxConnIdleTime,
		HealthCheckPeriod: cfg.PostgresHealthCheckPeriod, ConnectTimeout: cfg.PostgresConnectTimeout,
	})
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
	valkey, err := kv.OpenWithOptions(ctx, cfg.RedisURL.Value(), kv.OpenOptions{
		PoolSize: cfg.RedisPoolSize, MinIdleConns: cfg.RedisMinIdleConns,
		DialTimeout: cfg.RedisDialTimeout, ReadTimeout: cfg.RedisReadTimeout,
		WriteTimeout: cfg.RedisWriteTimeout, PoolTimeout: cfg.RedisPoolTimeout,
	})
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
	// Keep the established derivation label so previously issued invitation
	// codes remain valid after the registration subsystem refactor.
	registrationCodeKey := deriveServerKey(keyMaster, "commerce-code-digest")
	registrationProofKey := deriveServerKey(keyMaster, "keycloak-registration-attempt-proof")
	privacyExportKey := deriveServerKey(keyMaster, "privacy-export-artifact-encryption")
	serviceEmailKey := deriveServerKey(keyMaster, "service-email-encryption-and-challenges")
	adminRouteKey := deriveServerKey(keyMaster, "web-admin-route-encryption")
	humanVerificationKey := deriveServerKey(keyMaster, "human-verification-settings-encryption")
	directTransportKey := deriveServerKey(keyMaster, "direct-transport-ticket-ed25519")
	authentication := auth.Middleware{Tokens: verifier, Devices: repository, Nonces: valkey, SubjectKey: subjectKey}
	var keycloakAdmin *auth.KeycloakAdminClient
	if cfg.RegistrationRecoveryEnabled || cfg.AccountDeletionEnabled || !cfg.KeycloakAdminClientSecret.Empty() {
		var adminErr error
		keycloakAdmin, adminErr = auth.NewKeycloakAdminClient(auth.KeycloakAdminConfig{
			Issuer: cfg.OIDCIssuer, AdminBaseURL: cfg.KeycloakAdminBaseURL, ClientID: cfg.KeycloakAdminClientID,
			ClientSecret: cfg.KeycloakAdminClientSecret.Value(), RegistrationProofKey: registrationProofKey,
		})
		if adminErr != nil {
			return fmt.Errorf("initialize Keycloak administration client: %w", adminErr)
		}
	}
	var realmEmail serviceemail.RealmEmailConfigurator
	if keycloakAdmin != nil {
		realmEmail = keycloakRealmEmailAdapter{client: keycloakAdmin}
	}
	serviceEmail, err := serviceemail.New(repository, serviceEmailKey, nil, realmEmail)
	if err != nil {
		return fmt.Errorf("initialize service email: %w", err)
	}
	publicOrigin, parseErr := url.Parse(cfg.PublicURL)
	if parseErr != nil || publicOrigin.Hostname() == "" {
		return errors.New("initialize human verification: public URL hostname is invalid")
	}
	humanVerificationManager, err := humanverification.NewManager(humanverification.ManagerConfig{
		Repository: repository, EncryptionKey: humanVerificationKey, ExpectedHostname: publicOrigin.Hostname(),
		FallbackSiteKey: cfg.HumanVerificationSiteKey, FallbackSecret: cfg.HumanVerificationSecret.Value(),
	})
	if err != nil {
		return fmt.Errorf("initialize human verification settings: %w", err)
	}
	adminRoutes, err := adminroute.New(repository, adminRouteKey)
	if err != nil {
		return fmt.Errorf("initialize Web admin route service: %w", err)
	}
	var registrationRecoveryRunner *registrationrecovery.Runner
	if cfg.RegistrationRecoveryEnabled {
		registrationRecoveryRunner, err = registrationrecovery.New(repository, keycloakAdmin, registrationrecovery.Options{
			Issuer: cfg.OIDCIssuer, SubjectKey: subjectKey, Logger: slog.Default(),
		})
		if err != nil {
			return fmt.Errorf("initialize registration recovery runner: %w", err)
		}
	}
	appShipBaseURL := os.Getenv("TERMLINK_APPSHIP_BASE_URL")
	updateCatalog, err := agentupdate.NewAppShipCatalogWithAllowedPrivateIPs(
		appShipBaseURL,
		nil,
		os.Getenv("TERMLINK_APPSHIP_ALLOWED_PRIVATE_IPS"),
	)
	if err != nil {
		return fmt.Errorf("initialize Agent update catalog: %w", err)
	}
	var historyStore *history.Store
	var privacyExportService *privacyexport.Service
	var privacyExportRunner *privacyexport.Runner
	var accountDeletionBinder accountdeletion.RequestBinder
	var accountDeletionRunner *accountdeletion.Runner
	if cfg.HistoryEnabled {
		historyStore, err = history.Open(ctx, cfg.S3Endpoint, cfg.S3PublicEndpoint, cfg.S3Region, cfg.S3Bucket, cfg.S3AccessKey.Value(), cfg.S3SecretKey.Value())
		if err != nil {
			return fmt.Errorf("open encrypted history object store: %w", err)
		}
		privacyExportService, err = privacyexport.New(repository, historyStore, uuid.New(), privacyExportKey)
		if err != nil {
			return fmt.Errorf("initialize privacy export service: %w", err)
		}
		privacyExportRunner = privacyexport.NewRunner(privacyExportService, privacyexport.RunnerOptions{Logger: slog.Default()})
	}
	if cfg.AccountDeletionEnabled {
		identityKeys, keyErr := cfg.AccountDeletionIdentityKeys()
		if keyErr != nil {
			return fmt.Errorf("load account deletion identity keys: %w", keyErr)
		}
		codec, codecErr := accountdeletion.NewIdentityHandleCodec(identityKeys)
		for _, identityKey := range identityKeys {
			clear(identityKey)
		}
		if codecErr != nil {
			return fmt.Errorf("initialize account deletion identity codec: %w", codecErr)
		}
		binder, binderErr := accountdeletion.NewIdentityRequestBinder(repository, codec, cfg.AccountDeletionIdentityKeyVersion)
		if binderErr != nil {
			return fmt.Errorf("initialize account deletion request binder: %w", binderErr)
		}
		identityDisabler, disablerErr := accountdeletion.NewKeycloakIdentityDisabler(codec, keycloakAdmin)
		if disablerErr != nil {
			return fmt.Errorf("initialize account deletion identity disabler: %w", disablerErr)
		}
		deletionService, serviceErr := accountdeletion.New(repository, identityDisabler, historyStore, uuid.New())
		if serviceErr != nil {
			return fmt.Errorf("initialize account deletion service: %w", serviceErr)
		}
		accountDeletionBinder = binder
		accountDeletionRunner = accountdeletion.NewRunner(deletionService, accountdeletion.RunnerOptions{Logger: slog.Default()})
	}
	var privacyExportDownloader api.PrivacyExportDownloader
	var webPrivacyExportDownloader webportal.PrivacyExportDownloader
	if privacyExportService != nil {
		privacyExportDownloader = privacyExportService
		webPrivacyExportDownloader = privacyExportService
	}
	productionAPI := &api.API{
		Store: repository, Valkey: valkey, Tokens: verifier, Auth: authentication,
		ServiceEmail:      serviceEmail,
		HumanVerification: humanVerificationManager,
		SubjectKey:        subjectKey, TokenKey: tokenKey, RegistrationCodeKey: registrationCodeKey, History: optionalHistoryStore(historyStore),
		DataExportDownloader:  privacyExportDownloader,
		AccountDeletionBinder: accountDeletionBinder, AgentUpdateCatalog: updateCatalog,
		ClientConfig: api.ClientConfiguration{
			OIDCIssuer: cfg.OIDCIssuer, OIDCClientID: cfg.AndroidOIDCClientID, RedirectURI: cfg.AndroidOIDCRedirectURI,
			WindowsClientID: cfg.WindowsOIDCClientID, WindowsRegistrationParties: cfg.WindowsRegistrationOIDCParties,
			WindowsRedirectURI: cfg.WindowsOIDCRedirectURI,
			WebClientID:        cfg.WebOIDCClientID, WebRedirectURI: cfg.WebOIDCRedirectURI,
		},
	}
	if keycloakAdmin != nil {
		// All public account creation goes through the bounded service account.
		// A missing credential leaves registration unavailable rather than
		// falling back to Keycloak's native self-registration form.
		productionAPI.Registration = keycloakAdmin
		productionAPI.VerifiedRegistration = keycloakAdmin
	}
	var metrics *observability.Metrics
	var observer realtime.Observer
	if cfg.MetricsListenAddr != "" {
		metrics = observability.New(repository.Pool)
		observer = metrics
	}
	hub := realtime.NewHubWithObserver(repository, valkey, slog.Default(), observer)
	defer func() {
		shutdown, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = hub.Close(shutdown)
	}()
	directTransportEnabled, err := optionalFeatureFlag("TERMLINK_DIRECT_TRANSPORT_ENABLED")
	if err != nil {
		return err
	}
	directSTUNServers, err := parseDirectSTUNServers(os.Getenv("TERMLINK_DIRECT_STUN_SERVERS"))
	if err != nil {
		return err
	}
	var directHub *transport.Hub
	var directTickets *transport.RouteTicketService
	var directRepository *store.Postgres
	var directValkey *kv.Valkey
	var directAuthentication auth.Middleware
	if directTransportEnabled {
		directRedisURL, directSecretErr := readOptionalSecretFile("TERMLINK_DIRECT_REDIS_URL_FILE")
		if directSecretErr == nil {
			directRepository, err = store.OpenWithOptions(ctx, cfg.DatabaseURL.Value(), store.OpenOptions{
				MaxConns: 4, MinConns: 0, ConnectTimeout: 3 * time.Second,
				MaxConnLifetime: 15 * time.Minute, MaxConnIdleTime: time.Minute,
			})
			if err == nil {
				directRepository.AutoTrustDevices = true
				directValkey, err = kv.OpenWithOptions(ctx, directRedisURL, kv.OpenOptions{
					PoolSize: 8, MinIdleConns: 1, DialTimeout: 2 * time.Second,
					ReadTimeout: 2 * time.Second, WriteTimeout: 2 * time.Second, PoolTimeout: 2 * time.Second,
				})
			}
			if err == nil {
				var directState *kv.DirectTransportState
				directState, err = kv.NewDirectTransportState(directValkey.Client, "")
				if err == nil {
					directTickets, err = transport.NewSharedRouteTicketService(directRepository, directState, directTransportKey, directSTUNServers...)
				}
				if err == nil {
					directHub, err = transport.NewSharedHub(directTickets, directState)
				}
				if err == nil {
					directAuthentication = auth.Middleware{
						Tokens: verifier, Devices: directRepository, Nonces: directValkey, SubjectKey: subjectKey,
					}
					startCtx, cancel := context.WithTimeout(ctx, 3*time.Second)
					err = directHub.Start(startCtx, ctx)
					cancel()
				}
			}
		} else {
			err = directSecretErr
		}
		if err != nil {
			slog.Warn("direct transport degraded to Relay-only", "error_class", "direct_transport_unavailable")
			if directHub != nil {
				shutdown, cancel := context.WithTimeout(context.Background(), 2*time.Second)
				_ = directHub.Close(shutdown)
				cancel()
			}
			if directValkey != nil {
				_ = directValkey.Close()
			}
			if directRepository != nil {
				directRepository.Close()
			}
			directHub, directTickets, directValkey, directRepository = nil, nil, nil, nil
		}
	}
	if directHub != nil {
		defer directRepository.Close()
		defer directValkey.Close()
		defer func() {
			shutdown, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			_ = directHub.Close(shutdown)
		}()
	}
	portal, err := webportal.New(ctx, webportal.Config{
		PublicURL:                 cfg.PublicURL,
		Issuer:                    cfg.OIDCIssuer,
		ClientID:                  cfg.WebOIDCClientID,
		WindowsClientID:           cfg.WindowsOIDCClientID,
		StepUpClientID:            cfg.WebOIDCStepUpClientID,
		RedirectURI:               cfg.WebOIDCRedirectURI,
		HumanVerificationProvider: cfg.HumanVerificationProvider,
		HumanVerificationSiteKey:  cfg.HumanVerificationSiteKey,
		HumanVerification:         humanVerificationManager,
		CookieKey:                 cookieKey,
		SubjectKey:                subjectKey,
		AppShipBaseURL:            appShipBaseURL,
		AppShipAllowedPrivateIPs:  os.Getenv("TERMLINK_APPSHIP_ALLOWED_PRIVATE_IPS"),
		BuildInfo:                 buildinfo.Current(),
		RegistrationCodeKey:       registrationCodeKey,
		DataExportDownloader:      webPrivacyExportDownloader,
		AccountDeletionBinder:     accountDeletionBinder,
		ServiceEmail:              serviceEmail,
		AdminRoutes:               adminRoutes,
		IdentityProfiles:          keycloakAdmin,
		IdentityMFAStatus:         keycloakAdmin,
		Repository:                repository,
		AccountStatusChanged: func(ctx context.Context, accountID uuid.UUID, status string) error {
			return valkey.Client.Publish(ctx, "account-status-changed", accountID.String()).Err()
		},
	})
	if err != nil {
		return fmt.Errorf("initialize web portal: %w", err)
	}

	mux := http.NewServeMux()
	mux.Handle("/v1/realtime", authentication.Authenticate(hub.Handler()))
	if directHub != nil && directTickets != nil {
		registerDirectTransportRoutes(mux,
			directAuthentication.Authenticate(directTickets.ConfigHandler()),
			directAuthentication.Authenticate(directTickets.TicketHandler()),
			transport.AuthenticateSignals(directAuthentication, directHub.Handler()),
		)
	}
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
	go (outbox.Dispatcher{Store: repository, Valkey: valkey, History: historyStore, Logger: slog.Default()}).Run(ctx)
	if registrationRecoveryRunner != nil {
		go registrationRecoveryRunner.Run(ctx)
	}
	if privacyExportRunner != nil {
		go privacyExportRunner.Run(ctx)
	}
	if accountDeletionRunner != nil {
		go accountDeletionRunner.Run(ctx)
	}
	go fileoperationcleanup.New(repository, fileoperationcleanup.Options{Logger: slog.Default()}).Run(ctx)
	go sessioncleanup.New(repository, sessioncleanup.Options{Logger: slog.Default()}).Run(ctx)
	slog.Info("TermLink production server starting", "address", cfg.ListenAddr, "public_url", cfg.PublicURL, "history_enabled", cfg.HistoryEnabled, "metrics_enabled", cfg.MetricsListenAddr != "", "direct_transport_enabled", directHub != nil)
	applicationHandler := http.Handler(mux)
	if metrics != nil {
		applicationHandler = metrics.HTTPMiddleware(applicationHandler)
	}
	return serveProduction(ctx, serverruntime.New(cfg, applicationHandler), metrics, cfg.MetricsListenAddr)
}

func runAdminRouteReset() error {
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	cfg, err := config.Load()
	if err != nil {
		return fmt.Errorf("load production config: %w", err)
	}
	repository, err := store.OpenWithOptions(ctx, cfg.DatabaseURL.Value(), store.OpenOptions{MinConns: 0, MaxConns: 1})
	if err != nil {
		return err
	}
	defer repository.Close()
	if err = repository.VerifySchema(ctx); err != nil {
		return err
	}
	if err = repository.ResetWebPortalAdminRouteOffline(ctx); err != nil {
		return err
	}
	fmt.Println("TermLink management center route reset to /admin; the recovery was recorded in system audit.")
	return nil
}

type keycloakRealmEmailAdapter struct {
	client *auth.KeycloakAdminClient
}

func (a keycloakRealmEmailAdapter) ConfigureRealmEmail(ctx context.Context, config serviceemail.SMTPConfig, passwordResetEnabled bool) error {
	if a.client == nil {
		return serviceemail.ErrIdentityUnavailable
	}
	return a.client.ConfigureRealmEmail(ctx, auth.RealmSMTPConfig{
		Host: config.Host, Port: config.Port, Security: config.Security, FromName: config.FromName,
		FromAddress: config.FromAddress, ReplyTo: config.ReplyTo, Username: config.Username, Password: config.Password,
	}, passwordResetEnabled)
}

func configureLogging(levelName string) {
	level := slog.LevelInfo
	switch levelName {
	case "debug":
		level = slog.LevelDebug
	case "warn":
		level = slog.LevelWarn
	case "error":
		level = slog.LevelError
	}
	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stderr, &slog.HandlerOptions{Level: level})))
}

func deriveServerKey(master, purpose string) []byte {
	mac := hmac.New(sha256.New, []byte(master))
	_, _ = mac.Write([]byte("TermLink server key V1\x00"))
	_, _ = mac.Write([]byte(purpose))
	return mac.Sum(nil)
}

func optionalFeatureFlag(name string) (bool, error) {
	value, exists := os.LookupEnv(name)
	if !exists || strings.TrimSpace(value) == "" {
		return false, nil
	}
	switch strings.ToLower(strings.TrimSpace(value)) {
	case "true", "1", "yes", "on":
		return true, nil
	case "false", "0", "no", "off":
		return false, nil
	default:
		return false, fmt.Errorf("invalid boolean value for %s", name)
	}
}

func readOptionalSecretFile(name string) (string, error) {
	path, exists := os.LookupEnv(name)
	path = strings.TrimSpace(path)
	if !exists || path == "" {
		return "", fmt.Errorf("%s is required when direct transport is enabled", name)
	}
	info, err := os.Lstat(path)
	if err != nil || info == nil || info.IsDir() {
		return "", fmt.Errorf("%s does not reference a readable secret file", name)
	}
	if runtime.GOOS != "windows" {
		permissions := info.Mode().Perm()
		if permissions&0o077 != 0 || permissions&0o400 == 0 {
			return "", fmt.Errorf("%s secret file permissions are unsafe", name)
		}
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("read %s: %w", name, err)
	}
	value := strings.TrimSpace(string(data))
	if value == "" {
		return "", fmt.Errorf("%s is empty", name)
	}
	return value, nil
}

func parseDirectSTUNServers(raw string) ([]string, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return nil, nil
	}
	items := strings.Split(raw, ",")
	if len(items) > 2 {
		return nil, errors.New("TERMLINK_DIRECT_STUN_SERVERS accepts at most two endpoints")
	}
	result := make([]string, 0, len(items))
	seen := map[string]struct{}{}
	for _, item := range items {
		item = strings.TrimSpace(item)
		if !strings.HasPrefix(item, "stun:") || strings.ContainsAny(item, "?#/@") {
			return nil, errors.New("TERMLINK_DIRECT_STUN_SERVERS must use stun:host:port")
		}
		host, portText, err := net.SplitHostPort(strings.TrimPrefix(item, "stun:"))
		port, portErr := strconv.Atoi(portText)
		if err != nil || portErr != nil || port < 1 || port > 65535 || strconv.Itoa(port) != portText || !validSTUNHost(host) {
			return nil, errors.New("TERMLINK_DIRECT_STUN_SERVERS must use stun:host:port")
		}
		canonical := "stun:" + net.JoinHostPort(strings.ToLower(host), portText)
		if _, duplicate := seen[canonical]; duplicate {
			return nil, errors.New("TERMLINK_DIRECT_STUN_SERVERS contains a duplicate endpoint")
		}
		seen[canonical] = struct{}{}
		result = append(result, canonical)
	}
	return result, nil
}

func validSTUNHost(host string) bool {
	if host == "" || len(host) > 253 || strings.Contains(host, "%") {
		return false
	}
	if net.ParseIP(host) != nil {
		return true
	}
	for _, label := range strings.Split(host, ".") {
		if len(label) == 0 || len(label) > 63 || label[0] == '-' || label[len(label)-1] == '-' {
			return false
		}
		for _, character := range label {
			if character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' || character >= '0' && character <= '9' || character == '-' {
				continue
			}
			return false
		}
	}
	return true
}

func registerDirectTransportRoutes(mux *http.ServeMux, config, tickets, signals http.Handler) {
	mux.Handle("/v1/transport/config", config)
	mux.Handle("/v1/transport/tickets", tickets)
	mux.Handle("/v1/transport/signals", signals)
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
	if metricsAddr != "" && metrics != nil {
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
		// Schema compatibility is verified once, synchronously, before the
		// listener starts. Re-querying schema_migrations on every Docker/Caddy
		// health probe adds avoidable steady-state database traffic.
		if repository.Ping(ctx) != nil || valkey.Ping(ctx) != nil {
			http.Error(w, `{"status":"unavailable"}`, http.StatusServiceUnavailable)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprint(w, `{"status":"ready"}`)
	}
}

type authorityChangeTarget interface {
	Revoke(uuid.UUID)
	RefreshDevice(uuid.UUID)
	DisableAccount(uuid.UUID)
	RefreshControlPolicy(uuid.UUID)
}

func authorityChangeChannels() []string {
	return []string{
		"device-revoked",
		"device-authorization-changed",
		"account-status-changed",
		"pc-control-policy-changed",
	}
}

func applyAuthorityChange(target authorityChangeTarget, channel string, id uuid.UUID) {
	switch channel {
	case "device-revoked":
		target.Revoke(id)
	case "device-authorization-changed":
		target.RefreshDevice(id)
	case "account-status-changed":
		target.DisableAccount(id)
	case "pc-control-policy-changed":
		target.RefreshControlPolicy(id)
	}
}

func consumeAuthorityChanges(ctx context.Context, valkey *kv.Valkey, hub authorityChangeTarget, ready chan<- struct{}) {
	var readyOnce sync.Once
	channels := authorityChangeChannels()
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
				applyAuthorityChange(hub, message.Channel, id)
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
