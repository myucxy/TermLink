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
	"path"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	// The production catalog is deliberately bound to the repository that owns
	// the signed client release workflow. A future fork must opt in by using the
	// testable constructor instead of changing an environment variable.
	DefaultGitHubRepository          = "myucxy/TermLink"
	DefaultGitHubAPIBaseURL          = "https://api.github.com"
	DefaultGitHubWindowsSignerSHA1   = "91ae60bc06d866628b8eb2c62f32bde9dab56466"
	DefaultGitHubAndroidSignerSHA256 = "f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b"
	githubReleaseRequestTimeout      = 8 * time.Second
	githubReleaseResponseLimit       = 512 * 1024
	githubReleaseAssetMetadataLimit  = 2 * 1024 * 1024
	githubReleaseAssetMaximumBytes   = 2 * 1024 * 1024 * 1024
	githubReleaseRedirectLimit       = 3
	githubReleaseCacheTTL            = 5 * time.Minute
)

var githubVersionPattern = regexp.MustCompile(`^v(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$`)

// GitHubReleaseCatalog reads only the immutable public metadata needed by the
// remote Agent update contract. The Agent still verifies the downloaded EXE's
// Authenticode signature and SHA-256 before installing it.
type GitHubReleaseCatalog struct {
	repository        string
	apiBaseURL        *url.URL
	client            *http.Client
	windowsSignerSHA1 string
	cacheMu           sync.Mutex
	cache             githubReleaseSnapshot
}

type githubReleaseSnapshot struct {
	expiresAt time.Time
	release   githubRelease
	manifest  []byte
	metadata  []byte
}

type githubRelease struct {
	TagName     string        `json:"tag_name"`
	Draft       bool          `json:"draft"`
	Prerelease  bool          `json:"prerelease"`
	Body        string        `json:"body"`
	PublishedAt string        `json:"published_at"`
	Assets      []githubAsset `json:"assets"`
}

type githubAsset struct {
	ID                 int64  `json:"id"`
	Name               string `json:"name"`
	Size               int64  `json:"size"`
	State              string `json:"state"`
	Digest             string `json:"digest"`
	BrowserDownloadURL string `json:"browser_download_url"`
}

type githubArtifactManifest struct {
	SchemaVersion int                    `json:"schema_version"`
	Version       string                 `json:"version"`
	BuildNumber   int64                  `json:"build_number"`
	ReleaseMode   string                 `json:"release_mode"`
	Source        string                 `json:"source_revision"`
	Artifacts     []githubArtifactRecord `json:"artifacts"`
}

type githubArtifactRecord struct {
	Path     string `json:"path"`
	Size     int64  `json:"size"`
	SHA256   string `json:"sha256"`
	Platform string `json:"platform"`
	Type     string `json:"type"`
	Version  string `json:"version"`
}

type githubReleaseMetadata struct {
	SchemaVersion int                         `json:"schema_version"`
	Version       string                      `json:"version"`
	BuildNumber   int64                       `json:"build_number"`
	Source        string                      `json:"source_revision"`
	ArtifactTypes []string                    `json:"artifact_types"`
	Signers       map[string]string           `json:"signers"`
	Files         []githubReleaseMetadataFile `json:"files"`
}

type githubReleaseMetadataFile struct {
	Name   string `json:"name"`
	Size   int64  `json:"size"`
	SHA256 string `json:"sha256"`
}

// NewGitHubReleaseCatalog creates the production catalog bound to the fixed
// TermLink repository and GitHub API origin.
func NewGitHubReleaseCatalog(client *http.Client) (*GitHubReleaseCatalog, error) {
	return newGitHubReleaseCatalog(DefaultGitHubAPIBaseURL, DefaultGitHubRepository,
		DefaultGitHubWindowsSignerSHA1, client)
}

