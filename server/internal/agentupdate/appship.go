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
	"io"
	"net"
	"net/http"
	"net/url"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	DefaultAppShipBaseURL = "https://appship.kget.cc:8910"
	ApplicationSlug       = "termlink-win64"
	maximumResponseBytes  = 256 * 1024
	maximumArtifactBytes  = 2 * 1024 * 1024 * 1024
	appShipRequestTimeout = 5 * time.Second
	maximumHeaderBytes    = 32 * 1024
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
	// mu guards the live target so the management portal can retarget the
	// catalog without a restart; readers snapshot all three fields together.
	mu                      sync.Mutex
	baseURL                 *url.URL
	client                  *http.Client
	// signerFingerprintAnchor is an optional operator trust anchor (SHA-1
	// Authenticode thumbprint, 40 lowercase hex chars). When set, every update
	// offer must carry the matching artifact signer fingerprint; offers
	// without it, or with a different signer, are rejected before they can
	// reach agents (including REQUIRED/forced upgrade decisions).
	signerFingerprintAnchor string
}

type appShipResolver interface {
	LookupIPAddr(context.Context, string) ([]net.IPAddr, error)
}

type appShipDialFunc func(context.Context, string, string) (net.Conn, error)

// appShipNetworkOptions is an intentionally package-private test seam. The
// production constructor always uses the system trust store, resolver, and a
// bounded net.Dialer; callers cannot weaken the catalog's network policy.
type appShipNetworkOptions struct {
	resolver          appShipResolver
	dial              appShipDialFunc
	rootCAs           *x509.CertPool
	allowedPrivateIPs []net.IP
}

func NewAppShipCatalog(baseURL string, client *http.Client) (*AppShipCatalog, error) {
	return newAppShipCatalog(baseURL, client, appShipNetworkOptions{})
}

// NewAppShipCatalogWithAllowedPrivateIPs supports split-horizon DNS without
// weakening the default SSRF boundary. The allowlist accepts only exact
// RFC1918/ULA addresses; CIDRs, loopback, link-local, multicast, public, and
// otherwise special-use addresses fail closed. TLS still authenticates the
// configured AppShip hostname after the validated DNS answer is pinned.
func NewAppShipCatalogWithAllowedPrivateIPs(baseURL string, client *http.Client, value string) (*AppShipCatalog, error) {
	allowed, err := parseAllowedPrivateIPs(value)
	if err != nil {
		return nil, fmt.Errorf("%w: AppShip private IP allowlist is invalid", ErrUnavailable)
	}
	return newAppShipCatalog(baseURL, client, appShipNetworkOptions{allowedPrivateIPs: allowed})
}

// NewAppShipCatalogWithSignerAnchor behaves like
// NewAppShipCatalogWithAllowedPrivateIPs and additionally pins the artifact
// signer identity. An empty anchor keeps the historical pass-through behavior.
func NewAppShipCatalogWithSignerAnchor(baseURL string, client *http.Client, allowedPrivateIPsValue string, signerFingerprint string) (*AppShipCatalog, error) {
	catalog, err := NewAppShipCatalogWithAllowedPrivateIPs(baseURL, client, allowedPrivateIPsValue)
	if err != nil {
		return nil, err
	}
	if trimmed := strings.TrimSpace(signerFingerprint); trimmed != "" {
		normalized := normalizeHex(trimmed)
		if len(normalized) != 40 || !isLowerHex(normalized) {
			return nil, fmt.Errorf("%w: AppShip signer fingerprint anchor must be a 40-char SHA-1 hex thumbprint", ErrUnavailable)
		}
		catalog.signerFingerprintAnchor = normalized
	}
	return catalog, nil
}

