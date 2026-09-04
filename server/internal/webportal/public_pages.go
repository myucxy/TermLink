package webportal

import (
	"bytes"
	"context"
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
	"path"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf8"
)

const (
	DefaultPublicAppShipBaseURL = "https://appship.kget.cc:8910"
	publicReleaseResponseLimit  = 256 * 1024
	publicReleaseArtifactLimit  = int64(2 * 1024 * 1024 * 1024)
	publicReleaseRequestTimeout = 5 * time.Second
	publicReleaseCacheTTL       = 45 * time.Second
)

var ErrPublicReleaseUnavailable = errors.New("public release is unavailable")

// PublicReleaseTarget is one of the fixed public AppShip application targets.
// Custom sources receive this value but must not broaden it to other apps or
// channels.
type PublicReleaseTarget struct {
	Application  string
	Platform     string
	Architecture string
	FileKind     string
	DisplayName  string
}

// PublicRelease is the small, validated subset of AppShip metadata that may be
// rendered on an unauthenticated page.
type PublicRelease struct {
	Target                PublicReleaseTarget
	Required              bool
	Version               string
	Build                 int64
	PublishedAt           time.Time
	ReleaseNotes          string
	DownloadURL           string
	ArtifactFilename      string
	SizeBytes             int64
	SHA256                string
	MinimumOS             string
	MinimumSupportedBuild *int64
	ForceAfter            *time.Time
	SigningStatus         string
	SignerFingerprint     string
}

// PublicReleaseSource is deliberately read-only. Implementations may only
// retrieve already-published metadata for a fixed target.
type PublicReleaseSource interface {
	Latest(context.Context, PublicReleaseTarget) (PublicRelease, error)
}

var fixedPublicReleaseTargets = []PublicReleaseTarget{
	{
		Application: "termlink-android", Platform: "android", Architecture: "universal",
		FileKind: "apk", DisplayName: "Android",
	},
	{
		Application: "termlink-win64", Platform: "windows", Architecture: "x64",
		FileKind: "exe", DisplayName: "Windows",
	},
}

type appShipPublicReleaseSource struct {
	baseURL *url.URL
	client  *http.Client
}

type publicReleaseResolver interface {
	LookupIPAddr(context.Context, string) ([]net.IPAddr, error)
}

type publicReleaseDialFunc func(context.Context, string, string) (net.Conn, error)

type publicReleaseNetworkOptions struct {
	resolver          publicReleaseResolver
	dial              publicReleaseDialFunc
	rootCAs           *x509.CertPool
	allowedPrivateIPs []net.IP
}

type appShipPublicReleaseResponse struct {
	Status      string                `json:"status"`
	Application string                `json:"application"`
	Channel     string                `json:"channel"`
	Latest      appShipPublicLatest   `json:"latest"`
	Policy      appShipPublicPolicy   `json:"policy"`
	Artifact    appShipPublicArtifact `json:"artifact"`
}

type appShipPublicPolicy struct {
	MinimumSupportedBuild *int64  `json:"minSupportedBuild"`
	ForceAfter            *string `json:"forceAfter"`
}

type appShipPublicLatest struct {
	VersionName  string `json:"versionName"`
	BuildNumber  int64  `json:"buildNumber"`
	PublishedAt  string `json:"publishedAt"`
	ReleaseNotes string `json:"releaseNotes"`
}

type appShipPublicArtifact struct {
	ID                string          `json:"id"`
	ReleaseID         string          `json:"releaseId"`
	Platform          string          `json:"platform"`
	FileKind          string          `json:"fileKind"`
	Architecture      string          `json:"architecture"`
	OriginalFilename  string          `json:"originalFilename"`
	SizeBytes         int64           `json:"sizeBytes"`
	ExpectedSizeBytes int64           `json:"expectedSizeBytes"`
	SHA256            string          `json:"sha256"`
	MIMEType          string          `json:"mimeType"`
	Status            string          `json:"status"`
	FailureReason     json.RawMessage `json:"failureReason"`
	SigningStatus     string          `json:"signingStatus"`
	SignerFingerprint string          `json:"signerFingerprint"`
	MinimumOS         string          `json:"minOsVersion"`
	DownloadEnabled   bool            `json:"downloadEnabled"`
	DownloadURL       string          `json:"downloadUrl"`
	CreatedAt         string          `json:"createdAt"`
	UpdatedAt         string          `json:"updatedAt"`
}

