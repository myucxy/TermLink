package api

import "net/http"

// The stage is stable, additive metadata. It never includes a callback URL,
// user identifier, HTTP body or provider error. Older clients keep using code.
func connectionErrorStage(r *http.Request) string {
	if r == nil || r.URL == nil {
		return ""
	}
	switch r.URL.Path {
	case "/v1/client-config":
		return "configuration"
	case "/v1/devices/register":
		return "device_registration"
	case "/v1/devices/self", "/v1/devices/self/trust":
		return "device_identity"
	case "/v1/realtime":
		return "relay_handshake"
	case "/v1/file-transfers":
		return "file_transfer_handshake"
	case "/v1/devices/self/push":
		return "push_registration"
	}
	if isCurrentPCUpsertRequest(r) {
		return "pc_metadata"
	}
	return ""
}
