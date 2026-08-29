package api

import "testing"

func TestNormalizeCommerceCode(t *testing.T) {
	got := normalizeCommerceCode(" tl-i-ABCD 2345-6789-ABCDEFGH ")
	if got != "TLIABCD23456789ABCDEFGH" {
		t.Fatalf("normalized code = %q", got)
	}
	if normalizeCommerceCode("TLI-ABC-💥") != "" {
		t.Fatal("non-ASCII code was accepted")
	}
}

func TestCommerceDigestSeparatesPurpose(t *testing.T) {
	key := []byte("0123456789abcdef0123456789abcdef")
	a := keyedCommerceDigest(key, "invitation-code", "TLIABCDEFGH23456789")
	b := keyedCommerceDigest(key, "redemption-code", "TLIABCDEFGH23456789")
	if a == b {
		t.Fatal("commerce digest purposes were not separated")
	}
}

func TestNormalizedEmail(t *testing.T) {
	email, domain, err := normalizedEmail(" User@Example.COM ")
	if err != nil || email != "user@example.com" || domain != "example.com" {
		t.Fatalf("normalized email = %q/%q, err=%v", email, domain, err)
	}
}
