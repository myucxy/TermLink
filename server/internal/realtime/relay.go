// Package realtime implements the production opaque-ciphertext WebSocket
// Relay. It authorizes routing metadata but never decrypts terminal payloads.
package realtime

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"slices"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/presence"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	subprotocol     = "termlink.realtime.v1"
	maxHelloBytes   = 16 << 10
	helloTimeout    = 5 * time.Second
	pongTimeout     = 45 * time.Second
	pingInterval    = 15 * time.Second
	peerQueueFrames = 128
	// Frames remain bounded separately so a peer cannot consume roughly
	// peerQueueFrames * MaxFrameBytes while its socket is stalled.
	peerQueueBytes = 8 << 20
	// A replay can legitimately enqueue a short burst faster than the socket
	// writer can drain it. Give the writer a bounded opportunity to catch up
	// before classifying the peer as a slow consumer. This also propagates
	// backpressure to the sender's read loop without allowing an unbounded
	// Relay queue.
	peerEnqueueTimeout      = 500 * time.Millisecond
	maxLivenessControlBytes = 512
	// subscriptionAuthorityTTL bounds how long an active Android subscription
	// can reuse its last PostgreSQL authorization. Authority events invalidate
	// immediately; this short fence covers session deletion and missed events
	// without putting a database query on every input, resize, or ACK frame.
	subscriptionAuthorityTTL = 5 * time.Second
	// A transient PostgreSQL or coordination-store outage must not turn an
	// otherwise healthy WebSocket into a silent data break. Keep only the last
	// positively verified route for this bounded grace period. Authoritative
	// denials and expiry still invalidate immediately, and no failed refresh
	// extends the deadline.
	subscriptionAuthorityFailureGrace = 45 * time.Second
)

var requiredCapabilities = []string{"cumulative_ack", "e2ee_x25519_chacha20poly1305", "flow_control_v1", "lease_v1"}

var optionalCapabilities = []string{
	"bounded_event_replay_v1",
	"terminal_snapshot_v1",
	"terminal_snapshot_v2",
	"terminal_presentation_metadata_v1",
	"terminal_typed_input_v1",
	"liveness_probe_v1",
	"host_history_v1",
	"host_session_catalog_v1",
	"host_session_cleanup_v1",
}

const (
	directTransportDataV1Capability  = "direct_transport_data_v1"
	directSessionCutoverV1Capability = "direct_session_cutover_v1"
)

// HubOptions controls optional capabilities advertised by this Hub instance.
// Required Relay capabilities remain fixed so an optional transport can never
// make the authoritative WSS path unavailable to an older client.
type HubOptions struct {
	DirectTransportDataV1 bool
	// DirectTransportDataV1Override, when set, is consulted instead of the
	// static value so the portal-owned routing policy can toggle the data
	// plane at runtime without a server restart.
	DirectTransportDataV1Override func() bool
}

type Hub struct {
	store                         routeAuthorityStore
	policyStore                   controlPolicyStore
	leases                        liveSessionStore
	logger                        *slog.Logger
	observer                      Observer
	directTransportDataV1         bool
	directTransportDataV1Override func() bool
	presence                      *presence.Registry
	mu                            sync.RWMutex
	peers                         map[uuid.UUID]*peer
	subscriptions                 map[uuid.UUID]map[uuid.UUID]*peer
	closed                        bool
	// Fixed shards serialize each identity-scoped Valkey reservation with its
	// connection owner without making unrelated devices wait for the same
	// network round trip. ReleaseLiveSession has no generation parameter, so a
	// shard remains locked across reserve/release to close the replacement ABA.
	reservationShards [liveReservationShardCount]liveReservationShard
	// beforeExpiredReservationCleanup is a deterministic concurrency seam for
	// proving that PC replay locks are released before reservation cleanup.
	// Production hubs leave it nil.
	beforeExpiredReservationCleanup func()
}

const liveReservationShardCount = 64

type liveReservationShard struct {
	mu     sync.Mutex
	owners map[liveReservationKey]*peer
}

type liveReservationKey struct {
	userID    uuid.UUID
	sessionID uuid.UUID
	deviceID  uuid.UUID
}

type routeAuthorityStore interface {
	SessionRoute(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) (store.SessionRouteAuthorization, error)
	TouchPCPresence(context.Context, uuid.UUID, uuid.UUID) error
	TouchDevicePresence(context.Context, uuid.UUID, uuid.UUID) error
}

// batchRouteAuthorityStore is optional so focused tests and alternate stores
// can retain the single-route contract. PostgreSQL implements this interface.
type batchRouteAuthorityStore interface {
	SessionRoutes(context.Context, uuid.UUID, uuid.UUID, []uuid.UUID) (map[uuid.UUID]store.SessionRouteAuthorization, error)
}

type controlPolicyStore interface {
	SessionControlPolicy(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) (store.SessionControlPolicy, error)
}

type liveSessionStore interface {
	ReserveLiveSession(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, ...int) (bool, error)
	ReleaseLiveSession(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) error
	GetLease(context.Context, uuid.UUID) (kv.Lease, error)
}

// batchLiveSessionStore is optional. The production Valkey implementation
// uses one bounded script for a subscription refresh instead of one network
// round trip per session; reduced test and alternate stores keep the original
// single-reservation contract.
type batchLiveSessionStore interface {
	ReserveLiveSessions(context.Context, uuid.UUID, []uuid.UUID, uuid.UUID) error
}

func NewHub(repository *store.Postgres, leases liveSessionStore, logger *slog.Logger) *Hub {
	return NewHubWithObserver(repository, leases, logger, nil)
}

// Observer receives low-cardinality routing measurements. Implementations
// must never attach raw device/session identifiers or encrypted payloads.
type Observer interface {
	ConnectionOpened(role string)
	ConnectionClosed(role string)
	SubscriptionsChanged(delta int)
	EnvelopeRouted(direction, messageType, result string, size int, duration time.Duration)
	SlowConsumerDisconnected()
}

type noopObserver struct{}

func (noopObserver) ConnectionOpened(string)                                   {}
func (noopObserver) ConnectionClosed(string)                                   {}
func (noopObserver) SubscriptionsChanged(int)                                  {}
func (noopObserver) EnvelopeRouted(string, string, string, int, time.Duration) {}
func (noopObserver) SlowConsumerDisconnected()                                 {}

func NewHubWithObserver(repository *store.Postgres, leases liveSessionStore, logger *slog.Logger, observer Observer) *Hub {
	return NewHubWithObserverAndOptions(repository, leases, logger, observer, HubOptions{})
}

// NewHubWithObserverAndOptions keeps optional capability negotiation local to
// one Hub. In particular, enabling production Direct data on one instance does
// not mutate package state or affect concurrent Relay instances.
func NewHubWithObserverAndOptions(repository *store.Postgres, leases liveSessionStore, logger *slog.Logger, observer Observer, options HubOptions) *Hub {
	if logger == nil {
		logger = slog.Default()
	}
	if observer == nil {
		observer = noopObserver{}
	}
	var authorityStore routeAuthorityStore
	var policyStore controlPolicyStore
	if repository != nil {
		authorityStore = repository
		policyStore = repository
	}
	return &Hub{
		store: authorityStore, policyStore: policyStore, leases: leases, logger: logger, observer: observer,
		directTransportDataV1:         options.DirectTransportDataV1,
		directTransportDataV1Override: options.DirectTransportDataV1Override,
		peers:                         map[uuid.UUID]*peer{}, subscriptions: map[uuid.UUID]map[uuid.UUID]*peer{},
	}
}

func (h *Hub) Handler() http.Handler { return http.HandlerFunc(h.serveHTTP) }

// directTransportDataV1Allowed resolves the runtime routing policy: the
// override (portal-owned) wins over the static construction-time value.
func (h *Hub) directTransportDataV1Allowed() bool {
	if h == nil {
		return false
	}
	if h.directTransportDataV1Override != nil {
		return h.directTransportDataV1Override()
	}
	return h.directTransportDataV1
}

// SetPresence attaches the process-local online registry. It is intentionally
// optional so focused tests and legacy embedders keep the database fallback.
func (h *Hub) SetPresence(registry *presence.Registry) {
	if h == nil {
		return
	}
	h.mu.Lock()
	h.presence = registry
	h.mu.Unlock()
}

type clientHello struct {
	Role             string   `json:"role"`
	ProtocolVersions []uint32 `json:"protocol_versions"`
	Capabilities     []string `json:"capabilities"`
}
type serverWelcome struct {
	Type             string   `json:"type"`
	ProtocolVersion  uint32   `json:"protocol_version"`
	HeartbeatSeconds int      `json:"heartbeat_seconds"`
	ConnectionID     string   `json:"connection_id"`
	Capabilities     []string `json:"capabilities"`
}

type livenessControl struct {
	Type    string `json:"type"`
	ProbeID string `json:"probe_id"`
}

type peer struct {
	hub                     *Hub
	socket                  *websocket.Conn
	principal               auth.Principal
	role                    string
	send                    chan []byte
	controlSend             chan []byte
	done                    chan struct{}
	once                    sync.Once
	lastSequences           map[uuid.UUID]uint64
	pcReplayMu              sync.Mutex
	pcReplayRoutes          map[pcRouteKey]*pcReplayRoute
	subscriptionReplayReady bool
	subscriptions           map[uuid.UUID]bool
	queuedBytes             atomic.Int64
	// subscriptionRoutes is protected by hub.mu together with subscriptions.
	// It binds an authorized Android subscription to the owning Windows device
	// and the exact Grant expiry used by the zero-database-query output path.
	subscriptionRoutes map[uuid.UUID]cachedSessionRoute
	// Retain the byte-identical signed SUBSCRIBE and its cumulative ACK only
	// while this Android peer remains subscribed. A replacement PC connection
	// can then restore replay state without forging an Android frame.
	subscriptionFrames   map[uuid.UUID][]byte
	subscriptionACKs     map[uuid.UUID]uint64
	subscriptionControls map[uuid.UUID]retainedSubscriberControl
	controlPolicies      map[uuid.UUID]cachedControlPolicy
	capabilities         map[string]bool
	presenceID           uuid.UUID
}

type retainedSubscriberControl struct {
	ack   uint64
	frame []byte
}

type cachedSessionRoute struct {
	authorization store.SessionRouteAuthorization
	validUntil    time.Time
	failureUntil  time.Time
}