func newAppShipCatalog(baseURL string, client *http.Client, network appShipNetworkOptions) (*AppShipCatalog, error) {
	if strings.TrimSpace(baseURL) == "" {
		baseURL = DefaultAppShipBaseURL
	}
	trimmedBaseURL := strings.TrimSpace(baseURL)
	root, err := url.Parse(trimmedBaseURL)
	if err != nil || !strings.EqualFold(root.Scheme, "https") || root.Host == "" || root.Hostname() == "" ||
		root.User != nil || root.Opaque != "" || (root.Path != "" && root.Path != "/") || root.RawPath != "" ||
		root.RawQuery != "" || root.ForceQuery || root.Fragment != "" || strings.Contains(trimmedBaseURL, "#") ||
		strings.HasSuffix(root.Host, ":") {
		return nil, fmt.Errorf("%w: AppShip base URL must be a credential-free HTTPS origin", ErrUnavailable)
	}
	if port := root.Port(); port != "" {
		value, parseErr := strconv.Atoi(port)
		if parseErr != nil || value < 1 || value > 65535 {
			return nil, fmt.Errorf("%w: AppShip base URL must be a credential-free HTTPS origin", ErrUnavailable)
		}
	}
	if !validAppShipHost(root.Hostname()) {
		return nil, fmt.Errorf("%w: AppShip host is not public", ErrUnavailable)
	}
	if network.resolver == nil {
		network.resolver = net.DefaultResolver
	}
	if network.dial == nil {
		dialer := &net.Dialer{Timeout: appShipRequestTimeout, KeepAlive: 30 * time.Second}
		network.dial = dialer.DialContext
	}
	timeout := appShipRequestTimeout
	if client != nil && client.Timeout > 0 && client.Timeout < timeout {
		timeout = client.Timeout
	}
	transport := &http.Transport{
		Proxy:                  nil,
		DialContext:            appShipDialContext(network.resolver, network.dial, network.allowedPrivateIPs...),
		ForceAttemptHTTP2:      true,
		DisableCompression:     true,
		MaxIdleConns:           4,
		MaxIdleConnsPerHost:    2,
		MaxConnsPerHost:        2,
		IdleConnTimeout:        30 * time.Second,
		TLSHandshakeTimeout:    timeout,
		ResponseHeaderTimeout:  timeout,
		ExpectContinueTimeout:  time.Second,
		MaxResponseHeaderBytes: maximumHeaderBytes,
		TLSClientConfig: &tls.Config{
			MinVersion: tls.VersionTLS12,
			RootCAs:    network.rootCAs,
			ServerName: strings.TrimSuffix(root.Hostname(), "."),
		},
	}
	secureClient := &http.Client{
		Transport: transport,
		Timeout:   timeout,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	return &AppShipCatalog{baseURL: root, client: secureClient}, nil
}

func (c *AppShipCatalog) LatestWindowsStable(ctx context.Context, currentBuild int64) (Offer, error) {
	baseURL, client, signerAnchor := c.currentTarget()
	if c == nil || baseURL == nil || client == nil || ctx == nil || currentBuild < 0 {
		return Offer{}, ErrUnavailable
	}
	endpoint := *baseURL
	endpoint.Path = "/api/v1/apps/" + ApplicationSlug + "/updates"
	query := url.Values{}
	query.Set("platform", "windows")
	query.Set("arch", "x64")
	query.Set("channel", "stable")
	query.Set("build", strconv.FormatInt(currentBuild, 10))
	endpoint.RawQuery = query.Encode()
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint.String(), nil)
	if err != nil {
		return Offer{}, fmt.Errorf("%w: request could not be created", ErrUnavailable)
	}
	request.Header.Set("Accept", "application/json")
	request.Header.Set("Cache-Control", "no-cache")
	request.Header.Set("User-Agent", "TermLink-Server-Agent-Updates/1")
	response, err := client.Do(request)
	if err != nil {
		return Offer{}, fmt.Errorf("%w: request failed", ErrUnavailable)
	}
	defer response.Body.Close()
	if response.Request == nil || response.Request.URL == nil || !sameAppShipOrigin(baseURL, response.Request.URL) {
		return Offer{}, fmt.Errorf("%w: response origin is invalid", ErrUnavailable)
	}
	if response.StatusCode == http.StatusNotModified {
		return Offer{}, nil
	}
	if response.StatusCode != http.StatusOK {
		return Offer{}, fmt.Errorf("%w: unexpected HTTP status", ErrUnavailable)
	}
	if response.ContentLength > maximumResponseBytes {
		return Offer{}, fmt.Errorf("%w: response size is invalid", ErrUnavailable)
	}
	reader := io.LimitReader(response.Body, maximumResponseBytes+1)
	body, err := io.ReadAll(reader)
	if err != nil {
		return Offer{}, fmt.Errorf("%w: response could not be read", ErrUnavailable)
	}
	if len(body) == 0 || len(body) > maximumResponseBytes {
		return Offer{}, fmt.Errorf("%w: response size is invalid", ErrUnavailable)
	}
	return parseOffer(body, currentBuild, baseURL, signerAnchor)
}

