package config

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
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