type cachedControlPolicy struct {
	policy       store.SessionControlPolicy
	validUntil   time.Time
	failureUntil time.Time
}

type pcRouteKey struct {
	sessionID   uuid.UUID
	recipientID uuid.UUID
}

type pcReplayRoute struct {
	highWater uint64
	digests   map[uint64][32]byte
	order     []uint64
	epoch     *pcReplayEpoch
}

type pcReplayEpoch struct {
	next   uint64
	target uint64
}

// routeDenialError carries only a fixed, low-cardinality diagnostic class.
// Realtime authorization errors can otherwise contain database, cache, or
// request details that must never be copied into production logs. Wrapping the
// original error preserves errors.Is checks while allowing operators to tell
// an expired lease from a replaced connection or an unavailable PC.
type routeDenialError struct {
	class              string
	cause              error
	reservationRelease *peer
}

func (e *routeDenialError) Error() string { return e.class }
func (e *routeDenialError) Unwrap() error { return e.cause }

func denyRoute(class string, cause error) error {
	if cause == nil {
		cause = store.ErrDenied
	} else if !errors.Is(cause, store.ErrDenied) {
		cause = errors.Join(store.ErrDenied, cause)
	}
	return &routeDenialError{class: class, cause: cause}
}

func denyRouteAfterReservationRelease(class string, cause error, owner *peer) error {
	err := denyRoute(class, cause).(*routeDenialError)
	err.reservationRelease = owner
	return err
}

func routeDenialClass(err error) string {
	var denial *routeDenialError
	if errors.As(err, &denial) && denial.class != "" {
		return denial.class
	}
	if errors.Is(err, context.DeadlineExceeded) || errors.Is(err, context.Canceled) {
		return "authority_timeout"
	}
	return "authority_denied"
}

// A stale control lease or a temporarily unavailable PC is a per-frame
// routing result, not a protocol violation by the authenticated Android peer.
// The relay cannot create an E2EE InputResult for that frame, so the client
// resolves it as delivery-unknown while keeping the device socket healthy.
func routeDenialIsFatal(err error) bool {
	var denial *routeDenialError
	if !errors.As(err, &denial) {
		return true
	}
	switch denial.class {
	case "control_lease_inactive", "control_lease_holder_mismatch", "pc_connection_unavailable":
		return false
	default:
		return true
	}
}

