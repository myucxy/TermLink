// Package e2eserver provides the standalone, in-memory TermLink E2E server.
// It intentionally implements only the device, catalog, lease, and opaque
// realtime-relay surface needed by local end-to-end tests.
package e2eserver

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

const (
	DefaultToken                   = "termlink-e2e-local-token"
	DefaultDevelopmentUsername     = "dev"
	DefaultDevelopmentPassword     = "dev-password"
	DefaultDevelopmentAccountLabel = "Local E2E"
	DefaultDevelopmentTokenTTL     = time.Hour
	maxJSONBody                    = 1 << 20
	proofWindow                    = 60 * time.Second
	nonceTTL                       = 2 * time.Minute
	defaultLeaseTTL                = 30 * time.Second
)

type Config struct {
	Token                   string
	Now                     func() time.Time
	LeaseTTL                time.Duration
	DevelopmentUsername     string
	DevelopmentPassword     string
	DevelopmentAccountLabel string
	DevelopmentTokenTTL     time.Duration
}

type Server struct {
	token                   string
	now                     func() time.Time
	leaseTTL                time.Duration
	developmentUsername     string
	developmentPassword     string
	developmentAccountLabel string
	developmentTokenTTL     time.Duration

	mu            sync.RWMutex
	devices       map[uuid.UUID]Device
	pcs           map[uuid.UUID]PC
	sessions      map[uuid.UUID]Session
	leases        map[uuid.UUID]Lease
	leaseVersions map[uuid.UUID]int64
	nonces        map[string]time.Time
	peers         map[uuid.UUID]map[*peer]struct{}
	subscriptions map[uuid.UUID]map[uuid.UUID]*peer
	issuedTokens  map[string]issuedToken
	// androidReconnectBlockedUntil is used only by the local E2E disconnect
	// hook. It deliberately leaves every catalog and lease map untouched.
	androidReconnectBlockedUntil time.Time
}

// issuedToken is deliberately kept in memory. The standalone E2E server has
// no durable identity store, and tokens are invalidated when the process exits.
type issuedToken struct {
	expiresAt time.Time
}

type Device struct {
	ID                uuid.UUID `json:"id"`
	Type              string    `json:"type"`
	Name              string    `json:"name"`
	KeyID             string    `json:"key_id"`
	SigningPublicKey  []byte    `json:"-"`
	ExchangePublicKey []byte    `json:"exchange_public_key"`
	TrustState        string    `json:"trust_state"`
	CreatedAt         time.Time `json:"created_at"`
}

type PC struct {
	ID                   uuid.UUID  `json:"id"`
	DeviceID             uuid.UUID  `json:"device_id"`
	Name                 string     `json:"name"`
	AgentVersion         string     `json:"agent_version"`
	KeyEpoch             int64      `json:"key_epoch"`
	RemoteRoutingEnabled bool       `json:"remote_routing_enabled"`
	LastSeenAt           *time.Time `json:"last_seen_at,omitempty"`
}

type Session struct {
	ID                 uuid.UUID  `json:"id"`
	PCID               uuid.UUID  `json:"pc_id"`
	ShellType          string     `json:"shell_type"`
	TitleCipher        []byte     `json:"title_cipher,omitempty"`
	State              string     `json:"state"`
	Columns            int        `json:"columns"`
	Rows               int        `json:"rows"`
	LastOutputSequence uint64     `json:"last_output_seq"`
	CreatedAt          time.Time  `json:"created_at"`
	EndedAt            *time.Time `json:"ended_at,omitempty"`
}

type Lease struct {
	SessionID      uuid.UUID `json:"session_id"`
	HolderDeviceID uuid.UUID `json:"holder_device_id"`
	ID             uuid.UUID `json:"lease_id"`
	Version        int64     `json:"version"`
	ExpiresAt      time.Time `json:"expires_at"`
}

