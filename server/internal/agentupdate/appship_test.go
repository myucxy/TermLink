package agentupdate

import (
	"context"
	"crypto/x509"
	"errors"
	"net"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"
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

// The signer anchor is an operator trust identity: with it configured, an
// offer without the pinned artifact signer (or with a different one) must be
// rejected before it can trigger any agent upgrade decision.
func TestParseOfferSignerAnchorEnforcement(t *testing.T) {
	anchor := "0123456789abcdef0123456789abcdef01234567"
	// parseOffer via the exported seam has no anchor; exercise the anchor by
	// normalizing the JSON with a mutated signer per case.
	signerKey := `"signerFingerprint":"0123456789abcdef0123456789abcdef01234567"`
	mutate := func(signerValue string) []byte {
		if signerValue == "" {
			return []byte(strings.Replace(validOfferJSON, signerKey+",", "", 1))
		}
		return []byte(strings.Replace(validOfferJSON, signerKey,
			`"signerFingerprint":"`+signerValue+`"`, 1))
	}

	cases := []struct {
		name        string
		signer      string
		wantErrText string
	}{
		{name: "matching signer is accepted", signer: anchor, wantErrText: ""},
		{name: "missing signer is rejected", signer: "", wantErrText: "required by policy"},
		{name: "foreign signer is rejected", signer: "ffffffffffffffffffffffffffffffffffffffff", wantErrText: "not the pinned publisher"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			offer, err := parseOffer(mutate(tc.signer), 37, nil, anchor)
			if tc.wantErrText == "" {
				if err != nil || !offer.Available {
					t.Fatalf("pinned offer rejected: err=%v offer=%+v", err, offer)
				}
				return
			}
			if err == nil || !strings.Contains(err.Error(), tc.wantErrText) {
				t.Fatalf("anchor policy error = %v, want containing %q", err, tc.wantErrText)
			}
		})
	}
}

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
		{"credentialed download", strings.Replace(validOfferJSON, "https://appship.example", "https://user:secret@appship.example", 1)},
		{"fragmented download", strings.Replace(validOfferJSON, "TermLink-Setup.exe", "TermLink-Setup.exe#secret", 1)},
		{"invalid hash", strings.Replace(validOfferJSON, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "abcd", 1)},
		{"invalid signer", strings.Replace(validOfferJSON, "0123456789abcdef0123456789abcdef01234567", "abcd", 1)},
		{"wrong platform", strings.Replace(validOfferJSON, `"platform":"windows"`, `"platform":"android"`, 1)},
		{"path filename", strings.Replace(validOfferJSON, "TermLink-Setup-0.1.32.38-x64.exe", "folder/TermLink-Setup.exe", 1)},
		{"trailing document", validOfferJSON + `{}`},
	} {
		t.Run(test.name, func(t *testing.T) {
			if _, err := ParseOffer([]byte(test.body), 37); !errors.Is(err, ErrInvalidArtifact) {
				t.Fatalf("error=%v, want ErrInvalidArtifact", err)
			}
		})
	}
}

func TestNewAppShipCatalogRequiresHTTPSOrigin(t *testing.T) {
	for _, value := range []string{
		"http://appship.example.com",
		"https://user:secret@appship.example.com",
		"https://appship.example.com/api",
		"https://appship.example.com?token=secret",
		"https://appship.example.com?",
		"https://appship.example.com#fragment",
		"https://appship.example.com#",
		"https://appship.example.com/%2f",
		"https://appship.example.com:",
		"https://appship.example.com:0",
		"https://appship.example.com:65536",
		"https://localhost",
		"https://127.0.0.1",
		"https://10.0.0.1",
		"https://[::1]",
		"https://[fc00::1]",
		"not a URL",
	} {
		if _, err := NewAppShipCatalog(value, nil); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("base URL %q error=%v", value, err)
		}
	}

	catalog, err := NewAppShipCatalog("", nil)
	if err != nil {
		t.Fatalf("default public AppShip origin: %v", err)
	}
	if catalog.baseURL.String() != DefaultAppShipBaseURL {
		t.Fatalf("default origin=%q", catalog.baseURL.String())
	}
}

