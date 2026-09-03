package config

import (
	"encoding/base64"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

func TestLoadFromEnvReadsSecretsAndRedactsDiagnostics(t *testing.T) {
	files := writeSecrets(t, map[string]string{
		"oidc":  "oidc-secret",
		"db":    "postgres://termlink:password@postgres/termlink",
		"redis": "redis://:password@valkey:6379/0",
		"sign":  "01234567890123456789012345678901",
	})
	env := baseEnv(files)
	c, err := LoadFromEnv(env)
	if err != nil {
		t.Fatalf("LoadFromEnv() error = %v", err)
	}
	if got := c.OIDCClientSecret.Value(); got != "oidc-secret" {
		t.Fatalf("OIDCClientSecret = %q", got)
	}
	if got := c.DatabaseURL.Value(); got == "" {
		t.Fatal("DatabaseURL was empty")
	}
	if c.HistoryEnabled {
		t.Fatal("HistoryEnabled = true, want false")
	}
	if c.LogLevel != "info" {
		t.Fatalf("LogLevel = %q, want info", c.LogLevel)
	}
	if c.ListenAddr != ":8080" {
		t.Fatalf("ListenAddr = %q, want :8080", c.ListenAddr)
	}
	if c.MetricsListenAddr != "" {
		t.Fatalf("MetricsListenAddr = %q, want disabled", c.MetricsListenAddr)
	}
	if c.PostgresMaxConns != 20 || c.PostgresMinConns != 2 ||
		c.PostgresMaxConnLifetime != 30*time.Minute || c.PostgresMaxConnIdleTime != 5*time.Minute ||
		c.PostgresHealthCheckPeriod != time.Minute || c.PostgresConnectTimeout != 5*time.Second {
		t.Fatalf("PostgreSQL defaults not applied: %#v", c)
	}
	if c.RedisPoolSize != 20 || c.RedisMinIdleConns != 2 ||
		c.RedisDialTimeout != 5*time.Second || c.RedisReadTimeout != 3*time.Second ||
		c.RedisWriteTimeout != 3*time.Second || c.RedisPoolTimeout != 4*time.Second {
		t.Fatalf("Valkey defaults not applied: %#v", c)
	}
	if c.PaymentSecretsDir != "/run/secrets" {
		t.Fatalf("PaymentSecretsDir = %q, want /run/secrets", c.PaymentSecretsDir)
	}
	if c.RegistrationRecoveryEnabled || c.KeycloakAdminClientID != "termlink-registration-recovery" ||
		c.KeycloakAdminBaseURL != "" || !c.KeycloakAdminClientSecret.Empty() {
		t.Fatalf("registration recovery defaults not applied: %#v", c)
	}
	if c.HumanVerificationProvider != "" || c.HumanVerificationSiteKey != "" || !c.HumanVerificationSecret.Empty() {
		t.Fatalf("human verification must default to disabled: %#v", c)
	}
	output := c.String()
	for _, secret := range []string{"oidc-secret", "postgres://termlink:password@postgres/termlink", "redis://:password@valkey:6379/0", "01234567890123456789012345678901"} {
		if strings.Contains(output, secret) {
			t.Fatalf("String() leaked secret %q: %s", secret, output)
		}
	}
	if !strings.Contains(output, "[REDACTED]") {
		t.Fatalf("String() did not show redaction marker: %s", output)
	}
	if got := fmt.Sprintf("%#v", c); strings.Contains(got, "oidc-secret") {
		t.Fatalf("GoString() leaked secret: %s", got)
	}
}

func TestLoadTurnstileUsesDedicatedSecretAndFailsClosed(t *testing.T) {
	files := writeSecrets(t, map[string]string{
		"oidc": "oidc-secret", "human": "turnstile-secret", "db": "db-secret", "redis": "redis-secret",
		"sign": "01234567890123456789012345678901",
	})
	env := baseEnv(files)
	env["TERMLINK_HUMAN_VERIFICATION_PROVIDER"] = "turnstile"
	env["TERMLINK_HUMAN_VERIFICATION_SITE_KEY"] = "0x4AAAA-test-site-key"
	if _, err := LoadFromEnv(env); err == nil || !errors.Is(err, ErrMissing) ||
		!strings.Contains(err.Error(), "TERMLINK_HUMAN_VERIFICATION_SECRET_FILE") {
		t.Fatalf("missing Turnstile secret error = %v", err)
	}
	env["TERMLINK_HUMAN_VERIFICATION_SECRET_FILE"] = files["human"]
	c, err := LoadFromEnv(env)
	if err != nil {
		t.Fatalf("LoadFromEnv(Turnstile) error = %v", err)
	}
	if c.HumanVerificationProvider != "turnstile" || c.HumanVerificationSiteKey != "0x4AAAA-test-site-key" ||
		c.HumanVerificationSecret.Value() != "turnstile-secret" || c.HumanVerificationSecretFile != files["human"] {
		t.Fatalf("Turnstile config not loaded: %#v", c)
	}
	if output := c.String(); strings.Contains(output, "turnstile-secret") || strings.Contains(output, files["human"]) {
		t.Fatalf("String() leaked Turnstile secret material: %s", output)
	}

	for name, value := range map[string]string{
		"provider": "unknown-provider",
		"site key": "invalid site key",
	} {
		candidate := make(map[string]string, len(env))
		for key, item := range env {
			candidate[key] = item
		}
		field := "TERMLINK_HUMAN_VERIFICATION_SITE_KEY"
		if name == "provider" {
			field = "TERMLINK_HUMAN_VERIFICATION_PROVIDER"
		}
		candidate[field] = value
		if _, err := LoadFromEnv(candidate); err == nil || !errors.Is(err, ErrInvalid) || !strings.Contains(err.Error(), field) {
			t.Fatalf("invalid Turnstile %s error = %v", name, err)
		}
	}
}

func TestLoadRegistrationRecoveryUsesDedicatedSecret(t *testing.T) {
	files := writeSecrets(t, map[string]string{
		"oidc": "oidc-secret", "admin": "keycloak-admin-secret", "db": "db-secret", "redis": "redis-secret",
		"sign": "01234567890123456789012345678901",
	})
	env := baseEnv(files)
	env["TERMLINK_REGISTRATION_RECOVERY_ENABLED"] = "true"
	env["TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID"] = "registration-recovery"
	env["TERMLINK_KEYCLOAK_ADMIN_BASE_URL"] = "http://keycloak:8080"

	if _, err := LoadFromEnv(env); err == nil || !errors.Is(err, ErrMissing) || !strings.Contains(err.Error(), "TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE") {
		t.Fatalf("missing recovery secret error = %v, want field-scoped ErrMissing", err)
	}
	env["TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE"] = files["admin"]
	c, err := LoadFromEnv(env)
	if err != nil {
		t.Fatalf("LoadFromEnv() error = %v", err)
	}
	if !c.RegistrationRecoveryEnabled || c.KeycloakAdminClientID != "registration-recovery" ||
		c.KeycloakAdminBaseURL != "http://keycloak:8080" || c.KeycloakAdminClientSecret.Value() != "keycloak-admin-secret" {
		t.Fatalf("registration recovery config not loaded: %#v", c)
	}
	if c.KeycloakAdminClientSecretFile != files["admin"] {
		t.Fatalf("KeycloakAdminClientSecretFile = %q, want %q", c.KeycloakAdminClientSecretFile, files["admin"])
	}
	for _, forbidden := range []string{"keycloak-admin-secret", files["admin"]} {
		if output := c.String(); strings.Contains(output, forbidden) {
			t.Fatalf("String() leaked recovery secret material %q: %s", forbidden, output)
		}
	}

	env["TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID"] = "invalid client"
	if _, err := LoadFromEnv(env); err == nil || !errors.Is(err, ErrInvalid) || !strings.Contains(err.Error(), "TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID") {
		t.Fatalf("invalid recovery client ID error = %v, want field-scoped ErrInvalid", err)
	}
}

func TestLoadRegistrationRecoveryRejectsUnsafeAdminBaseURL(t *testing.T) {
	files := writeSecrets(t, map[string]string{
		"oidc": "oidc-secret", "admin": "keycloak-admin-secret", "db": "db-secret", "redis": "redis-secret",
		"sign": "01234567890123456789012345678901",
	})
	base := baseEnv(files)
	base["TERMLINK_REGISTRATION_RECOVERY_ENABLED"] = "true"
	base["TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE"] = files["admin"]
	for _, unsafe := range []string{
		"file:///run/keycloak", "https://user@identity.example.test", "https://identity.example.test?next=other",
		"https://identity.example.test/base/../other", "http://identity.example.test:8080", "http://keycloak",
		"https://0.0.0.0:8443",
	} {
		env := make(map[string]string, len(base)+1)
		for name, value := range base {
			env[name] = value
		}
		env["TERMLINK_KEYCLOAK_ADMIN_BASE_URL"] = unsafe
		if _, err := LoadFromEnv(env); err == nil || !errors.Is(err, ErrInvalid) ||
			!strings.Contains(err.Error(), "TERMLINK_KEYCLOAK_ADMIN_BASE_URL") {
			t.Fatalf("unsafe admin base %q error=%v", unsafe, err)
		}
	}
}

func TestLoadAccountDeletionRequiresDedicatedIdentityKeyAndDependencies(t *testing.T) {
	encodedKey := base64.RawURLEncoding.EncodeToString([]byte("0123456789abcdef0123456789abcdef"))
	files := writeSecrets(t, map[string]string{
		"oidc": "oidc-secret", "admin": "keycloak-admin-secret", "deletion": encodedKey,
		"db": "db-secret", "redis": "redis-secret", "sign": "01234567890123456789012345678901",
	})
	env := baseEnv(files)
	env["TERMLINK_ACCOUNT_DELETION_ENABLED"] = "true"
	env["TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE"] = files["admin"]
	env["TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE"] = files["deletion"]
	env["TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION"] = "7"

	if _, err := LoadFromEnv(env); err == nil || !errors.Is(err, ErrInvalid) ||
		!strings.Contains(err.Error(), "TERMLINK_HISTORY_ENABLED") {
		t.Fatalf("account deletion without history error = %v, want field-scoped ErrInvalid", err)
	}
	env["TERMLINK_HISTORY_ENABLED"] = "true"
	env["TERMLINK_S3_ENDPOINT"] = "http://minio:9000"
	env["TERMLINK_S3_ACCESS_KEY_FILE"] = files["access"]
	env["TERMLINK_S3_SECRET_KEY_FILE"] = files["secret"]
	c, err := LoadFromEnv(env)
	if err != nil {
		t.Fatalf("LoadFromEnv(account deletion) error = %v", err)
	}
	key, err := c.AccountDeletionIdentityKeyBytes()
	if err != nil || string(key) != "0123456789abcdef0123456789abcdef" {
		t.Fatalf("decoded account deletion key is invalid: len=%d err=%v", len(key), err)
	}
	clear(key)
	if !c.AccountDeletionEnabled || c.AccountDeletionIdentityKeyVersion != 7 ||
		c.AccountDeletionIdentityKeyFile != files["deletion"] || c.KeycloakAdminClientSecret.Empty() {
		t.Fatalf("account deletion config not loaded: %#v", c)
	}
	for _, forbidden := range []string{encodedKey, files["deletion"], "keycloak-admin-secret"} {
		if output := c.String(); strings.Contains(output, forbidden) {
			t.Fatalf("String() leaked account deletion material %q: %s", forbidden, output)
		}
	}

	invalidKeyPath := writeSecret(t, filepath.Dir(files["deletion"]), "invalid-deletion", "not-a-32-byte-base64-key")
	env["TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE"] = invalidKeyPath
	if _, err := LoadFromEnv(env); err == nil || !errors.Is(err, ErrSecret) ||
		!strings.Contains(err.Error(), "TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE") {
		t.Fatalf("invalid deletion key error = %v, want field-scoped ErrSecret", err)
	}

	delete(env, "TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE")
	if _, err := LoadFromEnv(env); err == nil || !errors.Is(err, ErrMissing) ||
		!strings.Contains(err.Error(), "TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE") {
		t.Fatalf("missing deletion key error = %v, want field-scoped ErrMissing", err)
	}
}

func TestAccountDeletionIdentityKeyRingSupportsSafeRotation(t *testing.T) {
	first := base64.RawURLEncoding.EncodeToString([]byte("11111111111111111111111111111111"))
	second := base64.StdEncoding.EncodeToString([]byte("22222222222222222222222222222222"))
	config := Config{
		AccountDeletionIdentityKey:        Secret(`[{"version":6,"key":"` + first + `"},{"version":7,"key":"` + second + `"}]`),
		AccountDeletionIdentityKeyVersion: 7,
	}
	keys, err := config.AccountDeletionIdentityKeys()
	if err != nil || len(keys) != 2 || string(keys[6]) != "11111111111111111111111111111111" ||
		string(keys[7]) != "22222222222222222222222222222222" {
		t.Fatalf("AccountDeletionIdentityKeys() = %#v, %v", keys, err)
	}
	clearAccountDeletionIdentityKeys(keys)

	for name, value := range map[string]string{
		"missing current": `[{"version":6,"key":"` + first + `"}]`,
		"duplicate":       `[{"version":7,"key":"` + first + `"},{"version":7,"key":"` + second + `"}]`,
		"unknown field":   `[{"version":7,"key":"` + second + `","secret":"unexpected"}]`,
		"trailing data":   `[{"version":7,"key":"` + second + `"}] true`,
	} {
		t.Run(name, func(t *testing.T) {
			config.AccountDeletionIdentityKey = Secret(value)
			if _, err := config.AccountDeletionIdentityKeys(); err == nil || !errors.Is(err, ErrSecret) {
				t.Fatalf("AccountDeletionIdentityKeys(%s) error = %v, want ErrSecret", name, err)
			}
		})
	}
}

func TestLoadConnectionPoolAndTimeoutOverrides(t *testing.T) {
	files := writeSecrets(t, map[string]string{
		"oidc": "oidc-secret", "db": "db-secret", "redis": "redis-secret",
		"sign": "01234567890123456789012345678901",
	})
	env := baseEnv(files)
	env["TERMLINK_POSTGRES_MAX_CONNS"] = "64"
	env["TERMLINK_POSTGRES_MIN_CONNS"] = "4"
	env["TERMLINK_POSTGRES_MAX_CONN_LIFETIME"] = "45m"
	env["TERMLINK_POSTGRES_MAX_CONN_IDLE_TIME"] = "10m"
	env["TERMLINK_POSTGRES_HEALTH_CHECK_PERIOD"] = "30s"
	env["TERMLINK_POSTGRES_CONNECT_TIMEOUT"] = "8s"
	env["TERMLINK_REDIS_POOL_SIZE"] = "48"
	env["TERMLINK_REDIS_MIN_IDLE_CONNS"] = "6"
	env["TERMLINK_REDIS_DIAL_TIMEOUT"] = "7s"
	env["TERMLINK_REDIS_READ_TIMEOUT"] = "4s"
	env["TERMLINK_REDIS_WRITE_TIMEOUT"] = "5s"
	env["TERMLINK_REDIS_POOL_TIMEOUT"] = "6s"
	env["TERMLINK_PAYMENT_SECRETS_DIR"] = filepath.Join(t.TempDir(), "payment-secrets")
	env["TERMLINK_LOG_LEVEL"] = "DEBUG"

	c, err := LoadFromEnv(env)
	if err != nil {
		t.Fatalf("LoadFromEnv() error = %v", err)
	}
	if c.PostgresMaxConns != 64 || c.PostgresMinConns != 4 ||
		c.PostgresMaxConnLifetime != 45*time.Minute || c.PostgresMaxConnIdleTime != 10*time.Minute ||
		c.PostgresHealthCheckPeriod != 30*time.Second || c.PostgresConnectTimeout != 8*time.Second {
		t.Fatalf("PostgreSQL overrides not applied: %#v", c)
	}
	if c.RedisPoolSize != 48 || c.RedisMinIdleConns != 6 ||
		c.RedisDialTimeout != 7*time.Second || c.RedisReadTimeout != 4*time.Second ||
		c.RedisWriteTimeout != 5*time.Second || c.RedisPoolTimeout != 6*time.Second {
		t.Fatalf("Valkey overrides not applied: %#v", c)
	}
	if c.LogLevel != "debug" {
		t.Fatalf("LogLevel = %q, want normalized debug", c.LogLevel)
	}
	if c.PaymentSecretsDir != env["TERMLINK_PAYMENT_SECRETS_DIR"] {
		t.Fatalf("PaymentSecretsDir = %q, want %q", c.PaymentSecretsDir, env["TERMLINK_PAYMENT_SECRETS_DIR"])
	}
}

func TestLoadRejectsInvalidConnectionControlsWithoutEchoingValues(t *testing.T) {
	files := writeSecrets(t, map[string]string{
		"oidc": "oidc-secret", "db": "db-secret", "redis": "redis-secret",
		"sign": "01234567890123456789012345678901",
	})
	tests := []struct {
		name  string
		value string
	}{
		{"TERMLINK_POSTGRES_MAX_CONNS", "0"},
		{"TERMLINK_POSTGRES_MIN_CONNS", "21"},
		{"TERMLINK_POSTGRES_MAX_CONN_LIFETIME", "secret-invalid-duration"},
		{"TERMLINK_POSTGRES_MAX_CONN_IDLE_TIME", "30s"},
		{"TERMLINK_POSTGRES_HEALTH_CHECK_PERIOD", "1s"},
		{"TERMLINK_POSTGRES_CONNECT_TIMEOUT", "0"},
		{"TERMLINK_REDIS_POOL_SIZE", "1001"},
		{"TERMLINK_REDIS_MIN_IDLE_CONNS", "21"},
		{"TERMLINK_REDIS_DIAL_TIMEOUT", "10ms"},
		{"TERMLINK_REDIS_READ_TIMEOUT", "forever"},
		{"TERMLINK_REDIS_WRITE_TIMEOUT", "0"},
		{"TERMLINK_REDIS_POOL_TIMEOUT", "3h"},
		{"TERMLINK_LOG_LEVEL", "trace"},
		{"TERMLINK_REGISTRATION_RECOVERY_ENABLED", "sometimes"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			env := baseEnv(files)
			env[test.name] = test.value
			_, err := LoadFromEnv(env)
			if err == nil || !errors.Is(err, ErrInvalid) || !strings.Contains(err.Error(), test.name) {
				t.Fatalf("LoadFromEnv() error = %v, want invalid %s", err, test.name)
			}
			if strings.Contains(err.Error(), test.value) {
				t.Fatalf("error echoed configuration value %q: %v", test.value, err)
			}
		})
	}
}