func (h *Hub) serveHTTP(w http.ResponseWriter, r *http.Request) {
	principal, ok := auth.PrincipalFromContext(r.Context())
	if !ok {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if !slices.Contains(websocket.Subprotocols(r), subprotocol) {
		http.Error(w, "realtime subprotocol required", http.StatusUpgradeRequired)
		return
	}
	h.mu.RLock()
	closed := h.closed
	h.mu.RUnlock()
	if closed {
		http.Error(w, "relay shutting down", http.StatusServiceUnavailable)
		return
	}
	upgrader := websocket.Upgrader{Subprotocols: []string{subprotocol}, ReadBufferSize: 16 << 10, WriteBufferSize: 16 << 10, CheckOrigin: func(r *http.Request) bool { return r.Header.Get("Origin") == "" }}
	socket, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	socket.SetReadLimit(realtimewire.MaxFrameBytes)
	if err := socket.SetReadDeadline(time.Now().Add(helloTimeout)); err != nil {
		_ = socket.Close()
		return
	}
	messageType, data, err := socket.ReadMessage()
	if err != nil || messageType != websocket.TextMessage || len(data) > maxHelloBytes {
		closeSocket(socket, websocket.CloseProtocolError, "valid ClientHello required")
		return
	}
	var hello clientHello
	if err := json.Unmarshal(data, &hello); err != nil || !validHello(hello) {
		closeSocket(socket, websocket.ClosePolicyViolation, "unsupported role or capabilities")
		return
	}
	if hello.Role == "pc" && !store.IsPCAgentDeviceType(principal.Device.Type) || hello.Role == "android" && principal.Device.Type != "ANDROID" {
		closeSocket(socket, websocket.ClosePolicyViolation, "role does not match device")
		return
	}
	negotiated := h.negotiatedCapabilities(hello.Capabilities)
	connection := &peer{hub: h, socket: socket, principal: principal, role: hello.Role, presenceID: uuid.New(), send: make(chan []byte, peerQueueFrames), controlSend: make(chan []byte, 4), done: make(chan struct{}), subscriptions: map[uuid.UUID]bool{}, subscriptionRoutes: map[uuid.UUID]cachedSessionRoute{}, subscriptionFrames: map[uuid.UUID][]byte{}, subscriptionACKs: map[uuid.UUID]uint64{}, subscriptionControls: map[uuid.UUID]retainedSubscriberControl{}, controlPolicies: map[uuid.UUID]cachedControlPolicy{}, lastSequences: map[uuid.UUID]uint64{}, pcReplayRoutes: map[pcRouteKey]*pcReplayRoute{}, capabilities: capabilitySet(negotiated)}
	if !h.add(connection) {
		closeSocket(socket, websocket.CloseTryAgainLater, "relay shutting down")
		return
	}
	h.observer.ConnectionOpened(connection.role)
	defer func() {
		h.remove(connection)
		h.mu.RLock()
		registry := h.presence
		h.mu.RUnlock()
		if registry != nil {
			registry.Remove(connection.presenceID)
		}
		h.observer.ConnectionClosed(connection.role)
	}()
	h.mu.RLock()
	registry := h.presence
	h.mu.RUnlock()
	if registry != nil {
		registry.Register(connection.principal.UserID, connection.principal.Device.ID, connection.presenceID, connection.role)
	}
	welcome := serverWelcome{
		Type: "welcome", ProtocolVersion: 1,
		HeartbeatSeconds: int(pingInterval.Seconds()), ConnectionID: uuid.NewString(),
		Capabilities: negotiated,
	}
	if err := socket.WriteJSON(welcome); err != nil {
		return
	}
	connection.refreshPresence()
	_ = socket.SetReadDeadline(time.Now().Add(pongTimeout))
	socket.SetPongHandler(func(string) error {
		if err := socket.SetReadDeadline(time.Now().Add(pongTimeout)); err != nil {
			return err
		}
		// Presence represents a live round trip from the peer, not merely the
		// Relay's local ping timer continuing to run.
		connection.refreshPresence()
		return nil
	})
	go connection.writeLoop()
	if connection.role == "pc" {
		// Android subscriptions survive a transient PC disconnect. Re-deliver
		// their signed SUBSCRIBE frames as soon as the replacement PC writer is
		// running instead of waiting for the Android periodic refresh.
		h.replaySubscriptionsToPC(connection)
	}
	connection.readLoop()
}

func validHello(hello clientHello) bool {
	if hello.Role != "pc" && hello.Role != "android" {
		return false
	}
	if !slices.Contains(hello.ProtocolVersions, uint32(1)) {
		return false
	}
	for _, required := range requiredCapabilities {
		if !slices.Contains(hello.Capabilities, required) {
			return false
		}
	}
	return true
}

func negotiatedCapabilities(offered []string) []string {
	result := make([]string, 0, len(requiredCapabilities)+len(optionalCapabilities))
	for _, capability := range requiredCapabilities {
		if slices.Contains(offered, capability) {
			result = append(result, capability)
		}
	}
	for _, capability := range optionalCapabilities {
		if slices.Contains(offered, capability) {
			result = append(result, capability)
		}
	}
	return result
}

func (h *Hub) negotiatedCapabilities(offered []string) []string {
	result := negotiatedCapabilities(offered)
	if h != nil && h.directTransportDataV1Allowed() &&
		slices.Contains(offered, directTransportDataV1Capability) &&
		slices.Contains(offered, directSessionCutoverV1Capability) {
		result = append(
			result,
			directTransportDataV1Capability,
			directSessionCutoverV1Capability,
		)
	}
	return result
}

func capabilitySet(capabilities []string) map[string]bool {
	result := make(map[string]bool, len(capabilities))
	for _, capability := range capabilities {
		result[capability] = true
	}
	return result
}

func (h *Hub) add(p *peer) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.closed {
		return false
	}
	if old := h.peers[p.principal.Device.ID]; old != nil {
		old.close(websocket.CloseNormalClosure, "connection replaced")
	}
	h.peers[p.principal.Device.ID] = p
	return true
}
func (h *Hub) remove(p *peer) {
	p.close(websocket.CloseNormalClosure, "connection closed")
	h.mu.Lock()
	if h.peers[p.principal.Device.ID] == p {
		delete(h.peers, p.principal.Device.ID)
	}
	removedSessions := make([]uuid.UUID, 0, len(p.subscriptions))
	for sessionID := range p.subscriptions {
		if subscribers := h.subscriptions[sessionID]; subscribers != nil {
			if subscribers[p.principal.Device.ID] != p {
				continue
			}
			delete(subscribers, p.principal.Device.ID)
			removedSessions = append(removedSessions, sessionID)
			if len(subscribers) == 0 {
				delete(h.subscriptions, sessionID)
			}
		}
		delete(p.subscriptionRoutes, sessionID)
		delete(p.subscriptionFrames, sessionID)
		delete(p.subscriptionACKs, sessionID)
		delete(p.subscriptionControls, sessionID)
		delete(p.controlPolicies, sessionID)
	}
	h.mu.Unlock()
	if len(removedSessions) > 0 {
		h.observer.SubscriptionsChanged(-len(removedSessions))
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	for _, sessionID := range removedSessions {
		h.releaseLiveSession(ctx, p, sessionID)
	}
}

func (p *peer) readLoop() {
	defer p.close(websocket.CloseNormalClosure, "reader stopped")
	for {
		messageType, frame, err := p.socket.ReadMessage()
		if err != nil {
			return
		}
		if messageType == websocket.TextMessage {
			if !p.handleLivenessControl(frame) {
				p.rejectApplicationFrame("unsupported_realtime_control", len(frame))
			}
			continue
		}
		if messageType != websocket.BinaryMessage {
			p.rejectApplicationFrame("binary_envelope_required", len(frame))
			continue
		}
		envelope, err := realtimewire.Unmarshal(frame)
		if err != nil {
			p.rejectApplicationFrame("invalid_production_envelope", len(frame))
			continue
		}
		if !bytes.Equal(envelope.SenderDeviceID, p.principal.Device.ID[:]) {
			p.rejectApplicationFrame("sender_rejected", len(frame))
			continue
		}
		if p.role == "pc" && envelope.Direction != realtimewire.PCToAndroid || p.role == "android" && envelope.Direction != realtimewire.AndroidToPC {
			p.rejectApplicationFrame("direction_rejected", len(frame))
			continue
		}
		if !allowedMessage(p.role, envelope.MessageType) || !validEnvelopeACK(envelope) {
			p.rejectApplicationFrame("message_or_ack_rejected", len(frame))
			continue
		}
		if (envelope.MessageType == realtimewire.HostHistoryRequest ||
			envelope.MessageType == realtimewire.HostHistoryResponse ||
			envelope.MessageType == realtimewire.HostHistoryControl ||
			envelope.MessageType == realtimewire.SessionTerminateResponse) &&
			!p.capabilities["host_history_v1"] {
			p.rejectApplicationFrame("host_history_capability_required", len(frame))
			continue
		}
		sessionID, err := uuid.FromBytes(envelope.SessionID)
		if err != nil {
			p.rejectApplicationFrame("session_rejected", len(frame))
			continue
		}
		var pcRecipient uuid.UUID
		if p.role == "pc" {
			pcRecipient, err = uuid.FromBytes(envelope.RecipientDeviceID)
			if err != nil {
				p.rejectApplicationFrame("recipient_rejected", len(frame))
				continue
			}
		} else if !p.acceptSequence(sessionID, envelope.Sequence) {
			p.rejectApplicationFrame("sequence_rejected", len(frame))
			continue
		}
		started := time.Now()
		var routeErr error
		if isSessionCatalogMessage(envelope.MessageType) {
			routeErr = p.routeSessionCatalog(envelope, sessionID, frame)
		} else if p.role == "pc" {
			routeErr = p.routePCFrame(envelope, sessionID, pcRecipient, frame)
		} else {
			routeErr = p.route(envelope, sessionID, frame)
		}
		result := "accepted"
		if routeErr != nil {
			result = "denied"
		}
		p.hub.observer.EnvelopeRouted(metricDirection(p.role), strconv.FormatUint(uint64(envelope.MessageType), 10), result, len(frame), time.Since(started))
		if routeErr != nil {
			p.hub.logger.Warn("realtime route denied", "device", shortID(p.principal.Device.ID), "session", shortID(sessionID), "type", uint32(envelope.MessageType), "reason", routeDenialClass(routeErr))
		}
	}
}

// rejectApplicationFrame deliberately keeps the authenticated physical WSS
// generation alive. A malformed or unauthorized Envelope is an application
// fault; the sender's sequence/ACK rules and the receiver's History Gap recovery
// fence its session impact. Socket read/write/close and liveness failures remain
// the only transport events handled by the connection lifecycle.
func (p *peer) rejectApplicationFrame(reason string, wireBytes int) {
	p.hub.observer.EnvelopeRouted(metricDirection(p.role), "0", "rejected", wireBytes, 0)
	p.hub.logger.Debug("realtime application frame rejected",
		"device", shortID(p.principal.Device.ID), "reason", reason, "wire_bytes", wireBytes)
}

func (p *peer) handleLivenessControl(frame []byte) bool {
	if !p.capabilities["liveness_probe_v1"] || len(frame) == 0 || len(frame) > maxLivenessControlBytes {
		return false
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(frame, &fields); err != nil || len(fields) != 2 ||
		fields["type"] == nil || fields["probe_id"] == nil {
		return false
	}
	var control livenessControl
	if err := json.Unmarshal(frame, &control); err != nil || control.Type != "ping" {
		return false
	}
	probeID, err := uuid.Parse(control.ProbeID)
	if err != nil || probeID.String() != control.ProbeID {
		return false
	}
	encoded, err := json.Marshal(livenessControl{Type: "pong", ProbeID: control.ProbeID})
	return err == nil && p.enqueueControl(encoded)
}

func allowedMessage(role string, messageType realtimewire.MessageType) bool {
	if role == "android" {
		switch messageType {
		case realtimewire.Heartbeat, realtimewire.Goodbye,
			realtimewire.Subscribe, realtimewire.Unsubscribe,
			realtimewire.OutputACK, realtimewire.FlowControl,
			realtimewire.InputEvent, realtimewire.ResizeRequest,
			realtimewire.ResumeRequest, realtimewire.LeaseRequest,
			realtimewire.SessionTerminateRequest,
			realtimewire.HostHistoryRequest, realtimewire.HostHistoryControl, realtimewire.SessionCatalogRequest, realtimewire.SessionCleanupRequest:
			return true
		default:
			return false
		}
	}
	if role == "pc" {
		switch messageType {
		case realtimewire.Heartbeat, realtimewire.Goodbye,
			realtimewire.SessionUpsert, realtimewire.SessionRemoved,
			realtimewire.OutputChunk, realtimewire.Gap,
			realtimewire.InputAccepted, realtimewire.InputRejected,
			realtimewire.ResizeApplied, realtimewire.SnapshotOffer,
			realtimewire.SnapshotChunk, realtimewire.LeaseGranted,
			realtimewire.LeaseDenied, realtimewire.LeaseRevoked,
			realtimewire.ThemeChanged, realtimewire.SessionStarted,
			realtimewire.SessionInterrupted, realtimewire.SessionEnded,
			realtimewire.HostHistoryResponse, realtimewire.SessionTerminateResponse, realtimewire.SessionCatalogResponse, realtimewire.SessionCleanupResponse:
			return true
		default:
			return false
		}
	}
	return false
}

func validEnvelopeACK(envelope realtimewire.Envelope) bool {
	switch envelope.MessageType {
	case realtimewire.Subscribe, realtimewire.OutputACK, realtimewire.FlowControl:
		return true
	default:
		return envelope.ACK == 0
	}
}

// Android-to-PC envelopes can execute input and therefore remain strictly
// monotonic for the lifetime of a connection. PC-to-Android envelopes carry
// replayable terminal events: after a cumulative-ACK resume the Agent may
// retransmit an older event over the same Relay connection. Android applies
// those events idempotently by terminal sequence, so rejecting the replay here
// would disconnect the PC and strand later InputAccepted results.
func (p *peer) acceptSequence(sessionID uuid.UUID, sequence uint64) bool {
	last := p.lastSequences[sessionID]
	if p.role == "android" && sequence <= last {
		return false
	}
	if sequence > last {
		if last == 0 && len(p.lastSequences) >= maxTrackedSessionsPerPeer {
			// Authenticated-peer memory guard: one connection may not register
			// an unbounded number of session sequence cursors. The client is
			// expected to reconnect; its real sessions re-register on demand.
			p.close(websocket.ClosePolicyViolation, "session tracking budget exceeded")
			return false
		}
		p.lastSequences[sessionID] = sequence
	}
	return true
}

const maxPCReplayFrames = 4096

// Per-connection bounds for the replay/sequence tracking maps. Both maps grow
// one entry per distinct session (or session/recipient pair) a peer mentions,
// so an authenticated peer could otherwise inflate relay memory without limit.
// Exceeding a budget closes the connection; real clients re-register their
// active sessions on the next connection.
const (
	maxTrackedSessionsPerPeer = 1024
	maxPCReplayRoutesPerPeer  = 1024
)

var errPCSequenceRejected = errors.New("pc sequence rejected")

var errCumulativeACKRegressed = errors.New("cumulative acknowledgement regressed")

type pcSequenceCommit struct {
	sequence uint64
	digest   [32]byte
	advance  bool
	gap      bool
}

// PC output normally reuses a sequence only as the exact serialized frame
// previously accepted for this route. A visible cumulative-ACK subscription
// opens a bounded replay epoch from ACK+1 through the route's prior high-water
// mark. This permits an ordered replay even when those old digests have aged
// out, while the first accepted frame in the epoch re-establishes the digest
// fence for subsequent duplicates.
func (p *peer) acceptPCSequence(sessionID, recipientID uuid.UUID, sequence uint64, frame []byte) bool {
	p.pcReplayMu.Lock()
	defer p.pcReplayMu.Unlock()
	state := p.pcReplayRoute(pcRouteKey{sessionID: sessionID, recipientID: recipientID})
	if state == nil {
		return false
	}
	commit, ok := state.validate(sequence, realtimewire.OutputChunk, sha256.Sum256(frame))
	if ok {
		state.commit(commit)
	}
	return ok
}

// routePCFrame keeps sequence validation, transient subscriber delivery and
// replay-state commit in one route-local critical section. An unavailable or
// replaced Android subscriber is an offline delivery window, so the PC route
// still advances its high-water mark. Android's next durable cumulative ACK
// opens the replay epoch that asks the Agent for the missed ordered frames.
func (p *peer) routePCFrame(envelope realtimewire.Envelope, sessionID, recipientID uuid.UUID, frame []byte) error {
	if envelope.MessageType == realtimewire.HostHistoryResponse ||
		envelope.MessageType == realtimewire.SessionTerminateResponse {
		// Host-history chunks and termination results are correlated by an E2EE
		// request UUID. They do not consume the ordered terminal event sequence
		// or replay window.
		return p.route(envelope, sessionID, frame)
	}
	p.pcReplayMu.Lock()
	state := p.pcReplayRoute(pcRouteKey{sessionID: sessionID, recipientID: recipientID})
	if state == nil {
		p.pcReplayMu.Unlock()
		return errPCSequenceRejected
	}
	commit, ok := state.validate(envelope.Sequence, envelope.MessageType, sha256.Sum256(frame))
	if !ok {
		p.pcReplayMu.Unlock()
		return errPCSequenceRejected
	}
	if err := p.route(envelope, sessionID, frame); err != nil {
		p.pcReplayMu.Unlock()
		var denial *routeDenialError
		if errors.As(err, &denial) && denial.reservationRelease != nil {
			ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
			p.hub.releaseLiveSession(ctx, denial.reservationRelease, sessionID)
			cancel()
		}
		return err
	}
	state.commit(commit)
	p.pcReplayMu.Unlock()
	return nil
}

func (state *pcReplayRoute) validate(sequence uint64, messageType realtimewire.MessageType, digest [32]byte) (pcSequenceCommit, bool) {
	commit := pcSequenceCommit{sequence: sequence, digest: digest, gap: messageType == realtimewire.Gap}
	if sequence == 0 {
		return pcSequenceCommit{}, false
	}
	if state.epoch != nil && state.epoch.next <= state.epoch.target {
		switch {
		case sequence < state.epoch.next:
			previous, ok := state.digests[sequence]
			return commit, ok && bytes.Equal(previous[:], digest[:])
		case sequence > state.epoch.next && !commit.gap:
			return pcSequenceCommit{}, false
		default:
			if previous, ok := state.digests[sequence]; ok && !bytes.Equal(previous[:], digest[:]) {
				return pcSequenceCommit{}, false
			}
			commit.advance = true
			return commit, true
		}
	}
	if sequence <= state.highWater {
		previous, ok := state.digests[sequence]
		return commit, ok && bytes.Equal(previous[:], digest[:])
	}
	if state.highWater == ^uint64(0) {
		return pcSequenceCommit{}, false
	}
	if sequence != state.highWater+1 && !commit.gap {
		return pcSequenceCommit{}, false
	}
	commit.advance = true
	return commit, true
}

func (state *pcReplayRoute) commit(commit pcSequenceCommit) {
	if !commit.advance {
		return
	}
	state.remember(commit.sequence, commit.digest)
	if commit.sequence > state.highWater {
		state.highWater = commit.sequence
	}
	if state.epoch == nil {
		return
	}
	if commit.gap {
		state.epoch = nil
		return
	}
	state.epoch.next = commit.sequence + 1
	if state.epoch.next > state.epoch.target {
		state.epoch = nil
	}
}

func (p *peer) pcReplayRoute(key pcRouteKey) *pcReplayRoute {
	if p.pcReplayRoutes == nil {
		p.pcReplayRoutes = map[pcRouteKey]*pcReplayRoute{}
	}
	state := p.pcReplayRoutes[key]
	if state == nil {
		if len(p.pcReplayRoutes) >= maxPCReplayRoutesPerPeer {
			// Authenticated-peer memory guard, mirrored from the session
			// sequence budget: unbounded (session, recipient) routes would let
			// one connection inflate relay memory without limit.
			p.close(websocket.ClosePolicyViolation, "replay tracking budget exceeded")
			return nil
		}
		state = &pcReplayRoute{digests: map[uint64][32]byte{}}
		p.pcReplayRoutes[key] = state
	}
	return state
}

func (state *pcReplayRoute) remember(sequence uint64, digest [32]byte) {
	if _, exists := state.digests[sequence]; exists {
		state.digests[sequence] = digest
		return
	}
	state.digests[sequence] = digest
	state.order = append(state.order, sequence)
	for len(state.order) > maxPCReplayFrames {
		oldest := state.order[0]
		state.order = state.order[1:]
		delete(state.digests, oldest)
	}
}

func (p *peer) beginPCReplayEpoch(sessionID, recipientID uuid.UUID, ack uint64) {
	p.pcReplayMu.Lock()
	defer p.pcReplayMu.Unlock()
	p.beginPCReplayEpochLocked(sessionID, recipientID, ack)
}

func (p *peer) beginPCReplayEpochLocked(sessionID, recipientID uuid.UUID, ack uint64) {
	state := p.pcReplayRoute(pcRouteKey{sessionID: sessionID, recipientID: recipientID})
	if state == nil {
		return
	}
	if ack == ^uint64(0) {
		state.epoch = nil
		return
	}
	if ack >= state.highWater {
		state.highWater = ack
		state.epoch = nil
		return
	}
	state.epoch = &pcReplayEpoch{next: ack + 1, target: state.highWater}
}

// advancePCVisibleACKLocked applies a live cumulative Android ACK to the
// current PC generation without opening a replay epoch. A live ACK may lag
// output that is already in flight, so only a cursor beyond the Relay's known
// high-water advances the fence. Replay epochs are created exclusively by a
// SUBSCRIBE or replacement-PC restoration boundary.
func (p *peer) advancePCVisibleACKLocked(sessionID, recipientID uuid.UUID, ack uint64) {
	state := p.pcReplayRoute(pcRouteKey{sessionID: sessionID, recipientID: recipientID})
	if state == nil {
		return
	}
	if ack > state.highWater {
		state.highWater = ack
	}
}

func (p *peer) route(envelope realtimewire.Envelope, sessionID uuid.UUID, frame []byte) error {
	if !p.hub.isCurrent(p) {
		return denyRoute("sender_connection_replaced", store.ErrDenied)
	}
	if len(envelope.RecipientDeviceID) != 16 {
		return denyRoute("recipient_invalid", store.ErrDenied)
	}
	recipient, err := uuid.FromBytes(envelope.RecipientDeviceID)
	if err != nil || recipient == p.principal.Device.ID {
		return denyRoute("recipient_invalid", store.ErrDenied)
	}
	if p.role == "pc" {
		// An Android client can disconnect or unsubscribe between the Agent's
		// subscriber snapshot and this frame. Report that as a failed route so the
		// PC reconnects and replays from the recipient's durable ACK; never advance
		// sequence state for a silently dropped frame. The cached subscription
		// still avoids a PostgreSQL query on the terminal-output hot path.
		return p.hub.routeToSubscriber(p, sessionID, recipient, frame)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if envelope.MessageType == realtimewire.Unsubscribe {
		cached, subscribed := p.hub.subscriptionRoute(p, sessionID)
		if p.hub.unsubscribe(p, sessionID) {
			p.hub.releaseLiveSession(ctx, p, sessionID)
		}
		if subscribed && recipient == cached.authorization.PCDeviceID {
			p.hub.sendToDevice(p, cached.authorization.PCDeviceID, frame)
		}
		return nil
	}
	var route store.SessionRouteAuthorization
	if envelope.MessageType == realtimewire.Subscribe {
		if p.hub.subscriptionACKRegresses(p, sessionID, envelope.ACK) {
			return denyRoute("cumulative_ack_regressed", errCumulativeACKRegressed)
		}
		route, err = p.hub.store.SessionRoute(ctx, p.principal.UserID, p.principal.Device.ID, sessionID)
		if err != nil {
			return denyRoute("subscription_authority_lookup", err)
		}
		if !routeAuthorizationLive(route, time.Now()) || !allowedRoutePermission(envelope.MessageType, route.Permission) || recipient != route.PCDeviceID {
			return denyRoute("subscription_authority_denied", store.ErrDenied)
		}
		if err := p.hub.reserveLiveSession(ctx, p, sessionID); err != nil {
			return denyRoute("subscription_reservation_failed", err)
		}
	} else {
		route, err = p.authorizedSubscriptionRoute(ctx, sessionID)
		if err != nil {
			return denyRoute("subscription_inactive", err)
		}
	}
	if !allowedRoutePermission(envelope.MessageType, route.Permission) {
		return denyRoute("route_permission_denied", store.ErrDenied)
	}
	if envelope.MessageType == realtimewire.InputEvent || envelope.MessageType == realtimewire.ResizeRequest {
		if err := p.authorizeRemoteControl(ctx, sessionID, route); err != nil {
			return err
		}
	}
	if recipient != route.PCDeviceID {
		return denyRoute("recipient_mismatch", store.ErrDenied)
	}
	if envelope.MessageType == realtimewire.Subscribe {
		if err := p.hub.activateSubscription(p, sessionID, route, envelope.ACK, frame); err != nil {
			p.hub.releaseLiveSession(ctx, p, sessionID)
			return err
		}
		return nil
	}
	if envelope.MessageType == realtimewire.InputEvent || envelope.MessageType == realtimewire.ResizeRequest {
		if !p.hub.sendControlToDevice(p, route.PCDeviceID, frame) {
			return denyRoute("pc_connection_unavailable", store.ErrDenied)
		}
		return nil
	}
	if envelope.MessageType == realtimewire.OutputACK || envelope.MessageType == realtimewire.FlowControl {
		if err := p.hub.routeSubscriberControlFrame(p, route.PCDeviceID, sessionID, envelope.ACK, frame); err != nil {
			if errors.Is(err, errCumulativeACKRegressed) {
				return denyRoute("cumulative_ack_regressed", err)
			}
			return denyRoute("subscription_inactive", err)
		}
		return nil
	}
	p.hub.sendToDevice(p, route.PCDeviceID, frame)
	return nil
}

// authorizeRemoteControl enforces only routing metadata. InputEventPayload
// remains opaque E2EE ciphertext; input_id/input_seq replay protection and the
// final serial terminal write stay at the Windows Agent, which owns the key.
func (p *peer) authorizeRemoteControl(ctx context.Context, sessionID uuid.UUID, route store.SessionRouteAuthorization) error {
	policy, err := p.controlPolicy(ctx, sessionID, route)
	if err != nil {
		return denyRoute("control_policy_denied", err)
	}
	lease, err := p.hub.leases.GetLease(ctx, sessionID)
	if err != nil || lease.ID == uuid.Nil || lease.Version < 1 || !time.Now().Before(lease.ExpiresAt) {
		return denyRoute("control_lease_inactive", err)
	}
	switch policy.Mode {
	case store.ControlModeSingle:
		if lease.HolderDeviceID != p.principal.Device.ID {
			return denyRoute("control_lease_holder_mismatch", store.ErrDenied)
		}
	case store.ControlModeTakeover:
		// V1 has no takeover request identity, expected fence, or holder approval
		// decision. Treat the current holder exactly like SINGLE and reject every
		// implicit join; never silently revoke and replace the holder.
		if lease.HolderDeviceID != p.principal.Device.ID {
			return denyRoute("control_lease_holder_mismatch", store.ErrDenied)
		}
	case store.ControlModeCollaborative:
		// The exact live subscription route already binds the authorized device,
		// account, session and PC. All participants use the same fenced lease.
	default:
		return denyRoute("control_policy_invalid", store.ErrDenied)
	}
	return nil
}

func (p *peer) controlPolicy(ctx context.Context, sessionID uuid.UUID, route store.SessionRouteAuthorization) (store.SessionControlPolicy, error) {
	now := time.Now()
	p.hub.mu.RLock()
	cached, ok := p.controlPolicies[sessionID]
	p.hub.mu.RUnlock()
	if ok && cached.policy.Valid() && cached.policy.PCDeviceID == route.PCDeviceID && now.Before(cached.validUntil) &&
		(cached.policy.GrantExpiresAt == nil || now.Before(*cached.policy.GrantExpiresAt)) {
		return cached.policy, nil
	}
	if p.hub.policyStore == nil {
		// The production Postgres store always supplies policy metadata. A
		// reduced alternate store gets the safe default and cannot collaborate.
		return store.SessionControlPolicy{
			Mode: store.ControlModeSingle, LocalPriority: true, PolicyVersion: 1,
			PCID: route.PCDeviceID, PCDeviceID: route.PCDeviceID, GrantExpiresAt: route.GrantExpiresAt,
		}, nil
	}
	policy, err := p.hub.policyStore.SessionControlPolicy(ctx, p.principal.UserID, p.principal.Device.ID, sessionID)
	if err != nil || !policy.Valid() || policy.PCDeviceID != route.PCDeviceID {
		if err != nil && !errors.Is(err, store.ErrDenied) && !errors.Is(err, store.ErrNotFound) &&
			ok && cached.policy.Valid() && cached.policy.PCDeviceID == route.PCDeviceID &&
			now.Before(cached.failureUntil) &&
			(cached.policy.GrantExpiresAt == nil || now.Before(*cached.policy.GrantExpiresAt)) {
			return cached.policy, nil
		}
		return store.SessionControlPolicy{}, store.ErrDenied
	}
	validUntil := now.Add(subscriptionAuthorityTTL)
	if policy.GrantExpiresAt != nil && policy.GrantExpiresAt.Before(validUntil) {
		validUntil = *policy.GrantExpiresAt
	}
	failureUntil := now.Add(subscriptionAuthorityFailureGrace)
	if policy.GrantExpiresAt != nil && policy.GrantExpiresAt.Before(failureUntil) {
		failureUntil = *policy.GrantExpiresAt
	}
	p.hub.mu.Lock()
	if p.hub.subscriptions[sessionID][p.principal.Device.ID] != p {
		p.hub.mu.Unlock()
		return store.SessionControlPolicy{}, store.ErrDenied
	}
	p.controlPolicies[sessionID] = cachedControlPolicy{
		policy: policy, validUntil: validUntil, failureUntil: failureUntil,
	}
	p.hub.mu.Unlock()
	return policy, nil
}

// authorizedSubscriptionRoute is the Android-to-PC hot-path authority fence.
// Only the exact live peer recorded as this session's subscriber may reuse a
// cached route. PostgreSQL is consulted again at the earlier of the Grant
// expiry and subscriptionAuthorityTTL; any authority denial removes the
// subscription before the frame can reach a PC.
func (p *peer) authorizedSubscriptionRoute(ctx context.Context, sessionID uuid.UUID) (store.SessionRouteAuthorization, error) {
	cached, subscribed := p.hub.subscriptionRoute(p, sessionID)
	if !subscribed {
		return store.SessionRouteAuthorization{}, store.ErrDenied
	}
	now := time.Now()
	if routeAuthorizationLive(cached.authorization, now) && now.Before(cached.validUntil) {
		return cached.authorization, nil
	}
	route, err := p.hub.store.SessionRoute(ctx, p.principal.UserID, p.principal.Device.ID, sessionID)
	if err != nil {
		if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) {
			p.invalidateSubscription(ctx, sessionID)
		} else if p.retainSubscriptionDuringTransientFailure(sessionID, now) {
			return cached.authorization, nil
		} else {
			p.invalidateSubscription(ctx, sessionID)
		}
		return store.SessionRouteAuthorization{}, err
	}
	if !routeAuthorizationLive(route, now) || !p.hub.updateSubscriptionRoute(p, sessionID, route) {
		p.invalidateSubscription(ctx, sessionID)
		return store.SessionRouteAuthorization{}, store.ErrDenied
	}
	return route, nil
}

func (p *peer) invalidateSubscription(ctx context.Context, sessionID uuid.UUID) {
	if p.hub.unsubscribe(p, sessionID) {
		p.hub.releaseLiveSession(ctx, p, sessionID)
	}
}

func reservationKey(p *peer, sessionID uuid.UUID) liveReservationKey {
	return liveReservationKey{
		userID:    p.principal.UserID,
		sessionID: sessionID,
		deviceID:  p.principal.Device.ID,
	}
}

func liveReservationShardIndex(key liveReservationKey) int {
	hash := byte(0)
	for index := range key.userID {
		hash ^= key.userID[index] ^ key.sessionID[index] ^ key.deviceID[index]
	}
	return int(hash) % liveReservationShardCount
}

func (h *Hub) reservationShard(key liveReservationKey) *liveReservationShard {
	return &h.reservationShards[liveReservationShardIndex(key)]
}

// reserveLiveSession and releaseLiveSession deliberately hold the fixed
// identity shard across the Valkey operation. The remote member is keyed only
// by account/session/device, so a local owner check followed by an unlocked
// release would still have an ABA window in which a replacement refreshes the
// same member before the old connection deletes it.
func (h *Hub) reserveLiveSession(ctx context.Context, p *peer, sessionID uuid.UUID) error {
	key := reservationKey(p, sessionID)
	shard := h.reservationShard(key)
	shard.mu.Lock()
	defer shard.mu.Unlock()
	// Reject a stale generation before it can refresh the replacement's
	// identity-scoped backend member. The post-check remains necessary because
	// Hub replacement can occur while the network operation is in flight.
	if !h.isCurrent(p) || p.role != "android" || !p.open() {
		return store.ErrDenied
	}
	if h.leases == nil {
		h.setReservationOwnerLocked(shard, key, p)
		return nil
	}
	if _, err := h.leases.ReserveLiveSession(ctx, p.principal.UserID, sessionID, p.principal.Device.ID); err != nil {
		return err
	}
	if !h.isCurrent(p) || p.role != "android" || !p.open() {
		_ = h.leases.ReleaseLiveSession(ctx, p.principal.UserID, sessionID, p.principal.Device.ID)
		// The shard is still held, so no replacement can have installed an
		// owner. Delete unconditionally: in an O -> R -> Q race the map may still
		// name O because R becomes owner only after its successful post-check.
		delete(shard.owners, key)
		return store.ErrDenied
	}
	h.setReservationOwnerLocked(shard, key, p)
	return nil
}

func (h *Hub) reserveLiveSessions(ctx context.Context, p *peer, sessionIDs []uuid.UUID, batch batchLiveSessionStore) error {
	var used [liveReservationShardCount]bool
	for _, sessionID := range sessionIDs {
		used[liveReservationShardIndex(reservationKey(p, sessionID))] = true
	}
	for index := range used {
		if used[index] {
			h.reservationShards[index].mu.Lock()
		}
	}
	defer func() {
		for index := len(used) - 1; index >= 0; index-- {
			if used[index] {
				h.reservationShards[index].mu.Unlock()
			}
		}
	}()
	// All identity shards are held before this current-generation check, so a
	// stale batch cannot touch any replacement-owned backend member. Retain the
	// post-I/O check for replacement during the batch network round trip.
	if !h.isCurrent(p) || p.role != "android" || !p.open() {
		return store.ErrDenied
	}
	if err := batch.ReserveLiveSessions(ctx, p.principal.UserID, sessionIDs, p.principal.Device.ID); err != nil {
		return err
	}
	if !h.isCurrent(p) || p.role != "android" || !p.open() {
		for _, sessionID := range sessionIDs {
			_ = h.leases.ReleaseLiveSession(ctx, p.principal.UserID, sessionID, p.principal.Device.ID)
			key := reservationKey(p, sessionID)
			shard := h.reservationShard(key)
			delete(shard.owners, key)
		}
		return store.ErrDenied
	}
	for _, sessionID := range sessionIDs {
		key := reservationKey(p, sessionID)
		h.setReservationOwnerLocked(h.reservationShard(key), key, p)
	}
	return nil
}

func (h *Hub) setReservationOwnerLocked(shard *liveReservationShard, key liveReservationKey, p *peer) {
	if shard.owners == nil {
		shard.owners = map[liveReservationKey]*peer{}
	}
	shard.owners[key] = p
}

func (h *Hub) releaseLiveSession(ctx context.Context, p *peer, sessionID uuid.UUID) {
	key := reservationKey(p, sessionID)
	shard := h.reservationShard(key)
	shard.mu.Lock()
	defer shard.mu.Unlock()
	if shard.owners[key] != p {
		return
	}
	if h.leases != nil {
		_ = h.leases.ReleaseLiveSession(ctx, p.principal.UserID, sessionID, p.principal.Device.ID)
	}
	delete(shard.owners, key)
}

func routeAuthorizationLive(route store.SessionRouteAuthorization, now time.Time) bool {
	return route.GrantExpiresAt == nil || now.Before(*route.GrantExpiresAt)
}

func cacheSessionRoute(route store.SessionRouteAuthorization, now time.Time) cachedSessionRoute {
	validUntil := now.Add(subscriptionAuthorityTTL)
	if route.GrantExpiresAt != nil && route.GrantExpiresAt.Before(validUntil) {
		validUntil = *route.GrantExpiresAt
	}
	failureUntil := now.Add(subscriptionAuthorityFailureGrace)
	if route.GrantExpiresAt != nil && route.GrantExpiresAt.Before(failureUntil) {
		failureUntil = *route.GrantExpiresAt
	}
	return cachedSessionRoute{
		authorization: route, validUntil: validUntil, failureUntil: failureUntil,
	}
}

func allowedRoutePermission(messageType realtimewire.MessageType, permission string) bool {
	switch messageType {
	case realtimewire.SessionTerminateRequest:
		return permission == "MANAGE"
	case realtimewire.InputEvent, realtimewire.ResizeRequest, realtimewire.LeaseRequest:
		return permission == "CONTROL" || permission == "MANAGE"
	default:
		return true
	}
}

func (h *Hub) subscribe(p *peer, sessionID uuid.UUID, route store.SessionRouteAuthorization) {
	key := reservationKey(p, sessionID)
	shard := h.reservationShard(key)
	shard.mu.Lock()
	h.setReservationOwnerLocked(shard, key, p)
	h.mu.Lock()
	added := h.subscribeLocked(p, sessionID, route)
	h.mu.Unlock()
	shard.mu.Unlock()
	if added {
		h.observer.SubscriptionsChanged(1)
	}
}

func (h *Hub) subscribeLocked(p *peer, sessionID uuid.UUID, route store.SessionRouteAuthorization) bool {
	if p.subscriptionRoutes == nil {
		p.subscriptionRoutes = map[uuid.UUID]cachedSessionRoute{}
	}
	if p.controlPolicies == nil {
		p.controlPolicies = map[uuid.UUID]cachedControlPolicy{}
	}
	subscribers := h.subscriptions[sessionID]
	if subscribers == nil {
		subscribers = map[uuid.UUID]*peer{}
		h.subscriptions[sessionID] = subscribers
	}
	_, existed := subscribers[p.principal.Device.ID]
	subscribers[p.principal.Device.ID] = p
	p.subscriptions[sessionID] = true
	p.subscriptionRoutes[sessionID] = cacheSessionRoute(route, time.Now())
	return !existed
}
func (h *Hub) unsubscribe(p *peer, sessionID uuid.UUID) bool {
	h.mu.Lock()
	removed := false
	if subscribers := h.subscriptions[sessionID]; subscribers != nil {
		if subscribers[p.principal.Device.ID] == p {
			delete(p.subscriptions, sessionID)
			delete(p.subscriptionRoutes, sessionID)
			delete(p.subscriptionFrames, sessionID)
			delete(p.subscriptionACKs, sessionID)
			delete(p.subscriptionControls, sessionID)
			delete(p.controlPolicies, sessionID)
			delete(subscribers, p.principal.Device.ID)
			removed = true
			if len(subscribers) == 0 {
				delete(h.subscriptions, sessionID)
			}
		}
	}
	h.mu.Unlock()
	if removed {
		h.observer.SubscriptionsChanged(-1)
	}
	return removed
}

func (h *Hub) isCurrent(p *peer) bool {
	h.mu.RLock()
	current := h.peers[p.principal.Device.ID] == p
	h.mu.RUnlock()
	if !current {
		return false
	}
	select {
	case <-p.done:
		return false
	default:
		return true
	}
}

func (h *Hub) subscriptionRoute(p *peer, sessionID uuid.UUID) (cachedSessionRoute, bool) {
	h.mu.RLock()
	defer h.mu.RUnlock()
	if h.subscriptions[sessionID][p.principal.Device.ID] != p {
		return cachedSessionRoute{}, false
	}
	route, ok := p.subscriptionRoutes[sessionID]
	return route, ok
}

func (h *Hub) updateSubscriptionRoute(p *peer, sessionID uuid.UUID, route store.SessionRouteAuthorization) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.subscriptions[sessionID][p.principal.Device.ID] != p {
		return false
	}
	previous := p.subscriptionRoutes[sessionID]
	p.subscriptionRoutes[sessionID] = cacheSessionRoute(route, time.Now())
	if previous.authorization.PCDeviceID != uuid.Nil && previous.authorization.PCDeviceID != route.PCDeviceID {
		delete(p.controlPolicies, sessionID)
	}
	return true
}

// rememberSubscriptionFrame stores only the already-authorized, byte-identical
// Android SUBSCRIBE frame. The cache is owned by the live Android peer and is
// removed with its subscription, so it cannot outlive an unsubscribe or peer
// replacement.
func (h *Hub) rememberSubscriptionFrame(p *peer, sessionID uuid.UUID, ack uint64, frame []byte) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.subscriptions[sessionID][p.principal.Device.ID] != p {
		return false
	}
	if _, ok := p.subscriptionRoutes[sessionID]; !ok {
		return false
	}
	if p.subscriptionFrames == nil {
		p.subscriptionFrames = map[uuid.UUID][]byte{}
	}
	if p.subscriptionACKs == nil {
		p.subscriptionACKs = map[uuid.UUID]uint64{}
	}
	if latest, ok := latestVisibleSubscriptionACKLocked(p, sessionID); ok && ack < latest {
		return false
	}
	p.subscriptionFrames[sessionID] = append([]byte(nil), frame...)
	p.subscriptionACKs[sessionID] = ack
	delete(p.subscriptionControls, sessionID)
	return true
}

