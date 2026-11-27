package agentupdate

import (
	"context"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

const githubTestSigner = "91ae60bc06d866628b8eb2c62f32bde9dab56466"

func TestGitHubReleaseCatalogAcceptsOnlyThePinnedWindowsAsset(t *testing.T) {
	setupDigest := strings.Repeat("ab", 32)
	apkDigest := strings.Repeat("cd", 32)
	manifest := []byte(`{
  "schema_version": 1,
  "version": "0.3.19",
  "build_number": 149,
  "release_mode": "production",
  "source_revision": "0123456789abcdef0123456789abcdef01234567",
  "core_revision": "fedcba9876543210fedcba9876543210fedcba98",
  "artifacts": [{
    "path": "artifacts/release/0.3.19-149/TermLink-Setup-0.3.19.149-x64.exe",
    "size": 1234,
    "sha256": "` + setupDigest + `",
    "platform": "windows-x64",
    "type": "setup",
    "version": "0.3.19"
  }, {
    "path": "artifacts/release/0.3.19-149/TermLink-Android-0.3.19-149.apk",
    "size": 2345,
    "sha256": "` + apkDigest + `",
    "platform": "android",
    "type": "apk",
    "version": "0.3.19"
  }]
}`)
	metadata := []byte(`{
  "schema_version": 1,
  "version": "0.3.19",
  "build_number": 149,
  "source_revision": "0123456789abcdef0123456789abcdef01234567",
  "artifact_types": ["apk", "setup"],
  "signers": {"android_signer_sha256": "f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b", "windows_signer_sha1": "` + githubTestSigner + `"},
  "files": [{"name": "TermLink-Setup-0.3.19.149-x64.exe", "size": 1234, "sha256": "` + setupDigest + `"}]
}`)
	metadata = []byte(strings.Replace(string(metadata),
		`"files": [{"name": "TermLink-Setup-0.3.19.149-x64.exe"`,
		`"files": [{"name": "artifact-manifest.json", "size": `+fmt.Sprint(len(manifest))+`, "sha256": "`+sha256Hex(manifest)+`"}, {"name": "TermLink-Android-0.3.19-149.apk", "size": 2345, "sha256": "`+apkDigest+`"}, {"name": "TermLink-Setup-0.3.19.149-x64.exe"`, 1))
	server, catalog, assets := newGitHubFixture(t, manifest, metadata, setupDigest, apkDigest)
	defer server.Close()

	offer, err := catalog.LatestWindowsStable(context.Background(), 148)
	if err != nil {
		t.Fatal(err)
	}
	if !offer.Available || offer.Version != "0.3.19" || offer.Build != 149 || offer.SizeBytes != 1234 ||
		offer.SHA256Hex != setupDigest || offer.SignerFingerprint == nil || *offer.SignerFingerprint != githubTestSigner ||
		offer.DownloadURL != "https://github.com/myucxy/TermLink/releases/download/v0.3.19/TermLink-Setup-0.3.19.149-x64.exe" {
		t.Fatalf("unexpected GitHub offer: %+v", offer)
	}
	android, err := catalog.LatestStable(context.Background(), "android", "universal", 148)
	if err != nil || !android.Available || android.ArtifactType != "apk" || android.SHA256Hex != apkDigest ||
		android.SignerFingerprint == nil || *android.SignerFingerprint != DefaultGitHubAndroidSignerSHA256 {
		t.Fatalf("unexpected Android offer: %+v %v", android, err)
	}
	if assets["artifact-manifest.json"] != 1 || assets["github-release.json"] != 1 {
		t.Fatalf("metadata assets were fetched more than once: %+v", assets)
	}
	if assets["latest"] != 1 {
		t.Fatalf("GitHub latest endpoint was not cached: %+v", assets)
	}

	current, err := catalog.LatestWindowsStable(context.Background(), 149)
	if err != nil || current.Available {
		t.Fatalf("current build was offered: %+v %v", current, err)
	}
}

func TestGitHubReleaseCatalogFailsClosedWhenManifestDigestChanges(t *testing.T) {
	manifest := []byte(`{"schema_version":1,"version":"0.3.19","build_number":149,"release_mode":"production","source_revision":"0123456789abcdef0123456789abcdef01234567","artifacts":[]}`)
	metadata := []byte(`{"schema_version":1,"version":"0.3.19","build_number":149,"source_revision":"0123456789abcdef0123456789abcdef01234567","artifact_types":["apk","setup"],"signers":{"windows_signer_sha1":"` + githubTestSigner + `"}}`)
	server, catalog, _ := newGitHubFixture(t, manifest, metadata, strings.Repeat("cd", 32), strings.Repeat("cd", 32), strings.Repeat("00", 32))
	defer server.Close()
	if _, err := catalog.LatestWindowsStable(context.Background(), 148); !errors.Is(err, ErrInvalidArtifact) {
		t.Fatalf("tampered metadata error=%v, want ErrInvalidArtifact", err)
	}
}

func TestFallbackCatalogChoosesHighestNewerBuildWithoutDowngrade(t *testing.T) {
	primary := fakePlatformCatalog{offer: Offer{Available: true, Build: 150, Version: "0.3.20", Platform: "windows", Architecture: "x64", ArtifactType: "exe"}}
	legacyCalls := 0
	legacy := fakeCatalog{offer: Offer{Available: true, Build: 149, Version: "0.3.19", Platform: "windows", Architecture: "x64", ArtifactType: "exe"}, calls: &legacyCalls}
	catalog := &FallbackCatalog{Primary: primary, Legacy: legacy}
	offer, err := catalog.LatestWindowsStable(context.Background(), 148)
	if err != nil || offer.Build != 150 {
		t.Fatalf("primary newer offer was not selected: %+v %v", offer, err)
	}
	if legacyCalls != 0 {
		t.Fatalf("legacy source was queried despite a suitable GitHub offer: %d", legacyCalls)
	}
	catalog = &FallbackCatalog{Primary: fakePlatformCatalog{offer: Offer{Available: false}}, Legacy: legacy}
	offer, err = catalog.LatestWindowsStable(context.Background(), 149)
	if err != nil || offer.Available {
		t.Fatalf("legacy downgrade was offered: %+v %v", offer, err)
	}
}

func newGitHubFixture(t *testing.T, manifest, metadata []byte, setupDigest, apkDigest string, manifestDigestOverride ...string) (*httptest.Server, *GitHubReleaseCatalog, map[string]int) {
	t.Helper()
	manifestDigest := sha256Hex(manifest)
	if len(manifestDigestOverride) > 0 {
		manifestDigest = manifestDigestOverride[0]
	}
	metadataDigest := sha256Hex(metadata)
	assets := map[string]int{}
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/repos/myucxy/TermLink/releases/latest":
			assets["latest"]++
			writeJSON(t, w, map[string]any{
				"tag_name": "v0.3.19", "draft": false, "prerelease": false,
				"published_at": "2026-11-27T00:00:00Z", "body": "GitHub release notes",
				"assets": []any{
					map[string]any{"id": 1, "name": "artifact-manifest.json", "size": len(manifest), "state": "uploaded", "digest": "sha256:" + manifestDigest, "browser_download_url": "https://github.com/myucxy/TermLink/releases/download/v0.3.19/artifact-manifest.json"},
					map[string]any{"id": 2, "name": "github-release.json", "size": len(metadata), "state": "uploaded", "digest": "sha256:" + metadataDigest, "browser_download_url": "https://github.com/myucxy/TermLink/releases/download/v0.3.19/github-release.json"},
					map[string]any{"id": 3, "name": "TermLink-Setup-0.3.19.149-x64.exe", "size": 1234, "state": "uploaded", "digest": "sha256:" + setupDigest, "browser_download_url": "https://github.com/myucxy/TermLink/releases/download/v0.3.19/TermLink-Setup-0.3.19.149-x64.exe"},
					map[string]any{"id": 4, "name": "TermLink-Android-0.3.19-149.apk", "size": 2345, "state": "uploaded", "digest": "sha256:" + apkDigest, "browser_download_url": "https://github.com/myucxy/TermLink/releases/download/v0.3.19/TermLink-Android-0.3.19-149.apk"},
				},
			})
		case "/repos/myucxy/TermLink/releases/assets/1":
			assets["artifact-manifest.json"]++
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write(manifest)
		case "/repos/myucxy/TermLink/releases/assets/2":
			assets["github-release.json"]++
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write(metadata)
		default:
			http.NotFound(w, r)
		}
	}))
	roots := x509.NewCertPool()
	roots.AddCert(server.Certificate())
	client := &http.Client{Transport: &http.Transport{TLSClientConfig: &tls.Config{RootCAs: roots}}}
	catalog, err := newGitHubReleaseCatalog(server.URL, DefaultGitHubRepository, githubTestSigner, client)
	if err != nil {
		server.Close()
		t.Fatal(err)
	}
	return server, catalog, assets
}

func writeJSON(t *testing.T, w http.ResponseWriter, value any) {
	t.Helper()
	w.Header().Set("Content-Type", "application/json")
	if err := json.NewEncoder(w).Encode(value); err != nil {
		t.Fatal(err)
	}
}

func sha256Hex(value []byte) string {
	digest := sha256.Sum256(value)
	return hex.EncodeToString(digest[:])
}

type fakeCatalog struct {
	offer Offer
	err   error
	calls *int
}

func (f fakeCatalog) LatestWindowsStable(context.Context, int64) (Offer, error) {
	if f.calls != nil {
		(*f.calls)++
	}
	return f.offer, f.err
}

type fakePlatformCatalog struct {
	offer Offer
	err   error
}

func (f fakePlatformCatalog) LatestStable(context.Context, string, string, int64) (Offer, error) {
	return f.offer, f.err
}

var _ PlatformCatalog = fakePlatformCatalog{}
var _ Catalog = fakeCatalog{}

func ExampleGitHubReleaseCatalog() {
	// The production constructor is used by the server startup wiring.
	_, _ = NewGitHubReleaseCatalog(nil)
	fmt.Print(DefaultGitHubRepository)
	// Output: myucxy/TermLink
}