func New(config Config) *Server {
	if config.Token == "" {
		config.Token = DefaultToken
	}
	if config.Now == nil {
		config.Now = time.Now
	}
	if config.LeaseTTL <= 0 {
		config.LeaseTTL = defaultLeaseTTL
	}
	if config.DevelopmentUsername == "" {
		config.DevelopmentUsername = DefaultDevelopmentUsername
	}
	if config.DevelopmentPassword == "" {
		config.DevelopmentPassword = DefaultDevelopmentPassword
	}
	if config.DevelopmentAccountLabel == "" {
		config.DevelopmentAccountLabel = DefaultDevelopmentAccountLabel
	}
	if config.DevelopmentTokenTTL <= 0 {
		config.DevelopmentTokenTTL = DefaultDevelopmentTokenTTL
	}
	return &Server{
		token: config.Token, now: config.Now, leaseTTL: config.LeaseTTL,
		developmentUsername:     config.DevelopmentUsername,
		developmentPassword:     config.DevelopmentPassword,
		developmentAccountLabel: config.DevelopmentAccountLabel,
		developmentTokenTTL:     config.DevelopmentTokenTTL,
		devices:                 map[uuid.UUID]Device{}, pcs: map[uuid.UUID]PC{},
		sessions: map[uuid.UUID]Session{}, leases: map[uuid.UUID]Lease{},
		leaseVersions: map[uuid.UUID]int64{}, nonces: map[string]time.Time{},
		peers: map[uuid.UUID]map[*peer]struct{}{}, subscriptions: map[uuid.UUID]map[uuid.UUID]*peer{},
		issuedTokens: map[string]issuedToken{},
	}
}

func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", s.health)
	mux.HandleFunc("GET /readyz", s.ready)
	mux.HandleFunc("GET /v1/client-config", s.clientConfig)
	mux.HandleFunc("POST /v1/auth/login", s.login)
	mux.Handle("POST /v1/devices/register", s.requireBearer(http.HandlerFunc(s.registerDevice)))
	mux.Handle("GET /v1/devices/self", s.authenticate(http.HandlerFunc(s.getCurrentDevice)))
	mux.Handle("GET /v1/devices", s.authenticate(http.HandlerFunc(s.listDevices)))
	mux.Handle("PUT /v1/pcs/{id}", s.authenticate(http.HandlerFunc(s.upsertPC)))
	mux.Handle("GET /v1/pcs", s.authenticate(http.HandlerFunc(s.listPCs)))
	mux.Handle("GET /v1/pcs/{id}/grants", s.authenticate(http.HandlerFunc(s.listImplicitPCGrants)))
	mux.Handle("PUT /v1/sessions/{id}", s.authenticate(http.HandlerFunc(s.upsertSession)))
	mux.Handle("GET /v1/pcs/{id}/sessions", s.authenticate(http.HandlerFunc(s.listSessions)))
	mux.Handle("POST /v1/sessions/{id}/lease", s.authenticate(http.HandlerFunc(s.acquireLease)))
	mux.Handle("GET /v1/sessions/{id}/lease", s.authenticate(http.HandlerFunc(s.getLease)))
	mux.Handle("POST /v1/sessions/{id}/lease/{leaseId}/renew", s.authenticate(http.HandlerFunc(s.renewLease)))
	mux.Handle("DELETE /v1/sessions/{id}/lease/{leaseId}", s.authenticate(http.HandlerFunc(s.releaseLease)))
	mux.Handle("DELETE /v1/sessions/{id}/lease", s.authenticate(http.HandlerFunc(s.forceReleaseLease)))
	mux.Handle("GET /v1/realtime", s.authenticate(http.HandlerFunc(s.serveRealtime)))
	mux.HandleFunc("POST /v1/e2e/realtime/disconnect-android", s.disconnectAndroidRealtime)
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		writeError(w, r, http.StatusNotFound, "NOT_FOUND")
	})
	return traceMiddleware(mux)
}

func (s *Server) Close(_ context.Context) error {
	s.mu.RLock()
	peers := make([]*peer, 0)
	for _, devicePeers := range s.peers {
		for p := range devicePeers {
			peers = append(peers, p)
		}
	}
	s.mu.RUnlock()
	for _, p := range peers {
		p.close()
	}
	// Do not retain issued credentials after an explicit server shutdown. The
	// process-local login store is intentionally non-persistent.
	s.mu.Lock()
	clear(s.issuedTokens)
	s.mu.Unlock()
	return nil
}