// newGitHubReleaseCatalog is kept package-private so tests can use a local TLS
// fixture without weakening the production repository/origin constants.
func newGitHubReleaseCatalog(apiBaseURL, repository, windowsSignerSHA1 string, template *http.Client) (*GitHubReleaseCatalog, error) {
	if !validGitHubRepository(repository) {
		return nil, fmt.Errorf("%w: GitHub repository is invalid", ErrUnavailable)
	}
	base, err := url.Parse(strings.TrimSpace(apiBaseURL))
	if err != nil || !strings.EqualFold(base.Scheme, "https") || base.Host == "" ||
		base.User != nil || base.RawQuery != "" || base.Fragment != "" || base.Opaque != "" ||
		(base.Path != "" && base.Path != "/") || !validGitHubAPIHost(base.Hostname(), apiBaseURL != DefaultGitHubAPIBaseURL) {
		return nil, fmt.Errorf("%w: GitHub API origin is invalid", ErrUnavailable)
	}
	anchor := normalizeHex(windowsSignerSHA1)
	if len(anchor) != 40 || !isLowerHex(anchor) {
		return nil, fmt.Errorf("%w: GitHub Windows signer anchor is invalid", ErrUnavailable)
	}
	transport := http.DefaultTransport.(*http.Transport).Clone()
	if template != nil {
		if source, ok := template.Transport.(*http.Transport); ok && source != nil {
			transport.TLSClientConfig = source.TLSClientConfig
		}
	}
	// Release metadata is public and must not inherit proxy credentials, jars,
	// or a caller-controlled redirect policy.
	transport.Proxy = nil
	client := &http.Client{
		Transport: transport,
		Timeout:   githubReleaseRequestTimeout,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= githubReleaseRedirectLimit || req.URL.Scheme != "https" || req.URL.User != nil ||
				(req.URL.Port() != "" && req.URL.Port() != "443") || !githubRedirectHost(req.URL.Hostname()) {
				return http.ErrUseLastResponse
			}
			return nil
		},
	}
	return &GitHubReleaseCatalog{
		repository:        repository,
		apiBaseURL:        base,
		client:            client,
		windowsSignerSHA1: anchor,
	}, nil
}

func (c *GitHubReleaseCatalog) LatestWindowsStable(ctx context.Context, currentBuild int64) (Offer, error) {
	return c.LatestStable(ctx, "windows", "x64", currentBuild)
}

