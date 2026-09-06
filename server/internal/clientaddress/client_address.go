// Package clientaddress derives the public client address only through the
// proxy topology TermLink controls. Untrusted forwarding headers are ignored.
package clientaddress

import (
	"net"
	"net/http"
	"net/netip"
	"strings"
)

// Identity returns a stable, non-secret address identity for rate limiting.
func Identity(r *http.Request) string {
	peer, ok := remoteAddress(r.RemoteAddr)
	if !ok {
		return "unknown"
	}
	if !trustedProxyPeer(peer) {
		return "ip:" + peer.String()
	}

	forwarded, ok := singleAddressHeader(r.Header.Values("X-Forwarded-For"))
	if !ok || !forwarded.IsGlobalUnicast() {
		return "ip:" + peer.String()
	}
	if publicClientAddress(forwarded) {
		return "ip:" + forwarded.String()
	}

	// Caddy owns X-Forwarded-For. In the Lucky topology it can contain Lucky's
	// private address, while X-Real-IP carries the public client address.
	if forwarded.IsPrivate() || forwarded.IsLoopback() {
		if realIP, valid := singleAddressHeader(r.Header.Values("X-Real-IP")); valid && publicClientAddress(realIP) {
			return "ip:" + realIP.String()
		}
		return "ip:" + forwarded.String()
	}
	return "ip:" + peer.String()
}

// IP returns the canonical address suitable for a bounded upstream risk
// signal. An empty result means the request address could not be authenticated.
func IP(r *http.Request) string {
	value := strings.TrimPrefix(Identity(r), "ip:")
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

func trustedProxyPeer(address netip.Addr) bool {
	return address.IsLoopback() || address.IsPrivate()
}

func publicClientAddress(address netip.Addr) bool {
	return address.IsGlobalUnicast() && !address.IsPrivate()
}