func TestAppShipCatalogUsesIsolatedBoundedHTTPSClient(t *testing.T) {
	proxy, _ := url.Parse("http://user:secret@proxy.internal:8080")
	jar, err := cookiejar.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	template := &http.Client{
		Transport: &http.Transport{Proxy: http.ProxyURL(proxy)},
		Jar:       jar,
		Timeout:   20 * time.Second,
	}
	catalog, err := NewAppShipCatalog("https://appship.example.com", template)
	if err != nil {
		t.Fatal(err)
	}
	transport, ok := catalog.client.Transport.(*http.Transport)
	if !ok {
		t.Fatalf("transport type=%T", catalog.client.Transport)
	}
	if transport.Proxy != nil || catalog.client.Jar != nil || catalog.client.Timeout != appShipRequestTimeout {
		t.Fatalf("catalog inherited unsafe client state: client=%#v transport=%#v", catalog.client, transport)
	}
	if transport.TLSClientConfig == nil || transport.TLSClientConfig.MinVersion < 0x0303 ||
		transport.TLSClientConfig.ServerName != "appship.example.com" {
		t.Fatalf("TLS policy=%#v", transport.TLSClientConfig)
	}
	if transport.DialContext == nil || transport.MaxResponseHeaderBytes != maximumHeaderBytes ||
		transport.ResponseHeaderTimeout > appShipRequestTimeout || !transport.DisableCompression {
		t.Fatalf("bounded transport policy is incomplete: %#v", transport)
	}
	request, _ := http.NewRequest(http.MethodGet, "https://appship.example.com", nil)
	if redirectErr := catalog.client.CheckRedirect(request, nil); !errors.Is(redirectErr, http.ErrUseLastResponse) {
		t.Fatalf("redirect policy error=%v", redirectErr)
	}
}

func TestAppShipNetworkPolicyRejectsUnsafeAddressesAndDNSRebinding(t *testing.T) {
	unsafeAddresses := []string{
		"0.0.0.0", "127.0.0.1", "10.0.0.1", "100.64.0.1", "169.254.1.1", "192.0.2.1",
		"198.18.0.1", "224.0.0.1", "::", "::1", "::ffff:127.0.0.1", "fc00::1", "fe80::1",
		"ff02::1", "64:ff9b::7f00:1", "2001:db8::1", "2002:7f00:1::1",
	}
	for _, value := range unsafeAddresses {
		if safeAppShipIP(net.ParseIP(value)) {
			t.Fatalf("unsafe address %s was accepted", value)
		}
	}
	for _, value := range []string{"93.184.216.34", "2606:4700:4700::1111"} {
		if !safeAppShipIP(net.ParseIP(value)) {
			t.Fatalf("public address %s was rejected", value)
		}
	}

	var lookupCount atomic.Int32
	resolver := testAppShipResolverFunc(func(context.Context, string) ([]net.IPAddr, error) {
		if lookupCount.Add(1) == 1 {
			return []net.IPAddr{{IP: net.ParseIP("93.184.216.34")}}, nil
		}
		return []net.IPAddr{{IP: net.ParseIP("127.0.0.1")}}, nil
	})
	var dialCount atomic.Int32
	dial := appShipDialContext(resolver, func(_ context.Context, _, address string) (net.Conn, error) {
		dialCount.Add(1)
		if address != "93.184.216.34:443" {
			t.Errorf("DNS answer was not pinned into the dial address: %s", address)
		}
		return nil, errors.New("fixture dial stop")
	})
	if _, err := dial(context.Background(), "tcp", "appship.example.com:443"); !errors.Is(err, ErrUnavailable) || dialCount.Load() != 1 {
		t.Fatalf("first safe resolution: dial=%d err=%v", dialCount.Load(), err)
	}
	if _, err := dial(context.Background(), "tcp", "appship.example.com:443"); !errors.Is(err, ErrUnavailable) || dialCount.Load() != 1 {
		t.Fatalf("DNS rebind reached dial: dial=%d err=%v", dialCount.Load(), err)
	}

	mixed := appShipDialContext(testAppShipResolverFunc(func(context.Context, string) ([]net.IPAddr, error) {
		return []net.IPAddr{{IP: net.ParseIP("93.184.216.34")}, {IP: net.ParseIP("10.0.0.2")}}, nil
	}), func(context.Context, string, string) (net.Conn, error) {
		t.Error("mixed public/private DNS answer reached dial")
		return nil, nil
	})
	if _, err := mixed(context.Background(), "tcp", "appship.example.com:443"); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("mixed DNS answer error=%v", err)
	}
}

