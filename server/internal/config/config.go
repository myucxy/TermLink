// Package config loads the non-secret server settings and Docker-secret files
// used by the production server. Secret values are intentionally represented
// by Secret, whose formatting methods never return the underlying value.
package config

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net"
	"net/url"
	"os"
	"path"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"
)

const (
	defaultListenAddr            = ":8080"
	defaultLogLevel              = "info"
	defaultKeycloakAdminClientID = "termlink-registration-recovery"

	defaultPostgresMaxConns          = int32(20)
	defaultPostgresMinConns          = int32(2)
	defaultPostgresMaxConnLifetime   = 30 * time.Minute
	defaultPostgresMaxConnIdleTime   = 5 * time.Minute
	defaultPostgresHealthCheckPeriod = time.Minute
	defaultPostgresConnectTimeout    = 5 * time.Second

	defaultRedisPoolSize     = 20
	defaultRedisMinIdleConns = 2
	defaultRedisDialTimeout  = 5 * time.Second
	defaultRedisReadTimeout  = 3 * time.Second
	defaultRedisWriteTimeout = 3 * time.Second
	defaultRedisPoolTimeout  = 4 * time.Second

	RuntimeModeDistributed = "distributed"
	RuntimeModeSingleNode  = "single-node"
)

var (
	// ErrMissing identifies a required configuration value that was not set.
	ErrMissing = errors.New("required configuration is missing")
	// ErrInvalid identifies a configuration value that was set but is invalid.
	ErrInvalid = errors.New("configuration is invalid")
	// ErrSecret identifies a secret file that cannot safely be loaded.
	ErrSecret = errors.New("secret file is unavailable")
)

// Secret keeps a loaded secret usable by the server while making accidental
// formatting/logging safe. Call Value only at the boundary that needs the
// plaintext value.
type Secret string

// Value returns the underlying secret. Callers should avoid retaining or
// logging the returned string.
func (s Secret) Value() string { return string(s) }

// Empty reports whether the secret has no non-whitespace content.
func (s Secret) Empty() bool { return strings.TrimSpace(string(s)) == "" }

// String deliberately does not expose a secret value.
func (Secret) String() string { return "[REDACTED]" }

// GoString deliberately does not expose a secret value in %#v output.
func (Secret) GoString() string { return "[REDACTED]" }

// Config is the validated production server configuration. Values ending in
// Secret, URL, or Key are loaded from secret files and are redacted by String.
type Config struct {
	RuntimeMode                    string
	PublicURL                      string
	OIDCIssuer                     string
	OIDCBackchannelIssuer          string
	OIDCClientID                   string
	OIDCAllowedParties             []string
	AndroidOIDCClientID            string
	AndroidOIDCRedirectURI         string
	WindowsOIDCClientID            string
	WindowsRegistrationOIDCParties []string
	WindowsOIDCRedirectURI         string
	WebOIDCClientID                string
	WebOIDCStepUpClientID          string
	WebOIDCRedirectURI             string
	HumanVerificationProvider      string
	HumanVerificationSiteKey       string

	OIDCClientSecret           Secret
	KeycloakAdminClientSecret  Secret
	HumanVerificationSecret    Secret
	AccountDeletionIdentityKey Secret
	DatabaseURL                Secret
	RedisURL                   Secret
	TokenSigningKey            Secret

	// Secret file paths are retained for diagnostics and dependency wiring, but
	// are omitted from String because deployment paths can be sensitive too.
	OIDCClientSecretFile           string
	KeycloakAdminClientSecretFile  string
	HumanVerificationSecretFile    string
	AccountDeletionIdentityKeyFile string
	DatabaseURLFile                string
	RedisURLFile                   string
	TokenSigningKeyFile            string

	// Pool and timeout values are non-secret, validated runtime controls. The
	// defaults preserve the previous PostgreSQL settings and make go-redis's
	// formerly implicit timeout behavior stable across CPU counts.
	PostgresMaxConns                  int32
	PostgresMinConns                  int32
	PostgresMaxConnLifetime           time.Duration
	PostgresMaxConnIdleTime           time.Duration
	PostgresHealthCheckPeriod         time.Duration
	PostgresConnectTimeout            time.Duration
	RedisPoolSize                     int
	RedisMinIdleConns                 int
	RedisDialTimeout                  time.Duration
	RedisReadTimeout                  time.Duration
	RedisWriteTimeout                 time.Duration
	RedisPoolTimeout                  time.Duration
	RegistrationRecoveryEnabled       bool
	KeycloakAdminClientID             string
	KeycloakAdminBaseURL              string
	AccountDeletionEnabled            bool
	AccountDeletionIdentityKeyVersion int16

	LogLevel          string
	ListenAddr        string
	MetricsListenAddr string
}

// String returns a safe diagnostic representation. It intentionally omits
// secret paths as well as secret values, because paths can contain identifying
// deployment information and are not needed in normal startup diagnostics.
func (c Config) String() string {
	return fmt.Sprintf("Config{RuntimeMode:%q PublicURL:%q OIDCIssuer:%q OIDCBackchannelIssuer:%q OIDCClientID:%q OIDCAllowedParties:%q AndroidOIDCClientID:%q AndroidOIDCRedirectURI:%q WindowsOIDCClientID:%q WindowsRegistrationOIDCParties:%q WindowsOIDCRedirectURI:%q WebOIDCClientID:%q WebOIDCStepUpClientID:%q WebOIDCRedirectURI:%q HumanVerificationProvider:%q HumanVerificationSiteKey:%q HumanVerificationSecret:%s OIDCClientSecret:%s KeycloakAdminClientSecret:%s KeycloakAdminClientID:%q KeycloakAdminBaseURL:%q RegistrationRecoveryEnabled:%t AccountDeletionEnabled:%t AccountDeletionIdentityKeyVersion:%d DatabaseURL:%s RedisURL:%s TokenSigningKey:%s PostgresMaxConns:%d PostgresMinConns:%d PostgresMaxConnLifetime:%s PostgresMaxConnIdleTime:%s PostgresHealthCheckPeriod:%s PostgresConnectTimeout:%s RedisPoolSize:%d RedisMinIdleConns:%d RedisDialTimeout:%s RedisReadTimeout:%s RedisWriteTimeout:%s RedisPoolTimeout:%s LogLevel:%q ListenAddr:%q MetricsListenAddr:%q}",
		c.RuntimeMode,
		c.PublicURL,
		c.OIDCIssuer,
		c.OIDCBackchannelIssuer,
		c.OIDCClientID,
		c.OIDCAllowedParties,
		c.AndroidOIDCClientID,
		c.AndroidOIDCRedirectURI,
		c.WindowsOIDCClientID,
		c.WindowsRegistrationOIDCParties,
		c.WindowsOIDCRedirectURI,
		c.WebOIDCClientID,
		c.WebOIDCStepUpClientID,
		c.WebOIDCRedirectURI,
		c.HumanVerificationProvider,
		c.HumanVerificationSiteKey,
		c.HumanVerificationSecret,
		c.OIDCClientSecret,
		c.KeycloakAdminClientSecret,
		c.KeycloakAdminClientID,
		c.KeycloakAdminBaseURL,
		c.RegistrationRecoveryEnabled,
		c.AccountDeletionEnabled,
		c.AccountDeletionIdentityKeyVersion,
		c.DatabaseURL,
		c.RedisURL,
		c.TokenSigningKey,
		c.PostgresMaxConns,
		c.PostgresMinConns,
		c.PostgresMaxConnLifetime,
		c.PostgresMaxConnIdleTime,
		c.PostgresHealthCheckPeriod,
		c.PostgresConnectTimeout,
		c.RedisPoolSize,
		c.RedisMinIdleConns,
		c.RedisDialTimeout,
		c.RedisReadTimeout,
		c.RedisWriteTimeout,
		c.RedisPoolTimeout,
		c.LogLevel,
		c.ListenAddr,
		c.MetricsListenAddr,
	)
}

