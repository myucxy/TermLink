// Package config loads the non-secret server settings and Docker-secret files
// used by the production server. Secret values are intentionally represented
// by Secret, whose formatting methods never return the underlying value.
package config

import (
	"errors"
	"fmt"
	"io/fs"
	"net/url"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
)

const (
	defaultListenAddr = ":8080"
	defaultLogLevel   = "info"
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
	PublicURL              string
	OIDCIssuer             string
	OIDCClientID           string
	AndroidOIDCClientID    string
	AndroidOIDCRedirectURI string

	OIDCClientSecret Secret
	DatabaseURL      Secret
	RedisURL         Secret
	TokenSigningKey  Secret

	// Secret file paths are retained for diagnostics and dependency wiring, but
	// are omitted from String because deployment paths can be sensitive too.
	OIDCClientSecretFile string
	DatabaseURLFile      string
	RedisURLFile         string
	TokenSigningKeyFile  string

	// S3Endpoint is the server-side endpoint used for bucket and object
	// verification/deletion. S3PublicEndpoint is used only in presigned URLs
	// returned to clients.
	S3Endpoint       string
	S3PublicEndpoint string
	S3Bucket         string
	S3Region         string
	S3AccessKey      Secret
	S3SecretKey      Secret
	S3AccessKeyFile  string
	S3SecretKeyFile  string

	LogLevel       string
	HistoryEnabled bool
	ListenAddr     string
}