func (c *GitHubReleaseCatalog) LatestStable(ctx context.Context, platform, architecture string, currentBuild int64) (Offer, error) {
	windows := platform == "windows" && architecture == "x64"
	if !windows && (platform != "android" || architecture != "universal") {
		return Offer{}, nil
	}
	if c == nil || c.apiBaseURL == nil || c.client == nil || ctx == nil || currentBuild < 0 {
		return Offer{}, ErrUnavailable
	}
	release, manifestBytes, metadataBytes, err := c.loadReleaseSnapshot(ctx)
	if err != nil {
		return Offer{}, err
	}
	publishedAt, err := time.Parse(time.RFC3339, release.PublishedAt)
	if release.Draft || release.Prerelease || !githubVersionPattern.MatchString(release.TagName) || err != nil {
		return Offer{}, fmt.Errorf("%w: GitHub release is not a published stable release", ErrInvalidArtifact)
	}
	version := strings.TrimPrefix(release.TagName, "v")
	var manifest githubArtifactManifest
	if err := decodeSingleJSON(manifestBytes, &manifest); err != nil {
		return Offer{}, fmt.Errorf("%w: GitHub artifact manifest is invalid", ErrInvalidArtifact)
	}
	if manifest.SchemaVersion != 1 || manifest.ReleaseMode != "production" || manifest.Version != version ||
		manifest.BuildNumber < 1 || manifest.BuildNumber > 65535 ||
		!regexp.MustCompile(`^[0-9a-f]{40}$`).MatchString(manifest.Source) {
		return Offer{}, fmt.Errorf("%w: GitHub artifact manifest identity is invalid", ErrInvalidArtifact)
	}
	if manifest.BuildNumber <= currentBuild {
		return Offer{}, nil
	}
	setup, err := githubWindowsArtifact(manifest)
	if err != nil {
		return Offer{}, err
	}
	asset, err := c.requireAsset(release, path.Base(setup.Path))
	if err != nil {
		return Offer{}, err
	}
	digest := normalizeGitHubDigest(asset.Digest)
	if asset.Size != setup.Size || digest != setup.SHA256 {
		return Offer{}, fmt.Errorf("%w: GitHub Windows asset digest or size differs from its manifest", ErrInvalidArtifact)
	}
	apk, err := githubAndroidArtifact(manifest)
	if err != nil {
		return Offer{}, err
	}
	apkAsset, err := c.requireAsset(release, path.Base(apk.Path))
	if err != nil {
		return Offer{}, err
	}
	if apkAsset.Size != apk.Size || normalizeGitHubDigest(apkAsset.Digest) != apk.SHA256 {
		return Offer{}, fmt.Errorf("%w: GitHub Android asset digest or size differs from its manifest", ErrInvalidArtifact)
	}
	var metadata githubReleaseMetadata
	if err := decodeSingleJSON(metadataBytes, &metadata); err != nil {
		return Offer{}, fmt.Errorf("%w: GitHub release metadata is invalid", ErrInvalidArtifact)
	}
	if metadata.SchemaVersion != 1 || metadata.Version != version || metadata.BuildNumber != manifest.BuildNumber ||
		metadata.Source != manifest.Source || !containsString(metadata.ArtifactTypes, "apk") ||
		!containsString(metadata.ArtifactTypes, "setup") ||
		normalizeHex(metadata.Signers["android_signer_sha256"]) != DefaultGitHubAndroidSignerSHA256 ||
		normalizeHex(metadata.Signers["windows_signer_sha1"]) != c.windowsSignerSHA1 {
		return Offer{}, fmt.Errorf("%w: GitHub release signer or identity policy is invalid", ErrInvalidArtifact)
	}
	if !metadataHasFile(metadata.Files, path.Base(setup.Path), setup.Size, setup.SHA256) {
		return Offer{}, fmt.Errorf("%w: GitHub release metadata is not bound to the Windows asset", ErrInvalidArtifact)
	}
	if !metadataHasFile(metadata.Files, path.Base(apk.Path), apk.Size, apk.SHA256) {
		return Offer{}, fmt.Errorf("%w: GitHub release metadata is not bound to the Android asset", ErrInvalidArtifact)
	}
	manifestAsset, err := c.requireAsset(release, "artifact-manifest.json")
	if err != nil {
		return Offer{}, err
	}
	if !metadataHasFile(metadata.Files, "artifact-manifest.json", manifestAsset.Size, normalizeGitHubDigest(manifestAsset.Digest)) {
		return Offer{}, fmt.Errorf("%w: GitHub release metadata is not bound to its artifact manifest", ErrInvalidArtifact)
	}
	notes := strings.TrimSpace(release.Body)
	if len(notes) > 32*1024 || strings.IndexFunc(notes, func(r rune) bool { return r == '\u0000' }) >= 0 {
		return Offer{}, fmt.Errorf("%w: GitHub release notes are invalid", ErrInvalidArtifact)
	}
	selected, fileType, signer := apk, "apk", DefaultGitHubAndroidSignerSHA256
	if windows {
		selected, fileType, signer = setup, "exe", c.windowsSignerSHA1
	}
	return Offer{
		Available: true, Version: version, Build: manifest.BuildNumber, ReleaseNotes: notes, PublishedAt: publishedAt,
		DownloadURL:      githubBrowserDownloadURL(c.repository, release.TagName, path.Base(selected.Path)),
		ArtifactFilename: path.Base(selected.Path), SizeBytes: selected.Size,
		SHA256: mustDecodeSHA256(selected.SHA256), SHA256Hex: selected.SHA256,
		SignerFingerprint: stringPointer(signer), Platform: platform, Architecture: architecture,
		ArtifactType: fileType,
	}, nil
}