// GoString is kept redacted for %#v diagnostics.
func (c Config) GoString() string { return c.String() }

// Validate checks an explicitly assembled Config without reading external
// state. Load and New already perform this check before returning.
func (c Config) Validate() error {
	_, err := validate(c)
	return err
}

// AccountDeletionIdentityKeyBytes decodes the dedicated AES-256 identity
// handle key. The encoded value is kept inside Secret so diagnostics cannot
// accidentally disclose either the text representation or the key bytes.
func (c Config) AccountDeletionIdentityKeyBytes() ([]byte, error) {
	keys, err := c.AccountDeletionIdentityKeys()
	if err != nil {
		return nil, err
	}
	key, ok := keys[c.AccountDeletionIdentityKeyVersion]
	for version, candidate := range keys {
		if version != c.AccountDeletionIdentityKeyVersion {
			clear(candidate)
		}
	}
	if !ok {
		return nil, fmt.Errorf("%w: TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE", ErrSecret)
	}
	return key, nil
}

type accountDeletionIdentityKeyEntry struct {
	Version int16  `json:"version"`
	Key     string `json:"key"`
}

// AccountDeletionIdentityKeys accepts either a single encoded current key or
// a bounded JSON key ring. The latter keeps older scheduled deletions
// decryptable during rotation without making any key observable in Config
// diagnostics.
func (c Config) AccountDeletionIdentityKeys() (map[int16][]byte, error) {
	if c.AccountDeletionIdentityKey.Empty() || c.AccountDeletionIdentityKeyVersion < 1 {
		return nil, fmt.Errorf("%w: TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE", ErrSecret)
	}
	encoded := c.AccountDeletionIdentityKey.Value()
	if key, ok := decodeAccountDeletionIdentityKey(encoded); ok {
		return map[int16][]byte{c.AccountDeletionIdentityKeyVersion: key}, nil
	}
	decoder := json.NewDecoder(bytes.NewBufferString(encoded))
	decoder.DisallowUnknownFields()
	var entries []accountDeletionIdentityKeyEntry
	if err := decoder.Decode(&entries); err != nil || len(entries) == 0 || len(entries) > 32 {
		return nil, fmt.Errorf("%w: TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE", ErrSecret)
	}
	if err := decoder.Decode(&struct{}{}); !errors.Is(err, io.EOF) {
		return nil, fmt.Errorf("%w: TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE", ErrSecret)
	}
	keys := make(map[int16][]byte, len(entries))
	for _, entry := range entries {
		key, ok := decodeAccountDeletionIdentityKey(entry.Key)
		if !ok || entry.Version < 1 {
			clearAccountDeletionIdentityKeys(keys)
			return nil, fmt.Errorf("%w: TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE", ErrSecret)
		}
		if _, duplicate := keys[entry.Version]; duplicate {
			clear(key)
			clearAccountDeletionIdentityKeys(keys)
			return nil, fmt.Errorf("%w: TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE", ErrSecret)
		}
		keys[entry.Version] = key
	}
	if _, ok := keys[c.AccountDeletionIdentityKeyVersion]; !ok {
		clearAccountDeletionIdentityKeys(keys)
		return nil, fmt.Errorf("%w: TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE", ErrSecret)
	}
	return keys, nil
}

func decodeAccountDeletionIdentityKey(encoded string) ([]byte, bool) {
	encodings := []*base64.Encoding{
		base64.RawURLEncoding,
		base64.URLEncoding,
		base64.RawStdEncoding,
		base64.StdEncoding,
	}
	for _, encoding := range encodings {
		decoded, err := encoding.DecodeString(encoded)
		if err == nil && len(decoded) == 32 {
			return decoded, true
		}
		clear(decoded)
	}
	return nil, false
}

func clearAccountDeletionIdentityKeys(keys map[int16][]byte) {
	for _, key := range keys {
		clear(key)
	}
}

// Input is the explicit-construction form used by tests and by embedders that
// obtain secrets from a managed secret provider instead of files. Production
// startup should use Load, which enforces file permissions.
type Input struct {
	RuntimeMode                    string
	PublicURL                      string
	OIDCIssuer                     string
	OIDCBackchannelIssuer          string
	OIDCClientID                   string
	OIDCAllowedParties             []string
	AndroidOIDCClientID            string
	AndroidOIDCRedirectURI         string
	WindowsOIDCClientID            string
	WindowsRegistrationOIDCParties []string
	WindowsOIDCRedirectURI         string
	WebOIDCClientID                string
	WebOIDCStepUpClientID          string
	WebOIDCRedirectURI             string
	HumanVerificationProvider      string
	HumanVerificationSiteKey       string

	OIDCClientSecret           string
	KeycloakAdminClientSecret  string
	HumanVerificationSecret    string
	AccountDeletionIdentityKey string
	DatabaseURL                string
	RedisURL                   string
	TokenSigningKey            string

	PostgresMaxConns                  int32
	PostgresMinConns                  int32
	PostgresMaxConnLifetime           time.Duration
	PostgresMaxConnIdleTime           time.Duration
	PostgresHealthCheckPeriod         time.Duration
	PostgresConnectTimeout            time.Duration
	RedisPoolSize                     int
	RedisMinIdleConns                 int
	RedisDialTimeout                  time.Duration
	RedisReadTimeout                  time.Duration
	RedisWriteTimeout                 time.Duration
	RedisPoolTimeout                  time.Duration
	RegistrationRecoveryEnabled       bool
	KeycloakAdminClientID             string
	KeycloakAdminBaseURL              string
	AccountDeletionEnabled            bool
	AccountDeletionIdentityKeyVersion int16

	LogLevel          string
	ListenAddr        string
	MetricsListenAddr string
}

