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
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

const (
	DefaultAppShipBaseURL = "https://appship.kget.cc:8910"
	ApplicationSlug       = "termlink-win64"
	maximumResponseBytes  = 256 * 1024
	maximumArtifactBytes  = 2 * 1024 * 1024 * 1024
	maximumRedirects      = 3
)

var (
	ErrUnavailable     = errors.New("Agent update catalog unavailable")
	ErrInvalidArtifact = errors.New("Agent update artifact is invalid")
)

type Offer struct {
	Available         bool
	Required          bool
	Version           string
	Build             int64
	ReleaseNotes      string
	DownloadURL       string
	ArtifactFilename  string
	SizeBytes         int64
	SHA256            [32]byte
	SHA256Hex         string
	SignerFingerprint *string
}

type Catalog interface {
	LatestWindowsStable(context.Context, int64) (Offer, error)
}

type AppShipCatalog struct {
	baseURL *url.URL
	client  *http.Client
}

func NewAppShipCatalog(baseURL string, client *http.Client) (*AppShipCatalog, error) {
	if strings.TrimSpace(baseURL) == "" {
		baseURL = DefaultAppShipBaseURL
	}
	root, err := url.Parse(strings.TrimRight(strings.TrimSpace(baseURL), "/") + "/")
	if err != nil || !strings.EqualFold(root.Scheme, "https") || root.Host == "" || root.User != nil {
		return nil, fmt.Errorf("%w: AppShip base URL must be an HTTPS origin", ErrUnavailable)
	}
	if client == nil {
		client = &http.Client{Timeout: 20 * time.Second}
	}
	clone := *client
	clone.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	return &AppShipCatalog{baseURL: root, client: &clone}, nil
}