func (h *Hub) subscriptionACKRegresses(p *peer, sessionID uuid.UUID, ack uint64) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	if h.subscriptions[sessionID][p.principal.Device.ID] != p {
		return false
	}
	latest, ok := latestVisibleSubscriptionACKLocked(p, sessionID)
	return ok && ack < latest
}

// rememberSubscriberControlFrame retains only the newest signed ACK-bearing
// frame that followed the current SUBSCRIBE. The Relay compares the visible
// cumulative cursor but never decrypts or rewrites the control payload. Equal
// ACKs replace older controls so the PC replacement observes the most recent
// flow-control epoch/window as byte-identical Android ciphertext.
func rememberSubscriberControlFrameLocked(h *Hub, p *peer, sessionID uuid.UUID, ack uint64, frame []byte) error {
	if h.subscriptions[sessionID][p.principal.Device.ID] != p {
		return store.ErrDenied
	}
	if _, ok := p.subscriptionRoutes[sessionID]; !ok {
		return store.ErrDenied
	}
	if latest, ok := latestVisibleSubscriptionACKLocked(p, sessionID); ok && ack < latest {
		return errCumulativeACKRegressed
	}
	if p.subscriptionControls == nil {
		p.subscriptionControls = map[uuid.UUID]retainedSubscriberControl{}
	}
	p.subscriptionControls[sessionID] = retainedSubscriberControl{
		ack:   ack,
		frame: append([]byte(nil), frame...),
	}
	return nil
}