// New validates explicitly supplied values. It is useful for focused tests;
// it does not read environment variables or secret files.
func New(input Input) (Config, error) {
	return validate(Config{
		RuntimeMode:                       strings.ToLower(strings.TrimSpace(input.RuntimeMode)),
		PublicURL:                         strings.TrimSpace(input.PublicURL),
		OIDCIssuer:                        strings.TrimSpace(input.OIDCIssuer),
		OIDCBackchannelIssuer:             strings.TrimSpace(input.OIDCBackchannelIssuer),
		OIDCClientID:                      strings.TrimSpace(input.OIDCClientID),
		OIDCAllowedParties:                append([]string(nil), input.OIDCAllowedParties...),
		AndroidOIDCClientID:               strings.TrimSpace(input.AndroidOIDCClientID),
		AndroidOIDCRedirectURI:            strings.TrimSpace(input.AndroidOIDCRedirectURI),
		WindowsOIDCClientID:               strings.TrimSpace(input.WindowsOIDCClientID),
		WindowsRegistrationOIDCParties:    append([]string(nil), input.WindowsRegistrationOIDCParties...),
		WindowsOIDCRedirectURI:            strings.TrimSpace(input.WindowsOIDCRedirectURI),
		WebOIDCClientID:                   strings.TrimSpace(input.WebOIDCClientID),
		WebOIDCStepUpClientID:             strings.TrimSpace(input.WebOIDCStepUpClientID),
		WebOIDCRedirectURI:                strings.TrimSpace(input.WebOIDCRedirectURI),
		HumanVerificationProvider:         strings.ToLower(strings.TrimSpace(input.HumanVerificationProvider)),
		HumanVerificationSiteKey:          strings.TrimSpace(input.HumanVerificationSiteKey),
		HumanVerificationSecret:           Secret(strings.TrimSpace(input.HumanVerificationSecret)),
		OIDCClientSecret:                  Secret(strings.TrimSpace(input.OIDCClientSecret)),
		KeycloakAdminClientSecret:         Secret(strings.TrimSpace(input.KeycloakAdminClientSecret)),
		KeycloakAdminClientID:             strings.TrimSpace(input.KeycloakAdminClientID),
		KeycloakAdminBaseURL:              strings.TrimSpace(input.KeycloakAdminBaseURL),
		RegistrationRecoveryEnabled:       input.RegistrationRecoveryEnabled,
		AccountDeletionEnabled:            input.AccountDeletionEnabled,
		AccountDeletionIdentityKey:        Secret(strings.TrimSpace(input.AccountDeletionIdentityKey)),
		AccountDeletionIdentityKeyVersion: input.AccountDeletionIdentityKeyVersion,
		DatabaseURL:                       Secret(strings.TrimSpace(input.DatabaseURL)),
		RedisURL:                          Secret(strings.TrimSpace(input.RedisURL)),
		TokenSigningKey:                   Secret(strings.TrimSpace(input.TokenSigningKey)),
		PostgresMaxConns:                  input.PostgresMaxConns,
		PostgresMinConns:                  input.PostgresMinConns,
		PostgresMaxConnLifetime:           input.PostgresMaxConnLifetime,
		PostgresMaxConnIdleTime:           input.PostgresMaxConnIdleTime,
		PostgresHealthCheckPeriod:         input.PostgresHealthCheckPeriod,
		PostgresConnectTimeout:            input.PostgresConnectTimeout,
		RedisPoolSize:                     input.RedisPoolSize,
		RedisMinIdleConns:                 input.RedisMinIdleConns,
		RedisDialTimeout:                  input.RedisDialTimeout,
		RedisReadTimeout:                  input.RedisReadTimeout,
		RedisWriteTimeout:                 input.RedisWriteTimeout,
		RedisPoolTimeout:                  input.RedisPoolTimeout,
		LogLevel:                          input.LogLevel,
		ListenAddr:                        input.ListenAddr,
		MetricsListenAddr:                 input.MetricsListenAddr,
	})
}

// NewForTest is an explicit name for construction from already supplied test
// values; it intentionally shares the same validation as New.
func NewForTest(input Input) (Config, error) { return New(input) }

// FromValues is a descriptive alias for explicit construction.
func FromValues(input Input) (Config, error) { return New(input) }

// Source provides the small set of system operations needed by Load. Tests
// can supply deterministic environment and file implementations without
// mutating process-global environment variables.
type Source struct {
	// Env is an optional explicit environment map. LookupEnv takes precedence
	// when both are supplied.
	Env       map[string]string
	LookupEnv func(string) (string, bool)
	ReadFile  func(string) ([]byte, error)
	Stat      func(string) (fs.FileInfo, error)
}

func (s Source) withDefaults() Source {
	if s.LookupEnv == nil {
		if s.Env != nil {
			s.LookupEnv = func(name string) (string, bool) {
				value, ok := s.Env[name]
				return value, ok
			}
		} else {
			s.LookupEnv = os.LookupEnv
		}
	}
	if s.ReadFile == nil {
		s.ReadFile = os.ReadFile
	}
	if s.Stat == nil {
		s.Stat = os.Lstat
	}
	return s
}

// Load reads the process environment and referenced secret files.
func Load(sources ...Source) (Config, error) {
	if len(sources) == 0 {
		return LoadSource(Source{})
	}
	return LoadSource(sources[0])
}

// LoadWith is an explicit-source spelling useful to callers that avoid
// variadic construction in APIs or tests.
func LoadWith(source Source) (Config, error) { return LoadSource(source) }

// LoadFromEnv is a convenience for tests. The supplied map is read only; it
// is never modified and secret values still come from the referenced files.
func LoadFromEnv(env map[string]string) (Config, error) {
	return LoadSource(Source{
		LookupEnv: func(name string) (string, bool) {
			value, ok := env[name]
			return value, ok
		},
	})
}

// LoadFrom is retained as a short alias for LoadFromEnv for callers that use
// an explicit environment map in tests.
func LoadFrom(env map[string]string) (Config, error) { return LoadFromEnv(env) }

// LoadEnv is a descriptive alias for LoadFromEnv.
func LoadEnv(env map[string]string) (Config, error) { return LoadFromEnv(env) }

