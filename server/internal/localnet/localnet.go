// Package localnet centralizes the operator-gated plain-HTTP relaxation used
// by local development deployments. Production deployments never set
// TERMLINK_ALLOW_LOCAL_HTTP and keep the https-only contract everywhere.
package localnet

import (
	"net"
	"os"
	"strings"
)

// AllowHTTP reports whether the operator explicitly opted in to plain HTTP
// for loopback or private-network origins via TERMLINK_ALLOW_LOCAL_HTTP.
func AllowHTTP() bool {
	switch strings.ToLower(strings.TrimSpace(os.Getenv("TERMLINK_ALLOW_LOCAL_HTTP"))) {
	case "true", "1", "yes", "on":
		return true
	default:
		return false
	}
}

// IsLoopbackOrPrivateHost reports whether the hostname resolves to a loopback
// or RFC1918/ULA address, or is a localhost name.
func IsLoopbackOrPrivateHost(hostname string) bool {
	if h, _, err := net.SplitHostPort(strings.TrimSpace(hostname)); err == nil {
		hostname = h
	}
	host := strings.ToLower(strings.Trim(hostname, "[]"))
	if host == "localhost" || strings.HasSuffix(host, ".localhost") {
		return true
	}
	ip := net.ParseIP(host)
	if ip == nil {
		return false
	}
	return ip.IsLoopback() || ip.IsPrivate()
}

// InsecureCookieAllowed reports whether cookies for the given request Host may
// be stored without the Secure attribute under the local-development HTTP
// relaxation (loopback/private host + explicit operator opt-in). Browsers
// reject Secure cookies on plain HTTP, so the browser login flow cannot
// establish its session without this.
func InsecureCookieAllowed(host string) bool {
	if !AllowHTTP() {
		return false
	}
	return IsLoopbackOrPrivateHost(host)
}

// CookieSecure is the Secure attribute for a cookie set on the request Host.
func CookieSecure(host string) bool { return !InsecureCookieAllowed(host) }

// CookieName strips the __Host- prefix when the local-development HTTP
// relaxation applies: browsers reject __Host- cookies on non-HTTPS origins,
// which would silently break the browser login flow.
func CookieName(host, name string) string {
	if InsecureCookieAllowed(host) && strings.HasPrefix(name, "__Host-") {
		return strings.TrimPrefix(name, "__Host-")
	}
	return name
}