// routeSubscriberControlFrame serializes a live ACK/control against the PC's
// replacement-subscription replay. A newly installed PC is not sent control
// until its retained SUBSCRIBE has been queued; if replay is still in progress,
// the latest byte-identical frame remains cached for that replay to pick up.
func (h *Hub) routeSubscriberControlFrame(sender *peer, deviceID, sessionID uuid.UUID, ack uint64, frame []byte) error {
	for {
		h.mu.RLock()
		target := h.peers[deviceID]
		h.mu.RUnlock()
		if target == nil || target.role != "pc" || target.principal.UserID != sender.principal.UserID || !target.open() {
			h.mu.Lock()
			current := h.peers[deviceID]
			if current != nil && current.role == "pc" && current.principal.UserID == sender.principal.UserID && current.open() {
				h.mu.Unlock()
				continue
			}
			err := rememberSubscriberControlFrameLocked(h, sender, sessionID, ack, frame)
			h.mu.Unlock()
			return err
		}

		target.pcReplayMu.Lock()
		h.mu.Lock()
		if err := rememberSubscriberControlFrameLocked(h, sender, sessionID, ack, frame); err != nil {
			h.mu.Unlock()
			target.pcReplayMu.Unlock()
			return err
		}
		current := h.peers[deviceID] == target && target.open() && target.role == "pc" &&
			target.principal.UserID == sender.principal.UserID
		ready := current && target.subscriptionReplayReady
		if current {
			target.advancePCVisibleACKLocked(sessionID, sender.principal.Device.ID, ack)
		}
		h.mu.Unlock()
		if !current {
			target.pcReplayMu.Unlock()
			continue
		}
		if ready {
			_ = target.enqueue(frame)
		}
		target.pcReplayMu.Unlock()
		return nil
	}
}

