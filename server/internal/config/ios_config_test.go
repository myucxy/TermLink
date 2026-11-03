package config

import (
	"errors"
	"testing"
)

func TestIOSIdentityIsOptInAndCannotImpersonateOtherClients(t *testing.T) {
	base := Input{PublicURL: "https://termlink.example.test", OIDCIssuer: "https://issuer.example.test", OIDCClientID: "server", OIDCClientSecret: "oidc", DatabaseURL: "postgres", RedisURL: "redis", TokenSigningKey: "01234567890123456789012345678901"}
	c, err := New(base)
	if err != nil {
		t.Fatal(err)
	}
	if c.IOSOIDCClientID != "" {
		t.Fatal("iOS login enabled without configuration")
	}
	input := base
	input.IOSOIDCClientID = "termlink-ios"
	c, err = New(input)
	if err != nil {
		t.Fatal(err)
	}
	if c.IOSOIDCRedirectURI != "dev.termlink.ios:/oauth2redirect" {
		t.Fatal("unexpected iOS callback")
	}
	for _, id := range []string{"termlink-android", "termlink-windows", "termlink-web", "termlink-web-step-up", "server", "bad client"} {
		input = base
		input.IOSOIDCClientID = id
		if _, err = New(input); !errors.Is(err, ErrInvalid) {
			t.Fatalf("client collision accepted: %q: %v", id, err)
		}
	}
	input = base
	input.IOSOIDCClientID = "termlink-ios"
	input.IOSOIDCRedirectURI = "dev.termlink:/oauth2redirect"
	if _, err = New(input); !errors.Is(err, ErrInvalid) {
		t.Fatal("Android callback accepted for iOS")
	}
	input = base
	input.IOSOIDCClientID = "termlink-ios"
	input.WindowsRegistrationOIDCParties = []string{"termlink-windows", "termlink-ios"}
	if _, err = New(input); !errors.Is(err, ErrInvalid) {
		t.Fatal("iOS can register Windows identities")
	}
}

func TestIOSLoadFromEnvRequiresItsExplicitPartyAndPreservesOtherClients(t *testing.T) {
	files := writeSecrets(t, map[string]string{
		"db":    "postgres://termlink:test@localhost/termlink",
		"admin": "Test_Administrator_Password_42",
		"sign":  "01234567890123456789012345678901",
	})
	env := map[string]string{
		"TERMLINK_PUBLIC_URL":                  "https://termlink.example.test",
		"TERMLINK_DATABASE_URL_FILE":           files["db"],
		"TERMLINK_INITIAL_ADMIN_PASSWORD_FILE": files["admin"],
		"TERMLINK_TOKEN_SIGNING_KEY_FILE":      files["sign"],
		"TERMLINK_IOS_OIDC_CLIENT_ID":          "termlink-ios",
		"TERMLINK_IOS_OIDC_REDIRECT_URI":       "dev.termlink.ios:/oauth2redirect",
	}
	c, err := LoadFromEnv(env)
	if err != nil {
		t.Fatal(err)
	}
	if c.IOSOIDCClientID != "termlink-ios" || c.IOSOIDCRedirectURI != env["TERMLINK_IOS_OIDC_REDIRECT_URI"] {
		t.Fatal("iOS configuration was not loaded")
	}
	if len(c.OIDCAllowedParties) != 3 || len(c.WindowsRegistrationOIDCParties) != 1 || c.WindowsRegistrationOIDCParties[0] != "termlink-windows" {
		t.Fatal("iOS configuration changed the Windows identity registration boundary")
	}
	env["TERMLINK_OIDC_ALLOWED_PARTIES"] = "termlink-android,termlink-windows"
	if _, err = LoadFromEnv(env); !errors.Is(err, ErrInvalid) {
		t.Fatal("enabled iOS omitted from explicit allowed parties was accepted")
	}
	env["TERMLINK_OIDC_ALLOWED_PARTIES"] += ",termlink-ios"
	if _, err = LoadFromEnv(env); err != nil {
		t.Fatal(err)
	}
	for _, callback := range []string{"dev.termlink:/oauth2redirect", "dev.termlink.ios://evil/oauth2redirect", "dev.termlink.ios:/oauth2redirect?redirect=evil", "https://example.test/callback"} {
		env["TERMLINK_IOS_OIDC_REDIRECT_URI"] = callback
		if _, err = LoadFromEnv(env); !errors.Is(err, ErrInvalid) {
			t.Fatalf("invalid iOS callback accepted: %q", callback)
		}
	}
}
