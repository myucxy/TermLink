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
