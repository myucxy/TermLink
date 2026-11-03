package agentupdate

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"
)

const macTestTeam = "ABCDEFGHIJ"

func macOfferBody(t *testing.T, architecture, origin string, change func(map[string]any)) []byte {
	t.Helper()
	value := map[string]any{
		"status": "optional",
		"latest": map[string]any{"versionName": "0.3.17", "buildNumber": 147, "releaseNotes": "Mac production update"},
		"artifact": map[string]any{
			"platform": "macos", "architecture": architecture, "fileKind": "pkg", "status": "ready", "downloadEnabled": true,
			"originalFilename": "TermLink-0.3.17-147-" + architecture + ".pkg",
			"downloadUrl":      origin + "/artifacts/TermLink-" + architecture + ".pkg",
			"sizeBytes":        123456, "expectedSizeBytes": 123456,
			"sha256":            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
			"signerFingerprint": "Developer ID Installer: Test Publisher (" + macTestTeam + ")",
		},
	}
	if change != nil {
		change(value)
	}
	encoded, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	return encoded
}

func TestMacCatalogChannelsUsePinnedHTTPSAndExplicitPlatformMetadata(t *testing.T) {
	for _, architecture := range []string{"arm64", "x64"} {
		t.Run(architecture, func(t *testing.T) {
			var origin string
			var requests atomic.Int32
			var pinned atomic.Bool
			catalog, fixtureOrigin := newTLSAppShipCatalog(t, nil, func(w http.ResponseWriter, r *http.Request) {
				requests.Add(1)
				if r.URL.Path != "/api/v1/apps/termlink-macos-"+architecture+"/updates" ||
					r.URL.Query().Get("platform") != "macos" || r.URL.Query().Get("arch") != architecture ||
					r.URL.Query().Get("channel") != "stable" || r.URL.Query().Get("build") != "146" {
					t.Errorf("incorrect Mac update channel: %s", r.URL.String())
				}
				if r.TLS == nil || r.TLS.ServerName != "appship.example.com" || r.Header.Get("Authorization") != "" ||
					r.Header.Get("Cookie") != "" || r.Header.Get("Proxy-Authorization") != "" {
					t.Error("Mac catalog weakened HTTPS or sent inherited credentials")
				}
				_, _ = w.Write(macOfferBody(t, architecture, origin, nil))
			}, &pinned)
			origin = fixtureOrigin
			catalog.appleTeamIDAnchor = macTestTeam
			var platformCatalog PlatformCatalog = catalog
			offer, err := platformCatalog.LatestStable(context.Background(), "macos", architecture, 146)
			if err != nil {
				t.Fatal(err)
			}
			if !offer.Available || offer.Required || offer.Version != "0.3.17" || offer.Build != 147 ||
				offer.Platform != "macos" || offer.Architecture != architecture || offer.ArtifactType != "pkg" ||
				offer.AppleTeamID != macTestTeam || offer.SignerFingerprint != nil || offer.SizeBytes != 123456 ||
				offer.SHA256Hex != fmt.Sprintf("%x", offer.SHA256[:]) || !strings.HasPrefix(offer.DownloadURL, origin+"/") {
				t.Fatalf("unexpected Mac offer: %+v", offer)
			}
			if requests.Load() != 1 || !pinned.Load() {
				t.Fatalf("Mac HTTPS fixture requests=%d validated DNS pin=%v", requests.Load(), pinned.Load())
			}
		})
	}
}

func TestMacOfferAcceptsTeamIdentityAndRequiredDecision(t *testing.T) {
	origin, _ := url.Parse("https://appship.example.com")
	for _, identity := range []string{macTestTeam, "Developer ID Installer: Test Publisher (" + macTestTeam + ")"} {
		body := macOfferBody(t, "arm64", origin.String(), func(value map[string]any) {
			value["status"] = "required"
			artifact := value["artifact"].(map[string]any)
			artifact["signerFingerprint"] = identity
			artifact["appleTeamId"] = macTestTeam
			artifact["signed"], artifact["notarized"] = true, true
			artifact["channel"] = "stable"
			value["latest"].(map[string]any)["buildChannel"] = "Production"
		})
		offer, err := parseMacOffer(body, 146, "arm64", origin, macTestTeam)
		if err != nil || !offer.Available || !offer.Required || offer.AppleTeamID != macTestTeam {
			t.Fatalf("matching Apple identity rejected: %+v %v", offer, err)
		}
	}
	for _, status := range []string{"no_update", "current"} {
		offer, err := parseMacOffer([]byte(`{"status":"`+status+`"}`), 146, "arm64", origin, macTestTeam)
		if err != nil || offer.Available {
			t.Fatalf("no-update response created an offer: %+v %v", offer, err)
		}
	}
}

