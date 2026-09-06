package api

import (
	"crypto/sha256"
	"encoding/hex"
	"net/http"

	"github.com/termlink/termlink/server/internal/clientaddress"
)

func accountRegistrationRateLimitScope(r *http.Request) string {
	digest := sha256.Sum256([]byte(accountRegistrationClientIdentity(r)))
	return "account-register:" + hex.EncodeToString(digest[:])
}

func accountRegistrationClientIdentity(r *http.Request) string {
	return clientaddress.Identity(r)
}

func accountRegistrationClientIP(r *http.Request) string {
	return clientaddress.IP(r)
}
