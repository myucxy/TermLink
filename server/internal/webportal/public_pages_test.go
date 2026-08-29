package webportal

import (
	"context"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/termlink/termlink/server/internal/store"
)

type publicReleaseSourceFunc func(context.Context, PublicReleaseTarget) (PublicRelease, error)

func (f publicReleaseSourceFunc) Latest(ctx context.Context, target PublicReleaseTarget) (PublicRelease, error) {
	return f(ctx, target)
}

type testPublicReleaseResolverFunc func(context.Context, string) ([]net.IPAddr, error)

func (resolver testPublicReleaseResolverFunc) LookupIPAddr(ctx context.Context, host string) ([]net.IPAddr, error) {
	return resolver(ctx, host)
}

type publicPlansRepository struct {
	*testPortalRepository
	version int64
	plans   []store.Plan
	err     error
}

func (r *publicPlansRepository) PublishedPlans(context.Context) (int64, []store.Plan, error) {
	return r.version, r.plans, r.err
}

func TestPublicDownloadsAndReleasesUseValidatedAppShipData(t *testing.T) {
	var mutex sync.Mutex
	requests := make(map[string]int)
	var appShipOrigin string
	var appShip *httptest.Server
	appShip = httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "" || r.Header.Get("Cookie") != "" {
			t.Errorf("public AppShip request carried credentials")
		}
		if r.Method != http.MethodGet || r.URL.Query().Get("channel") != "stable" || r.URL.Query().Get("build") != "0" {
			t.Errorf("unexpected AppShip request: %s %s", r.Method, r.URL.String())
		}
		target, ok := targetForAppShipPath(r.URL.Path)
		if !ok || r.URL.Query().Get("platform") != target.Platform || r.URL.Query().Get("arch") != target.Architecture {
			http.NotFound(w, r)
			return
		}
		mutex.Lock()
		requests[target.Platform]++
		mutex.Unlock()
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(publicAppShipFixture(t, appShipOrigin, target, `修复 <script>alert("xss")</script> 与重连显示。`))
	}))
	defer appShip.Close()

	source, origin := newLoopbackTestAppShipSource(t, appShip, 0)
	appShipOrigin = origin
	portal := newTestPortal(t, &testOIDCFlow{})
	portal.releaseSource = source

	for _, target := range []string{"/downloads", "/releases"} {
		response := performRequest(portal.Handler(), http.MethodGet, target, nil)
		if response.Code != http.StatusOK {
			t.Fatalf("GET %s status=%d", target, response.Code)
		}
		body := response.Body.String()
		for _, expected := range []string{"Android", "Windows", "v9.8.7", "Build 987", "0123456789abcdef"} {
			if !strings.Contains(body, expected) {
				t.Errorf("GET %s omitted %q", target, expected)
			}
		}
		if strings.Contains(body, `<script>alert("xss")</script>`) {
			t.Fatalf("GET %s rendered unescaped release notes", target)
		}
		if target == "/releases" && !strings.Contains(body, "&lt;script&gt;alert") {
			t.Fatal("releases page did not render escaped AppShip notes")
		}
		if target == "/downloads" && !strings.Contains(body, appShipOrigin+"/api/v1/artifacts/android/download") {
			t.Fatal("downloads page omitted the validated AppShip URL")
		}
	}
	mutex.Lock()
	defer mutex.Unlock()
	if requests["android"] != 1 || requests["windows"] != 1 {
		t.Fatalf("AppShip requests=%v, want one cached request per target", requests)
	}
}