func (c *GitHubReleaseCatalog) loadReleaseSnapshot(ctx context.Context) (githubRelease, []byte, []byte, error) {
	if c == nil || c.client == nil || c.apiBaseURL == nil {
		return githubRelease{}, nil, nil, ErrUnavailable
	}
	c.cacheMu.Lock()
	defer c.cacheMu.Unlock()
	if time.Now().Before(c.cache.expiresAt) {
		if c.cache.release.TagName == "" {
			return githubRelease{}, nil, nil, ErrUnavailable
		}
		return c.cache.release, c.cache.manifest, c.cache.metadata, nil
	}
	// Limit retries for unavailable or ineligible upstream releases.
	c.cache = githubReleaseSnapshot{expiresAt: time.Now().Add(time.Minute)}
	release, err := c.fetchLatestRelease(ctx)
	if err != nil {
		return githubRelease{}, nil, nil, err
	}
	manifestAsset, err := c.requireAsset(release, "artifact-manifest.json")
	if err != nil {
		return githubRelease{}, nil, nil, err
	}
	metadataAsset, err := c.requireAsset(release, "github-release.json")
	if err != nil {
		return githubRelease{}, nil, nil, err
	}
	manifest, err := c.fetchAsset(ctx, manifestAsset)
	if err != nil {
		return githubRelease{}, nil, nil, err
	}
	metadata, err := c.fetchAsset(ctx, metadataAsset)
	if err != nil {
		return githubRelease{}, nil, nil, err
	}
	c.cache = githubReleaseSnapshot{
		expiresAt: time.Now().Add(githubReleaseCacheTTL),
		release:   release,
		manifest:  append([]byte(nil), manifest...),
		metadata:  append([]byte(nil), metadata...),
	}
	return release, manifest, metadata, nil
}

// FallbackCatalog prefers a suitable GitHub release, but keeps already
// published AppShip releases available to old installations during migration.
// It compares builds before selecting a fallback so an older AppShip release
// can never downgrade an Agent that already observed a newer GitHub release.
type FallbackCatalog struct {
	Primary PlatformCatalog
	Legacy  Catalog
}

func (c *FallbackCatalog) LatestWindowsStable(ctx context.Context, currentBuild int64) (Offer, error) {
	if c == nil {
		return Offer{}, ErrUnavailable
	}
	var primary Offer
	var primaryErr error
	if c.Primary != nil {
		primary, primaryErr = c.Primary.LatestStable(ctx, "windows", "x64", currentBuild)
	}
	if primaryErr == nil && primary.Available && primary.Build > currentBuild {
		return primary, nil
	}
	var legacy Offer
	var legacyErr error
	if c.Legacy != nil {
		legacy, legacyErr = c.Legacy.LatestWindowsStable(ctx, currentBuild)
	}
	return chooseFallbackOffer(currentBuild, primaryErr, legacy, legacyErr)
}

func (c *FallbackCatalog) LatestStable(ctx context.Context, platform, architecture string, currentBuild int64) (Offer, error) {
	if c == nil {
		return Offer{}, ErrUnavailable
	}
	var primary Offer
	var primaryErr error
	if c.Primary != nil {
		primary, primaryErr = c.Primary.LatestStable(ctx, platform, architecture, currentBuild)
	}
	if primaryErr == nil && primary.Available && primary.Build > currentBuild {
		return primary, nil
	}
	var legacy Offer
	var legacyErr error
	if legacyCatalog, ok := c.Legacy.(PlatformCatalog); ok {
		legacy, legacyErr = legacyCatalog.LatestStable(ctx, platform, architecture, currentBuild)
	} else if platform == "windows" && architecture == "x64" && c.Legacy != nil {
		legacy, legacyErr = c.Legacy.LatestWindowsStable(ctx, currentBuild)
	}
	return chooseFallbackOffer(currentBuild, primaryErr, legacy, legacyErr)
}

func chooseFallbackOffer(currentBuild int64, primaryErr error, legacy Offer, legacyErr error) (Offer, error) {
	if legacyErr == nil && legacy.Available && legacy.Build > currentBuild {
		return legacy, nil
	}
	if primaryErr != nil || legacyErr != nil {
		return Offer{}, ErrUnavailable
	}
	return Offer{}, nil
}

func (c *GitHubReleaseCatalog) fetchLatestRelease(ctx context.Context) (githubRelease, error) {
	endpoint := *c.apiBaseURL
	endpoint.Path = "/repos/" + c.repository + "/releases/latest"
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint.String(), nil)
	if err != nil {
		return githubRelease{}, ErrUnavailable
	}
	request.Header.Set("Accept", "application/vnd.github+json")
	request.Header.Set("X-GitHub-Api-Version", "2022-11-28")
	request.Header.Set("User-Agent", "TermLink-Server-GitHub-Releases/1")
	response, err := c.client.Do(request)
	if err != nil {
		return githubRelease{}, ErrUnavailable
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return githubRelease{}, ErrUnavailable
	}
	body, err := readGitHubBounded(response.Body, githubReleaseResponseLimit)
	if err != nil {
		return githubRelease{}, ErrUnavailable
	}
	var release githubRelease
	if err := decodeSingleJSON(body, &release); err != nil {
		return githubRelease{}, fmt.Errorf("%w: GitHub release response is invalid", ErrInvalidArtifact)
	}
	return release, nil
}

