package main

import "testing"

func TestAppShipSourcesUseStartupPolicy(t *testing.T) {
	// An obsolete URL override cannot redirect either production reader.
	t.Setenv("TERMLINK_APPSHIP_BASE_URL", "http://127.0.0.1:9")
	t.Setenv("TERMLINK_APPSHIP_SIGNER_FINGERPRINT", "")
	t.Setenv("TERMLINK_APPSHIP_MACOS_TEAM_ID", "")
	for _, privateIPs := range []string{"", "192.168.9.250", "192.168.9.250,fd00::250"} {
		t.Setenv("TERMLINK_APPSHIP_ALLOWED_PRIVATE_IPS", privateIPs)
		catalog, publicSource, err := newAppShipSources()
		if err != nil || catalog == nil || publicSource == nil {
			t.Fatalf("valid startup policy failed: %v", err)
		}
	}
	for _, invalid := range []string{"192.168.9.0/24", "127.0.0.1", "169.254.169.254", "192.168.9.250,,10.0.0.1"} {
		t.Setenv("TERMLINK_APPSHIP_ALLOWED_PRIVATE_IPS", invalid)
		catalog, publicSource, err := newAppShipSources()
		if err == nil || catalog != nil || publicSource != nil {
			t.Fatal("unsafe startup policy was partially accepted")
		}
	}
}

func TestAppShipMacPublisherIsIndependentAndFailsClosed(t *testing.T) {
	t.Setenv("TERMLINK_APPSHIP_ALLOWED_PRIVATE_IPS", "")
	t.Setenv("TERMLINK_APPSHIP_SIGNER_FINGERPRINT", "")
	for _, team := range []string{"", "ABCDE12345"} {
		t.Setenv("TERMLINK_APPSHIP_MACOS_TEAM_ID", team)
		catalog, publicSource, err := newAppShipSources()
		if err != nil || catalog == nil || publicSource == nil {
			t.Fatalf("valid Mac publisher policy rejected: %v", err)
		}
	}
	for _, team := range []string{"adhoc", "abcde12345", "0123456789abcdef0123456789abcdef01234567", "ABCDE12345\nINJECT=true"} {
		t.Setenv("TERMLINK_APPSHIP_MACOS_TEAM_ID", team)
		if catalog, source, err := newAppShipSources(); err == nil || catalog != nil || source != nil {
			t.Fatal("invalid Mac publisher partly initialized")
		}
	}
}