func TestPublicAppShipSourceRejectsUnsafeTransportAndPayloads(t *testing.T) {
	insecure := httptest.NewServer(http.NotFoundHandler())
	defer insecure.Close()
	if _, err := NewAppShipPublicReleaseSource(insecure.URL, insecure.Client()); !errors.Is(err, ErrPublicReleaseUnavailable) {
		t.Fatalf("HTTP AppShip origin error=%v", err)
	}
	if _, err := NewAppShipPublicReleaseSource("https://user@appship.example", nil); !errors.Is(err, ErrPublicReleaseUnavailable) {
		t.Fatalf("credentialed AppShip origin error=%v", err)
	}
	for _, origin := range []string{"https://localhost", "https://api.localhost", "https://127.0.0.1", "https://[::1]"} {
		if _, err := NewAppShipPublicReleaseSource(origin, nil); !errors.Is(err, ErrPublicReleaseUnavailable) {
			t.Fatalf("private AppShip origin %q error=%v", origin, err)
		}
	}

	target := fixedPublicReleaseTargets[0]
	t.Run("redirect", func(t *testing.T) {
		finalRequests := 0
		var trustedOrigin string
		var server *httptest.Server
		server = httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.URL.Path == "/final" {
				finalRequests++
				_, _ = w.Write(publicAppShipFixture(t, trustedOrigin, target, "notes"))
				return
			}
			http.Redirect(w, r, server.URL+"/final", http.StatusFound)
		}))
		defer server.Close()
		source, origin := newLoopbackTestAppShipSource(t, server, 0)
		trustedOrigin = origin
		if _, requestErr := source.Latest(context.Background(), target); !errors.Is(requestErr, ErrPublicReleaseUnavailable) {
			t.Fatalf("redirect error=%v", requestErr)
		}
		if finalRequests != 0 {
			t.Fatal("public AppShip source followed a redirect")
		}
	})

	t.Run("oversized response", func(t *testing.T) {
		server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			_, _ = w.Write([]byte(strings.Repeat("x", publicReleaseResponseLimit+1)))
		}))
		defer server.Close()
		source, _ := newLoopbackTestAppShipSource(t, server, 0)
		if _, requestErr := source.Latest(context.Background(), target); !errors.Is(requestErr, ErrPublicReleaseUnavailable) {
			t.Fatalf("oversized response error=%v", requestErr)
		}
	})

	t.Run("untrusted download origin", func(t *testing.T) {
		trusted, _ := url.Parse("https://appship.example")
		body := publicAppShipFixture(t, "https://downloads.example", target, "notes")
		if _, err := parseAppShipPublicRelease(body, trusted, target); !errors.Is(err, ErrPublicReleaseUnavailable) {
			t.Fatalf("cross-origin artifact error=%v", err)
		}
	})

	t.Run("unknown fields and wrong target", func(t *testing.T) {
		trusted, _ := url.Parse("https://appship.example")
		body := string(publicAppShipFixture(t, trusted.String(), target, "notes"))
		body = strings.Replace(body, `"status":"optional"`, `"unexpected":true,"status":"optional"`, 1)
		if _, err := parseAppShipPublicRelease([]byte(body), trusted, target); !errors.Is(err, ErrPublicReleaseUnavailable) {
			t.Fatalf("unknown field error=%v", err)
		}
		body = string(publicAppShipFixture(t, trusted.String(), target, "notes"))
		body = strings.Replace(body, `"architecture":"universal"`, `"architecture":"x64"`, 1)
		if _, err := parseAppShipPublicRelease([]byte(body), trusted, target); !errors.Is(err, ErrPublicReleaseUnavailable) {
			t.Fatalf("wrong architecture error=%v", err)
		}
	})

	t.Run("client timeout", func(t *testing.T) {
		server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			time.Sleep(80 * time.Millisecond)
			_, _ = w.Write(publicAppShipFixture(t, "https://unused.example", target, "notes"))
		}))
		defer server.Close()
		source, _ := newLoopbackTestAppShipSource(t, server, 20*time.Millisecond)
		if _, requestErr := source.Latest(context.Background(), target); !errors.Is(requestErr, ErrPublicReleaseUnavailable) {
			t.Fatalf("timeout error=%v", requestErr)
		}
	})

	t.Run("invalid compatibility policy", func(t *testing.T) {
		trusted, _ := url.Parse("https://appship.example")
		body := string(publicAppShipFixture(t, trusted.String(), target, "notes"))
		body = strings.Replace(body, `"minSupportedBuild":null`, `"minSupportedBuild":988`, 1)
		if _, err := parseAppShipPublicRelease([]byte(body), trusted, target); !errors.Is(err, ErrPublicReleaseUnavailable) {
			t.Fatalf("future minimum build error=%v", err)
		}
		body = string(publicAppShipFixture(t, trusted.String(), target, "notes"))
		body = strings.Replace(body, `"forceAfter":null`, `"forceAfter":"not-a-time"`, 1)
		if _, err := parseAppShipPublicRelease([]byte(body), trusted, target); !errors.Is(err, ErrPublicReleaseUnavailable) {
			t.Fatalf("invalid force-after error=%v", err)
		}
	})

	t.Run("declared compatibility policy", func(t *testing.T) {
		trusted, _ := url.Parse("https://appship.example")
		body := string(publicAppShipFixture(t, trusted.String(), target, "notes"))
		body = strings.Replace(body, `"minSupportedBuild":null`, `"minSupportedBuild":900`, 1)
		body = strings.Replace(body, `"forceAfter":null`, `"forceAfter":"2026-09-01T00:00:00Z"`, 1)
		release, err := parseAppShipPublicRelease([]byte(body), trusted, target)
		if err != nil || release.MinimumSupportedBuild == nil || *release.MinimumSupportedBuild != 900 ||
			release.ForceAfter == nil || !release.ForceAfter.Equal(time.Date(2026, 9, 1, 0, 0, 0, 0, time.UTC)) {
			t.Fatalf("declared policy was not preserved: release=%#v err=%v", release, err)
		}
	})
}