func (s *Server) health(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *Server) ready(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ready"})
}

func (s *Server) clientConfig(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{
		"identity_mode":        "development_shared_token",
		"auth_mode":            "development_login",
		"login_enabled":        true,
		"login_path":           "/v1/auth/login",
		"token_env":            "TERMLINK_E2E_TOKEN",
		"protocol_versions":    []int{1},
		"realtime_subprotocol": realtimeSubprotocol,
		"persistent":           false,
	})
}

func (s *Server) requireBearer(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !s.validBearer(r.Header.Get("Authorization")) {
			writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED")
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (s *Server) validBearer(header string) bool {
	const prefix = "Bearer "
	if !strings.HasPrefix(header, prefix) {
		return false
	}
	provided := strings.TrimSpace(strings.TrimPrefix(header, prefix))
	if provided == "" {
		return false
	}
	// Keep the original shared-token path for scripted/device bootstrap tests.
	sharedMatch := len(provided) == len(s.token) && subtle.ConstantTimeCompare([]byte(provided), []byte(s.token)) == 1
	// Login tokens are short-lived and process-local. Expired entries are
	// removed while validating so a long-running local server does not retain
	// credentials indefinitely. Cleanup also runs for shared-token requests so
	// an idle login token cannot accumulate behind the legacy automation path.
	now := s.now().UTC()
	s.mu.Lock()
	s.cleanupIssuedTokensLocked(now)
	issued, ok := s.issuedTokens[provided]
	s.mu.Unlock()
	return sharedMatch || (ok && now.Before(issued.expiresAt))
}

func (s *Server) cleanupIssuedTokensLocked(now time.Time) {
	for token, issued := range s.issuedTokens {
		if !now.Before(issued.expiresAt) {
			delete(s.issuedTokens, token)
		}
	}
}

// newBearerToken creates an opaque token without putting credentials or token
// material in logs. crypto/rand errors are surfaced to the caller rather than
// silently falling back to a predictable value.
func newBearerToken() (string, error) {
	value := make([]byte, 32)
	if _, err := rand.Read(value); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(value), nil
}

type principalContextKey struct{}

func principalID(r *http.Request) (uuid.UUID, bool) {
	id, ok := r.Context().Value(principalContextKey{}).(uuid.UUID)
	return id, ok
}

func (s *Server) currentDevice(r *http.Request) (Device, bool) {
	id, ok := principalID(r)
	if !ok {
		return Device{}, false
	}
	s.mu.RLock()
	device, ok := s.devices[id]
	s.mu.RUnlock()
	return device, ok
}

func (s *Server) consumeNonce(scope, nonce string, now time.Time) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	for key, expires := range s.nonces {
		if !now.Before(expires) {
			delete(s.nonces, key)
		}
	}
	key := scope + "\x00" + nonce
	if expires, exists := s.nonces[key]; exists && now.Before(expires) {
		return false
	}
	s.nonces[key] = now.Add(nonceTTL)
	return true
}

func traceMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		trace := r.Header.Get("X-Trace-ID")
		if _, err := uuid.Parse(trace); err != nil {
			trace = uuid.NewString()
		}
		w.Header().Set("X-Trace-ID", trace)
		w.Header().Set("X-Content-Type-Options", "nosniff")
		next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), traceContextKey{}, trace)))
	})
}

type traceContextKey struct{}

func traceID(r *http.Request) string {
	id, _ := r.Context().Value(traceContextKey{}).(string)
	return id
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func writeError(w http.ResponseWriter, r *http.Request, status int, code string) {
	writeJSON(w, status, map[string]any{"error": map[string]string{
		"code": code, "message_key": "errors." + strings.ToLower(code), "trace_id": traceID(r),
	}})
}

func decodeJSON(w http.ResponseWriter, r *http.Request, target any) error {
	reader := io.LimitReader(r.Body, maxJSONBody+1)
	decoder := json.NewDecoder(reader)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		return err
	}
	if err := decoder.Decode(&struct{}{}); !errors.Is(err, io.EOF) {
		if err == nil {
			return errors.New("multiple JSON values")
		}
		return err
	}
	return nil
}