func TestPaymentSecretsDirectoryIsAbsoluteCleanAndNonRoot(t *testing.T) {
	input := Input{
		PublicURL: "https://termlink.example.test", OIDCIssuer: "https://issuer.example.test",
		OIDCClientID: "client", OIDCClientSecret: "oidc", DatabaseURL: "postgres",
		RedisURL: "redis", TokenSigningKey: "01234567890123456789012345678901",
		PaymentSecretsDir: filepath.Join(t.TempDir(), "payment", "..", "provider-secrets"),
	}
	c, err := New(input)
	if err != nil {
		t.Fatalf("New() error = %v", err)
	}
	if c.PaymentSecretsDir != filepath.Clean(input.PaymentSecretsDir) {
		t.Fatalf("PaymentSecretsDir = %q, want clean %q", c.PaymentSecretsDir, filepath.Clean(input.PaymentSecretsDir))
	}

	for _, invalid := range []string{"relative/secrets", "/"} {
		input.PaymentSecretsDir = invalid
		_, err := New(input)
		if err == nil || !errors.Is(err, ErrInvalid) || !strings.Contains(err.Error(), "TERMLINK_PAYMENT_SECRETS_DIR") {
			t.Fatalf("New(PaymentSecretsDir=%q) error = %v, want ErrInvalid", invalid, err)
		}
	}
}