func (c *AppShipCatalog) LatestWindowsStable(ctx context.Context, currentBuild int64) (Offer, error) {
	if c == nil || c.baseURL == nil || c.client == nil || currentBuild < 0 {
		return Offer{}, ErrUnavailable
	}
	relative := "api/v1/apps/" + ApplicationSlug +
		"/updates?platform=windows&arch=x64&channel=stable&build=" + strconv.FormatInt(currentBuild, 10)
	endpoint := c.baseURL.ResolveReference(&url.URL{Path: relative[:strings.IndexByte(relative, '?')], RawQuery: relative[strings.IndexByte(relative, '?')+1:]})
	response, err := c.send(ctx, endpoint)
	if err != nil {
		return Offer{}, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	defer response.Body.Close()
	if response.StatusCode == http.StatusNotModified {
		return Offer{}, nil
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return Offer{}, fmt.Errorf("%w: AppShip returned HTTP %d", ErrUnavailable, response.StatusCode)
	}
	reader := io.LimitReader(response.Body, maximumResponseBytes+1)
	body, err := io.ReadAll(reader)
	if err != nil {
		return Offer{}, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	if len(body) == 0 || len(body) > maximumResponseBytes {
		return Offer{}, fmt.Errorf("%w: response size is invalid", ErrUnavailable)
	}
	return ParseOffer(body, currentBuild)
}

func (c *AppShipCatalog) send(ctx context.Context, initial *url.URL) (*http.Response, error) {
	current := initial
	for redirects := 0; ; redirects++ {
		request, err := http.NewRequestWithContext(ctx, http.MethodGet, current.String(), nil)
		if err != nil {
			return nil, err
		}
		request.Header.Set("Accept", "application/json")
		response, err := c.client.Do(request)
		if err != nil {
			return nil, err
		}
		if response.StatusCode != http.StatusMovedPermanently && response.StatusCode != http.StatusFound &&
			response.StatusCode != http.StatusSeeOther && response.StatusCode != http.StatusTemporaryRedirect &&
			response.StatusCode != http.StatusPermanentRedirect {
			return response, nil
		}
		if redirects >= maximumRedirects {
			response.Body.Close()
			return nil, errors.New("redirect limit exceeded")
		}
		location, err := response.Location()
		response.Body.Close()
		if err != nil || !strings.EqualFold(location.Scheme, "https") || location.Host == "" || location.User != nil {
			return nil, errors.New("redirect target is not HTTPS")
		}
		current = location
	}
}

func ParseOffer(body []byte, currentBuild int64) (Offer, error) {
	var root map[string]any
	decoder := json.NewDecoder(strings.NewReader(string(body)))
	decoder.UseNumber()
	if err := decoder.Decode(&root); err != nil || root == nil {
		return Offer{}, fmt.Errorf("%w: response is not valid JSON", ErrInvalidArtifact)
	}
	release := objectAt(root, "release", "update", "latest")
	if release == nil {
		release = root
	}
	artifact := objectAt(release, "artifact")
	if artifact == nil {
		artifact = objectAt(root, "artifact")
	}
	version := stringAt(release, "versionName", "version_name", "version")
	build, hasBuild := int64At(release, "buildNumber", "build_number", "build")
	decision := strings.ToUpper(strings.TrimSpace(stringAt(root, "decision", "status", "result")))
	available := decision == "REQUIRED" || decision == "MANDATORY" || decision == "FORCE" ||
		decision == "OPTIONAL" || decision == "AVAILABLE" || decision == "UPDATE_AVAILABLE"
	if decision == "NO_UPDATE" || decision == "NONE" || decision == "CURRENT" ||
		decision == "UP_TO_DATE" || decision == "NOT_AVAILABLE" {
		return Offer{}, nil
	}
	if decision == "" {
		available = version != "" || hasBuild || artifact != nil
	}
	if hasBuild && build <= currentBuild {
		return Offer{}, nil
	}
	if !available {
		return Offer{}, nil
	}
	if strings.TrimSpace(version) == "" || !hasBuild || build <= currentBuild || artifact == nil {
		return Offer{}, fmt.Errorf("%w: release metadata is incomplete", ErrInvalidArtifact)
	}
	downloadURL := stringAt(artifact, "downloadUrl", "download_url", "url")
	download, err := url.Parse(downloadURL)
	if err != nil || !strings.EqualFold(download.Scheme, "https") || download.Host == "" || download.User != nil {
		return Offer{}, fmt.Errorf("%w: artifact URL is not HTTPS", ErrInvalidArtifact)
	}
	size, hasSize := int64At(artifact, "sizeBytes", "size_bytes", "size")
	expectedSize, hasExpectedSize := int64At(artifact, "expectedSizeBytes", "expected_size_bytes")
	if !hasSize || size < 1 || size > maximumArtifactBytes || (hasExpectedSize && expectedSize != size) {
		return Offer{}, fmt.Errorf("%w: artifact size is invalid", ErrInvalidArtifact)
	}
	hashText := normalizeHex(stringAt(artifact, "sha256", "sha_256"))
	hashBytes, err := hex.DecodeString(hashText)
	if err != nil || len(hashBytes) != sha256.Size {
		return Offer{}, fmt.Errorf("%w: artifact SHA-256 is invalid", ErrInvalidArtifact)
	}
	var digest [sha256.Size]byte
	copy(digest[:], hashBytes)
	signer := normalizeHex(stringAt(artifact, "signerFingerprint", "signer_fingerprint"))
	var signerPointer *string
	if signer != "" {
		if len(signer) != 40 || !isLowerHex(signer) {
			return Offer{}, fmt.Errorf("%w: signer fingerprint is invalid", ErrInvalidArtifact)
		}
		signerPointer = &signer
	}
	if !matchesOptional(artifact, "platform", "windows") ||
		!matchesOptional(artifact, "fileKind", "exe") ||
		!matchesOptional(artifact, "file_kind", "exe") ||
		!matchesArchitecture(artifact) || !matchesOptional(artifact, "status", "ready") ||
		explicitFalse(artifact, "downloadEnabled") {
		return Offer{}, fmt.Errorf("%w: artifact is not a ready Windows x64 EXE", ErrInvalidArtifact)
	}
	filename := stringAt(artifact, "originalFilename", "original_filename", "filename")
	if filename == "" {
		filename = fmt.Sprintf("TermLink-Setup-%s-%d-x64.exe", version, build)
	}
	if filepath.Ext(filename) != ".exe" && filepath.Ext(filename) != ".EXE" || filepath.Base(filename) != filename {
		return Offer{}, fmt.Errorf("%w: artifact filename is invalid", ErrInvalidArtifact)
	}
	return Offer{
		Available: true, Required: decision == "REQUIRED" || decision == "MANDATORY" || decision == "FORCE",
		Version: version, Build: build,
		ReleaseNotes: stringAt(release, "releaseNotes", "release_notes", "notes"),
		DownloadURL:  downloadURL, ArtifactFilename: filename, SizeBytes: size,
		SHA256: digest, SHA256Hex: hashText, SignerFingerprint: signerPointer,
	}, nil
}

func objectAt(parent map[string]any, names ...string) map[string]any {
	for _, name := range names {
		if value, ok := parent[name].(map[string]any); ok {
			return value
		}
	}
	return nil
}

func stringAt(parent map[string]any, names ...string) string {
	for _, name := range names {
		if value, ok := parent[name].(string); ok {
			return strings.TrimSpace(value)
		}
	}
	return ""
}

func int64At(parent map[string]any, names ...string) (int64, bool) {
	for _, name := range names {
		switch value := parent[name].(type) {
		case json.Number:
			parsed, err := value.Int64()
			return parsed, err == nil
		case float64:
			return int64(value), value == float64(int64(value))
		}
	}
	return 0, false
}

func matchesOptional(parent map[string]any, name, expected string) bool {
	value, present := parent[name]
	if !present || value == nil {
		return true
	}
	text, ok := value.(string)
	return ok && strings.EqualFold(strings.TrimSpace(text), expected)
}

func matchesArchitecture(parent map[string]any) bool {
	for _, name := range []string{"architecture", "arch"} {
		value, present := parent[name]
		if !present || value == nil {
			continue
		}
		text, ok := value.(string)
		return ok && (strings.EqualFold(text, "x64") || strings.EqualFold(text, "amd64"))
	}
	return true
}

func explicitFalse(parent map[string]any, name string) bool {
	value, present := parent[name]
	return present && value == false
}

func normalizeHex(value string) string {
	return strings.Map(func(character rune) rune {
		if character == ':' || character == '-' || character == ' ' || character == '\t' || character == '\r' || character == '\n' {
			return -1
		}
		return character
	}, strings.ToLower(strings.TrimSpace(value)))
}

func isLowerHex(value string) bool {
	for _, character := range value {
		if (character < '0' || character > '9') && (character < 'a' || character > 'f') {
			return false
		}
	}
	return true
}
