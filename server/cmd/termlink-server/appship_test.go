package main

import (
	"context"
	"testing"

	"github.com/termlink/termlink/server/internal/agentupdate"
	"github.com/termlink/termlink/server/internal/webportal"
)

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

type githubPortalCatalog func(context.Context, string, string, int64) (agentupdate.Offer, error)

func (f githubPortalCatalog) LatestStable(ctx context.Context, platform, arch string, build int64) (agentupdate.Offer, error) {
	return f(ctx, platform, arch, build)
}

type legacyPortalCatalog func(context.Context, webportal.PublicReleaseTarget) (webportal.PublicRelease, error)

func (f legacyPortalCatalog) Latest(ctx context.Context, target webportal.PublicReleaseTarget) (webportal.PublicRelease, error) {
	return f(ctx, target)
}

func TestPublicDownloadsUseGitHubForVerifiedNewerClient(t *testing.T) {
	const githubURL = "https://github.com/myucxy/TermLink/releases/download/v0.3.19/TermLink-Android-0.3.19-149.apk"
	target := webportal.PublicReleaseTarget{Application: "termlink-android", Platform: "android", Architecture: "universal", FileKind: "apk"}
	signer := agentupdate.DefaultGitHubAndroidSignerSHA256
	var fallbacks int
	source := githubPublicReleases{
		github: githubPortalCatalog(func(_ context.Context, platform, arch string, build int64) (agentupdate.Offer, error) {
			if platform != "android" || arch != "universal" || build != 148 {
				t.Fatalf("unexpected GitHub target %s/%s build %d", platform, arch, build)
			}
			return agentupdate.Offer{Available: true, Version: "0.3.19", Build: 149, DownloadURL: githubURL, SignerFingerprint: &signer}, nil
		}),
		legacy: legacyPortalCatalog(func(_ context.Context, _ webportal.PublicReleaseTarget) (webportal.PublicRelease, error) {
			fallbacks++
			return webportal.PublicRelease{Version: "0.3.18", Build: 148}, nil
		}),
	}
	got, err := source.Latest(context.Background(), target)
	if err != nil || got.DownloadURL != githubURL || got.Build != 149 || fallbacks != 0 {
		t.Fatalf("GitHub download: %+v, %v", got, err)
	}
	source.github = githubPortalCatalog(func(context.Context, string, string, int64) (agentupdate.Offer, error) {
		return agentupdate.Offer{}, agentupdate.ErrUnavailable
	})
	got, err = source.Latest(context.Background(), target)
	if err != nil || got.Build != 148 || fallbacks != 1 {
		t.Fatalf("legacy download: %+v, %v", got, err)
	}
}