func TestLoadRejectsMissingSecretFile(t *testing.T) {
	dir := t.TempDir()
	env := baseEnv(map[string]string{
		"oidc":  filepath.Join(dir, "missing"),
		"db":    filepath.Join(dir, "db"),
		"redis": filepath.Join(dir, "redis"),
		"sign":  filepath.Join(dir, "sign"),
	})
	_, err := LoadFromEnv(env)
	if err == nil || !errors.Is(err, ErrSecret) {
		t.Fatalf("LoadFromEnv() error = %v, want ErrSecret", err)
	}
	if strings.Contains(err.Error(), "oidc-secret") {
		t.Fatalf("error leaked secret content: %v", err)
	}
}

func TestLoadRejectsOverPermissiveSecretFile(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Windows file modes do not expose POSIX permission bits")
	}
	dir := t.TempDir()
	path := filepath.Join(dir, "oidc")
	if err := os.WriteFile(path, []byte("secret"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(path, 0o644); err != nil {
		t.Fatal(err)
	}
	env := baseEnv(map[string]string{
		"oidc":  path,
		"db":    writeSecret(t, dir, "db", "db-secret"),
		"redis": writeSecret(t, dir, "redis", "redis-secret"),
		"sign":  writeSecret(t, dir, "sign", "sign-secret"),
	})
	_, err := LoadFromEnv(env)
	if err == nil || !errors.Is(err, ErrSecret) {
		t.Fatalf("LoadFromEnv() error = %v, want ErrSecret", err)
	}
}

func TestLoadRejectsEmptySecretFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "oidc")
	if err := os.WriteFile(path, []byte(" \n\t"), 0o600); err != nil {
		t.Fatal(err)
	}
	env := baseEnv(map[string]string{
		"oidc":  path,
		"db":    writeSecret(t, dir, "db", "db-secret"),
		"redis": writeSecret(t, dir, "redis", "redis-secret"),
		"sign":  writeSecret(t, dir, "sign", "sign-secret"),
	})
	_, err := LoadFromEnv(env)
	if err == nil || !errors.Is(err, ErrSecret) {
		t.Fatalf("LoadFromEnv() error = %v, want ErrSecret", err)
	}
}