// String returns a safe diagnostic representation. It intentionally omits
// secret paths as well as secret values, because paths can contain identifying
// deployment information and are not needed in normal startup diagnostics.
func (c Config) String() string {
	return fmt.Sprintf("Config{PublicURL:%q OIDCIssuer:%q OIDCClientID:%q AndroidOIDCClientID:%q AndroidOIDCRedirectURI:%q OIDCClientSecret:%s DatabaseURL:%s RedisURL:%s TokenSigningKey:%s S3Endpoint:%q S3PublicEndpoint:%q S3Bucket:%q S3Region:%q S3AccessKey:%s S3SecretKey:%s LogLevel:%q HistoryEnabled:%t ListenAddr:%q}",
		c.PublicURL,
		c.OIDCIssuer,
		c.OIDCClientID,
		c.AndroidOIDCClientID,
		c.AndroidOIDCRedirectURI,
		c.OIDCClientSecret,
		c.DatabaseURL,
		c.RedisURL,
		c.TokenSigningKey,
		c.S3Endpoint,
		c.S3PublicEndpoint,
		c.S3Bucket,
		c.S3Region,
		c.S3AccessKey,
		c.S3SecretKey,
		c.LogLevel,
		c.HistoryEnabled,
		c.ListenAddr,
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

// Input is the explicit-construction form used by tests and by embedders that
// obtain secrets from a managed secret provider instead of files. Production
// startup should use Load, which enforces file permissions.
type Input struct {
	PublicURL              string
	OIDCIssuer             string
	OIDCClientID           string
	AndroidOIDCClientID    string
	AndroidOIDCRedirectURI string

	OIDCClientSecret string
	DatabaseURL      string
	RedisURL         string
	TokenSigningKey  string

	S3Endpoint       string
	S3PublicEndpoint string
	S3Bucket         string
	S3Region         string
	S3AccessKey      string
	S3SecretKey      string

	LogLevel       string
	HistoryEnabled bool
	ListenAddr     string
}

// New validates explicitly supplied values. It is useful for focused tests;
// it does not read environment variables or secret files.
func New(input Input) (Config, error) {
	return validate(Config{
		PublicURL:              strings.TrimSpace(input.PublicURL),
		OIDCIssuer:             strings.TrimSpace(input.OIDCIssuer),
		OIDCClientID:           strings.TrimSpace(input.OIDCClientID),
		AndroidOIDCClientID:    strings.TrimSpace(input.AndroidOIDCClientID),
		AndroidOIDCRedirectURI: strings.TrimSpace(input.AndroidOIDCRedirectURI),
		OIDCClientSecret:       Secret(strings.TrimSpace(input.OIDCClientSecret)),
		DatabaseURL:            Secret(strings.TrimSpace(input.DatabaseURL)),
		RedisURL:               Secret(strings.TrimSpace(input.RedisURL)),
		TokenSigningKey:        Secret(strings.TrimSpace(input.TokenSigningKey)),
		S3Endpoint:             strings.TrimSpace(input.S3Endpoint),
		S3PublicEndpoint:       strings.TrimSpace(input.S3PublicEndpoint),
		S3Bucket:               strings.TrimSpace(input.S3Bucket),
		S3Region:               strings.TrimSpace(input.S3Region),
		S3AccessKey:            Secret(strings.TrimSpace(input.S3AccessKey)),
		S3SecretKey:            Secret(strings.TrimSpace(input.S3SecretKey)),
		LogLevel:               input.LogLevel,
		HistoryEnabled:         input.HistoryEnabled,
		ListenAddr:             input.ListenAddr,
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

	publicURL, err := lookupRequired("TERMLINK_PUBLIC_URL")
	if err != nil {
		return Config{}, err
	}
	oidcIssuer, err := lookupRequired("TERMLINK_OIDC_ISSUER")
	if err != nil {
		return Config{}, err
	}
	oidcClientID, err := lookupRequired("TERMLINK_OIDC_CLIENT_ID")
	if err != nil {
		return Config{}, err
	}
	androidOIDCClientID := lookupOptional("TERMLINK_ANDROID_OIDC_CLIENT_ID", "termlink-android")
	androidOIDCRedirectURI := lookupOptional("TERMLINK_ANDROID_OIDC_REDIRECT_URI", "dev.termlink:/oauth2redirect")
	logLevel := lookupOptional("TERMLINK_LOG_LEVEL", defaultLogLevel)
	historyEnabled, err := parseBool("TERMLINK_HISTORY_ENABLED", lookupOptional("TERMLINK_HISTORY_ENABLED", "false"))
	if err != nil {
		return Config{}, err
	}
	listenAddr := lookupOptional("TERMLINK_LISTEN_ADDR", defaultListenAddr)

	oidcSecret, err := readSecret(source, "TERMLINK_OIDC_CLIENT_SECRET_FILE")
	if err != nil {
		return Config{}, err
	}
	databaseURL, err := readSecret(source, "TERMLINK_DATABASE_URL_FILE")
	if err != nil {
		return Config{}, err
	}
	redisURL, err := readSecret(source, "TERMLINK_REDIS_URL_FILE")
	if err != nil {
		return Config{}, err
	}
	tokenSigningKey, err := readSecret(source, "TERMLINK_TOKEN_SIGNING_KEY_FILE")
	if err != nil {
		return Config{}, err
	}

	s3Endpoint := lookupOptional("TERMLINK_S3_ENDPOINT", "")
	s3PublicEndpoint := lookupOptional("TERMLINK_S3_PUBLIC_ENDPOINT", s3Endpoint)
	s3Bucket := lookupOptional("TERMLINK_S3_BUCKET", "termlink-history")
	s3Region := lookupOptional("TERMLINK_S3_REGION", "us-east-1")
	var s3AccessKey, s3SecretKey Secret
	if historyEnabled {
		if s3Endpoint == "" {
			return Config{}, fmt.Errorf("%w: TERMLINK_S3_ENDPOINT", ErrMissing)
		}
		s3AccessKey, err = readSecret(source, "TERMLINK_S3_ACCESS_KEY_FILE")
		if err != nil {
			return Config{}, err
		}
		s3SecretKey, err = readSecret(source, "TERMLINK_S3_SECRET_KEY_FILE")
		if err != nil {
			return Config{}, err
		}
	}

	return validate(Config{
		PublicURL:              publicURL,
		OIDCIssuer:             oidcIssuer,
		OIDCClientID:           oidcClientID,
		AndroidOIDCClientID:    androidOIDCClientID,
		AndroidOIDCRedirectURI: androidOIDCRedirectURI,
		OIDCClientSecret:       oidcSecret,
		DatabaseURL:            databaseURL,
		RedisURL:               redisURL,
		TokenSigningKey:        tokenSigningKey,
		OIDCClientSecretFile:   envPath(source, "TERMLINK_OIDC_CLIENT_SECRET_FILE"),
		DatabaseURLFile:        envPath(source, "TERMLINK_DATABASE_URL_FILE"),
		RedisURLFile:           envPath(source, "TERMLINK_REDIS_URL_FILE"),
		TokenSigningKeyFile:    envPath(source, "TERMLINK_TOKEN_SIGNING_KEY_FILE"),
		S3Endpoint:             s3Endpoint,
		S3PublicEndpoint:       s3PublicEndpoint,
		S3Bucket:               s3Bucket,
		S3Region:               s3Region,
		S3AccessKey:            s3AccessKey,
		S3SecretKey:            s3SecretKey,
		S3AccessKeyFile:        envPath(source, "TERMLINK_S3_ACCESS_KEY_FILE"),
		S3SecretKeyFile:        envPath(source, "TERMLINK_S3_SECRET_KEY_FILE"),
		LogLevel:               logLevel,
		HistoryEnabled:         historyEnabled,
		ListenAddr:             listenAddr,
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

func validate(c Config) (Config, error) {
	if strings.TrimSpace(c.S3Bucket) == "" {
		c.S3Bucket = "termlink-history"
	}
	if strings.TrimSpace(c.S3Region) == "" {
		c.S3Region = "us-east-1"
	}
	if strings.TrimSpace(c.S3PublicEndpoint) == "" {
		c.S3PublicEndpoint = strings.TrimSpace(c.S3Endpoint)
	}
	if err := validateURL("TERMLINK_PUBLIC_URL", c.PublicURL); err != nil {
		return Config{}, err
	}
	if err := validateURL("TERMLINK_OIDC_ISSUER", c.OIDCIssuer); err != nil {
		return Config{}, err
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
	if c.OIDCClientSecret.Empty() {
		return Config{}, fmt.Errorf("%w: TERMLINK_OIDC_CLIENT_SECRET_FILE", ErrSecret)
	}
	if c.DatabaseURL.Empty() {
		return Config{}, fmt.Errorf("%w: TERMLINK_DATABASE_URL_FILE", ErrSecret)
	}
	if c.RedisURL.Empty() {
		return Config{}, fmt.Errorf("%w: TERMLINK_REDIS_URL_FILE", ErrSecret)
	}
	if c.TokenSigningKey.Empty() {
		return Config{}, fmt.Errorf("%w: TERMLINK_TOKEN_SIGNING_KEY_FILE", ErrSecret)
	}
	if len(c.TokenSigningKey.Value()) < 32 {
		return Config{}, fmt.Errorf("%w: TERMLINK_TOKEN_SIGNING_KEY_FILE", ErrSecret)
	}
	if c.S3Endpoint != "" {
		if err := validateURL("TERMLINK_S3_ENDPOINT", c.S3Endpoint); err != nil {
			return Config{}, err
		}
	}
	if c.S3PublicEndpoint != "" {
		if err := validateURL("TERMLINK_S3_PUBLIC_ENDPOINT", c.S3PublicEndpoint); err != nil {
			return Config{}, err
		}
	}
	if c.HistoryEnabled {
		if c.S3Endpoint == "" {
			return Config{}, fmt.Errorf("%w: TERMLINK_S3_ENDPOINT", ErrMissing)
		}
		if c.S3AccessKey.Empty() {
			return Config{}, fmt.Errorf("%w: TERMLINK_S3_ACCESS_KEY_FILE", ErrSecret)
		}
		if c.S3SecretKey.Empty() {
			return Config{}, fmt.Errorf("%w: TERMLINK_S3_SECRET_KEY_FILE", ErrSecret)
		}
		if !validBucketName(c.S3Bucket) {
			return Config{}, fmt.Errorf("%w: TERMLINK_S3_BUCKET", ErrInvalid)
		}
	}
	level := strings.ToLower(strings.TrimSpace(c.LogLevel))
	if level == "" {
		level = defaultLogLevel
	}
	switch level {
	case "info", "warn", "error":
		c.LogLevel = level
	default:
		return Config{}, fmt.Errorf("%w: TERMLINK_LOG_LEVEL", ErrInvalid)
	}
	if strings.TrimSpace(c.ListenAddr) == "" {
		c.ListenAddr = defaultListenAddr
	}
	return c, nil
}

func validBucketName(value string) bool {
	if len(value) < 3 || len(value) > 63 || value[0] == '.' || value[len(value)-1] == '.' || value[0] == '-' || value[len(value)-1] == '-' {
		return false
	}
	for _, character := range value {
		if (character < 'a' || character > 'z') && (character < '0' || character > '9') && character != '.' && character != '-' {
			return false
		}
	}
	return !strings.Contains(value, "..")
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