func (c *GitHubReleaseCatalog) requireAsset(release githubRelease, name string) (githubAsset, error) {
	var result githubAsset
	count := 0
	for _, asset := range release.Assets {
		if asset.Name != name {
			continue
		}
		result, count = asset, count+1
	}
	if count != 1 || result.ID < 1 || result.State != "uploaded" || result.Size < 1 || result.Size > githubReleaseAssetMaximumBytes ||
		normalizeGitHubDigest(result.Digest) == "" {
		return githubAsset{}, fmt.Errorf("%w: GitHub release asset is missing or not immutable", ErrInvalidArtifact)
	}
	if !validGitHubBrowserDownloadURL(result.BrowserDownloadURL, c.repository, release.TagName, name) {
		return githubAsset{}, fmt.Errorf("%w: GitHub release asset URL is not canonical", ErrInvalidArtifact)
	}
	return result, nil
}

func (c *GitHubReleaseCatalog) fetchAsset(ctx context.Context, asset githubAsset) ([]byte, error) {
	endpoint := *c.apiBaseURL
	endpoint.Path = "/repos/" + c.repository + "/releases/assets/" + strconv.FormatInt(asset.ID, 10)
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint.String(), nil)
	if err != nil {
		return nil, ErrUnavailable
	}
	request.Header.Set("Accept", "application/octet-stream")
	request.Header.Set("X-GitHub-Api-Version", "2022-11-28")
	request.Header.Set("User-Agent", "TermLink-Server-GitHub-Releases/1")
	response, err := c.client.Do(request)
	if err != nil {
		return nil, ErrUnavailable
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return nil, ErrUnavailable
	}
	body, err := readGitHubBounded(response.Body, githubReleaseAssetMetadataLimit)
	if err != nil {
		return nil, ErrUnavailable
	}
	digest := sha256.Sum256(body)
	if hex.EncodeToString(digest[:]) != normalizeGitHubDigest(asset.Digest) {
		return nil, fmt.Errorf("%w: GitHub release metadata digest changed", ErrInvalidArtifact)
	}
	return body, nil
}

func githubWindowsArtifact(manifest githubArtifactManifest) (githubArtifactRecord, error) {
	var result githubArtifactRecord
	count := 0
	for _, artifact := range manifest.Artifacts {
		if artifact.Platform == "windows-x64" && artifact.Type == "setup" {
			result, count = artifact, count+1
		}
	}
	if count != 1 || result.Version != manifest.Version || result.Size < 1 || result.Size > githubReleaseAssetMaximumBytes ||
		!regexp.MustCompile(`^[0-9a-f]{64}$`).MatchString(strings.ToLower(result.SHA256)) ||
		path.IsAbs(result.Path) || strings.Contains(result.Path, "\\") || strings.Contains(result.Path, "..") ||
		path.Ext(result.Path) != ".exe" {
		return githubArtifactRecord{}, fmt.Errorf("%w: GitHub Windows setup manifest entry is invalid", ErrInvalidArtifact)
	}
	expected := fmt.Sprintf("TermLink-Setup-%s.%d-x64.exe", manifest.Version, manifest.BuildNumber)
	if result.Path != "artifacts/release/"+manifest.Version+"-"+strconv.FormatInt(manifest.BuildNumber, 10)+"/"+expected {
		return githubArtifactRecord{}, fmt.Errorf("%w: GitHub Windows asset identity is invalid", ErrInvalidArtifact)
	}
	result.SHA256 = strings.ToLower(result.SHA256)
	return result, nil
}