func TestPublicAppShipClientPinsNetworkPolicyAndDNSAnswers(t *testing.T) {
	proxy, _ := url.Parse("http://proxy.internal:8080")
	templateClient := &http.Client{Transport: &http.Transport{Proxy: http.ProxyURL(proxy)}}
	source, err := NewAppShipPublicReleaseSource("https://appship.example", templateClient)
	if err != nil {
		t.Fatal(err)
	}
	concrete := source.(*appShipPublicReleaseSource)
	transport, ok := concrete.client.Transport.(*http.Transport)
	if !ok || transport.Proxy != nil || transport.TLSClientConfig == nil ||
		transport.TLSClientConfig.MinVersion < 0x0303 || concrete.client.Jar != nil {
		t.Fatalf("public client inherited unsafe network state: %#v", concrete.client.Transport)
	}

	unsafeAddresses := []string{
		"0.0.0.0", "127.0.0.1", "10.0.0.1", "100.64.0.1", "169.254.1.1",
		"224.0.0.1", "192.0.2.1", "::", "::1", "fc00::1", "fe80::1", "ff02::1", "2001:db8::1",
	}
	for _, value := range unsafeAddresses {
		if safePublicReleaseIP(net.ParseIP(value)) {
			t.Fatalf("unsafe address %s was accepted", value)
		}
	}
	if !safePublicReleaseIP(net.ParseIP("93.184.216.34")) || !safePublicReleaseIP(net.ParseIP("2606:4700:4700::1111")) {
		t.Fatal("globally routable addresses were rejected")
	}

	lookupCount := 0
	resolver := testPublicReleaseResolverFunc(func(context.Context, string) ([]net.IPAddr, error) {
		lookupCount++
		if lookupCount == 1 {
			return []net.IPAddr{{IP: net.ParseIP("93.184.216.34")}}, nil
		}
		return []net.IPAddr{{IP: net.ParseIP("127.0.0.1")}}, nil
	})
	dialCount := 0
	dial := publicReleaseDialContext(resolver, func(_ context.Context, _, address string) (net.Conn, error) {
		dialCount++
		if address != "93.184.216.34:443" {
			t.Fatalf("DNS answer was not pinned into the dial address: %s", address)
		}
		return nil, errors.New("expected test dial stop")
	})
	if _, err := dial(context.Background(), "tcp", "appship.example:443"); err == nil || dialCount != 1 {
		t.Fatalf("first safe resolution: dial=%d err=%v", dialCount, err)
	}
	if _, err := dial(context.Background(), "tcp", "appship.example:443"); !errors.Is(err, ErrPublicReleaseUnavailable) || dialCount != 1 {
		t.Fatalf("DNS rebind was not rejected before dial: dial=%d err=%v", dialCount, err)
	}

	mixed := publicReleaseDialContext(testPublicReleaseResolverFunc(func(context.Context, string) ([]net.IPAddr, error) {
		return []net.IPAddr{{IP: net.ParseIP("93.184.216.34")}, {IP: net.ParseIP("10.0.0.2")}}, nil
	}), func(context.Context, string, string) (net.Conn, error) {
		t.Fatal("mixed safe/private DNS answer reached dial")
		return nil, nil
	})
	if _, err := mixed(context.Background(), "tcp", "appship.example:443"); !errors.Is(err, ErrPublicReleaseUnavailable) {
		t.Fatalf("mixed DNS answer error=%v", err)
	}
}