type publicPageData struct {
	PageTitle   string
	Description string
	ActivePage  string
	SignedIn    bool
	AccountURL  string
	LoginURL    string
}

type publicReleaseCard struct {
	Target        PublicReleaseTarget
	Available     bool
	Release       PublicRelease
	PublishedDate string
	SizeLabel     string
}

type publicReleasePageData struct {
	publicPageData
	Releases []publicReleaseCard
}

type publicServiceStatus struct {
	Name        string
	State       string
	StateClass  string
	Description string
}

type publicStatusPageData struct {
	publicPageData
	CheckedAt string
	Services  []publicServiceStatus
}

type publicReleaseResult struct {
	index   int
	release PublicRelease
	err     error
}

type publicReleaseCache struct {
	mu       sync.Mutex
	ttl      time.Duration
	now      func() time.Time
	entries  map[string]publicReleaseCacheEntry
	inflight map[string]*publicReleaseFlight
}

type publicReleaseCacheEntry struct {
	release   PublicRelease
	expiresAt time.Time
}

type publicReleaseFlight struct {
	done    chan struct{}
	release PublicRelease
	err     error
}

// NewAppShipPublicReleaseSource creates a credential-free HTTPS reader. The
// client is cloned so redirect and timeout policy cannot be relaxed later.
func NewAppShipPublicReleaseSource(baseURL string, client *http.Client) (PublicReleaseSource, error) {
	return newAppShipPublicReleaseSource(baseURL, client, publicReleaseNetworkOptions{})
}

// NewAppShipPublicReleaseSourceWithAllowedPrivateIPs supports split-horizon
// DNS without weakening the default SSRF boundary. Only exact RFC1918/ULA
// addresses are accepted; CIDRs and all other special-use ranges fail closed.
// TLS continues to authenticate the configured AppShip hostname.
func NewAppShipPublicReleaseSourceWithAllowedPrivateIPs(baseURL string, client *http.Client, value string) (PublicReleaseSource, error) {
	allowed, err := parsePublicReleaseAllowedPrivateIPs(value)
	if err != nil {
		return nil, fmt.Errorf("%w: AppShip private IP allowlist is invalid", ErrPublicReleaseUnavailable)
	}
	return newAppShipPublicReleaseSource(baseURL, client, publicReleaseNetworkOptions{allowedPrivateIPs: allowed})
}