func TestLoadHistoryRequiresS3SecretsOnlyWhenEnabled(t *testing.T) {
	files := writeSecrets(t, map[string]string{
		"oidc":  "oidc-secret",
		"db":    "db-secret",
		"redis": "redis-secret",
		"sign":  "01234567890123456789012345678901",
	})
	env := baseEnv(files)
	env["TERMLINK_HISTORY_ENABLED"] = "true"
	env["TERMLINK_S3_ENDPOINT"] = "http://minio:9000"
	if _, err := LoadFromEnv(env); err == nil || !errors.Is(err, ErrMissing) {
		t.Fatalf("LoadFromEnv() error = %v, want missing S3 secret path", err)
	}

	env["TERMLINK_S3_ACCESS_KEY_FILE"] = files["access"]
	env["TERMLINK_S3_SECRET_KEY_FILE"] = files["secret"]
	c, err := LoadFromEnv(env)
	if err != nil {
		t.Fatalf("LoadFromEnv(history) error = %v", err)
	}
	if !c.HistoryEnabled || c.S3AccessKey.Value() != "access-key" || c.S3SecretKey.Value() != "secret-key" {
		t.Fatalf("history config not loaded: %#v", c)
	}
	if c.S3PublicEndpoint != c.S3Endpoint {
		t.Fatalf("S3PublicEndpoint = %q, want internal endpoint %q when omitted", c.S3PublicEndpoint, c.S3Endpoint)
	}

	env["TERMLINK_S3_PUBLIC_ENDPOINT"] = "http://127.0.0.1:59000"
	c, err = LoadFromEnv(env)
	if err != nil {
		t.Fatalf("LoadFromEnv(history public endpoint) error = %v", err)
	}
	if c.S3PublicEndpoint != env["TERMLINK_S3_PUBLIC_ENDPOINT"] {
		t.Fatalf("S3PublicEndpoint = %q, want configured public endpoint", c.S3PublicEndpoint)
	}
}