func TestPublicAppShipSourceAllowsOnlyExplicitSplitHorizonPrivateIPs(t *testing.T) {
	for _, value := range []string{
		"192.168.9.0/24", "127.0.0.1", "169.254.1.1", "93.184.216.34", "", "192.168.9.250,,10.0.0.1",
	} {
		if value == "" {
			continue
		}
		if _, err := NewAppShipPublicReleaseSourceWithAllowedPrivateIPs("https://appship.example", nil, value); !errors.Is(err, ErrPublicReleaseUnavailable) {
			t.Errorf("unsafe private allowlist %q error=%v", value, err)
		}
	}
	allowed, err := parsePublicReleaseAllowedPrivateIPs("192.168.9.250, fd00::250, 192.168.9.250")
	if err != nil || len(allowed) != 2 {
		t.Fatalf("valid private allowlist=%v err=%v", allowed, err)
	}

	dialCount := 0
	dial := publicReleaseDialContext(testPublicReleaseResolverFunc(func(context.Context, string) ([]net.IPAddr, error) {
		return []net.IPAddr{{IP: net.ParseIP("192.168.9.250")}}, nil
	}), func(_ context.Context, _, address string) (net.Conn, error) {
		dialCount++
		if address != "192.168.9.250:8910" {
			t.Fatalf("private DNS answer was not pinned: %s", address)
		}
		return nil, errors.New("expected test dial stop")
	}, allowed...)
	if _, err = dial(context.Background(), "tcp", "appship.example:8910"); err == nil || dialCount != 1 {
		t.Fatalf("allowlisted split DNS dial=%d err=%v", dialCount, err)
	}

	mixed := publicReleaseDialContext(testPublicReleaseResolverFunc(func(context.Context, string) ([]net.IPAddr, error) {
		return []net.IPAddr{{IP: net.ParseIP("192.168.9.250")}, {IP: net.ParseIP("192.168.9.251")}}, nil
	}), func(context.Context, string, string) (net.Conn, error) {
		t.Fatal("DNS answer containing an unapproved private address reached dial")
		return nil, nil
	}, allowed...)
	if _, err = mixed(context.Background(), "tcp", "appship.example:8910"); !errors.Is(err, ErrPublicReleaseUnavailable) {
		t.Fatalf("mixed private DNS answer error=%v", err)
	}

	source, err := NewAppShipPublicReleaseSourceWithAllowedPrivateIPs(
		"https://appship.example:8910", nil, "192.168.9.250",
	)
	if err != nil {
		t.Fatal(err)
	}
	transport := source.(*appShipPublicReleaseSource).client.Transport.(*http.Transport)
	if transport.TLSClientConfig == nil || transport.TLSClientConfig.ServerName != "appship.example" {
		t.Fatalf("TLS hostname was not pinned after split DNS: %#v", transport.TLSClientConfig)
	}
}

func TestPublicReleaseCacheDeduplicatesAndNeverServesExpiredData(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	now := time.Date(2026, time.August, 27, 2, 0, 0, 0, time.UTC)
	portal.releaseCache = newPublicReleaseCache(45*time.Second, func() time.Time { return now })
	var mutex sync.Mutex
	calls := make(map[string]int)
	fail := false
	portal.releaseSource = publicReleaseSourceFunc(func(_ context.Context, target PublicReleaseTarget) (PublicRelease, error) {
		mutex.Lock()
		calls[target.Platform]++
		shouldFail := fail
		mutex.Unlock()
		time.Sleep(10 * time.Millisecond)
		if shouldFail {
			return PublicRelease{}, ErrPublicReleaseUnavailable
		}
		return PublicRelease{
			Target: target, Version: "1.2.3", Build: 123, PublishedAt: now,
			ReleaseNotes: "fresh", DownloadURL: "https://appship.example/download",
			ArtifactFilename: "fixture." + target.FileKind, SizeBytes: 1024, SHA256: strings.Repeat("a", 64),
		}, nil
	})

	var wait sync.WaitGroup
	for index := 0; index < 12; index++ {
		wait.Add(1)
		go func() {
			defer wait.Done()
			cards := portal.loadPublicReleases(context.Background())
			if len(cards) != 2 || !cards[0].Available || !cards[1].Available {
				t.Errorf("concurrent release load returned %+v", cards)
			}
		}()
	}
	wait.Wait()
	mutex.Lock()
	if calls["android"] != 1 || calls["windows"] != 1 {
		t.Fatalf("concurrent calls=%v, want one per target", calls)
	}
	fail = true
	mutex.Unlock()

	if cards := portal.loadPublicReleases(context.Background()); !cards[0].Available || !cards[1].Available {
		t.Fatal("fresh cache was not served within its TTL")
	}
	now = now.Add(46 * time.Second)
	cards := portal.loadPublicReleases(context.Background())
	if cards[0].Available || cards[1].Available {
		t.Fatal("expired cached releases were served after refresh failure")
	}
	mutex.Lock()
	defer mutex.Unlock()
	if calls["android"] != 2 || calls["windows"] != 2 {
		t.Fatalf("post-expiry calls=%v, want a refresh per target", calls)
	}
}

