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
	peerEnqueueTimeout = 500 * time.Millisecond
)

var requiredCapabilities = []string{"cumulative_ack", "e2ee_x25519_chacha20poly1305", "flow_control_v1", "lease_v1"}

type Hub struct {
	store         *store.Postgres
	leases        *kv.Valkey
	logger        *slog.Logger
	observer      Observer
	mu            sync.RWMutex
	peers         map[uuid.UUID]*peer
	subscriptions map[uuid.UUID]map[uuid.UUID]*peer
	closed        bool
}

func NewHub(repository *store.Postgres, leases *kv.Valkey, logger *slog.Logger) *Hub {
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

func NewHubWithObserver(repository *store.Postgres, leases *kv.Valkey, logger *slog.Logger, observer Observer) *Hub {
	if logger == nil {
		logger = slog.Default()
	}
	if observer == nil {
		observer = noopObserver{}
	}
	return &Hub{store: repository, leases: leases, logger: logger, observer: observer, peers: map[uuid.UUID]*peer{}, subscriptions: map[uuid.UUID]map[uuid.UUID]*peer{}}
}

func (h *Hub) Handler() http.Handler { return http.HandlerFunc(h.serveHTTP) }

type clientHello struct {
	Role             string   `json:"role"`
	ProtocolVersions []uint32 `json:"protocol_versions"`
	Capabilities     []string `json:"capabilities"`
}
type serverWelcome struct {
	Type             string `json:"type"`
	ProtocolVersion  uint32 `json:"protocol_version"`
	HeartbeatSeconds int    `json:"heartbeat_seconds"`
	ConnectionID     string `json:"connection_id"`
}

type peer struct {
	hub            *Hub
	socket         *websocket.Conn
	principal      auth.Principal
	role           string
	send           chan []byte
	done           chan struct{}
	once           sync.Once
	lastSequences  map[uuid.UUID]uint64
	pcReplayMu     sync.Mutex
	pcReplayRoutes map[pcRouteKey]*pcReplayRoute
	subscriptions  map[uuid.UUID]bool
	queuedBytes    atomic.Int64
	// subscriptionRoutes is protected by hub.mu together with subscriptions.
	// It binds an authorized Android subscription to the owning Windows device
	// and the exact Grant expiry used by the zero-database-query output path.
	subscriptionRoutes map[uuid.UUID]store.SessionRouteAuthorization
	// Retain the byte-identical signed SUBSCRIBE and its cumulative ACK only
	// while this Android peer remains subscribed. A replacement PC connection
	// can then restore replay state without forging an Android frame.
	subscriptionFrames map[uuid.UUID][]byte
	subscriptionACKs   map[uuid.UUID]uint64
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
	if hello.Role == "pc" && principal.Device.Type != "WINDOWS_PC" || hello.Role == "android" && principal.Device.Type != "ANDROID" {
		closeSocket(socket, websocket.ClosePolicyViolation, "role does not match device")
		return
	}
	connection := &peer{hub: h, socket: socket, principal: principal, role: hello.Role, send: make(chan []byte, peerQueueFrames), done: make(chan struct{}), subscriptions: map[uuid.UUID]bool{}, subscriptionRoutes: map[uuid.UUID]store.SessionRouteAuthorization{}, subscriptionFrames: map[uuid.UUID][]byte{}, subscriptionACKs: map[uuid.UUID]uint64{}, lastSequences: map[uuid.UUID]uint64{}, pcReplayRoutes: map[pcRouteKey]*pcReplayRoute{}}
	if !h.add(connection) {
		closeSocket(socket, websocket.CloseTryAgainLater, "relay shutting down")
		return
	}
	h.observer.ConnectionOpened(connection.role)
	defer func() {
		h.remove(connection)
		h.observer.ConnectionClosed(connection.role)
	}()
	welcome := serverWelcome{Type: "welcome", ProtocolVersion: 1, HeartbeatSeconds: int(pingInterval.Seconds()), ConnectionID: uuid.NewString()}
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
	}
	h.mu.Unlock()
	if len(removedSessions) > 0 {
		h.observer.SubscriptionsChanged(-len(removedSessions))
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	for _, sessionID := range removedSessions {
		_ = h.leases.ReleaseLiveSession(ctx, p.principal.UserID, sessionID, p.principal.Device.ID)
	}
}

func (p *peer) readLoop() {
	defer p.close(websocket.CloseNormalClosure, "reader stopped")
	for {
		messageType, frame, err := p.socket.ReadMessage()
		if err != nil {
			return
		}
		if messageType != websocket.BinaryMessage {
			p.close(websocket.CloseUnsupportedData, "binary Envelope required")
			return
		}
		envelope, err := realtimewire.Unmarshal(frame)
		if err != nil {
			p.close(websocket.CloseProtocolError, "invalid production Envelope")
			return
		}
		if !bytes.Equal(envelope.SenderDeviceID, p.principal.Device.ID[:]) {
			p.close(websocket.ClosePolicyViolation, "sender or sequence rejected")
			return
		}
		if p.role == "pc" && envelope.Direction != realtimewire.PCToAndroid || p.role == "android" && envelope.Direction != realtimewire.AndroidToPC {
			p.close(websocket.ClosePolicyViolation, "direction rejected")
			return
		}
		if !allowedMessage(p.role, envelope.MessageType) || !validEnvelopeACK(envelope) {
			p.close(websocket.ClosePolicyViolation, "message type or acknowledgement rejected")
			return
		}
		sessionID, err := uuid.FromBytes(envelope.SessionID)
		if err != nil {
			p.close(websocket.CloseProtocolError, "invalid session")
			return
		}
		if p.role == "pc" {
			recipient, recipientErr := uuid.FromBytes(envelope.RecipientDeviceID)
			if recipientErr != nil || !p.acceptPCSequence(sessionID, recipient, envelope.Sequence, frame) {
				p.close(websocket.ClosePolicyViolation, "sequence rejected")
				return
			}
		} else if !p.acceptSequence(sessionID, envelope.Sequence) {
			p.close(websocket.ClosePolicyViolation, "sequence rejected")
			return
		}
		started := time.Now()
		routeErr := p.route(envelope, sessionID, frame)
		result := "accepted"
		if routeErr != nil {
			result = "denied"
		}
		p.hub.observer.EnvelopeRouted(metricDirection(p.role), strconv.FormatUint(uint64(envelope.MessageType), 10), result, len(frame), time.Since(started))
		if routeErr != nil {
			p.hub.logger.Warn("realtime route denied", "device", shortID(p.principal.Device.ID), "session", shortID(sessionID), "type", uint32(envelope.MessageType))
			p.close(websocket.ClosePolicyViolation, "routing authorization denied")
			return
		}
	}
}

func allowedMessage(role string, messageType realtimewire.MessageType) bool {
	if role == "android" {
		switch messageType {
		case realtimewire.Heartbeat, realtimewire.Goodbye,
			realtimewire.Subscribe, realtimewire.Unsubscribe,
			realtimewire.OutputACK, realtimewire.FlowControl,
			realtimewire.InputEvent, realtimewire.ResizeRequest,
			realtimewire.ResumeRequest, realtimewire.LeaseRequest,
			realtimewire.SessionTerminateRequest:
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
			realtimewire.SessionInterrupted, realtimewire.SessionEnded:
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
		p.lastSequences[sessionID] = sequence
	}
	return true
}

const maxPCReplayFrames = 4096

// PC output normally reuses a sequence only as the exact serialized frame
// previously accepted for this route. A visible cumulative-ACK subscription
// opens a bounded replay epoch from ACK+1 through the route's prior high-water
// mark. This permits an ordered replay even when those old digests have aged
// out, while the first accepted frame in the epoch re-establishes the digest
// fence for subsequent duplicates.
func (p *peer) acceptPCSequence(sessionID, recipientID uuid.UUID, sequence uint64, frame []byte) bool {
	p.pcReplayMu.Lock()
	defer p.pcReplayMu.Unlock()
	routeKey := pcRouteKey{sessionID: sessionID, recipientID: recipientID}
	state := p.pcReplayRoute(routeKey)
	digest := sha256.Sum256(frame)

	if state.epoch != nil {
		if state.epoch.next > state.epoch.target {
			state.epoch = nil
		} else {
			switch {
			case sequence < state.epoch.next:
				previous, ok := state.digests[sequence]
				return ok && bytes.Equal(previous[:], digest[:])
			case sequence > state.epoch.next:
				return false
			default:
				if previous, ok := state.digests[sequence]; ok && !bytes.Equal(previous[:], digest[:]) {
					return false
				}
				state.remember(sequence, digest)
				state.epoch.next++
				if sequence > state.highWater {
					state.highWater = sequence
				}
				if state.epoch.next > state.epoch.target {
					state.epoch = nil
				}
				return true
			}
		}
	}

	if sequence <= state.highWater {
		previous, ok := state.digests[sequence]
		return ok && bytes.Equal(previous[:], digest[:])
	}
	state.highWater = sequence
	state.remember(sequence, digest)
	return true
}

func (p *peer) pcReplayRoute(key pcRouteKey) *pcReplayRoute {
	if p.pcReplayRoutes == nil {
		p.pcReplayRoutes = map[pcRouteKey]*pcReplayRoute{}
	}
	state := p.pcReplayRoutes[key]
	if state == nil {
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
	state := p.pcReplayRoute(pcRouteKey{sessionID: sessionID, recipientID: recipientID})
	if ack >= state.highWater || ack == ^uint64(0) {
		state.epoch = nil
		return
	}
	state.epoch = &pcReplayEpoch{next: ack + 1, target: state.highWater}
}

func (p *peer) route(envelope realtimewire.Envelope, sessionID uuid.UUID, frame []byte) error {
	if !p.hub.isCurrent(p) {
		return store.ErrDenied
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if len(envelope.RecipientDeviceID) != 16 {
		return store.ErrDenied
	}
	recipient, err := uuid.FromBytes(envelope.RecipientDeviceID)
	if err != nil || recipient == p.principal.Device.ID {
		return store.ErrDenied
	}
	if p.role == "pc" {
		// An Android client can disconnect or unsubscribe between the Agent's
		// subscriber snapshot and this frame. Treat that as an idempotent drop,
		// not as a policy violation that tears down the entire PC connection. The
		// cached subscription binds this session to its authoritative PC owner,
		// so the terminal-output hot path does not query PostgreSQL per frame.
		return p.hub.routeToSubscriber(ctx, p, sessionID, recipient, frame)
	}
	if envelope.MessageType == realtimewire.Unsubscribe {
		route, subscribed := p.hub.subscriptionRoute(p, sessionID)
		if p.hub.unsubscribe(p, sessionID) {
			_ = p.hub.leases.ReleaseLiveSession(ctx, p.principal.UserID, sessionID, p.principal.Device.ID)
		}
		if subscribed && recipient == route.PCDeviceID {
			p.hub.sendToDevice(p, route.PCDeviceID, frame)
		}
		return nil
	}
	route, err := p.hub.store.SessionRoute(ctx, p.principal.UserID, p.principal.Device.ID, sessionID)
	if err != nil {
		return err
	}
	switch envelope.MessageType {
	case realtimewire.Subscribe:
		if _, err := p.hub.leases.ReserveLiveSession(ctx, p.principal.UserID, sessionID, p.principal.Device.ID); err != nil {
			return err
		}
		p.hub.subscribe(p, sessionID, route)
	}
	if !allowedRoutePermission(envelope.MessageType, route.Permission) {
		return store.ErrDenied
	}
	if envelope.MessageType == realtimewire.OutputACK || envelope.MessageType == realtimewire.FlowControl ||
		envelope.MessageType == realtimewire.ResumeRequest {
		if _, subscribed := p.hub.subscriptionRoute(p, sessionID); !subscribed {
			return store.ErrDenied
		}
	}
	if envelope.MessageType == realtimewire.InputEvent || envelope.MessageType == realtimewire.ResizeRequest {
		lease, err := p.hub.leases.GetLease(ctx, sessionID)
		if err != nil || lease.HolderDeviceID != p.principal.Device.ID {
			return store.ErrDenied
		}
	}
	if recipient != route.PCDeviceID {
		return store.ErrDenied
	}
	if envelope.MessageType == realtimewire.Subscribe {
		p.hub.rememberSubscriptionFrame(p, sessionID, envelope.ACK, frame)
		p.hub.sendSubscriptionToPC(p, route.PCDeviceID, sessionID, envelope.ACK, frame)
		return nil
	}
	p.hub.sendToDevice(p, route.PCDeviceID, frame)
	return nil
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
	h.mu.Lock()
	if p.subscriptionRoutes == nil {
		p.subscriptionRoutes = map[uuid.UUID]store.SessionRouteAuthorization{}
	}
	subscribers := h.subscriptions[sessionID]
	if subscribers == nil {
		subscribers = map[uuid.UUID]*peer{}
		h.subscriptions[sessionID] = subscribers
	}
	_, existed := subscribers[p.principal.Device.ID]
	subscribers[p.principal.Device.ID] = p
	p.subscriptions[sessionID] = true
	p.subscriptionRoutes[sessionID] = route
	h.mu.Unlock()
	if !existed {
		h.observer.SubscriptionsChanged(1)
	}
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

func (h *Hub) subscriptionRoute(p *peer, sessionID uuid.UUID) (store.SessionRouteAuthorization, bool) {
	h.mu.RLock()
	defer h.mu.RUnlock()
	if h.subscriptions[sessionID][p.principal.Device.ID] != p {
		return store.SessionRouteAuthorization{}, false
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
	p.subscriptionRoutes[sessionID] = route
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
	p.subscriptionFrames[sessionID] = append([]byte(nil), frame...)
	p.subscriptionACKs[sessionID] = ack
	return true
}

type replayedSubscription struct {
	sessionID   uuid.UUID
	recipientID uuid.UUID
	ack         uint64
	frame       []byte
}

// replaySubscriptionsToPC restores every still-live Android subscription that
// is routed to this PC. Snapshotting under hub.mu keeps the route, ACK and frame
// consistent; enqueueing after the lock avoids blocking unrelated Relay work
// on the PC's bounded writer queue.
func (h *Hub) replaySubscriptionsToPC(target *peer) {
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
			route, routePresent := subscriber.subscriptionRoutes[sessionID]
			frame, framePresent := subscriber.subscriptionFrames[sessionID]
			ack, ackPresent := subscriber.subscriptionACKs[sessionID]
			if !routePresent || route.PCDeviceID != target.principal.Device.ID || !framePresent || !ackPresent || len(frame) == 0 {
				continue
			}
			replays = append(replays, replayedSubscription{
				sessionID:   sessionID,
				recipientID: recipientID,
				ack:         ack,
				frame:       append([]byte(nil), frame...),
			})
		}
	}
	h.mu.RUnlock()

	for _, replay := range replays {
		if !h.isCurrent(target) {
			return
		}
		target.beginPCReplayEpoch(replay.sessionID, replay.recipientID, replay.ack)
		target.enqueue(replay.frame)
	}
}

func (h *Hub) routeToSubscriber(ctx context.Context, sender *peer, sessionID, recipient uuid.UUID, frame []byte) error {
	h.mu.RLock()
	target := h.subscriptions[sessionID][recipient]
	var route store.SessionRouteAuthorization
	var routePresent bool
	if target != nil {
		route, routePresent = target.subscriptionRoutes[sessionID]
	}
	h.mu.RUnlock()
	if target == nil || target.principal.UserID != sender.principal.UserID {
		return nil
	}
	if !routePresent || route.PCDeviceID != sender.principal.Device.ID {
		return store.ErrDenied
	}
	if route.GrantExpiresAt != nil && !time.Now().Before(*route.GrantExpiresAt) {
		if h.unsubscribe(target, sessionID) {
			_ = h.leases.ReleaseLiveSession(ctx, target.principal.UserID, sessionID, target.principal.Device.ID)
		}
		return nil
	}
	target.enqueue(frame)
	return nil
}
func (h *Hub) sendToDevice(sender *peer, deviceID uuid.UUID, frame []byte) {
	h.mu.RLock()
	target := h.peers[deviceID]
	h.mu.RUnlock()
	if target != nil && target.principal.UserID == sender.principal.UserID {
		target.enqueue(frame)
	}
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
	target.beginPCReplayEpoch(sessionID, sender.principal.Device.ID, ack)
	target.enqueue(frame)
}

func (p *peer) enqueue(frame []byte) {
	copyFrame := append([]byte(nil), frame...)
	if !p.reserveQueueBytes(int64(len(copyFrame))) {
		if p.close(websocket.CloseTryAgainLater, "slow consumer; byte budget exceeded; resume from cumulative ACK") {
			p.hub.observer.SlowConsumerDisconnected()
		}
		return
	}
	timer := time.NewTimer(peerEnqueueTimeout)
	defer timer.Stop()
	select {
	case p.send <- copyFrame:
	case <-p.done:
		p.releaseQueueBytes(int64(len(copyFrame)))
	case <-timer.C:
		p.releaseQueueBytes(int64(len(copyFrame)))
		if p.close(websocket.CloseTryAgainLater, "slow consumer; resume from cumulative ACK") {
			p.hub.observer.SlowConsumerDisconnected()
		}
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
	for _, id := range sessions {
		route, err := p.hub.store.SessionRoute(ctx, p.principal.UserID, p.principal.Device.ID, id)
		if err != nil {
			if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) {
				if p.hub.unsubscribe(p, id) {
					_ = p.hub.leases.ReleaseLiveSession(ctx, p.principal.UserID, id, p.principal.Device.ID)
				}
			}
			continue
		}
		if _, err := p.hub.leases.ReserveLiveSession(ctx, p.principal.UserID, id, p.principal.Device.ID); err == nil {
			p.hub.updateSubscriptionRoute(p, id, route)
		}
	}
}

func (p *peer) refreshPresence() {
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

// RefreshAccount is retained for the entitlement-change pub/sub event. Quota
// and billing metadata changes do not alter routing authority, so active
// transports and subscriptions remain connected.
func (h *Hub) RefreshAccount(_ uuid.UUID) {}

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