func githubAndroidArtifact(manifest githubArtifactManifest) (githubArtifactRecord, error) {
	var result githubArtifactRecord
	count := 0
	for _, artifact := range manifest.Artifacts {
		if artifact.Platform == "android" && artifact.Type == "apk" {
			result, count = artifact, count+1
		}
	}
	if count != 1 || result.Version != manifest.Version || result.Size < 1 || result.Size > githubReleaseAssetMaximumBytes ||
		!regexp.MustCompile(`^[0-9a-f]{64}$`).MatchString(strings.ToLower(result.SHA256)) ||
		path.IsAbs(result.Path) || strings.Contains(result.Path, "\\") || strings.Contains(result.Path, "..") ||
		path.Ext(result.Path) != ".apk" {
		return githubArtifactRecord{}, fmt.Errorf("%w: GitHub Android APK manifest entry is invalid", ErrInvalidArtifact)
	}
	expected := fmt.Sprintf("TermLink-Android-%s-%d.apk", manifest.Version, manifest.BuildNumber)
	if result.Path != "artifacts/release/"+manifest.Version+"-"+strconv.FormatInt(manifest.BuildNumber, 10)+"/"+expected {
		return githubArtifactRecord{}, fmt.Errorf("%w: GitHub Android asset identity is invalid", ErrInvalidArtifact)
	}
	result.SHA256 = strings.ToLower(result.SHA256)
	return result, nil
}

func metadataHasFile(files []githubReleaseMetadataFile, name string, size int64, digest string) bool {
	for _, file := range files {
		if file.Name == name && file.Size == size && strings.EqualFold(file.SHA256, digest) {
			return true
		}
	}
	return false
}

func decodeSingleJSON(body []byte, target any) error {
	decoder := json.NewDecoder(strings.NewReader(string(body)))
	if err := decoder.Decode(target); err != nil {
		return err
	}
	var trailing any
	if err := decoder.Decode(&trailing); !errors.Is(err, io.EOF) {
		return errors.New("trailing JSON")
	}
	return nil
}

func readGitHubBounded(reader io.Reader, maximum int64) ([]byte, error) {
	if reader == nil || maximum < 1 {
		return nil, errors.New("invalid bounded reader")
	}
	body, err := io.ReadAll(io.LimitReader(reader, maximum+1))
	if err != nil || int64(len(body)) > maximum {
		return nil, errors.New("response exceeds limit")
	}
	return body, nil
}

func validGitHubRepository(value string) bool {
	parts := strings.Split(value, "/")
	return len(parts) == 2 && parts[0] != "" && parts[1] != "" &&
		regexp.MustCompile(`^[A-Za-z0-9_.-]+$`).MatchString(parts[0]) &&
		regexp.MustCompile(`^[A-Za-z0-9_.-]+$`).MatchString(parts[1])
}

func validGitHubAPIHost(host string, testOrigin bool) bool {
	return testOrigin || strings.EqualFold(host, "api.github.com")
}

func githubRedirectHost(host string) bool {
	host = strings.ToLower(strings.TrimSuffix(host, "."))
	return host == "api.github.com" || host == "github.com" || host == "release-assets.githubusercontent.com" || host == "objects.githubusercontent.com"
}

func validGitHubBrowserDownloadURL(value, repository, tag, name string) bool {
	parsed, err := url.Parse(value)
	if err != nil || parsed.Scheme != "https" || parsed.Host != "github.com" || parsed.User != nil || parsed.RawQuery != "" || parsed.Fragment != "" {
		return false
	}
	expected := githubBrowserDownloadURL(repository, tag, name)
	return value == expected
}

func githubBrowserDownloadURL(repository, tag, name string) string {
	return "https://github.com/" + repository + "/releases/download/" + tag + "/" + name
}

func normalizeGitHubDigest(value string) string {
	value = strings.TrimSpace(strings.ToLower(value))
	if !strings.HasPrefix(value, "sha256:") {
		return ""
	}
	digest := strings.TrimPrefix(value, "sha256:")
	if len(digest) != 64 || !isLowerHex(digest) {
		return ""
	}
	return digest
}

func mustDecodeSHA256(value string) [32]byte {
	var result [32]byte
	decoded, _ := hex.DecodeString(value)
	copy(result[:], decoded)
	return result
}

func stringPointer(value string) *string {
	return &value
}

func containsString(values []string, expected string) bool {
	for _, value := range values {
		if value == expected {
			return true
		}
	}
	return false
}
