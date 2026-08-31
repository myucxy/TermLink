package store

import "testing"

func TestNewAccountIdentityProfileKeepsOnlyBoundedDisplayData(t *testing.T) {
	t.Parallel()
	profile := NewAccountIdentityProfile(" alice ", " 爱丽丝 ", "Alice.Secret@example.test")
	if profile.Username != "alice" || profile.Nickname != "爱丽丝" || profile.EmailMasked != "a***t@example.test" {
		t.Fatalf("unexpected identity profile: %+v", profile)
	}
	if profile.EmailMasked == "Alice.Secret@example.test" {
		t.Fatal("raw e-mail crossed the identity profile boundary")
	}

	unsafe := NewAccountIdentityProfile("bad\nname", "", "not-an-email")
	if !unsafe.empty() {
		t.Fatalf("unsafe identity data was accepted: %+v", unsafe)
	}
}

func TestNormalizeIdentityProfileProjectionRejectsRawEmail(t *testing.T) {
	t.Parallel()
	profile := normalizeIdentityProfileProjection(AccountIdentityProfile{
		Username: "alice", EmailMasked: "alice@example.test",
	})
	if profile.EmailMasked != "" || profile.Username != "alice" {
		t.Fatalf("raw e-mail was not removed from display projection: %+v", profile)
	}
}