func TestAppShipNetworkPolicyAllowsOnlyExplicitExactPrivateAddresses(t *testing.T) {
	allowed, err := parseAllowedPrivateIPs("192.168.9.250, fd00::250")
	if err != nil {
		t.Fatal(err)
	}
	for _, value := range []string{"192.168.9.250", "fd00::250"} {
		if !permittedAppShipIP(net.ParseIP(value), allowed) {
			t.Fatalf("explicit private address %s was rejected", value)
		}
	}
	for _, value := range []string{"192.168.9.251", "fd00::251", "127.0.0.1", "169.254.1.1"} {
		if permittedAppShipIP(net.ParseIP(value), allowed) {
			t.Fatalf("unlisted or special-use address %s was accepted", value)
		}
	}

	var dialed atomic.Bool
	dial := appShipDialContext(testAppShipResolverFunc(func(context.Context, string) ([]net.IPAddr, error) {
		return []net.IPAddr{{IP: net.ParseIP("192.168.9.250")}}, nil
	}), func(context.Context, string, string) (net.Conn, error) {
		dialed.Store(true)
		return nil, errors.New("fixture dial stop")
	}, allowed...)
	if _, err := dial(context.Background(), "tcp", "appship.example.com:443"); !errors.Is(err, ErrUnavailable) || !dialed.Load() {
		t.Fatalf("explicit private address did not reach pinned dial: dialed=%v err=%v", dialed.Load(), err)
	}
}

func TestAppShipPrivateAddressAllowlistFailsClosed(t *testing.T) {
	for _, value := range []string{
		"192.168.9.0/24", "127.0.0.1", "169.254.1.1", "93.184.216.34", "not-an-ip", ",",
		"10.0.0.1,10.0.0.2,10.0.0.3,10.0.0.4,10.0.0.5,10.0.0.6,10.0.0.7,10.0.0.8,10.0.0.9",
	} {
		if _, err := NewAppShipCatalogWithAllowedPrivateIPs("https://appship.example.com", nil, value); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("allowlist %q error=%v, want ErrUnavailable", value, err)
		}
	}
	if _, err := NewAppShipCatalogWithAllowedPrivateIPs("https://appship.example.com", nil, "192.168.9.250"); err != nil {
		t.Fatalf("valid exact private allowlist: %v", err)
	}
}

func TestAppShipCatalogAcceptsNormalFixtureWithoutCredentials(t *testing.T) {
	var sawPinnedAddress atomic.Bool
	var requestCount atomic.Int32
	jar, err := cookiejar.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	cookieOrigin, _ := url.Parse("https://appship.example.com")
	jar.SetCookies(cookieOrigin, []*http.Cookie{{Name: "catalog_secret", Value: "must-not-be-sent", Secure: true}})
	proxy, _ := url.Parse("http://proxy_user:proxy_secret@proxy.internal:8080")
	template := &http.Client{
		Transport: &http.Transport{Proxy: http.ProxyURL(proxy)},
		Jar:       jar,
		Timeout:   20 * time.Second,
	}
	var origin string
	catalog, fixtureOrigin := newTLSAppShipCatalog(t, template, func(w http.ResponseWriter, r *http.Request) {
		requestCount.Add(1)
		if r.Method != http.MethodGet || r.URL.Path != "/api/v1/apps/"+ApplicationSlug+"/updates" {
			t.Errorf("unexpected request: %s %s", r.Method, r.URL.Path)
		}
		query := r.URL.Query()
		if query.Get("platform") != "windows" || query.Get("arch") != "x64" ||
			query.Get("channel") != "stable" || query.Get("build") != "37" {
			t.Errorf("unexpected query: %v", query)
		}
		if r.Header.Get("Authorization") != "" || r.Header.Get("Cookie") != "" || r.Header.Get("Proxy-Authorization") != "" {
			t.Errorf("catalog request carried credentials")
		}
		if r.TLS == nil {
			t.Error("request did not use TLS")
		} else if r.TLS.ServerName != "appship.example.com" {
			t.Errorf("TLS SNI=%q", r.TLS.ServerName)
		}
		_, _ = w.Write([]byte(strings.Replace(validOfferJSON, "https://appship.example", origin, 1)))
	}, &sawPinnedAddress)
	origin = fixtureOrigin

	offer, err := catalog.LatestWindowsStable(context.Background(), 37)
	if err != nil {
		t.Fatal(err)
	}
	if !offer.Available || offer.Build != 38 || !strings.HasPrefix(offer.DownloadURL, origin+"/") {
		t.Fatalf("offer=%+v", offer)
	}
	if requestCount.Load() != 1 || !sawPinnedAddress.Load() {
		t.Fatalf("request count=%d pinned=%v", requestCount.Load(), sawPinnedAddress.Load())
	}
}