func TestPublicPlansUseOnlyPublishedCatalog(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	portal.config.Repository = &publicPlansRepository{
		testPortalRepository: newTestPortalRepository(),
		version:              73,
		plans: []store.Plan{{
			Key: "dynamic-pro", DisplayName: `<img src=x onerror=alert(1)> 动态专业版`,
			Description: "仅来自 PublishedPlans", BillingPeriod: "MONTHLY", PriceMinor: 1234, Currency: "CNY",
		}},
	}
	response := performRequest(portal.Handler(), http.MethodGet, "/plans", nil)
	if response.Code != http.StatusOK {
		t.Fatalf("plans status=%d", response.Code)
	}
	body := response.Body.String()
	for _, expected := range []string{"当前目录 v73", "动态专业版", "仅来自 PublishedPlans", "¥12.34"} {
		if !strings.Contains(body, expected) {
			t.Errorf("plans page omitted %q", expected)
		}
	}
	if strings.Contains(body, `<img src=x onerror=alert(1)>`) || !strings.Contains(body, `&lt;img src=x onerror=alert(1)&gt;`) {
		t.Fatal("plans page did not HTML-escape catalog content")
	}

	portal.config.Repository = &publicPlansRepository{testPortalRepository: newTestPortalRepository(), err: errors.New("postgresql://secret@db.internal")}
	response = performRequest(portal.Handler(), http.MethodGet, "/plans", nil)
	body = response.Body.String()
	if !strings.Contains(body, "套餐暂不可用") || strings.Contains(body, "secret") || strings.Contains(body, "db.internal") {
		t.Fatalf("plans failure was not safely degraded: %s", body)
	}
}

func TestPublicPagesExposeResponsiveThemeDOMContract(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	response := performRequest(portal.Handler(), http.MethodGet, "/security", nil)
	if response.Code != http.StatusOK {
		t.Fatalf("security status=%d", response.Code)
	}
	body := response.Body.String()
	for _, expected := range []string{
		`name="viewport"`, "viewport-fit=cover", "data-public-shell", "data-public-main",
		"data-desktop-nav", "data-mobile-nav", "data-theme-toggle", `data-security-grid`,
	} {
		if !strings.Contains(body, expected) {
			t.Errorf("public DOM omitted %q", expected)
		}
	}
	initIndex := strings.Index(body, "/assets/theme-init.js")
	styleIndex := strings.Index(body, "/assets/public-pages.css")
	if initIndex < 0 || styleIndex < 0 || initIndex > styleIndex {
		t.Fatal("pre-paint theme setup must load before public page CSS")
	}

	css := performRequest(portal.Handler(), http.MethodGet, "/assets/public-pages.css", nil)
	if css.Code != http.StatusOK || !strings.Contains(css.Header().Get("Content-Type"), "text/css") {
		t.Fatalf("public CSS response=%d %q", css.Code, css.Header().Get("Content-Type"))
	}
	for _, expected := range []string{"[data-desktop-nav]", "[data-mobile-nav]", "@media (max-width: 820px)", "grid-template-columns"} {
		if !strings.Contains(css.Body.String(), expected) {
			t.Errorf("public responsive CSS omitted %q", expected)
		}
	}
}

