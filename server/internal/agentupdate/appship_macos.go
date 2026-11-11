package agentupdate

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"unicode"
	"unicode/utf8"
)

var (
	appleTeamIDPattern            = regexp.MustCompile(`^[A-Z0-9]{10}$`)
	appleInstallerIdentityPattern = regexp.MustCompile(`^Developer ID Installer: [^\r\n]+ \(([A-Z0-9]{10})\)$`)
	macVersionPattern             = regexp.MustCompile(`^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$`)
)

// NewAppShipCatalogWithPlatformAnchors preserves the Windows trust policy and
// independently configures a production Apple Developer ID Team ID. A missing
// Apple anchor keeps Mac updates disabled; a Windows SHA-1 thumbprint can never
// serve as an Apple identity. Network/SSRF/TLS policy remains constructor-owned.
func NewAppShipCatalogWithPlatformAnchors(baseURL string, client *http.Client, allowedPrivateIPs, windowsSignerFingerprint, appleTeamID string) (*AppShipCatalog, error) {
	catalog, err := NewAppShipCatalogWithSignerAnchor(baseURL, client, allowedPrivateIPs, windowsSignerFingerprint)
	if err != nil {
		return nil, err
	}
	if team := strings.TrimSpace(appleTeamID); team != "" {
		if !appleTeamIDPattern.MatchString(team) {
			return nil, fmt.Errorf("%w: Apple publisher anchor must be a 10-character Team ID", ErrUnavailable)
		}
		catalog.appleTeamIDAnchor = team
	}
	return catalog, nil
}

// LatestStable selects exactly one supported platform/architecture channel.
// Mac offers validate trusted catalog metadata only: the Mac installer must
// still verify the downloaded PKG's Developer ID signature and notarization
// with pkgutil/spctl, pinned to this Team ID, before stopping sessions/installing.
func (c *AppShipCatalog) LatestStable(ctx context.Context, platform, architecture string, currentBuild int64) (Offer, error) {
	switch {
	case platform == "windows" && architecture == "x64":
		return c.LatestWindowsStable(ctx, currentBuild)
	case platform == "macos" && (architecture == "arm64" || architecture == "x64"):
		if c == nil || !appleTeamIDPattern.MatchString(c.appleTeamIDAnchor) || currentBuild < 1 || currentBuild > 65535 {
			return Offer{}, fmt.Errorf("%w: Mac publisher policy or current build is not configured", ErrUnavailable)
		}
		return c.latestStable(ctx, "termlink-macos-"+architecture, platform, architecture, currentBuild, func(body []byte) (Offer, error) {
			return parseMacOffer(body, currentBuild, architecture, c.baseURL, c.appleTeamIDAnchor)
		})
	default:
		return Offer{}, fmt.Errorf("%w: unsupported update platform or architecture", ErrUnavailable)
	}
}