func latestVisibleSubscriptionACKLocked(p *peer, sessionID uuid.UUID) (uint64, bool) {
	ack, present := p.subscriptionACKs[sessionID]
	if control, ok := p.subscriptionControls[sessionID]; ok && (!present || control.ack > ack) {
		return control.ack, true
	}
	return ack, present
}

// activateSubscription publishes the Android target, its complete retained
// SUBSCRIBE and the PC's ACK fence as one transition. Whenever a current PC is
// present, the global lock is acquired only after that PC's replay lock; this
// is the same pcReplayMu -> hub.mu order used by PC frame routing. If the PC is
// absent, the complete subscription is cached under hub.mu so add/replay can
// install the identical fence before the replacement PC starts its read loop.
func (h *Hub) activateSubscription(sender *peer, sessionID uuid.UUID, route store.SessionRouteAuthorization, ack uint64, frame []byte) error {
	key := reservationKey(sender, sessionID)
	shard := h.reservationShard(key)
	shard.mu.Lock()
	defer shard.mu.Unlock()
	if shard.owners[key] != sender {
		// Reduced test/alternate hubs without a live-session backend still use
		// the exact same local generation fence.
		if h.leases != nil {
			return denyRoute("subscription_reservation_replaced", store.ErrDenied)
		}
		h.setReservationOwnerLocked(shard, key, sender)
	}
	for {
		if !h.isCurrent(sender) || sender.role != "android" || !routeAuthorizationLive(route, time.Now()) {
			return denyRoute("sender_connection_replaced", store.ErrDenied)
		}

		h.mu.RLock()
		target := h.peers[route.PCDeviceID]
		h.mu.RUnlock()
		if target != nil && target.role == "pc" && target.principal.UserID == sender.principal.UserID && target.open() {
			target.pcReplayMu.Lock()
			h.mu.Lock()
			if h.peers[route.PCDeviceID] != target || !target.open() {
				h.mu.Unlock()
				target.pcReplayMu.Unlock()
				continue
			}
			if h.peers[sender.principal.Device.ID] != sender || !sender.open() ||
				sender.role != "android" || target.role != "pc" ||
				target.principal.UserID != sender.principal.UserID || !routeAuthorizationLive(route, time.Now()) {
				h.mu.Unlock()
				target.pcReplayMu.Unlock()
				return denyRoute("sender_connection_replaced", store.ErrDenied)
			}
			if latest, ok := latestVisibleSubscriptionACKLocked(sender, sessionID); ok && ack < latest {
				h.mu.Unlock()
				target.pcReplayMu.Unlock()
				return denyRoute("cumulative_ack_regressed", errCumulativeACKRegressed)
			}
			target.beginPCReplayEpochLocked(sessionID, sender.principal.Device.ID, ack)
			added := h.activateSubscriptionLocked(sender, sessionID, route, ack, frame)
			h.mu.Unlock()
			// Keep pcReplayMu held until the byte-identical SUBSCRIBE is queued.
			// PC output cannot observe the published target without its ACK fence.
			_ = target.enqueue(frame)
			target.pcReplayMu.Unlock()
			if added {
				h.observer.SubscriptionsChanged(1)
			}
			return nil
		}

		// No usable PC is present. Recheck under the publishing lock so a PC
		// that raced with the snapshot is either fenced above or sees the full
		// retained subscription in replaySubscriptionsToPC.
		h.mu.Lock()
		currentPC := h.peers[route.PCDeviceID]
		if currentPC != nil && currentPC.role == "pc" && currentPC.principal.UserID == sender.principal.UserID && currentPC.open() {
			h.mu.Unlock()
			continue
		}
		if h.peers[sender.principal.Device.ID] != sender || !sender.open() || sender.role != "android" ||
			!routeAuthorizationLive(route, time.Now()) {
			h.mu.Unlock()
			return denyRoute("sender_connection_replaced", store.ErrDenied)
		}
		if latest, ok := latestVisibleSubscriptionACKLocked(sender, sessionID); ok && ack < latest {
			h.mu.Unlock()
			return denyRoute("cumulative_ack_regressed", errCumulativeACKRegressed)
		}
		added := h.activateSubscriptionLocked(sender, sessionID, route, ack, frame)
		h.mu.Unlock()
		if added {
			h.observer.SubscriptionsChanged(1)
		}
		return nil
	}
}

