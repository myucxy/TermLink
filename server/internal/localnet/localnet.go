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