func parseMacOffer(body []byte, currentBuild int64, architecture string, trustedOrigin *url.URL, appleTeamID string) (Offer, error) {
	if currentBuild < 1 || currentBuild > 65535 || architecture != "arm64" && architecture != "x64" ||
		trustedOrigin == nil || !appleTeamIDPattern.MatchString(appleTeamID) {
		return Offer{}, fmt.Errorf("%w: invalid Mac update publisher policy", ErrUnavailable)
	}
	if len(body) == 0 || len(body) > maximumResponseBytes {
		return Offer{}, fmt.Errorf("%w: Mac response size is invalid", ErrInvalidArtifact)
	}
	var root map[string]any
	decoder := json.NewDecoder(strings.NewReader(string(body)))
	decoder.UseNumber()
	if err := decoder.Decode(&root); err != nil || root == nil {
		return Offer{}, fmt.Errorf("%w: Mac response is not valid JSON", ErrInvalidArtifact)
	}
	var trailing any
	if err := decoder.Decode(&trailing); !errors.Is(err, io.EOF) {
		return Offer{}, fmt.Errorf("%w: Mac response contains trailing data", ErrInvalidArtifact)
	}
	// Use the same canonical AppShip fields as the Mac desktop update client.
	// Missing fields never fall back to a Windows format or another architecture.
	status, ok := root["status"].(string)
	if !ok {
		return Offer{}, fmt.Errorf("%w: Mac response status is invalid", ErrInvalidArtifact)
	}
	if status == "no_update" || status == "current" {
		return Offer{}, nil
	}
	if status != "optional" && status != "required" {
		return Offer{}, fmt.Errorf("%w: Mac response status is invalid", ErrInvalidArtifact)
	}
	release, _ := root["latest"].(map[string]any)
	artifact, _ := root["artifact"].(map[string]any)
	version, validVersion := release["versionName"].(string)
	build, hasBuild := int64At(release, "buildNumber")
	if !validVersion || !validMacVersion(version) || !hasBuild || build <= currentBuild || build > 65535 || artifact == nil {
		return Offer{}, fmt.Errorf("%w: Mac release must have a valid version and newer build", ErrInvalidArtifact)
	}
	for field, expected := range map[string]string{
		"platform": "macos", "architecture": architecture, "fileKind": "pkg", "status": "ready",
	} {
		if value, ok := artifact[field].(string); !ok || value != expected {
			return Offer{}, fmt.Errorf("%w: Mac artifact platform, architecture, format or readiness differs", ErrInvalidArtifact)
		}
	}
	// Conflicting explicit aliases must not describe a different package to a
	// downstream consumer while the canonical fields pass validation here.
	for field, expected := range map[string]string{
		"arch": architecture, "file_kind": "pkg", "artifact_type": "pkg",
	} {
		if value, present := artifact[field]; present && value != expected {
			return Offer{}, fmt.Errorf("%w: conflicting Mac artifact metadata", ErrInvalidArtifact)
		}
	}
	if enabled, present := artifact["downloadEnabled"]; present && enabled != true {
		return Offer{}, fmt.Errorf("%w: Mac downloads are disabled", ErrInvalidArtifact)
	}
	// Explicit development/ad-hoc/failed-notarization claims may never become an
	// automatic-update offer even if another field carries the production Team.
	if err := validateMacProductionMetadata(root, release, artifact); err != nil {
		return Offer{}, err
	}
	size, hasSize := int64At(artifact, "sizeBytes")
	if !hasSize || size < 1 || size > maximumArtifactBytes {
		return Offer{}, fmt.Errorf("%w: Mac artifact size is invalid", ErrInvalidArtifact)
	}
	if _, present := artifact["expectedSizeBytes"]; present {
		expected, valid := int64At(artifact, "expectedSizeBytes")
		if !valid || expected != size {
			return Offer{}, fmt.Errorf("%w: Mac artifact expected size is invalid", ErrInvalidArtifact)
		}
	}
	hashText, _ := artifact["sha256"].(string)
	digest, err := hex.DecodeString(hashText)
	if err != nil || len(hashText) != 64 || len(digest) != sha256.Size {
		return Offer{}, fmt.Errorf("%w: Mac artifact SHA-256 is invalid", ErrInvalidArtifact)
	}
	signer, _ := artifact["signerFingerprint"].(string)
	if team := macInstallerTeamID(signer); team == "" || team != appleTeamID {
		return Offer{}, fmt.Errorf("%w: Mac artifact requires the pinned Developer ID publisher", ErrInvalidArtifact)
	}
	for _, field := range []string{"appleTeamId", "apple_team_id"} {
		if declared, present := artifact[field]; present && declared != appleTeamID {
			return Offer{}, fmt.Errorf("%w: Mac artifact publisher claims conflict", ErrInvalidArtifact)
		}
	}
	filename, _ := artifact["originalFilename"].(string)
	if !validMacArtifactFilename(filename) {
		return Offer{}, fmt.Errorf("%w: Mac artifact filename is invalid or development-only", ErrInvalidArtifact)
	}
	downloadURL, _ := artifact["downloadUrl"].(string)
	download, err := url.Parse(downloadURL)
	if err != nil || downloadURL == "" || download.Opaque != "" || download.Fragment != "" ||
		strings.Contains(downloadURL, "#") || download.RawPath != "" || strings.Contains(downloadURL, "\\") ||
		strings.IndexFunc(downloadURL, unicode.IsControl) >= 0 || !sameAppShipOrigin(trustedOrigin, download) {
		return Offer{}, fmt.Errorf("%w: Mac artifact URL must use the catalog HTTPS origin", ErrInvalidArtifact)
	}
	notes := ""
	if raw, present := release["releaseNotes"]; present {
		var valid bool
		notes, valid = raw.(string)
		if !valid || !utf8.ValidString(notes) || len(notes) > 32*1024 {
			return Offer{}, fmt.Errorf("%w: Mac release notes are invalid", ErrInvalidArtifact)
		}
	}
	var sha [sha256.Size]byte
	copy(sha[:], digest)
	return Offer{
		Available: true, Required: status == "required", Version: version, Build: build,
		ReleaseNotes: notes, DownloadURL: downloadURL, ArtifactFilename: filename, SizeBytes: size,
		SHA256: sha, SHA256Hex: strings.ToLower(hashText), Platform: "macos", Architecture: architecture,
		ArtifactType: "pkg", AppleTeamID: appleTeamID,
	}, nil
}