func (h *Hub) activateSubscriptionLocked(sender *peer, sessionID uuid.UUID, route store.SessionRouteAuthorization, ack uint64, frame []byte) bool {
	added := h.subscribeLocked(sender, sessionID, route)
	if sender.subscriptionFrames == nil {
		sender.subscriptionFrames = map[uuid.UUID][]byte{}
	}
	if sender.subscriptionACKs == nil {
		sender.subscriptionACKs = map[uuid.UUID]uint64{}
	}
	sender.subscriptionFrames[sessionID] = append([]byte(nil), frame...)
	sender.subscriptionACKs[sessionID] = ack
	delete(sender.subscriptionControls, sessionID)
	return added
}

type replayedSubscription struct {
	sessionID   uuid.UUID
	recipientID uuid.UUID
}

// replaySubscriptionsToPC restores every still-live Android subscription that
// is routed to this PC. The first pass snapshots eligible route identities;
// each retained SUBSCRIBE, ACK/control and route are revalidated together before
// enqueueing outside hub.mu onto the PC's bounded writer queue.
func (h *Hub) replaySubscriptionsToPC(target *peer) {
	target.pcReplayMu.Lock()
	defer target.pcReplayMu.Unlock()
	h.mu.RLock()
	if h.peers[target.principal.Device.ID] != target || target.role != "pc" {
		h.mu.RUnlock()
		return
	}
	replays := make([]replayedSubscription, 0)
	for sessionID, subscribers := range h.subscriptions {
		for recipientID, subscriber := range subscribers {
			if subscriber == nil || subscriber.role != "android" || subscriber.principal.UserID != target.principal.UserID {
				continue
			}
			cached, routePresent := subscriber.subscriptionRoutes[sessionID]
			frame, framePresent := subscriber.subscriptionFrames[sessionID]
			_, ackPresent := subscriber.subscriptionACKs[sessionID]
			if !routePresent || cached.authorization.PCDeviceID != target.principal.Device.ID ||
				!routeAuthorizationLive(cached.authorization, time.Now()) || !framePresent || !ackPresent || len(frame) == 0 {
				continue
			}
			replays = append(replays, replayedSubscription{
				sessionID:   sessionID,
				recipientID: recipientID,
			})
		}
	}
	h.mu.RUnlock()

	for _, replay := range replays {
		h.mu.RLock()
		subscriber := h.subscriptions[replay.sessionID][replay.recipientID]
		cached, routePresent := cachedSessionRoute{}, false
		frame, framePresent := []byte(nil), false
		ack, ackPresent := uint64(0), false
		control, controlSet := retainedSubscriberControl{}, false
		if subscriber != nil {
			cached, routePresent = subscriber.subscriptionRoutes[replay.sessionID]
			frame, framePresent = subscriber.subscriptionFrames[replay.sessionID]
			ack, ackPresent = subscriber.subscriptionACKs[replay.sessionID]
			control, controlSet = subscriber.subscriptionControls[replay.sessionID]
		}
		valid := h.peers[target.principal.Device.ID] == target && target.role == "pc" && target.open() &&
			subscriber != nil && h.peers[replay.recipientID] == subscriber && subscriber.open() &&
			subscriber.role == "android" && subscriber.principal.UserID == target.principal.UserID &&
			routePresent && cached.authorization.PCDeviceID == target.principal.Device.ID &&
			routeAuthorizationLive(cached.authorization, time.Now()) && framePresent && ackPresent && len(frame) > 0
		if valid {
			frame = append([]byte(nil), frame...)
			control.frame = append([]byte(nil), control.frame...)
			latestACK := ack
			controlSet = controlSet && len(control.frame) > 0 && control.ack >= latestACK
			if controlSet {
				latestACK = control.ack
			}
			target.beginPCReplayEpochLocked(replay.sessionID, replay.recipientID, latestACK)
		}
		h.mu.RUnlock()
		if !valid {
			continue
		}
		queued := target.enqueue(frame)
		if queued && controlSet {
			queued = target.enqueue(control.frame)
		}
		if !queued {
			return
		}
	}
	target.subscriptionReplayReady = true
}

func (h *Hub) routeToSubscriber(sender *peer, sessionID, recipient uuid.UUID, frame []byte) error {
	h.mu.RLock()
	target := h.subscriptions[sessionID][recipient]
	var cached cachedSessionRoute
	var routePresent bool
	if target != nil {
		cached, routePresent = target.subscriptionRoutes[sessionID]
	}
	h.mu.RUnlock()
	if target == nil {
		// Android can disappear after the Agent snapshots its recipients. Treat
		// that as a normal offline delivery window: committing the PC high-water
		// mark lets the next SUBSCRIBE open a replay epoch from Android's durable
		// ACK. Closing the authenticated PC here would make it reconnect and
		// resend the same unavailable frame forever.
		return nil
	}
	if target.principal.UserID != sender.principal.UserID {
		return denyRoute("subscriber_account_mismatch", store.ErrDenied)
	}
	if !routePresent || cached.authorization.PCDeviceID != sender.principal.Device.ID {
		return denyRoute("subscription_route_inactive", store.ErrDenied)
	}
	if !routeAuthorizationLive(cached.authorization, time.Now()) {
		if h.unsubscribe(target, sessionID) {
			if h.beforeExpiredReservationCleanup != nil {
				h.beforeExpiredReservationCleanup()
			}
			return denyRouteAfterReservationRelease("subscription_authority_expired", store.ErrDenied, target)
		}
		return denyRoute("subscription_authority_expired", store.ErrDenied)
	}
	if !h.senderConnectionCurrent(sender) {
		return denyRoute("subscriber_replaced", store.ErrDenied)
	}
	if !h.subscriberRouteCurrent(sender, target, sessionID, recipient) {
		return nil
	}
	if !target.enqueue(frame) {
		return nil
	}
	if !h.senderConnectionCurrent(sender) {
		return denyRoute("subscriber_replaced", store.ErrDenied)
	}
	if !h.subscriberRouteCurrent(sender, target, sessionID, recipient) || !target.open() {
		return nil
	}
	return nil
}

func (h *Hub) senderConnectionCurrent(sender *peer) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return h.peers[sender.principal.Device.ID] == sender && sender.open()
}

func (h *Hub) subscriberRouteCurrent(sender, target *peer, sessionID, recipient uuid.UUID) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	cached, routePresent := target.subscriptionRoutes[sessionID]
	return h.peers[sender.principal.Device.ID] == sender && sender.open() &&
		h.peers[recipient] == target &&
		h.subscriptions[sessionID][recipient] == target &&
		target.role == "android" && target.principal.UserID == sender.principal.UserID &&
		routePresent && cached.authorization.PCDeviceID == sender.principal.Device.ID &&
		routeAuthorizationLive(cached.authorization, time.Now())
}

func (h *Hub) sendToDevice(sender *peer, deviceID uuid.UUID, frame []byte) {
	h.mu.RLock()
	target := h.peers[deviceID]
	h.mu.RUnlock()
	if target != nil && sender.role == "android" && target.role == "pc" && target.principal.UserID == sender.principal.UserID {
		_ = target.enqueue(frame)
	}
}

func (h *Hub) sendControlToDevice(sender *peer, deviceID uuid.UUID, frame []byte) bool {
	h.mu.RLock()
	target := h.peers[deviceID]
	valid := target != nil && sender.role == "android" && target.role == "pc" &&
		target.principal.UserID == sender.principal.UserID
	h.mu.RUnlock()
	if !valid {
		return false
	}
	return target.enqueue(frame) && h.isCurrent(target)
}

// sendSubscriptionToPC makes the Android-visible cumulative ACK authoritative
// for exactly the current PC connection and recipient before that subscription
// is enqueued. Selecting the peer once avoids a replacement race in which the
// subscription reaches a new PC connection without its replay epoch.
func (h *Hub) sendSubscriptionToPC(sender *peer, deviceID, sessionID uuid.UUID, ack uint64, frame []byte) {
	h.mu.RLock()
	target := h.peers[deviceID]
	h.mu.RUnlock()
	if target == nil || target.role != "pc" || target.principal.UserID != sender.principal.UserID {
		return
	}
	target.pcReplayMu.Lock()
	h.mu.RLock()
	current := h.peers[deviceID] == target && target.open() &&
		h.peers[sender.principal.Device.ID] == sender && sender.open() &&
		h.subscriptions[sessionID][sender.principal.Device.ID] == sender
	if current {
		target.beginPCReplayEpochLocked(sessionID, sender.principal.Device.ID, ack)
	}
	h.mu.RUnlock()
	if current {
		_ = target.enqueue(frame)
	}
	target.pcReplayMu.Unlock()
}

func (p *peer) open() bool {
	select {
	case <-p.done:
		return false
	default:
		return true
	}
}

func (p *peer) enqueue(frame []byte) bool {
	if !p.open() {
		return false
	}
	copyFrame := append([]byte(nil), frame...)
	if !p.reserveQueueBytes(int64(len(copyFrame))) {
		if p.close(websocket.CloseTryAgainLater, "slow consumer; byte budget exceeded; resume from cumulative ACK") {
			p.hub.observer.SlowConsumerDisconnected()
		}
		return false
	}
	// The healthy path normally has queue capacity. Avoid allocating and
	// registering a runtime timer for every terminal frame; create the bounded
	// backpressure timer only after the first non-blocking enqueue fails.
	select {
	case p.send <- copyFrame:
		return true
	case <-p.done:
		p.releaseQueueBytes(int64(len(copyFrame)))
		return false
	default:
	}
	timer := time.NewTimer(peerEnqueueTimeout)
	defer timer.Stop()
	select {
	case p.send <- copyFrame:
		return true
	case <-p.done:
		p.releaseQueueBytes(int64(len(copyFrame)))
		return false
	case <-timer.C:
		p.releaseQueueBytes(int64(len(copyFrame)))
		if p.close(websocket.CloseTryAgainLater, "slow consumer; resume from cumulative ACK") {
			p.hub.observer.SlowConsumerDisconnected()
		}
		return false
	}
}