// UpdateTarget retargets the catalog at runtime so the management portal can
// repoint agent update distribution without a restart. The replacement goes
// through the same constructor validation as startup (HTTPS-only origin,
// public host, exact-address private allowlist); the signer anchor set at
// startup is preserved. Custom test seams (injected resolver or client) are
// intentionally replaced by the production defaults.
func (c *AppShipCatalog) UpdateTarget(baseURL string, allowedPrivateIPsValue string) error {
	if c == nil {
		return nil
	}
	fresh, err := NewAppShipCatalogWithAllowedPrivateIPs(baseURL, nil, allowedPrivateIPsValue)
	if err != nil {
		return err
	}
	c.mu.Lock()
	c.baseURL = fresh.baseURL
	c.client = fresh.client
	c.mu.Unlock()
	return nil
}

func (c *AppShipCatalog) currentTarget() (*url.URL, *http.Client, string) {
	if c == nil {
		return nil, nil, ""
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.baseURL, c.client, c.signerFingerprintAnchor
}

func appShipDialContext(resolver appShipResolver, dial appShipDialFunc, allowedPrivateIPs ...net.IP) appShipDialFunc {
	return func(ctx context.Context, network, address string) (net.Conn, error) {
		if network != "tcp" && network != "tcp4" && network != "tcp6" {
			return nil, ErrUnavailable
		}
		host, port, err := net.SplitHostPort(address)
		if err != nil || host == "" || port == "" || resolver == nil || dial == nil {
			return nil, ErrUnavailable
		}
		var addresses []net.IPAddr
		if literal := net.ParseIP(host); literal != nil {
			addresses = []net.IPAddr{{IP: literal}}
		} else {
			addresses, err = resolver.LookupIPAddr(ctx, host)
			if err != nil {
				return nil, ErrUnavailable
			}
		}
		if len(addresses) == 0 || len(addresses) > 16 {
			return nil, ErrUnavailable
		}
		for _, candidate := range addresses {
			if candidate.Zone != "" || !permittedAppShipIP(candidate.IP, allowedPrivateIPs) {
				return nil, ErrUnavailable
			}
		}
		for _, candidate := range addresses {
			connection, dialErr := dial(ctx, network, net.JoinHostPort(candidate.IP.String(), port))
			if dialErr == nil {
				return connection, nil
			}
		}
		return nil, ErrUnavailable
	}
}

func parseAllowedPrivateIPs(value string) ([]net.IP, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return nil, nil
	}
	parts := strings.Split(value, ",")
	if len(parts) > 8 {
		return nil, ErrUnavailable
	}
	result := make([]net.IP, 0, len(parts))
	seen := make(map[string]struct{}, len(parts))
	for _, part := range parts {
		text := strings.TrimSpace(part)
		ip := net.ParseIP(text)
		if text == "" || ip == nil || !allowlistEligiblePrivateIP(ip) {
			return nil, ErrUnavailable
		}
		if ipv4 := ip.To4(); ipv4 != nil {
			ip = ipv4
		} else {
			ip = ip.To16()
		}
		key := ip.String()
		if _, duplicate := seen[key]; duplicate {
			continue
		}
		seen[key] = struct{}{}
		result = append(result, ip)
	}
	return result, nil
}