// LoadSource loads and validates configuration through an injectable Source.
func LoadSource(source Source) (Config, error) {
	source = source.withDefaults()
	lookupRequired := func(name string) (string, error) {
		value, ok := source.LookupEnv(name)
		if !ok || strings.TrimSpace(value) == "" {
			return "", fmt.Errorf("%w: %s", ErrMissing, name)
		}
		return strings.TrimSpace(value), nil
	}
	lookupOptional := func(name, fallback string) string {
		value, ok := source.LookupEnv(name)
		if !ok || strings.TrimSpace(value) == "" {
			return fallback
		}
		return strings.TrimSpace(value)
	}

	runtimeMode := strings.ToLower(lookupOptional("TERMLINK_RUNTIME_MODE", RuntimeModeDistributed))
	publicURL, err := lookupRequired("TERMLINK_PUBLIC_URL")
	if err != nil {
		return Config{}, err
	}
	oidcIssuer, err := lookupRequired("TERMLINK_OIDC_ISSUER")
	if err != nil {
		return Config{}, err
	}
	oidcBackchannelIssuer := lookupOptional("TERMLINK_OIDC_BACKCHANNEL_ISSUER", oidcIssuer)
	oidcClientID, err := lookupRequired("TERMLINK_OIDC_CLIENT_ID")
	if err != nil {
		return Config{}, err
	}
	androidOIDCClientID := lookupOptional("TERMLINK_ANDROID_OIDC_CLIENT_ID", "termlink-android")
	androidOIDCRedirectURI := lookupOptional("TERMLINK_ANDROID_OIDC_REDIRECT_URI", "dev.termlink:/oauth2redirect")
	windowsOIDCClientID := lookupOptional("TERMLINK_WINDOWS_OIDC_CLIENT_ID", "termlink-windows")
	windowsRegistrationOIDCParties := strings.Split(lookupOptional(
		"TERMLINK_WINDOWS_REGISTRATION_OIDC_PARTIES",
		windowsOIDCClientID,
	), ",")
	oidcAllowedParties := strings.Split(lookupOptional(
		"TERMLINK_OIDC_ALLOWED_PARTIES",
		androidOIDCClientID+","+windowsOIDCClientID,
	), ",")
	windowsOIDCRedirectURI := lookupOptional("TERMLINK_WINDOWS_OIDC_REDIRECT_URI", "http://127.0.0.1/oauth2/callback")
	webOIDCClientID := lookupOptional("TERMLINK_WEB_OIDC_CLIENT_ID", "termlink-web")
	webOIDCStepUpClientID := lookupOptional("TERMLINK_WEB_STEP_UP_OIDC_CLIENT_ID", "termlink-web-step-up")
	webOIDCRedirectURI := lookupOptional("TERMLINK_WEB_OIDC_REDIRECT_URI", strings.TrimRight(publicURL, "/")+"/auth/callback")
	humanVerificationProvider := strings.ToLower(lookupOptional("TERMLINK_HUMAN_VERIFICATION_PROVIDER", ""))
	if humanVerificationProvider != "" && humanVerificationProvider != "turnstile" {
		return Config{}, fmt.Errorf("%w: TERMLINK_HUMAN_VERIFICATION_PROVIDER", ErrInvalid)
	}
	humanVerificationSiteKey := lookupOptional("TERMLINK_HUMAN_VERIFICATION_SITE_KEY", "")
	registrationRecoveryEnabled, err := parseBool("TERMLINK_REGISTRATION_RECOVERY_ENABLED", lookupOptional("TERMLINK_REGISTRATION_RECOVERY_ENABLED", "false"))
	if err != nil {
		return Config{}, err
	}
	accountDeletionEnabled, err := parseBool("TERMLINK_ACCOUNT_DELETION_ENABLED", lookupOptional("TERMLINK_ACCOUNT_DELETION_ENABLED", "false"))
	if err != nil {
		return Config{}, err
	}
	accountDeletionIdentityKeyVersion, err := parseIntRange(
		"TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION",
		lookupOptional("TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION", "1"),
		1,
		32767,
	)
	if err != nil {
		return Config{}, err
	}
	keycloakAdminClientID := lookupOptional("TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID", defaultKeycloakAdminClientID)
	keycloakAdminBaseURL := lookupOptional("TERMLINK_KEYCLOAK_ADMIN_BASE_URL", "")
	logLevel := lookupOptional("TERMLINK_LOG_LEVEL", defaultLogLevel)
	listenAddr := lookupOptional("TERMLINK_LISTEN_ADDR", defaultListenAddr)
	metricsListenAddr := lookupOptional("TERMLINK_METRICS_LISTEN_ADDR", "")
	postgresMaxConns, err := parseIntRange("TERMLINK_POSTGRES_MAX_CONNS", lookupOptional("TERMLINK_POSTGRES_MAX_CONNS", strconv.Itoa(int(defaultPostgresMaxConns))), 1, 1000)
	if err != nil {
		return Config{}, err
	}
	postgresMinConns, err := parseIntRange("TERMLINK_POSTGRES_MIN_CONNS", lookupOptional("TERMLINK_POSTGRES_MIN_CONNS", strconv.Itoa(int(defaultPostgresMinConns))), 0, postgresMaxConns)
	if err != nil {
		return Config{}, err
	}
	postgresMaxConnLifetime, err := parseDurationRange("TERMLINK_POSTGRES_MAX_CONN_LIFETIME", lookupOptional("TERMLINK_POSTGRES_MAX_CONN_LIFETIME", defaultPostgresMaxConnLifetime.String()), time.Minute, 24*time.Hour)
	if err != nil {
		return Config{}, err
	}
	postgresMaxConnIdleTime, err := parseDurationRange("TERMLINK_POSTGRES_MAX_CONN_IDLE_TIME", lookupOptional("TERMLINK_POSTGRES_MAX_CONN_IDLE_TIME", defaultPostgresMaxConnIdleTime.String()), time.Minute, 24*time.Hour)
	if err != nil {
		return Config{}, err
	}
	postgresHealthCheckPeriod, err := parseDurationRange("TERMLINK_POSTGRES_HEALTH_CHECK_PERIOD", lookupOptional("TERMLINK_POSTGRES_HEALTH_CHECK_PERIOD", defaultPostgresHealthCheckPeriod.String()), 5*time.Second, 10*time.Minute)
	if err != nil {
		return Config{}, err
	}
	postgresConnectTimeout, err := parseDurationRange("TERMLINK_POSTGRES_CONNECT_TIMEOUT", lookupOptional("TERMLINK_POSTGRES_CONNECT_TIMEOUT", defaultPostgresConnectTimeout.String()), 100*time.Millisecond, 2*time.Minute)
	if err != nil {
		return Config{}, err
	}
	redisPoolSize, err := parseIntRange("TERMLINK_REDIS_POOL_SIZE", lookupOptional("TERMLINK_REDIS_POOL_SIZE", strconv.Itoa(defaultRedisPoolSize)), 1, 1000)
	if err != nil {
		return Config{}, err
	}
	redisMinIdleConns, err := parseIntRange("TERMLINK_REDIS_MIN_IDLE_CONNS", lookupOptional("TERMLINK_REDIS_MIN_IDLE_CONNS", strconv.Itoa(defaultRedisMinIdleConns)), 0, redisPoolSize)
	if err != nil {
		return Config{}, err
	}
	redisDialTimeout, err := parseDurationRange("TERMLINK_REDIS_DIAL_TIMEOUT", lookupOptional("TERMLINK_REDIS_DIAL_TIMEOUT", defaultRedisDialTimeout.String()), 100*time.Millisecond, 2*time.Minute)
	if err != nil {
		return Config{}, err
	}
	redisReadTimeout, err := parseDurationRange("TERMLINK_REDIS_READ_TIMEOUT", lookupOptional("TERMLINK_REDIS_READ_TIMEOUT", defaultRedisReadTimeout.String()), 100*time.Millisecond, 2*time.Minute)
	if err != nil {
		return Config{}, err
	}
	redisWriteTimeout, err := parseDurationRange("TERMLINK_REDIS_WRITE_TIMEOUT", lookupOptional("TERMLINK_REDIS_WRITE_TIMEOUT", defaultRedisWriteTimeout.String()), 100*time.Millisecond, 2*time.Minute)
	if err != nil {
		return Config{}, err
	}
	redisPoolTimeout, err := parseDurationRange("TERMLINK_REDIS_POOL_TIMEOUT", lookupOptional("TERMLINK_REDIS_POOL_TIMEOUT", defaultRedisPoolTimeout.String()), 100*time.Millisecond, 2*time.Minute)
	if err != nil {
		return Config{}, err
	}

	oidcSecret, err := readSecret(source, "TERMLINK_OIDC_CLIENT_SECRET_FILE")
	if err != nil {
		return Config{}, err
	}
	databaseURL, err := readSecret(source, "TERMLINK_DATABASE_URL_FILE")
	if err != nil {
		return Config{}, err
	}
	var redisURL Secret
	if runtimeMode != RuntimeModeSingleNode {
		redisURL, err = readSecret(source, "TERMLINK_REDIS_URL_FILE")
		if err != nil {
			return Config{}, err
		}
	}
	tokenSigningKey, err := readSecret(source, "TERMLINK_TOKEN_SIGNING_KEY_FILE")
	if err != nil {
		return Config{}, err
	}
	var keycloakAdminClientSecret Secret
	if registrationRecoveryEnabled || accountDeletionEnabled {
		keycloakAdminClientSecret, err = readSecret(source, "TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE")
		if err != nil {
			return Config{}, err
		}
	}
	var humanVerificationSecret Secret
	if humanVerificationProvider == "turnstile" {
		humanVerificationSecret, err = readSecret(source, "TERMLINK_HUMAN_VERIFICATION_SECRET_FILE")
		if err != nil {
			return Config{}, err
		}
	}
	var accountDeletionIdentityKey Secret
	if accountDeletionEnabled {
		accountDeletionIdentityKey, err = readSecret(source, "TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE")
		if err != nil {
			return Config{}, err
		}
	}

	return validate(Config{
		RuntimeMode:                       runtimeMode,
		PublicURL:                         publicURL,
		OIDCIssuer:                        oidcIssuer,
		OIDCBackchannelIssuer:             oidcBackchannelIssuer,
		OIDCClientID:                      oidcClientID,
		OIDCAllowedParties:                oidcAllowedParties,
		AndroidOIDCClientID:               androidOIDCClientID,
		AndroidOIDCRedirectURI:            androidOIDCRedirectURI,
		WindowsOIDCClientID:               windowsOIDCClientID,
		WindowsRegistrationOIDCParties:    windowsRegistrationOIDCParties,
		WindowsOIDCRedirectURI:            windowsOIDCRedirectURI,
		WebOIDCClientID:                   webOIDCClientID,
		WebOIDCStepUpClientID:             webOIDCStepUpClientID,
		WebOIDCRedirectURI:                webOIDCRedirectURI,
		HumanVerificationProvider:         humanVerificationProvider,
		HumanVerificationSiteKey:          humanVerificationSiteKey,
		HumanVerificationSecret:           humanVerificationSecret,
		OIDCClientSecret:                  oidcSecret,
		KeycloakAdminClientSecret:         keycloakAdminClientSecret,
		KeycloakAdminClientID:             keycloakAdminClientID,
		KeycloakAdminBaseURL:              keycloakAdminBaseURL,
		RegistrationRecoveryEnabled:       registrationRecoveryEnabled,
		AccountDeletionEnabled:            accountDeletionEnabled,
		AccountDeletionIdentityKey:        accountDeletionIdentityKey,
		AccountDeletionIdentityKeyVersion: int16(accountDeletionIdentityKeyVersion),
		DatabaseURL:                       databaseURL,
		RedisURL:                          redisURL,
		TokenSigningKey:                   tokenSigningKey,
		OIDCClientSecretFile:              envPath(source, "TERMLINK_OIDC_CLIENT_SECRET_FILE"),
		KeycloakAdminClientSecretFile:     envPath(source, "TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE"),
		HumanVerificationSecretFile:       envPath(source, "TERMLINK_HUMAN_VERIFICATION_SECRET_FILE"),
		AccountDeletionIdentityKeyFile:    envPath(source, "TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE"),
		DatabaseURLFile:                   envPath(source, "TERMLINK_DATABASE_URL_FILE"),
		RedisURLFile:                      envPath(source, "TERMLINK_REDIS_URL_FILE"),
		TokenSigningKeyFile:               envPath(source, "TERMLINK_TOKEN_SIGNING_KEY_FILE"),
		PostgresMaxConns:                  int32(postgresMaxConns),
		PostgresMinConns:                  int32(postgresMinConns),
		PostgresMaxConnLifetime:           postgresMaxConnLifetime,
		PostgresMaxConnIdleTime:           postgresMaxConnIdleTime,
		PostgresHealthCheckPeriod:         postgresHealthCheckPeriod,
		PostgresConnectTimeout:            postgresConnectTimeout,
		RedisPoolSize:                     redisPoolSize,
		RedisMinIdleConns:                 redisMinIdleConns,
		RedisDialTimeout:                  redisDialTimeout,
		RedisReadTimeout:                  redisReadTimeout,
		RedisWriteTimeout:                 redisWriteTimeout,
		RedisPoolTimeout:                  redisPoolTimeout,
		LogLevel:                          logLevel,
		ListenAddr:                        listenAddr,
		MetricsListenAddr:                 metricsListenAddr,
	})
}