// enqueueControl keeps all data writes on writeLoop. Gorilla permits control
// close frames concurrently, but Text/Binary data writers must remain unique.
func (p *peer) enqueueControl(frame []byte) bool {
	if !p.open() || len(frame) == 0 || len(frame) > maxLivenessControlBytes || p.controlSend == nil {
		return false
	}
	copyFrame := append([]byte(nil), frame...)
	select {
	case p.controlSend <- copyFrame:
		return true
	case <-p.done:
		return false
	default:
		p.close(websocket.CloseTryAgainLater, "realtime control queue exceeded")
		return false
	}
}

func (p *peer) reserveQueueBytes(size int64) bool {
	if size <= 0 || size > peerQueueBytes {
		return false
	}
	for {
		current := p.queuedBytes.Load()
		if current > peerQueueBytes-size {
			return false
		}
		if p.queuedBytes.CompareAndSwap(current, current+size) {
			return true
		}
	}
}

func (p *peer) releaseQueueBytes(size int64) {
	for {
		current := p.queuedBytes.Load()
		next := current - size
		if next < 0 {
			next = 0
		}
		if p.queuedBytes.CompareAndSwap(current, next) {
			return
		}
	}
}

func (p *peer) writeLoop() {
	ticker := time.NewTicker(pingInterval)
	defer ticker.Stop()
	for {
		select {
		case control := <-p.controlSend:
			_ = p.socket.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := p.socket.WriteMessage(websocket.TextMessage, control); err != nil {
				p.close(websocket.CloseAbnormalClosure, "control write failed")
				return
			}
		case frame := <-p.send:
			p.releaseQueueBytes(int64(len(frame)))
			_ = p.socket.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := p.socket.WriteMessage(websocket.BinaryMessage, frame); err != nil {
				p.close(websocket.CloseAbnormalClosure, "write failed")
				return
			}
		case <-ticker.C:
			p.refreshSubscriptions()
			_ = p.socket.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := p.socket.WriteControl(websocket.PingMessage, nil, time.Now().Add(10*time.Second)); err != nil {
				p.close(websocket.CloseAbnormalClosure, "ping failed")
				return
			}
		case <-p.done:
			return
		}
	}
}

func (p *peer) refreshSubscriptions() {
	if !p.hub.isCurrent(p) {
		return
	}
	p.hub.mu.RLock()
	sessions := make([]uuid.UUID, 0, len(p.subscriptions))
	for id := range p.subscriptions {
		sessions = append(sessions, id)
	}
	p.hub.mu.RUnlock()
	if len(sessions) == 0 {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if batchStore, ok := p.hub.store.(batchRouteAuthorityStore); ok {
		p.refreshSubscriptionsBatched(ctx, batchStore, sessions)
		return
	}
	// Safe compatibility path for stores that implement only the original
	// single-session authority contract.
	for _, id := range sessions {
		route, err := p.hub.store.SessionRoute(ctx, p.principal.UserID, p.principal.Device.ID, id)
		if err != nil {
			if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) ||
				!p.retainSubscriptionDuringTransientFailure(id, time.Now()) {
				p.invalidateSubscription(ctx, id)
			}
			continue
		}
		if !routeAuthorizationLive(route, time.Now()) {
			p.invalidateSubscription(ctx, id)
			continue
		}
		if err := p.hub.reserveLiveSession(ctx, p, id); err != nil {
			if !p.retainSubscriptionDuringTransientFailure(id, time.Now()) {
				p.invalidateSubscription(ctx, id)
			}
			continue
		}
		p.hub.updateSubscriptionRoute(p, id, route)
	}
}

func (p *peer) refreshSubscriptionsBatched(ctx context.Context, batchStore batchRouteAuthorityStore, sessions []uuid.UUID) {
	for start := 0; start < len(sessions); start += store.MaxSessionRouteBatchSize {
		end := min(start+store.MaxSessionRouteBatchSize, len(sessions))
		batch := sessions[start:end]
		routes, err := batchStore.SessionRoutes(ctx, p.principal.UserID, p.principal.Device.ID, batch)
		if err != nil {
			now := time.Now()
			for _, sessionID := range batch {
				if !p.retainSubscriptionDuringTransientFailure(sessionID, now) {
					p.invalidateSubscription(ctx, sessionID)
				}
			}
			continue
		}
		now := time.Now()
		live := make([]uuid.UUID, 0, len(batch))
		for _, sessionID := range batch {
			route, authorized := routes[sessionID]
			if !authorized || !routeAuthorizationLive(route, now) {
				p.invalidateSubscription(ctx, sessionID)
				continue
			}
			live = append(live, sessionID)
		}
		if batchLeases, ok := p.hub.leases.(batchLiveSessionStore); ok {
			if err := p.hub.reserveLiveSessions(ctx, p, live, batchLeases); err != nil {
				now := time.Now()
				for _, sessionID := range live {
					if !p.retainSubscriptionDuringTransientFailure(sessionID, now) {
						p.invalidateSubscription(ctx, sessionID)
					}
				}
				continue
			}
			for _, sessionID := range live {
				p.hub.updateSubscriptionRoute(p, sessionID, routes[sessionID])
			}
			continue
		}
		for _, sessionID := range live {
			if err := p.hub.reserveLiveSession(ctx, p, sessionID); err != nil {
				if !p.retainSubscriptionDuringTransientFailure(sessionID, time.Now()) {
					p.invalidateSubscription(ctx, sessionID)
				}
				continue
			}
			p.hub.updateSubscriptionRoute(p, sessionID, routes[sessionID])
		}
	}
}

func (p *peer) retainSubscriptionDuringTransientFailure(sessionID uuid.UUID, now time.Time) bool {
	cached, subscribed := p.hub.subscriptionRoute(p, sessionID)
	return subscribed && routeAuthorizationLive(cached.authorization, now) &&
		now.Before(cached.failureUntil)
}

func (p *peer) refreshPresence() {
	p.hub.mu.RLock()
	registry := p.hub.presence
	p.hub.mu.RUnlock()
	if registry != nil {
		registry.Touch(p.presenceID)
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	var err error
	if p.role == "pc" {
		err = p.hub.store.TouchPCPresence(ctx, p.principal.UserID, p.principal.Device.ID)
	} else {
		err = p.hub.store.TouchDevicePresence(ctx, p.principal.UserID, p.principal.Device.ID)
	}
	if errors.Is(err, store.ErrDenied) {
		p.close(websocket.ClosePolicyViolation, "device no longer trusted")
	}
}
func (p *peer) close(code int, reason string) bool {
	closed := false
	p.once.Do(func() {
		closed = true
		close(p.done)
		if p.socket != nil {
			closeSocket(p.socket, code, reason)
		}
	})
	return closed
}
func closeSocket(socket *websocket.Conn, code int, reason string) {
	_ = socket.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(code, reason), time.Now().Add(time.Second))
	_ = socket.Close()
}

// Revoke immediately disconnects a device after the database transaction has
// committed. Pub/Sub callers use this across Relay instances.
func (h *Hub) Revoke(deviceID uuid.UUID) {
	h.mu.RLock()
	p := h.peers[deviceID]
	h.mu.RUnlock()
	if p != nil {
		p.close(websocket.ClosePolicyViolation, "device revoked")
	}
}

// DisableAccount terminates every current device connection for an account.
// PostgreSQL status checks remain the authority on every later handshake and
// route; this hook makes a committed administrator lock effective immediately
// across Relay instances through Valkey Pub/Sub.
func (h *Hub) DisableAccount(userID uuid.UUID) {
	h.closeAccount(userID, "account disabled")
}

func (h *Hub) closeAccount(userID uuid.UUID, reason string) {
	h.mu.RLock()
	peers := make([]*peer, 0)
	for _, p := range h.peers {
		if p.principal.UserID == userID {
			peers = append(peers, p)
		}
	}
	h.mu.RUnlock()
	for _, p := range peers {
		p.close(websocket.ClosePolicyViolation, reason)
	}
}

// RefreshDevice closes the current transport after a committed grant change.
// The device may reconnect, but every new route is then evaluated against the
// updated PostgreSQL authority and key epoch.
func (h *Hub) RefreshDevice(deviceID uuid.UUID) {
	h.mu.RLock()
	p := h.peers[deviceID]
	h.mu.RUnlock()
	if p != nil {
		p.close(websocket.ClosePolicyViolation, "device authorization changed")
	}
}

// RefreshControlPolicy invalidates every cached policy for one PC immediately
// after its committed policy_version change. Callers may publish either the PC
// row ID or its Agent device ID; the next control frame reloads PostgreSQL.
// Existing subscriptions stay connected and read-only output is unaffected.
func (h *Hub) RefreshControlPolicy(pcOrDeviceID uuid.UUID) {
	h.mu.Lock()
	for _, p := range h.peers {
		for sessionID, cached := range p.controlPolicies {
			if cached.policy.PCID == pcOrDeviceID || cached.policy.PCDeviceID == pcOrDeviceID {
				delete(p.controlPolicies, sessionID)
			}
		}
	}
	h.mu.Unlock()
}
func (h *Hub) Close(ctx context.Context) error {
	h.mu.Lock()
	h.closed = true
	peers := make([]*peer, 0, len(h.peers))
	for _, p := range h.peers {
		peers = append(peers, p)
	}
	h.mu.Unlock()
	for _, p := range peers {
		p.close(websocket.CloseGoingAway, "relay shutting down")
	}
	select {
	case <-ctx.Done():
		return ctx.Err()
	default:
		return nil
	}
}
func shortID(id uuid.UUID) string { return fmt.Sprintf("%x", id[:4]) }

func metricDirection(role string) string {
	if role == "pc" {
		return "pc_to_android"
	}
	return "android_to_pc"
}

var _ = errors.Is