func TestMacOfferRejectsWrongIdentityFormatIntegrityAndBuild(t *testing.T) {
	origin, _ := url.Parse("https://appship.example.com")
	artifactField := func(field string, value any) func(map[string]any) {
		return func(root map[string]any) { root["artifact"].(map[string]any)[field] = value }
	}
	releaseField := func(field string, value any) func(map[string]any) {
		return func(root map[string]any) { root["latest"].(map[string]any)[field] = value }
	}
	cases := []struct {
		name   string
		change func(map[string]any)
	}{
		{"wrong platform", artifactField("platform", "windows")},
		{"osx alias is not registered platform", artifactField("platform", "osx")},
		{"wrong architecture", artifactField("architecture", "x64")},
		{"ambiguous architecture alias", artifactField("architecture", "aarch64")},
		{"conflicting architecture alias", artifactField("arch", "x64")},
		{"conflicting format alias", artifactField("file_kind", "exe")},
		{"conflicting artifact type", artifactField("artifact_type", "dmg")},
		{"wrong format", artifactField("fileKind", "zip")},
		{"wrong extension", artifactField("originalFilename", "TermLink.dmg")},
		{"missing filename", artifactField("originalFilename", nil)},
		{"path filename", artifactField("originalFilename", "folder/TermLink.pkg")},
		{"windows path filename", artifactField("originalFilename", `folder\TermLink.pkg`)},
		{"filename controls", artifactField("originalFilename", "TermLink\n.pkg")},
		{"non-ready", artifactField("status", "uploaded")},
		{"downloads disabled", artifactField("downloadEnabled", false)},
		{"malformed download flag", artifactField("downloadEnabled", "true")},
		{"zero size", artifactField("sizeBytes", 0)},
		{"negative size", artifactField("sizeBytes", -1)},
		{"huge size", artifactField("sizeBytes", maximumArtifactBytes+1)},
		{"fractional size", artifactField("sizeBytes", 123456.5)},
		{"expected size mismatch", artifactField("expectedSizeBytes", 123457)},
		{"expected size malformed", artifactField("expectedSizeBytes", "123456")},
		{"hash too short", artifactField("sha256", "abcd")},
		{"hash nonhex", artifactField("sha256", strings.Repeat("g", 64))},
		{"hash normalized punctuation", artifactField("sha256", "01:"+strings.Repeat("ab", 31))},
		{"publisher missing", artifactField("signerFingerprint", nil)},
		{"publisher foreign", artifactField("signerFingerprint", "ZYXWVUTSRQ")},
		{"publisher wrong case", artifactField("signerFingerprint", strings.ToLower(macTestTeam))},
		{"Windows SHA1 is not Apple identity", artifactField("signerFingerprint", strings.Repeat("a", 40))},
		{"application certificate is not installer", artifactField("signerFingerprint", "Developer ID Application: Test Publisher ("+macTestTeam+")")},
		{"publisher ambiguous suffix", artifactField("signerFingerprint", "Developer ID Installer: Test Publisher ("+macTestTeam+") extra")},
		{"publisher controls", artifactField("signerFingerprint", "Developer ID Installer: Test\tPublisher ("+macTestTeam+")")},
		{"TeamID claims conflict", artifactField("appleTeamId", "ZYXWVUTSRQ")},
		{"TeamID alias conflicts", artifactField("apple_team_id", "ZYXWVUTSRQ")},
		{"explicit adhoc signature", artifactField("signatureType", "adhoc")},
		{"explicit development flag", artifactField("isDevelopment", true)},
		{"adhoc signature", artifactField("signerFingerprint", "adhoc")},
		{"unsigned", artifactField("signed", false)},
		{"not notarized", artifactField("notarized", false)},
		{"DEV artifact", artifactField("originalFilename", "TermLink-DEV-osx-arm64.pkg")},
		{"adhoc artifact", artifactField("originalFilename", "TermLink-ad-hoc.pkg")},
		{"debug artifact", artifactField("originalFilename", "TermLink-debug.pkg")},
		{"nonproduction artifact", artifactField("buildChannel", "Development")},
		{"nonstable root", func(value map[string]any) { value["channel"] = "beta" }},
		{"development release", releaseField("buildChannel", "Development")},
		{"current build", releaseField("buildNumber", 146)},
		{"downgrade", releaseField("buildNumber", 145)},
		{"build upper bound", releaseField("buildNumber", 65536)},
		{"build fractional", releaseField("buildNumber", 147.5)},
		{"version missing", releaseField("versionName", nil)},
		{"version dev", releaseField("versionName", "0.3.17-dev")},
		{"version four components", releaseField("versionName", "0.3.17.147")},
		{"version upper bound", releaseField("versionName", "65536.3.17")},
		{"oversize release notes", releaseField("releaseNotes", strings.Repeat("x", 32769))},
		{"HTTP URL", artifactField("downloadUrl", "http://appship.example.com/artifact.pkg")},
		{"credential URL", artifactField("downloadUrl", "https://user:password@appship.example.com/artifact.pkg")},
		{"cross origin URL", artifactField("downloadUrl", "https://cdn.example.com/artifact.pkg")},
		{"different port URL", artifactField("downloadUrl", "https://appship.example.com:8443/artifact.pkg")},
		{"fragment URL", artifactField("downloadUrl", "https://appship.example.com/artifact.pkg#secret")},
		{"empty fragment URL", artifactField("downloadUrl", "https://appship.example.com/artifact.pkg#")},
		{"missing latest", func(value map[string]any) { delete(value, "latest") }},
		{"missing artifact", func(value map[string]any) { delete(value, "artifact") }},
		{"missing status", func(value map[string]any) { delete(value, "status") }},
		{"unknown status", func(value map[string]any) { value["status"] = "forced" }},
	}
	for _, field := range []string{"platform", "architecture", "fileKind", "status", "signerFingerprint", "sha256", "sizeBytes"} {
		field := field
		cases = append(cases, struct {
			name   string
			change func(map[string]any)
		}{"missing " + field, func(value map[string]any) { delete(value["artifact"].(map[string]any), field) }})
	}
	for _, test := range cases {
		t.Run(test.name, func(t *testing.T) {
			offer, err := parseMacOffer(macOfferBody(t, "arm64", origin.String(), test.change), 146, "arm64", origin, macTestTeam)
			if !errors.Is(err, ErrInvalidArtifact) || offer.Available {
				t.Fatalf("invalid Mac offer accepted: available=%v err=%v", offer.Available, err)
			}
		})
	}
	for _, invalid := range []string{"", "{}{}", "null", "[]", `{"status":"optional"} {}`} {
		if _, err := parseMacOffer([]byte(invalid), 146, "arm64", origin, macTestTeam); !errors.Is(err, ErrInvalidArtifact) {
			t.Fatalf("invalid JSON/envelope accepted: %q %v", invalid, err)
		}
	}
}