func validMacVersion(version string) bool {
	if !macVersionPattern.MatchString(version) {
		return false
	}
	for _, component := range strings.Split(version, ".") {
		value, err := strconv.ParseUint(component, 10, 16)
		if err != nil || value > 65535 {
			return false
		}
	}
	return true
}

func macInstallerTeamID(identity string) string {
	if appleTeamIDPattern.MatchString(identity) {
		return identity
	}
	match := appleInstallerIdentityPattern.FindStringSubmatch(identity)
	if len(match) != 2 || strings.IndexFunc(identity, unicode.IsControl) >= 0 {
		return ""
	}
	return match[1]
}

func validMacArtifactFilename(filename string) bool {
	if !utf8.ValidString(filename) || len(filename) < 5 || len(filename) > 255 ||
		strings.TrimSpace(filename) != filename || strings.ContainsAny(filename, "/\\") ||
		strings.IndexFunc(filename, unicode.IsControl) >= 0 || !strings.HasSuffix(strings.ToLower(filename), ".pkg") {
		return false
	}
	normalizedName := strings.ToLower(filename)
	if strings.Contains(normalizedName, "ad-hoc") || strings.Contains(normalizedName, "ad_hoc") {
		return false
	}
	for _, component := range strings.FieldsFunc(normalizedName, func(value rune) bool {
		return value == '-' || value == '_' || value == '.' || unicode.IsSpace(value)
	}) {
		if component == "dev" || component == "development" || component == "adhoc" || component == "unsigned" || component == "debug" {
			return false
		}
	}
	return true
}

func validateMacProductionMetadata(objects ...map[string]any) error {
	for _, object := range objects {
		for _, field := range []string{"channel", "buildChannel"} {
			if value, present := object[field]; present {
				text, ok := value.(string)
				if !ok || text != "stable" && text != "production" && text != "Production" {
					return fmt.Errorf("%w: Mac update is not a production artifact", ErrInvalidArtifact)
				}
			}
		}
		if value, present := object["isDevelopment"]; present && value != false {
			return fmt.Errorf("%w: Mac update is a development artifact", ErrInvalidArtifact)
		}
		if value, present := object["signatureType"]; present && value != "developer-id-installer" && value != "Developer ID Installer" {
			return fmt.Errorf("%w: Mac update has no Developer ID Installer signature", ErrInvalidArtifact)
		}
		for _, field := range []string{"signed", "notarized"} {
			if value, present := object[field]; present && value != true {
				return fmt.Errorf("%w: Mac signature or notarization is not valid", ErrInvalidArtifact)
			}
		}
	}
	return nil
}