func TestS3EndpointValidationCoversInternalAndPublicEndpoints(t *testing.T) {
	base := Input{
		PublicURL:        "https://termlink.example.test",
		OIDCIssuer:       "https://issuer.example.test",
		OIDCClientID:     "termlink-server",
		OIDCClientSecret: "oidc",
		DatabaseURL:      "postgres",
		RedisURL:         "redis",
		TokenSigningKey:  "01234567890123456789012345678901",
		S3Endpoint:       "http://minio:9000",
		S3AccessKey:      "access-key",
		S3SecretKey:      "secret-key",
		HistoryEnabled:   true,
	}
	tests := []struct {
		name      string
		mutate    func(*Input)
		fieldName string
	}{
		{
			name: "internal",
			mutate: func(input *Input) {
				input.S3Endpoint = "ftp://minio:9000"
			},
			fieldName: "TERMLINK_S3_ENDPOINT",
		},
		{
			name: "public",
			mutate: func(input *Input) {
				input.S3PublicEndpoint = "ftp://127.0.0.1:59000"
			},
			fieldName: "TERMLINK_S3_PUBLIC_ENDPOINT",
		},
		{
			name: "internal path",
			mutate: func(input *Input) {
				input.S3Endpoint = "http://minio:9000/admin"
			},
			fieldName: "TERMLINK_S3_ENDPOINT",
		},
		{
			name: "public query",
			mutate: func(input *Input) {
				input.S3PublicEndpoint = "https://history.example.test?credential=leak"
			},
			fieldName: "TERMLINK_S3_PUBLIC_ENDPOINT",
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			input := base
			test.mutate(&input)
			_, err := New(input)
			if err == nil || !errors.Is(err, ErrInvalid) || !strings.Contains(err.Error(), test.fieldName) {
				t.Fatalf("New() error = %v, want invalid %s", err, test.fieldName)
			}
		})
	}
}

