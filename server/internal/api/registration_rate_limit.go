package api

import (
	"crypto/sha256"
	"encoding/hex"
	"net"
	"net/http"
	"net/netip"
	"strings"
)

func accountRegistrationRateLimitScope(r *http.Request) string {
	digest := sha256.Sum256([]byte(accountRegistrationClientIdentity(r)))
	return "account-register:" + hex.EncodeToString(digest[:])
}

func accountRegistrationClientIdentity(r *http.Request) string {
	peer, ok := remoteAddress(r.RemoteAddr)
	if !ok {
		return "unknown"
	}
	if !trustedRegistrationProxyPeer(peer) {
		return "ip:" + peer.String()
	}

	forwarded, ok := singleAddressHeader(r.Header.Values("X-Forwarded-For"))
	if !ok || !forwarded.IsGlobalUnicast() {
		return "ip:" + peer.String()
	}
	if publicRegistrationClientAddress(forwarded) {
		return "ip:" + forwarded.String()
	}

	// Caddy sets X-Forwarded-For itself. In the Lucky topology that value may be
	// Lucky's private address; if Lucky supplies X-Real-IP, Caddy passes it
	// through. Only use that second header for this exact private-proxy-hop
	// shape. This prevents a public client connected directly to the server
	// from choosing its own rate-limit identity with an arbitrary header.
	if forwarded.IsPrivate() || forwarded.IsLoopback() {
		if realIP, valid := singleAddressHeader(r.Header.Values("X-Real-IP")); valid && publicRegistrationClientAddress(realIP) {
			return "ip:" + realIP.String()
		}
		return "ip:" + forwarded.String()
	}
	return "ip:" + peer.String()
}

func accountRegistrationClientIP(r *http.Request) string {
	value := strings.TrimPrefix(accountRegistrationClientIdentity(r), "ip:")
	address, err := netip.ParseAddr(value)
	if err != nil {
		return ""
	}
	return address.Unmap().String()
}

func remoteAddress(value string) (netip.Addr, bool) {
	value = strings.TrimSpace(value)
	host, _, err := net.SplitHostPort(value)
	if err != nil {
		host = value
	}
	address, err := netip.ParseAddr(strings.TrimSpace(host))
	if err != nil {
		return netip.Addr{}, false
	}
	return address.Unmap(), true
}

func singleAddressHeader(values []string) (netip.Addr, bool) {
	if len(values) != 1 {
		return netip.Addr{}, false
	}
	value := strings.TrimSpace(values[0])
	if value == "" || strings.Contains(value, ",") {
		return netip.Addr{}, false
	}
	address, err := netip.ParseAddr(value)
	if err != nil {
		return netip.Addr{}, false
	}
	return address.Unmap(), true
}

func trustedRegistrationProxyPeer(address netip.Addr) bool {
	return address.IsLoopback() || address.IsPrivate()
}

func publicRegistrationClientAddress(address netip.Addr) bool {
	return address.IsGlobalUnicast() && !address.IsPrivate()
}