func newAppShipPublicReleaseSource(baseURL string, client *http.Client, network publicReleaseNetworkOptions) (PublicReleaseSource, error) {
	if strings.TrimSpace(baseURL) == "" {
		baseURL = DefaultPublicAppShipBaseURL
	}
	trimmedBaseURL := strings.TrimSpace(baseURL)
	root, err := url.Parse(trimmedBaseURL)
	if err != nil || !strings.EqualFold(root.Scheme, "https") || root.Host == "" || root.Hostname() == "" ||
		root.User != nil || root.Opaque != "" || (root.Path != "" && root.Path != "/") || root.RawPath != "" ||
		root.RawQuery != "" || root.ForceQuery || root.Fragment != "" || strings.Contains(trimmedBaseURL, "#") ||
		strings.HasSuffix(root.Host, ":") {
		return nil, fmt.Errorf("%w: AppShip base URL must be a credential-free HTTPS origin", ErrPublicReleaseUnavailable)
	}
	if port := root.Port(); port != "" {
		value, parseErr := strconv.Atoi(port)
		if parseErr != nil || value < 1 || value > 65535 {
			return nil, fmt.Errorf("%w: AppShip base URL must be a credential-free HTTPS origin", ErrPublicReleaseUnavailable)
		}
	}
	if !validPublicReleaseHost(root.Hostname(), network.allowedPrivateIPs) {
		return nil, fmt.Errorf("%w: AppShip host is not public", ErrPublicReleaseUnavailable)
	}
	if network.resolver == nil {
		network.resolver = net.DefaultResolver
	}
	if network.dial == nil {
		dialer := &net.Dialer{Timeout: publicReleaseRequestTimeout, KeepAlive: 30 * time.Second}
		network.dial = dialer.DialContext
	}
	timeout := publicReleaseRequestTimeout
	if client != nil && client.Timeout > 0 && client.Timeout < timeout {
		timeout = client.Timeout
	}
	transport := &http.Transport{
		Proxy:                 nil,
		DialContext:           publicReleaseDialContext(network.resolver, network.dial, network.allowedPrivateIPs...),
		ForceAttemptHTTP2:     true,
		MaxIdleConns:          4,
		MaxIdleConnsPerHost:   2,
		MaxConnsPerHost:       2,
		IdleConnTimeout:       30 * time.Second,
		TLSHandshakeTimeout:   publicReleaseRequestTimeout,
		ResponseHeaderTimeout: publicReleaseRequestTimeout,
		ExpectContinueTimeout: time.Second,
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
	return &appShipPublicReleaseSource{baseURL: root, client: secureClient}, nil
}

func publicReleaseDialContext(resolver publicReleaseResolver, dial publicReleaseDialFunc, allowedPrivateIPs ...net.IP) publicReleaseDialFunc {
	return func(ctx context.Context, network, address string) (net.Conn, error) {
		if network != "tcp" && network != "tcp4" && network != "tcp6" {
			return nil, ErrPublicReleaseUnavailable
		}
		host, port, err := net.SplitHostPort(address)
		if err != nil || host == "" || port == "" || resolver == nil || dial == nil {
			return nil, ErrPublicReleaseUnavailable
		}
		var addresses []net.IPAddr
		if literal := net.ParseIP(host); literal != nil {
			addresses = []net.IPAddr{{IP: literal}}
		} else {
			addresses, err = resolver.LookupIPAddr(ctx, host)
			if err != nil {
				return nil, ErrPublicReleaseUnavailable
			}
		}
		if len(addresses) == 0 || len(addresses) > 16 {
			return nil, ErrPublicReleaseUnavailable
		}
		for _, candidate := range addresses {
			if candidate.Zone != "" || !permittedPublicReleaseIP(candidate.IP, allowedPrivateIPs) {
				return nil, ErrPublicReleaseUnavailable
			}
		}
		var lastErr error
		for _, candidate := range addresses {
			connection, dialErr := dial(ctx, network, net.JoinHostPort(candidate.IP.String(), port))
			if dialErr == nil {
				return connection, nil
			}
			lastErr = dialErr
		}
		if lastErr == nil {
			lastErr = ErrPublicReleaseUnavailable
		}
		return nil, lastErr
	}
}

func validPublicReleaseHost(host string, allowedPrivateIPs []net.IP) bool {
	host = strings.TrimSuffix(strings.ToLower(strings.TrimSpace(host)), ".")
	if host == "" || strings.Contains(host, "%") || host == "localhost" || strings.HasSuffix(host, ".localhost") {
		return false
	}
	if literal := net.ParseIP(host); literal != nil {
		return permittedPublicReleaseIP(literal, allowedPrivateIPs)
	}
	return true
}

func parsePublicReleaseAllowedPrivateIPs(value string) ([]net.IP, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return nil, nil
	}
	parts := strings.Split(value, ",")
	if len(parts) > 8 {
		return nil, ErrPublicReleaseUnavailable
	}
	result := make([]net.IP, 0, len(parts))
	seen := make(map[string]struct{}, len(parts))
	for _, part := range parts {
		text := strings.TrimSpace(part)
		ip := net.ParseIP(text)
		if text == "" || ip == nil || !allowlistEligiblePublicReleasePrivateIP(ip) {
			return nil, ErrPublicReleaseUnavailable
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

func allowlistEligiblePublicReleasePrivateIP(ip net.IP) bool {
	return ip != nil && ip.IsPrivate() && ip.IsGlobalUnicast() && !ip.IsUnspecified() &&
		!ip.IsLoopback() && !ip.IsMulticast() && !ip.IsLinkLocalUnicast() && !ip.IsLinkLocalMulticast()
}

func permittedPublicReleaseIP(ip net.IP, allowedPrivateIPs []net.IP) bool {
	if safePublicReleaseIP(ip) {
		return true
	}
	if !allowlistEligiblePublicReleasePrivateIP(ip) {
		return false
	}
	for _, allowed := range allowedPrivateIPs {
		if allowed != nil && allowed.Equal(ip) {
			return true
		}
	}
	return false
}

func safePublicReleaseIP(ip net.IP) bool {
	if ip == nil || ip.IsUnspecified() || ip.IsLoopback() || ip.IsPrivate() || ip.IsMulticast() ||
		ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() || !ip.IsGlobalUnicast() {
		return false
	}
	for _, network := range publicReleaseDeniedNetworks {
		if network.Contains(ip) {
			return false
		}
	}
	return true
}

var publicReleaseDeniedNetworks = func() []*net.IPNet {
	values := []string{
		"0.0.0.0/8", "100.64.0.0/10", "192.0.0.0/24", "192.0.2.0/24",
		"198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4",
		"2001:db8::/32",
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

func newPublicReleaseCache(ttl time.Duration, now func() time.Time) *publicReleaseCache {
	if now == nil {
		now = time.Now
	}
	return &publicReleaseCache{
		ttl: ttl, now: now, entries: make(map[string]publicReleaseCacheEntry),
		inflight: make(map[string]*publicReleaseFlight),
	}
}

func (c *publicReleaseCache) latest(ctx context.Context, source PublicReleaseSource, target PublicReleaseTarget) (PublicRelease, error) {
	if c == nil || source == nil || c.ttl <= 0 {
		return PublicRelease{}, ErrPublicReleaseUnavailable
	}
	key := target.Application + ":" + target.Platform + ":" + target.Architecture
	now := c.now().UTC()
	c.mu.Lock()
	if entry, ok := c.entries[key]; ok && now.Before(entry.expiresAt) {
		c.mu.Unlock()
		return clonePublicRelease(entry.release), nil
	}
	if flight, ok := c.inflight[key]; ok {
		c.mu.Unlock()
		select {
		case <-flight.done:
			return clonePublicRelease(flight.release), flight.err
		case <-ctx.Done():
			return PublicRelease{}, ErrPublicReleaseUnavailable
		}
	}
	flight := &publicReleaseFlight{done: make(chan struct{})}
	c.inflight[key] = flight
	c.mu.Unlock()

	fetchContext, cancel := context.WithTimeout(context.WithoutCancel(ctx), publicReleaseRequestTimeout)
	release, err := source.Latest(fetchContext, target)
	cancel()
	if err == nil && release.Target != target {
		err = ErrPublicReleaseUnavailable
	}

	c.mu.Lock()
	flight.release = clonePublicRelease(release)
	flight.err = err
	if err == nil {
		c.entries[key] = publicReleaseCacheEntry{release: clonePublicRelease(release), expiresAt: c.now().UTC().Add(c.ttl)}
	}
	delete(c.inflight, key)
	close(flight.done)
	c.mu.Unlock()
	return clonePublicRelease(release), err
}

func clonePublicRelease(release PublicRelease) PublicRelease {
	if release.MinimumSupportedBuild != nil {
		minimum := *release.MinimumSupportedBuild
		release.MinimumSupportedBuild = &minimum
	}
	if release.ForceAfter != nil {
		forceAfter := *release.ForceAfter
		release.ForceAfter = &forceAfter
	}
	return release
}

func (s *appShipPublicReleaseSource) Latest(ctx context.Context, target PublicReleaseTarget) (PublicRelease, error) {
	if s == nil || s.baseURL == nil || s.client == nil || !isFixedPublicReleaseTarget(target) {
		return PublicRelease{}, ErrPublicReleaseUnavailable
	}
	endpoint := *s.baseURL
	endpoint.Path = "/api/v1/apps/" + target.Application + "/updates"
	query := url.Values{}
	query.Set("platform", target.Platform)
	query.Set("arch", target.Architecture)
	query.Set("channel", "stable")
	query.Set("build", "0")
	endpoint.RawQuery = query.Encode()

	request, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint.String(), nil)
	if err != nil {
		return PublicRelease{}, ErrPublicReleaseUnavailable
	}
	request.Header.Set("Accept", "application/json")
	request.Header.Set("Cache-Control", "no-cache")
	request.Header.Set("User-Agent", "TermLink-Web-Public-Releases/1")
	response, err := s.client.Do(request)
	if err != nil {
		return PublicRelease{}, fmt.Errorf("%w: request failed", ErrPublicReleaseUnavailable)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return PublicRelease{}, fmt.Errorf("%w: unexpected HTTP status", ErrPublicReleaseUnavailable)
	}
	contents, err := io.ReadAll(io.LimitReader(response.Body, publicReleaseResponseLimit+1))
	if err != nil || len(contents) == 0 || len(contents) > publicReleaseResponseLimit {
		return PublicRelease{}, fmt.Errorf("%w: response size is invalid", ErrPublicReleaseUnavailable)
	}
	return parseAppShipPublicRelease(contents, s.baseURL, target)
}

func parseAppShipPublicRelease(contents []byte, trustedOrigin *url.URL, target PublicReleaseTarget) (PublicRelease, error) {
	if trustedOrigin == nil || !isFixedPublicReleaseTarget(target) {
		return PublicRelease{}, ErrPublicReleaseUnavailable
	}
	var payload appShipPublicReleaseResponse
	decoder := json.NewDecoder(bytes.NewReader(contents))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&payload); err != nil {
		return PublicRelease{}, fmt.Errorf("%w: response fields are invalid", ErrPublicReleaseUnavailable)
	}
	var trailing any
	if err := decoder.Decode(&trailing); !errors.Is(err, io.EOF) {
		return PublicRelease{}, fmt.Errorf("%w: response contains trailing data", ErrPublicReleaseUnavailable)
	}
	status := strings.ToLower(strings.TrimSpace(payload.Status))
	if status != "optional" && status != "required" && status != "available" && status != "update_available" {
		return PublicRelease{}, fmt.Errorf("%w: release status is invalid", ErrPublicReleaseUnavailable)
	}
	if payload.Application != target.Application || payload.Channel != "stable" {
		return PublicRelease{}, fmt.Errorf("%w: release target is invalid", ErrPublicReleaseUnavailable)
	}
	if !safePublicVersion(payload.Latest.VersionName) || payload.Latest.BuildNumber < 1 || payload.Latest.BuildNumber > 1_000_000_000 {
		return PublicRelease{}, fmt.Errorf("%w: release version is invalid", ErrPublicReleaseUnavailable)
	}
	publishedAt, err := time.Parse(time.RFC3339Nano, strings.TrimSpace(payload.Latest.PublishedAt))
	if err != nil || publishedAt.IsZero() {
		return PublicRelease{}, fmt.Errorf("%w: release timestamp is invalid", ErrPublicReleaseUnavailable)
	}
	if !safePublicText(payload.Latest.ReleaseNotes, 16*1024, true) || strings.TrimSpace(payload.Latest.ReleaseNotes) == "" {
		return PublicRelease{}, fmt.Errorf("%w: release notes are invalid", ErrPublicReleaseUnavailable)
	}
	if payload.Policy.MinimumSupportedBuild != nil &&
		(*payload.Policy.MinimumSupportedBuild < 1 || *payload.Policy.MinimumSupportedBuild > payload.Latest.BuildNumber) {
		return PublicRelease{}, fmt.Errorf("%w: minimum supported build is invalid", ErrPublicReleaseUnavailable)
	}
	var forceAfter *time.Time
	if payload.Policy.ForceAfter != nil {
		parsed, parseErr := time.Parse(time.RFC3339Nano, strings.TrimSpace(*payload.Policy.ForceAfter))
		if parseErr != nil || parsed.IsZero() {
			return PublicRelease{}, fmt.Errorf("%w: force-after timestamp is invalid", ErrPublicReleaseUnavailable)
		}
		utc := parsed.UTC()
		forceAfter = &utc
	}

	artifact := payload.Artifact
	if artifact.Platform != target.Platform || artifact.Architecture != target.Architecture || artifact.FileKind != target.FileKind ||
		!strings.EqualFold(artifact.Status, "ready") || !artifact.DownloadEnabled {
		return PublicRelease{}, fmt.Errorf("%w: artifact target is invalid", ErrPublicReleaseUnavailable)
	}
	if artifact.SizeBytes < 1 || artifact.SizeBytes > publicReleaseArtifactLimit || artifact.ExpectedSizeBytes != artifact.SizeBytes {
		return PublicRelease{}, fmt.Errorf("%w: artifact size is invalid", ErrPublicReleaseUnavailable)
	}
	filename := strings.TrimSpace(artifact.OriginalFilename)
	if !safePublicText(filename, 240, false) || strings.ContainsAny(filename, `/\`) || filename == "." || filename == ".." ||
		!strings.EqualFold(path.Ext(filename), "."+target.FileKind) {
		return PublicRelease{}, fmt.Errorf("%w: artifact filename is invalid", ErrPublicReleaseUnavailable)
	}
	digest := strings.ToLower(strings.TrimSpace(artifact.SHA256))
	decodedDigest, err := hex.DecodeString(digest)
	if err != nil || len(decodedDigest) != 32 {
		return PublicRelease{}, fmt.Errorf("%w: artifact digest is invalid", ErrPublicReleaseUnavailable)
	}
	fingerprint := strings.ToLower(strings.TrimSpace(artifact.SignerFingerprint))
	decodedFingerprint, err := hex.DecodeString(fingerprint)
	if err != nil || (len(decodedFingerprint) != 20 && len(decodedFingerprint) != 32) {
		return PublicRelease{}, fmt.Errorf("%w: signer fingerprint is invalid", ErrPublicReleaseUnavailable)
	}
	signingStatus := strings.ToLower(strings.TrimSpace(artifact.SigningStatus))
	if !validPublicSigningStatus(signingStatus) {
		return PublicRelease{}, fmt.Errorf("%w: signing status is invalid", ErrPublicReleaseUnavailable)
	}
	if !safePublicText(artifact.MinimumOS, 128, false) {
		return PublicRelease{}, fmt.Errorf("%w: minimum OS is invalid", ErrPublicReleaseUnavailable)
	}
	download, err := url.Parse(strings.TrimSpace(artifact.DownloadURL))
	if err != nil || download.User != nil || download.Opaque != "" || download.Fragment != "" || download.Host == "" ||
		!strings.EqualFold(download.Scheme, "https") || !strings.EqualFold(download.Scheme, trustedOrigin.Scheme) ||
		!strings.EqualFold(download.Host, trustedOrigin.Host) {
		return PublicRelease{}, fmt.Errorf("%w: artifact URL is outside the trusted origin", ErrPublicReleaseUnavailable)
	}

	return PublicRelease{
		Target: target, Required: status == "required", Version: payload.Latest.VersionName, Build: payload.Latest.BuildNumber,
		PublishedAt: publishedAt.UTC(), ReleaseNotes: strings.TrimSpace(payload.Latest.ReleaseNotes),
		DownloadURL: download.String(), ArtifactFilename: filename, SizeBytes: artifact.SizeBytes,
		SHA256: digest, MinimumOS: strings.TrimSpace(artifact.MinimumOS),
		MinimumSupportedBuild: payload.Policy.MinimumSupportedBuild, ForceAfter: forceAfter,
		SigningStatus: signingStatus, SignerFingerprint: fingerprint,
	}, nil
}

func (p *Portal) publicDownloads(w http.ResponseWriter, r *http.Request) {
	p.render(w, http.StatusOK, "public-downloads.html", publicReleasePageData{
		publicPageData: p.publicPage(r, "下载", "获取经过发布校验的 Android 与 Windows 正式版本。", "downloads"),
		Releases:       p.loadPublicReleases(r.Context()),
	})
}

func (p *Portal) publicReleases(w http.ResponseWriter, r *http.Request) {
	p.render(w, http.StatusOK, "public-releases.html", publicReleasePageData{
		publicPageData: p.publicPage(r, "版本记录", "查看稳定通道当前版本及其修复内容。", "releases"),
		Releases:       p.loadPublicReleases(r.Context()),
	})
}

func (p *Portal) publicSecurity(w http.ResponseWriter, r *http.Request) {
	p.render(w, http.StatusOK, "public-security.html", p.publicPage(r, "安全说明", "了解 TermLink 的加密边界、设备隔离与本地优先策略。", "security"))
}

func (p *Portal) publicStatus(w http.ResponseWriter, r *http.Request) {
	releases := p.loadPublicReleases(r.Context())
	available := 0
	for _, release := range releases {
		if release.Available {
			available++
		}
	}
	distribution := publicServiceStatus{Name: "客户端版本分发", State: "暂不可用", StateClass: "unavailable", Description: "当前无法确认公开稳定版本。"}
	if available == len(releases) && available > 0 {
		distribution = publicServiceStatus{Name: "客户端版本分发", State: "正常", StateClass: "operational", Description: "Android 与 Windows 稳定版本元数据可用。"}
	} else if available > 0 {
		distribution = publicServiceStatus{Name: "客户端版本分发", State: "部分受影响", StateClass: "degraded", Description: "部分平台的稳定版本元数据暂不可用。"}
	}
	data := publicStatusPageData{
		publicPageData: p.publicPage(r, "服务状态", "仅公开服务级摘要，不披露内部拓扑或诊断细节。", "status"),
		CheckedAt:      p.now().Format("2006-01-02 15:04 UTC"),
		Services: []publicServiceStatus{
			{Name: "Web 门户", State: "正常", StateClass: "operational", Description: "公开页面当前可访问。"},
			{Name: "账号与身份", State: "未公开", StateClass: "unknown", Description: "详细依赖状态不在公开页面披露。"},
			{Name: "实时连接", State: "未公开", StateClass: "unknown", Description: "连接拓扑与路由信息保持私有。"},
			distribution,
		},
	}
	p.render(w, http.StatusOK, "public-status.html", data)
}

func (p *Portal) publicPage(r *http.Request, title, description, active string) publicPageData {
	data := publicPageData{
		PageTitle: title, Description: description, ActivePage: active,
		AccountURL: "/account", LoginURL: "/login",
	}
	if session, err := p.readSession(r); err == nil {
		if _, accountErr := p.currentAccount(r.Context(), session); accountErr == nil {
			data.SignedIn = true
		}
	}
	return data
}

func (p *Portal) loadPublicReleases(parent context.Context) []publicReleaseCard {
	cards := make([]publicReleaseCard, len(fixedPublicReleaseTargets))
	for index, target := range fixedPublicReleaseTargets {
		cards[index].Target = target
	}
	if p.releaseSource == nil || p.releaseCache == nil {
		return cards
	}
	ctx, cancel := context.WithTimeout(parent, publicReleaseRequestTimeout)
	defer cancel()
	results := make(chan publicReleaseResult, len(cards))
	for index, target := range fixedPublicReleaseTargets {
		go func(index int, target PublicReleaseTarget) {
			release, err := p.releaseCache.latest(ctx, p.releaseSource, target)
			results <- publicReleaseResult{index: index, release: release, err: err}
		}(index, target)
	}
	for remaining := len(cards); remaining > 0; remaining-- {
		select {
		case result := <-results:
			if result.err != nil || result.release.Target != cards[result.index].Target {
				continue
			}
			cards[result.index].Available = true
			cards[result.index].Release = result.release
			cards[result.index].PublishedDate = result.release.PublishedAt.Format("2006-01-02")
			cards[result.index].SizeLabel = formatPublicArtifactSize(result.release.SizeBytes)
		case <-ctx.Done():
			return cards
		}
	}
	return cards
}

func isFixedPublicReleaseTarget(candidate PublicReleaseTarget) bool {
	for _, target := range fixedPublicReleaseTargets {
		if candidate == target {
			return true
		}
	}
	return false
}

func safePublicVersion(value string) bool {
	value = strings.TrimSpace(value)
	if value == "" || len(value) > 64 {
		return false
	}
	for index, character := range value {
		if (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
			(character >= '0' && character <= '9') || (index > 0 && strings.ContainsRune("._+-", character)) {
			continue
		}
		return false
	}
	return true
}

func safePublicText(value string, maximumBytes int, allowLineBreaks bool) bool {
	if len(value) > maximumBytes || !utf8.ValidString(value) {
		return false
	}
	for _, character := range value {
		if !unicode.IsControl(character) {
			continue
		}
		if allowLineBreaks && (character == '\n' || character == '\r' || character == '\t') {
			continue
		}
		return false
	}
	return true
}

func validPublicSigningStatus(value string) bool {
	switch strings.ToLower(strings.TrimSpace(value)) {
	case "manual_approved", "approved", "signed", "verified":
		return true
	default:
		return false
	}
}

func formatPublicArtifactSize(size int64) string {
	if size < 1024*1024 {
		return strconv.FormatInt((size+1023)/1024, 10) + " KiB"
	}
	return fmt.Sprintf("%.1f MiB", float64(size)/(1024*1024))
}