func readSecret(source Source, envName string) (Secret, error) {
	path, ok := source.LookupEnv(envName)
	if !ok || strings.TrimSpace(path) == "" {
		return "", fmt.Errorf("%w: %s", ErrMissing, envName)
	}
	path = filepath.Clean(path)
	info, err := source.Stat(path)
	if err != nil || info == nil || info.IsDir() {
		return "", fmt.Errorf("%w: %s", ErrSecret, envName)
	}
	// Docker secrets and local fixture files must be owner-readable only. This
	// rejects group/world access, write bits, and executable secret files.
	if runtime.GOOS != "windows" {
		permissions := info.Mode().Perm()
		if permissions&0o077 != 0 || permissions&0o400 == 0 {
			return "", fmt.Errorf("%w: %s", ErrSecret, envName)
		}
	}
	data, err := source.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("%w: %s", ErrSecret, envName)
	}
	value := strings.TrimSpace(string(data))
	if value == "" {
		return "", fmt.Errorf("%w: %s", ErrSecret, envName)
	}
	return Secret(value), nil
}

func envPath(source Source, envName string) string {
	value, ok := source.LookupEnv(envName)
	if !ok {
		return ""
	}
	return filepath.Clean(strings.TrimSpace(value))
}

func parseBool(name, value string) (bool, error) {
	parsed, err := strconv.ParseBool(strings.TrimSpace(value))
	if err != nil {
		return false, fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	return parsed, nil
}

func parseIntRange(name, value string, minimum, maximum int) (int, error) {
	parsed, err := strconv.Atoi(strings.TrimSpace(value))
	if err != nil || parsed < minimum || parsed > maximum {
		return 0, fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	return parsed, nil
}

func parseDurationRange(name, value string, minimum, maximum time.Duration) (time.Duration, error) {
	parsed, err := time.ParseDuration(strings.TrimSpace(value))
	if err != nil || parsed < minimum || parsed > maximum {
		return 0, fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	return parsed, nil
}

func validate(c Config) (Config, error) {
	c.RuntimeMode = strings.ToLower(strings.TrimSpace(c.RuntimeMode))
	if c.RuntimeMode == "" {
		c.RuntimeMode = RuntimeModeDistributed
	}
	if c.RuntimeMode != RuntimeModeDistributed && c.RuntimeMode != RuntimeModeSingleNode {
		return Config{}, fmt.Errorf("%w: TERMLINK_RUNTIME_MODE", ErrInvalid)
	}
	if err := validateHTTPSURL("TERMLINK_PUBLIC_URL", c.PublicURL); err != nil {
		return Config{}, err
	}
	if err := validateHTTPSURL("TERMLINK_OIDC_ISSUER", c.OIDCIssuer); err != nil {
		return Config{}, err
	}
	if strings.TrimSpace(c.OIDCBackchannelIssuer) == "" {
		c.OIDCBackchannelIssuer = c.OIDCIssuer
	}
	if err := validateOIDCBackchannelIssuer(c.OIDCIssuer, c.OIDCBackchannelIssuer); err != nil {
		return Config{}, err
	}
	c.HumanVerificationProvider = strings.ToLower(strings.TrimSpace(c.HumanVerificationProvider))
	c.HumanVerificationSiteKey = strings.TrimSpace(c.HumanVerificationSiteKey)
	if c.HumanVerificationProvider == "" {
		if c.HumanVerificationSiteKey != "" || !c.HumanVerificationSecret.Empty() {
			return Config{}, fmt.Errorf("%w: TERMLINK_HUMAN_VERIFICATION_PROVIDER", ErrInvalid)
		}
	} else if c.HumanVerificationProvider != "turnstile" {
		return Config{}, fmt.Errorf("%w: TERMLINK_HUMAN_VERIFICATION_PROVIDER", ErrInvalid)
	} else {
		if c.HumanVerificationSiteKey == "" || len(c.HumanVerificationSiteKey) > 256 ||
			strings.ContainsAny(c.HumanVerificationSiteKey, " \t\r\n") {
			return Config{}, fmt.Errorf("%w: TERMLINK_HUMAN_VERIFICATION_SITE_KEY", ErrInvalid)
		}
		if c.HumanVerificationSecret.Empty() {
			return Config{}, fmt.Errorf("%w: TERMLINK_HUMAN_VERIFICATION_SECRET_FILE", ErrSecret)
		}
	}
	if strings.TrimSpace(c.OIDCClientID) == "" {
		return Config{}, fmt.Errorf("%w: TERMLINK_OIDC_CLIENT_ID", ErrMissing)
	}
	if strings.TrimSpace(c.AndroidOIDCClientID) == "" {
		c.AndroidOIDCClientID = "termlink-android"
	}
	if strings.TrimSpace(c.AndroidOIDCRedirectURI) == "" {
		c.AndroidOIDCRedirectURI = "dev.termlink:/oauth2redirect"
	}
	redirect, err := url.Parse(c.AndroidOIDCRedirectURI)
	if err != nil || redirect.Scheme != "dev.termlink" || redirect.Host != "" || redirect.Path != "/oauth2redirect" {
		return Config{}, fmt.Errorf("%w: TERMLINK_ANDROID_OIDC_REDIRECT_URI", ErrInvalid)
	}
	if strings.TrimSpace(c.WindowsOIDCClientID) == "" {
		c.WindowsOIDCClientID = "termlink-windows"
	}
	if c.WindowsOIDCClientID == c.AndroidOIDCClientID {
		return Config{}, fmt.Errorf("%w: Android and Windows OIDC clients must be distinct", ErrInvalid)
	}
	if len(c.OIDCAllowedParties) == 0 {
		c.OIDCAllowedParties = []string{c.AndroidOIDCClientID, c.WindowsOIDCClientID}
	}
	allowed := make([]string, 0, len(c.OIDCAllowedParties))
	seenAllowed := make(map[string]struct{}, len(c.OIDCAllowedParties))
	for _, party := range c.OIDCAllowedParties {
		party = strings.TrimSpace(party)
		if party == "" || len(party) > 128 || party == c.OIDCClientID || strings.ContainsAny(party, " \t\r\n") {
			return Config{}, fmt.Errorf("%w: TERMLINK_OIDC_ALLOWED_PARTIES", ErrInvalid)
		}
		if _, duplicate := seenAllowed[party]; duplicate {
			continue
		}
		seenAllowed[party] = struct{}{}
		allowed = append(allowed, party)
	}
	if _, ok := seenAllowed[c.AndroidOIDCClientID]; !ok {
		return Config{}, fmt.Errorf("%w: TERMLINK_OIDC_ALLOWED_PARTIES", ErrInvalid)
	}
	if _, ok := seenAllowed[c.WindowsOIDCClientID]; !ok {
		return Config{}, fmt.Errorf("%w: TERMLINK_OIDC_ALLOWED_PARTIES", ErrInvalid)
	}
	c.OIDCAllowedParties = allowed
	if len(c.WindowsRegistrationOIDCParties) == 0 {
		c.WindowsRegistrationOIDCParties = []string{c.WindowsOIDCClientID}
	}
	windowsRegistrationParties := make([]string, 0, len(c.WindowsRegistrationOIDCParties))
	seenWindowsRegistrationParty := make(map[string]struct{}, len(c.WindowsRegistrationOIDCParties))
	for _, party := range c.WindowsRegistrationOIDCParties {
		party = strings.TrimSpace(party)
		_, tokenAllowed := seenAllowed[party]
		if party == "" || party == c.AndroidOIDCClientID || !tokenAllowed {
			return Config{}, fmt.Errorf("%w: TERMLINK_WINDOWS_REGISTRATION_OIDC_PARTIES", ErrInvalid)
		}
		if _, duplicate := seenWindowsRegistrationParty[party]; duplicate {
			continue
		}
		seenWindowsRegistrationParty[party] = struct{}{}
		windowsRegistrationParties = append(windowsRegistrationParties, party)
	}
	if _, ok := seenWindowsRegistrationParty[c.WindowsOIDCClientID]; !ok {
		return Config{}, fmt.Errorf("%w: TERMLINK_WINDOWS_REGISTRATION_OIDC_PARTIES", ErrInvalid)
	}
	c.WindowsRegistrationOIDCParties = windowsRegistrationParties
	if strings.TrimSpace(c.WindowsOIDCRedirectURI) == "" {
		c.WindowsOIDCRedirectURI = "http://127.0.0.1/oauth2/callback"
	}
	windowsRedirect, err := url.Parse(c.WindowsOIDCRedirectURI)
	if err != nil || windowsRedirect.Scheme != "http" || windowsRedirect.Host != "127.0.0.1" ||
		windowsRedirect.Path != "/oauth2/callback" || windowsRedirect.RawPath != "" ||
		windowsRedirect.RawQuery != "" || windowsRedirect.ForceQuery || windowsRedirect.Fragment != "" ||
		windowsRedirect.User != nil {
		return Config{}, fmt.Errorf("%w: TERMLINK_WINDOWS_OIDC_REDIRECT_URI", ErrInvalid)
	}
	if c.OIDCClientSecret.Empty() {
		return Config{}, fmt.Errorf("%w: TERMLINK_OIDC_CLIENT_SECRET_FILE", ErrSecret)
	}
	if strings.TrimSpace(c.KeycloakAdminClientID) == "" {
		c.KeycloakAdminClientID = defaultKeycloakAdminClientID
	}
	adminIdentityRequired := c.RegistrationRecoveryEnabled || c.AccountDeletionEnabled
	if adminIdentityRequired {
		if len(c.KeycloakAdminClientID) > 128 || strings.ContainsAny(c.KeycloakAdminClientID, " \t\r\n") {
			return Config{}, fmt.Errorf("%w: TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID", ErrInvalid)
		}
		if c.KeycloakAdminClientSecret.Empty() {
			return Config{}, fmt.Errorf("%w: TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE", ErrSecret)
		}
		if c.KeycloakAdminBaseURL != "" {
			if err := validateKeycloakAdminBaseURL(c.KeycloakAdminBaseURL); err != nil {
				return Config{}, err
			}
		}
	}
	if c.AccountDeletionIdentityKeyVersion == 0 && !c.AccountDeletionEnabled {
		c.AccountDeletionIdentityKeyVersion = 1
	}
	if c.AccountDeletionEnabled {
		if c.AccountDeletionIdentityKeyVersion < 1 {
			return Config{}, fmt.Errorf("%w: TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION", ErrInvalid)
		}
		keys, err := c.AccountDeletionIdentityKeys()
		if err != nil {
			return Config{}, err
		}
		clearAccountDeletionIdentityKeys(keys)
	}
	if strings.TrimSpace(c.WebOIDCClientID) == "" {
		c.WebOIDCClientID = "termlink-web"
	}
	if strings.TrimSpace(c.WebOIDCStepUpClientID) == "" {
		c.WebOIDCStepUpClientID = "termlink-web-step-up"
	}
	if c.WebOIDCStepUpClientID == c.WebOIDCClientID {
		return Config{}, fmt.Errorf("%w: TERMLINK_WEB_STEP_UP_OIDC_CLIENT_ID must differ from TERMLINK_WEB_OIDC_CLIENT_ID", ErrInvalid)
	}
	if strings.TrimSpace(c.WebOIDCRedirectURI) == "" {
		c.WebOIDCRedirectURI = strings.TrimRight(c.PublicURL, "/") + "/auth/callback"
	}
	publicURL, _ := url.Parse(c.PublicURL)
	webRedirect, err := url.Parse(c.WebOIDCRedirectURI)
	if err != nil || webRedirect.Scheme != "https" ||
		!strings.EqualFold(webRedirect.Host, publicURL.Host) || webRedirect.Path != "/auth/callback" ||
		webRedirect.RawQuery != "" || webRedirect.Fragment != "" || webRedirect.User != nil {
		return Config{}, fmt.Errorf("%w: TERMLINK_WEB_OIDC_REDIRECT_URI", ErrInvalid)
	}
	if c.DatabaseURL.Empty() {
		return Config{}, fmt.Errorf("%w: TERMLINK_DATABASE_URL_FILE", ErrSecret)
	}
	if c.RuntimeMode == RuntimeModeDistributed && c.RedisURL.Empty() {
		return Config{}, fmt.Errorf("%w: TERMLINK_REDIS_URL_FILE", ErrSecret)
	}
	if c.TokenSigningKey.Empty() {
		return Config{}, fmt.Errorf("%w: TERMLINK_TOKEN_SIGNING_KEY_FILE", ErrSecret)
	}
	if len(c.TokenSigningKey.Value()) < 32 {
		return Config{}, fmt.Errorf("%w: TERMLINK_TOKEN_SIGNING_KEY_FILE", ErrSecret)
	}
	postgresPoolUnspecified := c.PostgresMaxConns == 0 && c.PostgresMinConns == 0 &&
		c.PostgresMaxConnLifetime == 0 && c.PostgresMaxConnIdleTime == 0 &&
		c.PostgresHealthCheckPeriod == 0 && c.PostgresConnectTimeout == 0
	if c.PostgresMaxConns == 0 {
		c.PostgresMaxConns = defaultPostgresMaxConns
	}
	if postgresPoolUnspecified {
		c.PostgresMinConns = defaultPostgresMinConns
	}
	if c.PostgresMaxConnLifetime == 0 {
		c.PostgresMaxConnLifetime = defaultPostgresMaxConnLifetime
	}
	if c.PostgresMaxConnIdleTime == 0 {
		c.PostgresMaxConnIdleTime = defaultPostgresMaxConnIdleTime
	}
	if c.PostgresHealthCheckPeriod == 0 {
		c.PostgresHealthCheckPeriod = defaultPostgresHealthCheckPeriod
	}
	if c.PostgresConnectTimeout == 0 {
		c.PostgresConnectTimeout = defaultPostgresConnectTimeout
	}
	if c.PostgresMaxConns < 1 || c.PostgresMaxConns > 1000 {
		return Config{}, fmt.Errorf("%w: TERMLINK_POSTGRES_MAX_CONNS", ErrInvalid)
	}
	if c.PostgresMinConns < 0 || c.PostgresMinConns > c.PostgresMaxConns {
		return Config{}, fmt.Errorf("%w: TERMLINK_POSTGRES_MIN_CONNS", ErrInvalid)
	}
	if c.PostgresMaxConnLifetime < time.Minute || c.PostgresMaxConnLifetime > 24*time.Hour {
		return Config{}, fmt.Errorf("%w: TERMLINK_POSTGRES_MAX_CONN_LIFETIME", ErrInvalid)
	}
	if c.PostgresMaxConnIdleTime < time.Minute || c.PostgresMaxConnIdleTime > 24*time.Hour {
		return Config{}, fmt.Errorf("%w: TERMLINK_POSTGRES_MAX_CONN_IDLE_TIME", ErrInvalid)
	}
	if c.PostgresHealthCheckPeriod < 5*time.Second || c.PostgresHealthCheckPeriod > 10*time.Minute {
		return Config{}, fmt.Errorf("%w: TERMLINK_POSTGRES_HEALTH_CHECK_PERIOD", ErrInvalid)
	}
	if c.PostgresConnectTimeout < 100*time.Millisecond || c.PostgresConnectTimeout > 2*time.Minute {
		return Config{}, fmt.Errorf("%w: TERMLINK_POSTGRES_CONNECT_TIMEOUT", ErrInvalid)
	}

	redisPoolUnspecified := c.RedisPoolSize == 0 && c.RedisMinIdleConns == 0 &&
		c.RedisDialTimeout == 0 && c.RedisReadTimeout == 0 &&
		c.RedisWriteTimeout == 0 && c.RedisPoolTimeout == 0
	if c.RedisPoolSize == 0 {
		c.RedisPoolSize = defaultRedisPoolSize
	}
	if redisPoolUnspecified {
		c.RedisMinIdleConns = defaultRedisMinIdleConns
	}
	if c.RedisDialTimeout == 0 {
		c.RedisDialTimeout = defaultRedisDialTimeout
	}
	if c.RedisReadTimeout == 0 {
		c.RedisReadTimeout = defaultRedisReadTimeout
	}
	if c.RedisWriteTimeout == 0 {
		c.RedisWriteTimeout = defaultRedisWriteTimeout
	}
	if c.RedisPoolTimeout == 0 {
		c.RedisPoolTimeout = defaultRedisPoolTimeout
	}
	if c.RedisPoolSize < 1 || c.RedisPoolSize > 1000 {
		return Config{}, fmt.Errorf("%w: TERMLINK_REDIS_POOL_SIZE", ErrInvalid)
	}
	if c.RedisMinIdleConns < 0 || c.RedisMinIdleConns > c.RedisPoolSize {
		return Config{}, fmt.Errorf("%w: TERMLINK_REDIS_MIN_IDLE_CONNS", ErrInvalid)
	}
	if c.RedisDialTimeout < 100*time.Millisecond || c.RedisDialTimeout > 2*time.Minute {
		return Config{}, fmt.Errorf("%w: TERMLINK_REDIS_DIAL_TIMEOUT", ErrInvalid)
	}
	if c.RedisReadTimeout < 100*time.Millisecond || c.RedisReadTimeout > 2*time.Minute {
		return Config{}, fmt.Errorf("%w: TERMLINK_REDIS_READ_TIMEOUT", ErrInvalid)
	}
	if c.RedisWriteTimeout < 100*time.Millisecond || c.RedisWriteTimeout > 2*time.Minute {
		return Config{}, fmt.Errorf("%w: TERMLINK_REDIS_WRITE_TIMEOUT", ErrInvalid)
	}
	if c.RedisPoolTimeout < 100*time.Millisecond || c.RedisPoolTimeout > 2*time.Minute {
		return Config{}, fmt.Errorf("%w: TERMLINK_REDIS_POOL_TIMEOUT", ErrInvalid)
	}
	level := strings.ToLower(strings.TrimSpace(c.LogLevel))
	if level == "" {
		level = defaultLogLevel
	}
	switch level {
	case "debug", "info", "warn", "error":
		c.LogLevel = level
	default:
		return Config{}, fmt.Errorf("%w: TERMLINK_LOG_LEVEL", ErrInvalid)
	}
	if strings.TrimSpace(c.ListenAddr) == "" {
		c.ListenAddr = defaultListenAddr
	}
	c.MetricsListenAddr = strings.TrimSpace(c.MetricsListenAddr)
	if c.MetricsListenAddr != "" {
		if err := validateListenAddr("TERMLINK_METRICS_LISTEN_ADDR", c.MetricsListenAddr); err != nil {
			return Config{}, err
		}
		if c.MetricsListenAddr == c.ListenAddr {
			return Config{}, fmt.Errorf("%w: TERMLINK_METRICS_LISTEN_ADDR must differ from TERMLINK_LISTEN_ADDR", ErrInvalid)
		}
	}
	return c, nil
}

func normalizeAbsoluteDirectory(name, value string) (string, error) {
	value = strings.TrimSpace(value)
	cleaned := filepath.Clean(value)
	// Production runs in a Linux container even when Compose is rendered or
	// tests run on Windows, so recognize and preserve an absolute POSIX path.
	if path.IsAbs(value) {
		cleaned = path.Clean(value)
	}
	if !path.IsAbs(cleaned) && !filepath.IsAbs(cleaned) {
		return "", fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	if cleaned == "/" || cleaned == `\` || filepath.Dir(cleaned) == cleaned {
		return "", fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	return cleaned, nil
}

func validateListenAddr(name, value string) error {
	host, port, err := net.SplitHostPort(value)
	if err != nil || port == "" {
		return fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	if parsed, err := strconv.ParseUint(port, 10, 16); err != nil || parsed == 0 {
		return fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	if host != "" {
		if ip := net.ParseIP(host); ip == nil && !strings.EqualFold(host, "localhost") {
			return fmt.Errorf("%w: %s", ErrInvalid, name)
		}
	}
	return nil
}

func validateURL(name, value string) error {
	parsed, err := url.Parse(strings.TrimSpace(value))
	if err != nil || parsed.Scheme == "" || parsed.Host == "" || parsed.User != nil {
		return fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	switch strings.ToLower(parsed.Scheme) {
	case "http", "https":
		return nil
	default:
		return fmt.Errorf("%w: %s", ErrInvalid, name)
	}
}

func validateHTTPSURL(name, value string) error {
	if err := validateURL(name, value); err != nil {
		return err
	}
	parsed, _ := url.Parse(strings.TrimSpace(value))
	if !strings.EqualFold(parsed.Scheme, "https") {
		return fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	return nil
}

func validateOIDCBackchannelIssuer(publicIssuer, value string) error {
	const name = "TERMLINK_OIDC_BACKCHANNEL_ISSUER"
	public, publicErr := url.Parse(strings.TrimRight(strings.TrimSpace(publicIssuer), "/"))
	parsed, err := url.Parse(strings.TrimRight(strings.TrimSpace(value), "/"))
	if publicErr != nil || parsed == nil || err != nil || parsed.Opaque != "" || parsed.Host == "" ||
		parsed.User != nil || parsed.RawQuery != "" || parsed.Fragment != "" || parsed.RawPath != "" ||
		(parsed.Scheme != "https" && parsed.Scheme != "http") ||
		strings.Contains(parsed.Path, "\\") || strings.Contains(parsed.Path, "//") {
		return fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	if strings.Trim(parsed.Path, "/") != "" {
		for _, segment := range strings.Split(strings.Trim(parsed.Path, "/"), "/") {
			if segment == "" || segment == "." || segment == ".." {
				return fmt.Errorf("%w: %s", ErrInvalid, name)
			}
		}
	}
	if parsed.Scheme == "http" {
		if parsed.Port() == "" || !isLoopbackOrSingleLabelHost(parsed.Hostname()) {
			return fmt.Errorf("%w: %s", ErrInvalid, name)
		}
	}
	// A same-origin HTTPS issuer remains the ordinary distributed-provider
	// path. A distinct HTTPS backchannel may be a private service mesh name.
	if strings.EqualFold(parsed.Scheme, public.Scheme) && strings.EqualFold(parsed.Host, public.Host) &&
		strings.TrimRight(parsed.Path, "/") != strings.TrimRight(public.Path, "/") {
		return fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	return nil
}

func validateKeycloakAdminBaseURL(value string) error {
	const name = "TERMLINK_KEYCLOAK_ADMIN_BASE_URL"
	parsed, err := url.Parse(strings.TrimSpace(value))
	if err != nil || parsed == nil || parsed.Opaque != "" || parsed.Host == "" || parsed.User != nil ||
		parsed.RawQuery != "" || parsed.Fragment != "" || parsed.RawPath != "" ||
		(parsed.Scheme != "https" && parsed.Scheme != "http") || strings.Contains(parsed.Path, "\\") ||
		strings.Contains(parsed.Path, "//") {
		return fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	for _, segment := range strings.Split(strings.Trim(parsed.Path, "/"), "/") {
		if segment == "." || segment == ".." {
			return fmt.Errorf("%w: %s", ErrInvalid, name)
		}
	}
	hostname := parsed.Hostname()
	if parsed.Scheme == "http" {
		if parsed.Port() == "" || !isLoopbackOrSingleLabelHost(hostname) {
			return fmt.Errorf("%w: %s", ErrInvalid, name)
		}
	} else if ip := net.ParseIP(hostname); ip != nil &&
		(ip.IsUnspecified() || ip.IsMulticast() || ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast()) {
		return fmt.Errorf("%w: %s", ErrInvalid, name)
	}
	return nil
}

func isLoopbackOrSingleLabelHost(hostname string) bool {
	if strings.EqualFold(hostname, "localhost") {
		return true
	}
	if ip := net.ParseIP(hostname); ip != nil {
		return ip.IsLoopback()
	}
	if len(hostname) == 0 || len(hostname) > 63 || strings.Contains(hostname, ".") ||
		hostname[0] == '-' || hostname[len(hostname)-1] == '-' {
		return false
	}
	for _, char := range hostname {
		if (char < 'a' || char > 'z') && (char < 'A' || char > 'Z') &&
			(char < '0' || char > '9') && char != '-' {
			return false
		}
	}
	return true
}