func allowlistEligiblePrivateIP(ip net.IP) bool {
	return ip != nil && ip.IsPrivate() && ip.IsGlobalUnicast() && !ip.IsUnspecified() &&
		!ip.IsLoopback() && !ip.IsMulticast() && !ip.IsLinkLocalUnicast() && !ip.IsLinkLocalMulticast()
}

func permittedAppShipIP(ip net.IP, allowedPrivateIPs []net.IP) bool {
	if safeAppShipIP(ip) {
		return true
	}
	if !allowlistEligiblePrivateIP(ip) {
		return false
	}
	for _, allowed := range allowedPrivateIPs {
		if allowed != nil && allowed.Equal(ip) {
			return true
		}
	}
	return false
}

func validAppShipHost(host string) bool {
	host = strings.TrimSuffix(strings.ToLower(strings.TrimSpace(host)), ".")
	if host == "" || strings.Contains(host, "%") || host == "localhost" || strings.HasSuffix(host, ".localhost") {
		return false
	}
	if literal := net.ParseIP(host); literal != nil {
		return safeAppShipIP(literal)
	}
	return true
}

func safeAppShipIP(ip net.IP) bool {
	if ip == nil || ip.IsUnspecified() || ip.IsLoopback() || ip.IsPrivate() || ip.IsMulticast() ||
		ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() || !ip.IsGlobalUnicast() {
		return false
	}
	for _, network := range appShipDeniedNetworks {
		if network.Contains(ip) {
			return false
		}
	}
	return true
}

var appShipDeniedNetworks = func() []*net.IPNet {
	values := []string{
		"0.0.0.0/8", "100.64.0.0/10", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24",
		"198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4",
		"64:ff9b::/96", "64:ff9b:1::/48", "100::/64", "2001::/23", "2001:db8::/32",
		"2002::/16", "3fff::/20", "fec0::/10",
	}
	result := make([]*net.IPNet, 0, len(values))
	for _, value := range values {
		_, network, err := net.ParseCIDR(value)
		if err != nil {
			panic(err)
		}
		result = append(result, network)
	}
	return result
}()

func ParseOffer(body []byte, currentBuild int64) (Offer, error) {
	return parseOffer(body, currentBuild, nil, "")
}

func parseOffer(body []byte, currentBuild int64, trustedOrigin *url.URL, signerFingerprintAnchor string) (Offer, error) {
	var root map[string]any
	decoder := json.NewDecoder(strings.NewReader(string(body)))
	decoder.UseNumber()
	if err := decoder.Decode(&root); err != nil || root == nil {
		return Offer{}, fmt.Errorf("%w: response is not valid JSON", ErrInvalidArtifact)
	}
	var trailing any
	if err := decoder.Decode(&trailing); !errors.Is(err, io.EOF) {
		return Offer{}, fmt.Errorf("%w: response contains trailing data", ErrInvalidArtifact)
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
	if err != nil || !strings.EqualFold(download.Scheme, "https") || download.Host == "" || download.Hostname() == "" ||
		download.User != nil || download.Opaque != "" || download.Fragment != "" ||
		(trustedOrigin != nil && !sameAppShipOrigin(trustedOrigin, download)) {
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
	if signerFingerprintAnchor != "" {
		// Operator-pinned publisher identity: without a matching artifact
		// signer the offer must not reach agents, and a forced (REQUIRED)
		// upgrade decision must never originate from an unverified source.
		if signerPointer == nil {
			return Offer{}, fmt.Errorf("%w: artifact signer fingerprint is required by policy", ErrInvalidArtifact)
		}
		if signer != signerFingerprintAnchor {
			return Offer{}, fmt.Errorf("%w: artifact signer fingerprint is not the pinned publisher", ErrInvalidArtifact)
		}
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

func sameAppShipOrigin(left, right *url.URL) bool {
	return left != nil && right != nil && left.User == nil && right.User == nil &&
		strings.EqualFold(left.Scheme, "https") && strings.EqualFold(right.Scheme, "https") &&
		strings.EqualFold(left.Host, right.Host)
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
