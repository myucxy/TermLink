// Package identityproxy exposes the public Keycloak routes through TermLink
// Server in explicit single-node deployments.
package identityproxy

import (
	"errors"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"strings"
	"time"
)

type Proxy struct {
	handler http.Handler
}

func New(publicIssuer, backchannelIssuer string) (*Proxy, error) {
	publicURL, err := url.Parse(strings.TrimRight(strings.TrimSpace(publicIssuer), "/"))
	if err != nil || publicURL == nil || publicURL.Scheme != "https" || publicURL.Host == "" {
		return nil, errors.New("public Keycloak issuer is invalid")
	}
	backchannelURL, err := url.Parse(strings.TrimRight(strings.TrimSpace(backchannelIssuer), "/"))
	if err != nil || backchannelURL == nil || backchannelURL.Host == "" ||
		(backchannelURL.Scheme != "http" && backchannelURL.Scheme != "https") {
		return nil, errors.New("Keycloak backchannel issuer is invalid")
	}
	publicPrefix, ok := keycloakBasePath(publicURL.Path)
	if !ok {
		return nil, errors.New("public issuer is not a Keycloak realm URL")
	}
	backchannelPrefix, ok := keycloakBasePath(backchannelURL.Path)
	if !ok {
		return nil, errors.New("backchannel issuer is not a Keycloak realm URL")
	}
	target := &url.URL{Scheme: backchannelURL.Scheme, Host: backchannelURL.Host, Path: backchannelPrefix}
	publicOrigin := &url.URL{Scheme: publicURL.Scheme, Host: publicURL.Host, Path: publicPrefix}
	transport := &http.Transport{
		Proxy:             http.ProxyFromEnvironment,
		DialContext:       (&net.Dialer{Timeout: 5 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		ForceAttemptHTTP2: true, MaxIdleConns: 64, MaxIdleConnsPerHost: 64,
		IdleConnTimeout: 90 * time.Second, TLSHandshakeTimeout: 5 * time.Second,
		ResponseHeaderTimeout: 30 * time.Second, ExpectContinueTimeout: time.Second,
	}
	proxy := &httputil.ReverseProxy{
		Transport: transport,
		Rewrite: func(request *httputil.ProxyRequest) {
			request.SetURL(target)
			request.Out.Host = target.Host
			request.SetXForwarded()
			request.Out.Header.Set("X-Forwarded-Proto", publicURL.Scheme)
			request.Out.Header.Set("X-Forwarded-Host", publicURL.Host)
			request.Out.Header.Set("X-Forwarded-Port", publicPort(publicURL))
			request.Out.Header.Del("Forwarded")
		},
		ModifyResponse: func(response *http.Response) error {
			location := response.Header.Get("Location")
			if location == "" {
				return nil
			}
			parsed, parseErr := url.Parse(location)
			if parseErr != nil || !strings.EqualFold(parsed.Scheme, backchannelURL.Scheme) ||
				!strings.EqualFold(parsed.Host, backchannelURL.Host) {
				return nil
			}
			if parsed.Path != backchannelPrefix && !strings.HasPrefix(parsed.Path, backchannelPrefix+"/") {
				return errors.New("Keycloak redirect escaped the configured backchannel path")
			}
			parsed.Scheme = publicOrigin.Scheme
			parsed.Host = publicOrigin.Host
			parsed.Path = publicPrefix + strings.TrimPrefix(parsed.Path, backchannelPrefix)
			parsed.RawPath = ""
			response.Header.Set("Location", parsed.String())
			return nil
		},
		ErrorHandler: func(w http.ResponseWriter, _ *http.Request, _ error) {
			http.Error(w, "Identity service temporarily unavailable / 身份服务暂时不可用", http.StatusBadGateway)
		},
	}
	return &Proxy{handler: proxy}, nil
}

func (p *Proxy) Handler() http.Handler { return p.handler }

func keycloakBasePath(issuerPath string) (string, bool) {
	marker := "/realms/"
	index := strings.LastIndex(issuerPath, marker)
	if index < 0 || strings.Trim(issuerPath[index+len(marker):], "/") == "" {
		return "", false
	}
	base := strings.TrimRight(issuerPath[:index], "/")
	if base == "/" {
		base = ""
	}
	return base, true
}

func publicPort(origin *url.URL) string {
	if port := origin.Port(); port != "" {
		return port
	}
	return "443"
}