func TestNewExplicitInputAppliesDefaultsAndValidates(t *testing.T) {
	c, err := New(Input{
		PublicURL:        "https://termlink.example.test",
		OIDCIssuer:       "https://issuer.example.test",
		OIDCClientID:     "client",
		OIDCClientSecret: "oidc",
		DatabaseURL:      "postgres",
		RedisURL:         "redis",
		TokenSigningKey:  "01234567890123456789012345678901",
	})
	if err != nil {
		t.Fatalf("New() error = %v", err)
	}
	if c.LogLevel != "info" || c.ListenAddr != ":8080" {
		t.Fatalf("defaults not applied: %#v", c)
	}
	if c.WindowsOIDCClientID != "termlink-windows" ||
		strings.Join(c.WindowsRegistrationOIDCParties, ",") != "termlink-windows" ||
		c.WindowsOIDCRedirectURI != "http://127.0.0.1/oauth2/callback" {
		t.Fatalf("Windows OIDC defaults not applied: %#v", c)
	}
	if c.WebOIDCClientID != "termlink-web" || c.WebOIDCStepUpClientID != "termlink-web-step-up" ||
		c.WebOIDCRedirectURI != "https://termlink.example.test/auth/callback" {
		t.Fatalf("Web OIDC defaults not applied: %#v", c)
	}
}

func TestWebOIDCStepUpClientMustBeDistinct(t *testing.T) {
	_, err := New(Input{
		PublicURL:             "https://termlink.example.test",
		OIDCIssuer:            "https://issuer.example.test",
		OIDCClientID:          "client",
		WebOIDCClientID:       "termlink-web",
		WebOIDCStepUpClientID: "termlink-web",
		OIDCClientSecret:      "oidc",
		DatabaseURL:           "postgres",
		RedisURL:              "redis",
		TokenSigningKey:       "01234567890123456789012345678901",
	})
	if err == nil || !errors.Is(err, ErrInvalid) || !strings.Contains(err.Error(), "TERMLINK_WEB_STEP_UP_OIDC_CLIENT_ID") {
		t.Fatalf("New() error=%v, want distinct Web Step-up client error", err)
	}
}