func TestPublicFailuresShowUnavailableWithoutLeakingDiagnosticsOrStaleData(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	portal.releaseSource = publicReleaseSourceFunc(func(context.Context, PublicReleaseTarget) (PublicRelease, error) {
		return PublicRelease{}, errors.New("postgres db.internal queue=payments password=secret")
	})

	downloads := performRequest(portal.Handler(), http.MethodGet, "/downloads", nil)
	if downloads.Code != http.StatusOK || strings.Count(downloads.Body.String(), "暂不可用") < 2 {
		t.Fatalf("downloads did not degrade both targets: %s", downloads.Body.String())
	}
	status := performRequest(portal.Handler(), http.MethodGet, "/status", nil)
	body := status.Body.String()
	for _, forbidden := range []string{"db.internal", "queue=", "password=", "postgres"} {
		if strings.Contains(body, forbidden) {
			t.Errorf("status page leaked %q", forbidden)
		}
	}
	for _, expected := range []string{"Web 门户", "账号与身份", "实时连接", "支付服务", "客户端版本分发", "暂不可用"} {
		if !strings.Contains(body, expected) {
			t.Errorf("status page omitted coarse status %q", expected)
		}
	}
}

func publicAppShipFixture(t *testing.T, origin string, target PublicReleaseTarget, notes string) []byte {
	t.Helper()
	fingerprint := strings.Repeat("ab", 20)
	filename := "TermLink-Setup-9.8.7.987-x64.exe"
	minimumOS := "Windows 10 1809"
	if target.Platform == "android" {
		fingerprint = strings.Repeat("ab", 32)
		filename = "TermLink-Android-9.8.7-987.apk"
		minimumOS = "Android 8.0"
	}
	payload := map[string]any{
		"status": "optional", "application": target.Application, "channel": "stable",
		"latest": map[string]any{
			"versionName": "9.8.7", "buildNumber": 987,
			"publishedAt": "2026-08-27T01:02:03Z", "releaseNotes": notes,
		},
		"policy": map[string]any{"minSupportedBuild": nil, "forceAfter": nil},
		"artifact": map[string]any{
			"id": "11111111-1111-4111-8111-111111111111", "releaseId": "22222222-2222-4222-8222-222222222222",
			"platform": target.Platform, "fileKind": target.FileKind, "architecture": target.Architecture,
			"originalFilename": filename, "sizeBytes": 12345678, "expectedSizeBytes": 12345678,
			"sha256":   "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
			"mimeType": "application/octet-stream", "status": "ready", "failureReason": nil,
			"signingStatus": "manual_approved", "signerFingerprint": fingerprint, "minOsVersion": minimumOS,
			"downloadEnabled": true, "downloadUrl": fmt.Sprintf("%s/api/v1/artifacts/%s/download", origin, target.Platform),
			"createdAt": "2026-08-27T01:01:00Z", "updatedAt": "2026-08-27T01:02:00Z",
		},
	}
	encoded, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("marshal AppShip fixture: %v", err)
	}
	return encoded
}

func targetForAppShipPath(value string) (PublicReleaseTarget, bool) {
	for _, target := range fixedPublicReleaseTargets {
		if value == "/api/v1/apps/"+target.Application+"/updates" {
			return target, true
		}
	}
	return PublicReleaseTarget{}, false
}

func newLoopbackTestAppShipSource(t *testing.T, server *httptest.Server, timeout time.Duration) (PublicReleaseSource, string) {
	t.Helper()
	_, port, err := net.SplitHostPort(server.Listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	origin := "https://example.com:" + port
	roots := x509.NewCertPool()
	roots.AddCert(server.Certificate())
	resolver := testPublicReleaseResolverFunc(func(_ context.Context, host string) ([]net.IPAddr, error) {
		if host != "example.com" {
			return nil, errors.New("unexpected test host")
		}
		return []net.IPAddr{{IP: net.ParseIP("93.184.216.34")}}, nil
	})
	dialer := &net.Dialer{Timeout: time.Second}
	dial := func(ctx context.Context, network, _ string) (net.Conn, error) {
		return dialer.DialContext(ctx, network, server.Listener.Addr().String())
	}
	client := &http.Client{Timeout: timeout}
	source, err := newAppShipPublicReleaseSource(origin, client, publicReleaseNetworkOptions{
		resolver: resolver, dial: dial, rootCAs: roots,
	})
	if err != nil {
		t.Fatal(err)
	}
	return source, origin
}
