package agentupdate

import (
	"errors"
	"strings"
	"testing"
)

const validOfferJSON = `{
  "decision":"OPTIONAL",
  "release":{
    "versionName":"0.1.32.38",
    "buildNumber":38,
    "releaseNotes":"Remote update bootstrap",
    "artifact":{
      "downloadUrl":"https://appship.example/artifacts/TermLink-Setup.exe",
      "originalFilename":"TermLink-Setup-0.1.32.38-x64.exe",
      "sizeBytes":123456,
      "expectedSizeBytes":123456,
      "sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "signerFingerprint":"0123456789abcdef0123456789abcdef01234567",
      "platform":"windows",
      "architecture":"x64",
      "fileKind":"exe",
      "status":"ready",
      "downloadEnabled":true
    }
  }
}`

func TestParseOfferAcceptsPinnedWindowsSetupContract(t *testing.T) {
	offer, err := ParseOffer([]byte(validOfferJSON), 37)
	if err != nil {
		t.Fatal(err)
	}
	if !offer.Available || offer.Build != 38 || offer.Version != "0.1.32.38" ||
		offer.SizeBytes != 123456 || offer.SignerFingerprint == nil ||
		*offer.SignerFingerprint != "0123456789abcdef0123456789abcdef01234567" {
		t.Fatalf("unexpected offer: %+v", offer)
	}
}

func TestParseOfferTreatsCurrentBuildAsNoUpdate(t *testing.T) {
	offer, err := ParseOffer([]byte(validOfferJSON), 38)
	if err != nil {
		t.Fatal(err)
	}
	if offer.Available {
		t.Fatalf("current build was offered: %+v", offer)
	}
}

func TestParseOfferRejectsUnpinnedOrUnsafeArtifacts(t *testing.T) {
	for _, test := range []struct {
		name string
		body string
	}{
		{"http download", strings.Replace(validOfferJSON, "https://appship.example", "http://appship.example", 1)},
		{"invalid hash", strings.Replace(validOfferJSON, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "abcd", 1)},
		{"invalid signer", strings.Replace(validOfferJSON, "0123456789abcdef0123456789abcdef01234567", "abcd", 1)},
		{"wrong platform", strings.Replace(validOfferJSON, `"platform":"windows"`, `"platform":"android"`, 1)},
		{"path filename", strings.Replace(validOfferJSON, "TermLink-Setup-0.1.32.38-x64.exe", "folder/TermLink-Setup.exe", 1)},
	} {
		t.Run(test.name, func(t *testing.T) {
			if _, err := ParseOffer([]byte(test.body), 37); !errors.Is(err, ErrInvalidArtifact) {
				t.Fatalf("error=%v, want ErrInvalidArtifact", err)
			}
		})
	}
}

func TestNewAppShipCatalogRequiresHTTPSOrigin(t *testing.T) {
	for _, value := range []string{"http://appship.example", "https://user@appship.example", "not a URL"} {
		if _, err := NewAppShipCatalog(value, nil); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("base URL %q error=%v", value, err)
		}
	}
}