func TestAppShipCatalogRejectsRedirectOversizeAndCrossOriginResponse(t *testing.T) {
	t.Run("redirect", func(t *testing.T) {
		var redirected atomic.Int32
		catalog, _ := newTLSAppShipCatalog(t, nil, func(w http.ResponseWriter, r *http.Request) {
			if r.URL.Path == "/redirected" {
				redirected.Add(1)
				_, _ = w.Write([]byte(validOfferJSON))
				return
			}
			http.Redirect(w, r, "/redirected?token=must-not-leak", http.StatusFound)
		}, nil)
		_, err := catalog.LatestWindowsStable(context.Background(), 37)
		if !errors.Is(err, ErrUnavailable) || redirected.Load() != 0 {
			t.Fatalf("redirect was followed: hits=%d err=%v", redirected.Load(), err)
		}
		if strings.Contains(err.Error(), "token") || strings.Contains(err.Error(), "redirected") {
			t.Fatalf("redirect response leaked through error: %v", err)
		}
	})

	t.Run("oversize", func(t *testing.T) {
		catalog, _ := newTLSAppShipCatalog(t, nil, func(w http.ResponseWriter, _ *http.Request) {
			w.Header().Set("Content-Length", strconv.Itoa(maximumResponseBytes+1))
			_, _ = w.Write([]byte(strings.Repeat("x", maximumResponseBytes+1)))
		}, nil)
		if _, err := catalog.LatestWindowsStable(context.Background(), 37); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("oversize response error=%v", err)
		}
	})

	t.Run("cross origin download", func(t *testing.T) {
		catalog, _ := newTLSAppShipCatalog(t, nil, func(w http.ResponseWriter, _ *http.Request) {
			_, _ = w.Write([]byte(strings.Replace(validOfferJSON, "https://appship.example", "https://downloads.example.com", 1)))
		}, nil)
		_, err := catalog.LatestWindowsStable(context.Background(), 37)
		if !errors.Is(err, ErrInvalidArtifact) {
			t.Fatalf("cross-origin download error=%v", err)
		}
		if strings.Contains(err.Error(), "downloads.example.com") {
			t.Fatalf("artifact URL leaked through error: %v", err)
		}
	})
}

func TestAppShipCatalogMasksResolverAndDialErrors(t *testing.T) {
	catalog, err := newAppShipCatalog("https://appship.example.com", nil, appShipNetworkOptions{
		resolver: testAppShipResolverFunc(func(context.Context, string) ([]net.IPAddr, error) {
			return nil, errors.New("https://user:super-secret@internal.example/private-response")
		}),
	})
	if err != nil {
		t.Fatal(err)
	}
	_, err = catalog.LatestWindowsStable(context.Background(), 37)
	if !errors.Is(err, ErrUnavailable) {
		t.Fatalf("resolver error=%v", err)
	}
	for _, forbidden := range []string{"super-secret", "internal.example", "private-response", "appship.example.com"} {
		if strings.Contains(err.Error(), forbidden) {
			t.Fatalf("error leaked %q: %v", forbidden, err)
		}
	}
}

type testAppShipResolverFunc func(context.Context, string) ([]net.IPAddr, error)

func (resolver testAppShipResolverFunc) LookupIPAddr(ctx context.Context, host string) ([]net.IPAddr, error) {
	return resolver(ctx, host)
}

func newTLSAppShipCatalog(
	t *testing.T,
	template *http.Client,
	handler http.HandlerFunc,
	sawPinnedAddress *atomic.Bool,
) (*AppShipCatalog, string) {
	t.Helper()
	server := httptest.NewTLSServer(handler)
	t.Cleanup(server.Close)
	_, port, err := net.SplitHostPort(server.Listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	origin := "https://appship.example.com:" + port
	roots := x509.NewCertPool()
	roots.AddCert(server.Certificate())
	resolver := testAppShipResolverFunc(func(_ context.Context, host string) ([]net.IPAddr, error) {
		if host != "appship.example.com" {
			return nil, errors.New("unexpected fixture host")
		}
		return []net.IPAddr{{IP: net.ParseIP("93.184.216.34")}}, nil
	})
	fixtureDialer := &net.Dialer{Timeout: time.Second}
	dial := func(ctx context.Context, network, pinnedAddress string) (net.Conn, error) {
		if pinnedAddress == "93.184.216.34:"+port && sawPinnedAddress != nil {
			sawPinnedAddress.Store(true)
		}
		if pinnedAddress != "93.184.216.34:"+port {
			return nil, errors.New("catalog did not pin the validated DNS answer")
		}
		return fixtureDialer.DialContext(ctx, network, server.Listener.Addr().String())
	}
	catalog, err := newAppShipCatalog(origin, template, appShipNetworkOptions{resolver: resolver, dial: dial, rootCAs: roots})
	if err != nil {
		t.Fatal(err)
	}
	return catalog, origin
}