func TestMacCatalogRequiresSeparatePublisherPolicyBeforeNetwork(t *testing.T) {
	var requests atomic.Int32
	catalog, _ := newTLSAppShipCatalog(t, nil, func(w http.ResponseWriter, _ *http.Request) { requests.Add(1) }, nil)
	catalog.signerFingerprintAnchor = strings.Repeat("a", 40)
	for _, request := range []struct {
		platform, architecture string
		build                  int64
	}{
		{"macos", "arm64", 146}, {"macos", "x64", 146}, {"windows", "arm64", 146},
		{"osx", "arm64", 146}, {"linux", "x64", 146}, {"macos", "aarch64", 146},
	} {
		if _, err := catalog.LatestStable(context.Background(), request.platform, request.architecture, request.build); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("unconfigured/unsupported request accepted: %+v %v", request, err)
		}
	}
	catalog.appleTeamIDAnchor = macTestTeam
	for _, build := range []int64{-1, 0, 65536} {
		if _, err := catalog.LatestStable(context.Background(), "macos", "arm64", build); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("invalid current build %d: %v", build, err)
		}
	}
	if requests.Load() != 0 {
		t.Fatal("Unconfigured or unsupported update selection made a network request")
	}
	var absent *AppShipCatalog
	if _, err := absent.LatestStable(context.Background(), "macos", "arm64", 146); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("nil platform catalog: %v", err)
	}
	if _, err := absent.LatestWindowsStable(context.Background(), 146); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("nil Windows catalog: %v", err)
	}
}

