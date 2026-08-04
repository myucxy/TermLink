// Package realtime implements the production opaque-ciphertext WebSocket
// Relay. It authorizes routing metadata but never decrypts terminal payloads.
package realtime

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"slices"
	"strconv"
	"sync"
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
)

var requiredCapabilities = []string{"cumulative_ack", "e2ee_x25519_chacha20poly1305", "lease_v1"}

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
	hub           *Hub
	socket        *websocket.Conn
	principal     auth.Principal
	role          string
	send          chan []byte
	done          chan struct{}
	once          sync.Once
	lastSequences map[uuid.UUID]uint64
	subscriptions map[uuid.UUID]bool
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
	connection := &peer{hub: h, socket: socket, principal: principal, role: hello.Role, send: make(chan []byte, peerQueueFrames), done: make(chan struct{}), subscriptions: map[uuid.UUID]bool{}, lastSequences: map[uuid.UUID]uint64{}}
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
	_ = socket.SetReadDeadline(time.Now().Add(pongTimeout))
	socket.SetPongHandler(func(string) error { return socket.SetReadDeadline(time.Now().Add(pongTimeout)) })
	go connection.writeLoop()
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
		sessionID, err := uuid.FromBytes(envelope.SessionID)
		if err != nil {
			p.close(websocket.CloseProtocolError, "invalid session")
			return
		}
		if !p.acceptSequence(sessionID, envelope.Sequence) {
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
		owner, err := p.hub.store.SessionPCDevice(ctx, p.principal.UserID, sessionID)
		if err != nil || owner != p.principal.Device.ID {
			return store.ErrDenied
		}
		if !p.hub.sendToSubscriber(p, sessionID, recipient, frame) {
			return store.ErrDenied
		}
		return nil
	}
	permission, err := p.hub.store.SessionPermission(ctx, p.principal.UserID, p.principal.Device.ID, sessionID)
	if err != nil {
		return err
	}
	switch envelope.MessageType {
	case realtimewire.Subscribe:
		entitlement, err := p.hub.store.GetEntitlement(ctx, p.principal.UserID)
		if err != nil {
			return err
		}
		if _, err := p.hub.leases.ReserveLiveSession(ctx, p.principal.UserID, sessionID, p.principal.Device.ID, entitlement.MaxLiveSessions); err != nil {
			return store.ErrEntitlement
		}
		p.hub.subscribe(p, sessionID)
	case realtimewire.Unsubscribe:
		p.hub.unsubscribe(p, sessionID)
		_ = p.hub.leases.ReleaseLiveSession(ctx, p.principal.UserID, sessionID, p.principal.Device.ID)
	}
	if envelope.MessageType == realtimewire.InputEvent || envelope.MessageType == realtimewire.ResizeRequest {
		if permission != "CONTROL" && permission != "MANAGE" {
			return store.ErrDenied
		}
		lease, err := p.hub.leases.GetLease(ctx, sessionID)
		if err != nil || lease.HolderDeviceID != p.principal.Device.ID {
			return store.ErrDenied
		}
	}
	owner, err := p.hub.store.SessionPCDevice(ctx, p.principal.UserID, sessionID)
	if err != nil {
		return err
	}
	if recipient != owner {
		return store.ErrDenied
	}
	p.hub.sendToDevice(p, owner, frame)
	return nil
}

func (h *Hub) subscribe(p *peer, sessionID uuid.UUID) {
	h.mu.Lock()
	subscribers := h.subscriptions[sessionID]
	if subscribers == nil {
		subscribers = map[uuid.UUID]*peer{}
		h.subscriptions[sessionID] = subscribers
	}
	_, existed := subscribers[p.principal.Device.ID]
	subscribers[p.principal.Device.ID] = p
	p.subscriptions[sessionID] = true
	h.mu.Unlock()
	if !existed {
		h.observer.SubscriptionsChanged(1)
	}
}
func (h *Hub) unsubscribe(p *peer, sessionID uuid.UUID) {
	h.mu.Lock()
	delete(p.subscriptions, sessionID)
	removed := false
	if subscribers := h.subscriptions[sessionID]; subscribers != nil {
		if subscribers[p.principal.Device.ID] == p {
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
}

func (h *Hub) isCurrent(p *peer) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return h.peers[p.principal.Device.ID] == p
}
func (h *Hub) sendToSubscriber(sender *peer, sessionID, recipient uuid.UUID, frame []byte) bool {
	h.mu.RLock()
	target := h.subscriptions[sessionID][recipient]
	h.mu.RUnlock()
	if target == nil || target.principal.UserID != sender.principal.UserID {
		return false
	}
	target.enqueue(frame)
	return true
}
func (h *Hub) sendToDevice(sender *peer, deviceID uuid.UUID, frame []byte) {
	h.mu.RLock()
	target := h.peers[deviceID]
	h.mu.RUnlock()
	if target != nil && target.principal.UserID == sender.principal.UserID {
		target.enqueue(frame)
	}
}

func (p *peer) enqueue(frame []byte) {
	copyFrame := append([]byte(nil), frame...)
	select {
	case p.send <- copyFrame:
	default:
		if p.close(websocket.CloseTryAgainLater, "slow consumer; resume from cumulative ACK") {
			p.hub.observer.SlowConsumerDisconnected()
		}
	}
}
func (p *peer) writeLoop() {
	ticker := time.NewTicker(pingInterval)
	defer ticker.Stop()
	for {
		select {
		case frame := <-p.send:
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
	entitlement, err := p.hub.store.GetEntitlement(ctx, p.principal.UserID)
	if err != nil {
		return
	}
	for _, id := range sessions {
		_, _ = p.hub.leases.ReserveLiveSession(ctx, p.principal.UserID, id, p.principal.Device.ID, entitlement.MaxLiveSessions)
	}
}
func (p *peer) close(code int, reason string) bool {
	closed := false
	p.once.Do(func() {
		closed = true
		close(p.done)
		closeSocket(p.socket, code, reason)
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