func TestPublicAndOIDCEndpointsRequireHTTPS(t *testing.T) {
	base := Input{
		PublicURL:        "https://termlink.example.test",
		OIDCIssuer:       "https://issuer.example.test/realms/termlink",
		OIDCClientID:     "client",
		OIDCClientSecret: "oidc",
		DatabaseURL:      "postgres",
		RedisURL:         "redis",
		TokenSigningKey:  "01234567890123456789012345678901",
	}
	tests := []struct {
		name  string
		field string
		set   func(*Input)
	}{
		{"public URL", "TERMLINK_PUBLIC_URL", func(input *Input) { input.PublicURL = "http://termlink.example.test" }},
		{"OIDC issuer", "TERMLINK_OIDC_ISSUER", func(input *Input) { input.OIDCIssuer = "http://issuer.example.test/realms/termlink" }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			input := base
			test.set(&input)
			_, err := New(input)
			if err == nil || !errors.Is(err, ErrInvalid) || !strings.Contains(err.Error(), test.field) {
				t.Fatalf("New() error = %v, want invalid %s", err, test.field)
			}
		})
	}
}

func TestOIDCAllowedPartiesAreExplicitAndClientBound(t *testing.T) {
	base := Input{
		PublicURL:              "https://termlink.example.test",
		OIDCIssuer:             "https://issuer.example.test/realms/termlink",
		OIDCClientID:           "termlink-server",
		AndroidOIDCClientID:    "termlink-android",
		AndroidOIDCRedirectURI: "dev.termlink:/oauth2redirect",
		WindowsOIDCClientID:    "termlink-windows",
		WindowsOIDCRedirectURI: "http://127.0.0.1/oauth2/callback",
		OIDCClientSecret:       "oidc",
		DatabaseURL:            "postgres",
		RedisURL:               "redis",
		TokenSigningKey:        "01234567890123456789012345678901",
	}

	configured, err := New(base)
	if err != nil {
		t.Fatal(err)
	}
	if got := strings.Join(configured.OIDCAllowedParties, ","); got != "termlink-android,termlink-windows" {
		t.Fatalf("default OIDC allowed parties = %q", got)
	}
	if got := strings.Join(configured.WindowsRegistrationOIDCParties, ","); got != "termlink-windows" {
		t.Fatalf("default Windows registration parties = %q", got)
	}

	tests := []struct {
		name    string
		parties []string
	}{
		{"missing Android", []string{"termlink-windows"}},
		{"missing Windows", []string{"termlink-android"}},
		{"resource audience used as party", []string{"termlink-android", "termlink-windows", "termlink-server"}},
		{"whitespace in party", []string{"termlink-android", "termlink-windows bad"}},
		{"empty party", []string{"termlink-android", "termlink-windows", ""}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			input := base
			input.OIDCAllowedParties = test.parties
			if _, err := New(input); err == nil || !errors.Is(err, ErrInvalid) ||
				!strings.Contains(err.Error(), "TERMLINK_OIDC_ALLOWED_PARTIES") {
				t.Fatalf("New() error = %v, want invalid allowed parties", err)
			}
		})
	}

	base.OIDCAllowedParties = []string{" termlink-android ", "termlink-windows", "termlink-android", "acceptance-probe"}
	configured, err = New(base)
	if err != nil {
		t.Fatal(err)
	}
	if got := strings.Join(configured.OIDCAllowedParties, ","); got != "termlink-android,termlink-windows,acceptance-probe" {
		t.Fatalf("normalized OIDC allowed parties = %q", got)
	}

	base.WindowsRegistrationOIDCParties = []string{" termlink-windows ", "acceptance-probe", "termlink-windows"}
	configured, err = New(base)
	if err != nil {
		t.Fatal(err)
	}
	if got := strings.Join(configured.WindowsRegistrationOIDCParties, ","); got != "termlink-windows,acceptance-probe" {
		t.Fatalf("normalized Windows registration parties = %q", got)
	}

	for _, parties := range [][]string{
		{"acceptance-probe"},
		{"termlink-windows", "termlink-android"},
		{"termlink-windows", "not-token-allowed"},
		{"termlink-windows", ""},
	} {
		input := base
		input.WindowsRegistrationOIDCParties = parties
		if _, err := New(input); err == nil || !errors.Is(err, ErrInvalid) ||
			!strings.Contains(err.Error(), "TERMLINK_WINDOWS_REGISTRATION_OIDC_PARTIES") {
			t.Fatalf("New(WindowsRegistrationOIDCParties=%q) error = %v, want invalid binding", parties, err)
		}
	}
}