func TestPlatformCatalogConstructorsKeepWindowsAndAppleAnchorsSeparate(t *testing.T) {
	windows := strings.Repeat("a", 40)
	for _, badTeam := range []string{"abc", strings.ToLower(macTestTeam), windows, "Developer ID Installer: Test (" + macTestTeam + ")"} {
		if _, err := NewAppShipCatalogWithPlatformAnchors("https://appship.example.com", nil, "", windows, badTeam); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("invalid Apple Team anchor %q accepted: %v", badTeam, err)
		}
	}
	catalog, err := NewAppShipCatalogWithPlatformAnchors("https://appship.example.com", nil, "192.168.9.250", windows, macTestTeam)
	if err != nil || catalog.signerFingerprintAnchor != windows || catalog.appleTeamIDAnchor != macTestTeam || catalog.client.CheckRedirect == nil {
		t.Fatalf("platform trust policies changed: %+v %v", catalog, err)
	}
	if _, err := NewAppShipCatalogWithPlatformAnchors("https://appship.example.com", nil, "127.0.0.1", windows, macTestTeam); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("platform constructor bypassed the existing SSRF policy: %v", err)
	}
	if _, err := NewAppShipCatalogWithPlatformAnchors("https://appship.example.com", nil, "", "invalid", macTestTeam); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("platform constructor accepted an invalid Windows anchor: %v", err)
	}
	legacy, err := NewAppShipCatalogWithSignerAnchor("https://appship.example.com", nil, "", windows)
	if err != nil || legacy.appleTeamIDAnchor != "" || legacy.signerFingerprintAnchor != windows {
		t.Fatalf("old constructor changed trust behavior: %+v %v", legacy, err)
	}
}

func TestMacCatalogRejectsRedirectOversizeAndForeignArtifact(t *testing.T) {
	for _, mode := range []string{"redirect", "oversize", "foreign-artifact"} {
		t.Run(mode, func(t *testing.T) {
			var redirected atomic.Int32
			catalog, _ := newTLSAppShipCatalog(t, nil, func(w http.ResponseWriter, r *http.Request) {
				if r.URL.Path == "/redirected" {
					redirected.Add(1)
				}
				switch mode {
				case "redirect":
					http.Redirect(w, r, "/redirected?secret=must-not-leak", http.StatusFound)
				case "oversize":
					w.Header().Set("Content-Length", fmt.Sprint(maximumResponseBytes+1))
					_, _ = w.Write([]byte(strings.Repeat("x", maximumResponseBytes+1)))
				case "foreign-artifact":
					_, _ = w.Write(macOfferBody(t, "arm64", "https://foreign.example.com", nil))
				}
			}, nil)
			catalog.appleTeamIDAnchor = macTestTeam
			_, err := catalog.LatestStable(context.Background(), "macos", "arm64", 146)
			want := ErrUnavailable
			if mode == "foreign-artifact" {
				want = ErrInvalidArtifact
			}
			if !errors.Is(err, want) || redirected.Load() != 0 {
				t.Fatalf("unsafe Mac response: redirectHits=%d err=%v", redirected.Load(), err)
			}
			if strings.Contains(err.Error(), "secret") || strings.Contains(err.Error(), "foreign.example.com") {
				t.Fatalf("unsafe response leaked through error: %v", err)
			}
		})
	}
}

func TestPlatformWindowsDelegationPreservesExistingOffer(t *testing.T) {
	var origin string
	catalog, fixtureOrigin := newTLSAppShipCatalog(t, nil, func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write([]byte(strings.Replace(validOfferJSON, "https://appship.example", origin, 1)))
	}, nil)
	origin = fixtureOrigin
	catalog.signerFingerprintAnchor = "0123456789abcdef0123456789abcdef01234567"
	windows, err := catalog.LatestWindowsStable(context.Background(), 37)
	if err != nil {
		t.Fatal(err)
	}
	platform, err := catalog.LatestStable(context.Background(), "windows", "x64", 37)
	if err != nil || !reflect.DeepEqual(windows, platform) || platform.Platform != "windows" || platform.Architecture != "x64" ||
		platform.ArtifactType != "exe" || platform.AppleTeamID != "" || platform.SignerFingerprint == nil {
		t.Fatalf("Windows compatibility failed: legacy=%+v platform=%+v err=%v", windows, platform, err)
	}
}