func TestWebOIDCRedirectMustUsePublicHTTPSOrigin(t *testing.T) {
	base := Input{
		PublicURL:        "https://termlink.example.test",
		OIDCIssuer:       "https://issuer.example.test",
		OIDCClientID:     "client",
		OIDCClientSecret: "oidc",
		DatabaseURL:      "postgres",
		RedisURL:         "redis",
		TokenSigningKey:  "01234567890123456789012345678901",
	}
	for _, redirect := range []string{
		"http://termlink.example.test/auth/callback",
		"https://other.example.test/auth/callback",
		"https://termlink.example.test/wrong",
		"https://termlink.example.test/auth/callback?code=leak",
	} {
		input := base
		input.WebOIDCRedirectURI = redirect
		if _, err := New(input); err == nil || !errors.Is(err, ErrInvalid) ||
			!strings.Contains(err.Error(), "TERMLINK_WEB_OIDC_REDIRECT_URI") {
			t.Fatalf("New(WebOIDCRedirectURI=%q) error = %v, want redirect error", redirect, err)
		}
	}
}

func TestWindowsOIDCRedirectRequiresPortlessLoopbackCallback(t *testing.T) {
	base := Input{
		PublicURL:        "https://termlink.example.test",
		OIDCIssuer:       "https://issuer.example.test",
		OIDCClientID:     "client",
		OIDCClientSecret: "oidc",
		DatabaseURL:      "postgres",
		RedisURL:         "redis",
		TokenSigningKey:  "01234567890123456789012345678901",
	}
	for _, redirect := range []string{
		"https://127.0.0.1:43971/oauth2/callback",
		"http://localhost:43971/oauth2/callback",
		"http://127.0.0.1:43971/wrong",
		"http://127.0.0.1:43971/oauth2/callback",
		"http://127.0.0.1:80/oauth2/callback",
		"http://user@127.0.0.1/oauth2/callback",
		"http://127.0.0.1/oauth2/callback?code=leak",
		"http://127.0.0.1/oauth2/callback#fragment",
		"http://127.0.0.1/oauth2/%63allback",
	} {
		input := base
		input.WindowsOIDCRedirectURI = redirect
		if _, err := New(input); err == nil || !errors.Is(err, ErrInvalid) ||
			!strings.Contains(err.Error(), "TERMLINK_WINDOWS_OIDC_REDIRECT_URI") {
			t.Fatalf("New(WindowsOIDCRedirectURI=%q) error = %v, want redirect error", redirect, err)
		}
	}
}

func TestMetricsListenerIsExplicitAndSeparate(t *testing.T) {
	input := Input{
		PublicURL:         "https://termlink.example.test",
		OIDCIssuer:        "https://issuer.example.test",
		OIDCClientID:      "client",
		OIDCClientSecret:  "oidc",
		DatabaseURL:       "postgres",
		RedisURL:          "redis",
		TokenSigningKey:   "01234567890123456789012345678901",
		MetricsListenAddr: ":9090",
	}
	c, err := New(input)
	if err != nil {
		t.Fatalf("New() error = %v", err)
	}
	if c.MetricsListenAddr != ":9090" {
		t.Fatalf("MetricsListenAddr = %q, want :9090", c.MetricsListenAddr)
	}

	for _, invalid := range []string{"9090", ":0", "metrics.internal:9090", ":8080"} {
		input.MetricsListenAddr = invalid
		if _, err := New(input); err == nil || !errors.Is(err, ErrInvalid) {
			t.Fatalf("New(MetricsListenAddr=%q) error = %v, want ErrInvalid", invalid, err)
		}
	}
}

func baseEnv(files map[string]string) map[string]string {
	return map[string]string{
		"TERMLINK_PUBLIC_URL":              "https://termlink.example.test",
		"TERMLINK_OIDC_ISSUER":             "https://issuer.example.test",
		"TERMLINK_OIDC_CLIENT_ID":          "termlink-server",
		"TERMLINK_OIDC_CLIENT_SECRET_FILE": files["oidc"],
		"TERMLINK_DATABASE_URL_FILE":       files["db"],
		"TERMLINK_REDIS_URL_FILE":          files["redis"],
		"TERMLINK_TOKEN_SIGNING_KEY_FILE":  files["sign"],
	}
}

func writeSecrets(t *testing.T, values map[string]string) map[string]string {
	t.Helper()
	dir := t.TempDir()
	paths := make(map[string]string, len(values))
	for name, value := range values {
		paths[name] = writeSecret(t, dir, name, value)
	}
	paths["access"] = writeSecret(t, dir, "access", "access-key")
	paths["secret"] = writeSecret(t, dir, "secret", "secret-key")
	return paths
}

func writeSecret(t *testing.T, dir, name, value string) string {
	t.Helper()
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, []byte(value+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(path, 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}
