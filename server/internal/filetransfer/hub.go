package filetransfer

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"slices"
	"sync"
	"sync/atomic"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	subprotocol        = "termlink.file-transfer.v2"
	helloTimeout       = 10 * time.Second
	pingInterval       = 15 * time.Second
	pongTimeout        = 45 * time.Second
	writeTimeout       = 10 * time.Second
	peerQueueFrames    = 16
	peerQueueBytes     = 4 << 20
	peerEnqueueTimeout = 500 * time.Millisecond
	authorityInterval  = 5 * time.Second
	maxHelloBytes      = 4 << 10
)

type routeStore interface {
	FileTransferRoute(context.Context, uuid.UUID, uuid.UUID) (store.FileTransferRouteAuthorization, error)
}

type Hub struct {
	store  routeStore
	logger *slog.Logger
	mu     sync.RWMutex
	closed bool
	peers  map[routeKey]*peer
}

type routeKey struct {
	transferID uuid.UUID
	deviceID   uuid.UUID
}

type peer struct {
	hub       *Hub
	socket    *websocket.Conn
	principal auth.Principal
	route     store.FileTransferRouteAuthorization
	role      string
	send      chan []byte
	done      chan struct{}
	once      sync.Once
	queued    atomic.Int64
	lastSeq   uint64
}

type clientHello struct {
	Role             string   `json:"role"`
	TransferID       string   `json:"transfer_id"`
	ProtocolVersions []uint32 `json:"protocol_versions"`
	Capabilities     []string `json:"capabilities"`
}

type serverWelcome struct {
	Type             string   `json:"type"`
	ProtocolVersion  uint32   `json:"protocol_version"`
	HeartbeatSeconds int      `json:"heartbeat_seconds"`
	Capabilities     []string `json:"capabilities"`
}

func NewHub(repository routeStore, logger *slog.Logger) *Hub {
	if logger == nil {
		logger = slog.Default()
	}
	return &Hub{store: repository, logger: logger, peers: make(map[routeKey]*peer)}
}

func (h *Hub) Handler() http.Handler { return http.HandlerFunc(h.serveHTTP) }

func (h *Hub) Close(ctx context.Context) error {
	h.mu.Lock()
	if h.closed {
		h.mu.Unlock()
		return nil
	}
	h.closed = true
	peers := make([]*peer, 0, len(h.peers))
	for _, item := range h.peers {
		peers = append(peers, item)
	}
	h.peers = make(map[routeKey]*peer)
	h.mu.Unlock()
	for _, item := range peers {
		item.close(websocket.CloseGoingAway, "file-transfer relay shutting down")
	}
	for _, item := range peers {
		select {
		case <-item.done:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	return nil
}

func (h *Hub) serveHTTP(w http.ResponseWriter, r *http.Request) {
	principal, ok := auth.PrincipalFromContext(r.Context())
	if !ok {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if h.store == nil {
		http.Error(w, "file-transfer relay unavailable", http.StatusServiceUnavailable)
		return
	}
	if !slices.Contains(websocket.Subprotocols(r), subprotocol) {
		http.Error(w, "file-transfer subprotocol required", http.StatusUpgradeRequired)
		return
	}
	h.mu.RLock()
	closed := h.closed
	h.mu.RUnlock()
	if closed {
		http.Error(w, "file-transfer relay shutting down", http.StatusServiceUnavailable)
		return
	}
	upgrader := websocket.Upgrader{
		Subprotocols: []string{subprotocol}, ReadBufferSize: 16 << 10, WriteBufferSize: 16 << 10,
		CheckOrigin: func(r *http.Request) bool { return r.Header.Get("Origin") == "" },
	}
	socket, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	socket.SetReadLimit(MaxFrameBytes)
	if err := socket.SetReadDeadline(time.Now().Add(helloTimeout)); err != nil {
		_ = socket.Close()
		return
	}
	messageType, body, err := socket.ReadMessage()
	if err != nil || messageType != websocket.TextMessage || len(body) > maxHelloBytes {
		closeSocket(socket, websocket.CloseProtocolError, "valid ClientHello required")
		return
	}
	var hello clientHello
	if err := json.Unmarshal(body, &hello); err != nil || !validHello(hello) {
		closeSocket(socket, websocket.ClosePolicyViolation, "unsupported file-transfer hello")
		return
	}
	transferID, err := uuid.Parse(hello.TransferID)
	if err != nil || transferID == uuid.Nil || transferID.String() != hello.TransferID {
		closeSocket(socket, websocket.ClosePolicyViolation, "invalid transfer identifier")
		return
	}
	if hello.Role == "pc" && !store.IsPCAgentDeviceType(principal.Device.Type) ||
		hello.Role == "android" && principal.Device.Type != "ANDROID" {
		closeSocket(socket, websocket.ClosePolicyViolation, "role does not match device")
		return
	}
	authorityCtx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
	route, routeErr := h.store.FileTransferRoute(authorityCtx, principal.UserID, transferID)
	cancel()
	if routeErr != nil || !routeAllows(route, principal.Device.ID, hello.Role, time.Now()) {
		closeSocket(socket, websocket.ClosePolicyViolation, "file-transfer route denied")
		return
	}
	connection := &peer{
		hub: h, socket: socket, principal: principal, route: route, role: hello.Role,
		send: make(chan []byte, peerQueueFrames), done: make(chan struct{}),
	}
	if !h.add(connection) {
		closeSocket(socket, websocket.CloseTryAgainLater, "file-transfer relay shutting down")
		return
	}
	defer h.remove(connection)
	if err := socket.WriteJSON(serverWelcome{
		Type: "welcome", ProtocolVersion: uint32(ProtocolVersion),
		HeartbeatSeconds: int(pingInterval.Seconds()), Capabilities: []string{"e2ee", "resume_offset_v2"},
	}); err != nil {
		return
	}
	_ = socket.SetReadDeadline(time.Now().Add(pongTimeout))
	socket.SetPongHandler(func(string) error {
		return socket.SetReadDeadline(time.Now().Add(pongTimeout))
	})
	go connection.writeLoop()
	go connection.authorizationLoop()
	connection.readLoop()
}

func validHello(value clientHello) bool {
	return (value.Role == "pc" || value.Role == "android") && len(value.ProtocolVersions) == 1 &&
		value.ProtocolVersions[0] == uint32(ProtocolVersion) && len(value.Capabilities) == 2 &&
		slices.Contains(value.Capabilities, "e2ee") && slices.Contains(value.Capabilities, "resume_offset_v2")
}

func routeAllows(route store.FileTransferRouteAuthorization, deviceID uuid.UUID, role string, now time.Time) bool {
	if route.TransferID == uuid.Nil || route.ExpiresAt.Before(now) ||
		(route.Status != store.FileOperationPending && route.Status != store.FileOperationProcessing) {
		return false
	}
	if role == "android" {
		return deviceID == route.RequesterDevice &&
			(route.Status == store.FileOperationPending ||
				route.TransferProtocol != nil && *route.TransferProtocol == store.FileTransferRealtimeV2)
	}
	return route.Status == store.FileOperationProcessing && deviceID == route.PCDevice &&
		route.TransferProtocol != nil && *route.TransferProtocol == store.FileTransferRealtimeV2
}

func (h *Hub) add(value *peer) bool {
	key := routeKey{transferID: value.route.TransferID, deviceID: value.principal.Device.ID}
	h.mu.Lock()
	if h.closed {
		h.mu.Unlock()
		return false
	}
	previous := h.peers[key]
	h.peers[key] = value
	h.mu.Unlock()
	if previous != nil && previous != value {
		previous.close(websocket.CloseNormalClosure, "new file-transfer connection")
	}
	return true
}

func (h *Hub) remove(value *peer) {
	key := routeKey{transferID: value.route.TransferID, deviceID: value.principal.Device.ID}
	h.mu.Lock()
	if h.peers[key] == value {
		delete(h.peers, key)
	}
	h.mu.Unlock()
	value.close(websocket.CloseNormalClosure, "file-transfer connection closed")
}

func (p *peer) readLoop() {
	for {
		messageType, frame, err := p.socket.ReadMessage()
		if err != nil {
			return
		}
		if messageType != websocket.BinaryMessage {
			p.close(websocket.CloseUnsupportedData, "binary file-transfer frame required")
			return
		}
		envelope, err := ParseEnvelope(frame)
		if err != nil || !p.validEnvelope(envelope) {
			p.close(websocket.ClosePolicyViolation, "invalid file-transfer frame")
			return
		}
		p.lastSeq = envelope.Sequence
		if !p.hub.route(p, frame) {
			// An offline peer is normal. OFFER/RESUME and ACK timeouts drive
			// retransmission without retaining file data in the service.
			continue
		}
	}
}

func (p *peer) validEnvelope(value Envelope) bool {
	transferID, transferErr := uuid.FromBytes(value.TransferID)
	senderID, senderErr := uuid.FromBytes(value.SenderDeviceID)
	recipientID, recipientErr := uuid.FromBytes(value.RecipientDeviceID)
	if transferErr != nil || senderErr != nil || recipientErr != nil || transferID != p.route.TransferID ||
		senderID != p.principal.Device.ID || value.Sequence <= p.lastSeq ||
		value.KeyEpoch == 0 {
		return false
	}
	if p.role == "pc" {
		return recipientID == p.route.RequesterDevice
	}
	return recipientID == p.route.PCDevice
}

func (h *Hub) route(sender *peer, frame []byte) bool {
	recipientID := sender.route.PCDevice
	if sender.role == "pc" {
		recipientID = sender.route.RequesterDevice
	}
	h.mu.RLock()
	target := h.peers[routeKey{transferID: sender.route.TransferID, deviceID: recipientID}]
	h.mu.RUnlock()
	if target == nil || target == sender {
		return false
	}
	if target.enqueue(frame) {
		return true
	}
	target.close(websocket.CloseTryAgainLater, "file-transfer receiver is too slow")
	return false
}

func (p *peer) authorizationLoop() {
	ticker := time.NewTicker(authorityInterval)
	defer ticker.Stop()
	consecutiveFailures := 0
	for {
		select {
		case <-ticker.C:
			ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
			route, err := p.hub.store.FileTransferRoute(
				ctx,
				p.principal.UserID,
				p.route.TransferID,
			)
			cancel()
			if err != nil {
				consecutiveFailures++
				if errors.Is(err, store.ErrDenied) || consecutiveFailures >= 3 {
					p.close(websocket.ClosePolicyViolation, "file-transfer route is no longer active")
					return
				}
				continue
			}
			consecutiveFailures = 0
			if route.TransferID != p.route.TransferID ||
				route.RequesterDevice != p.route.RequesterDevice ||
				route.PCDevice != p.route.PCDevice ||
				!routeAllows(route, p.principal.Device.ID, p.role, time.Now()) {
				p.close(websocket.ClosePolicyViolation, "file-transfer route is no longer active")
				return
			}
		case <-p.done:
			return
		}
	}
}

func (p *peer) enqueue(frame []byte) bool {
	size := int64(len(frame))
	for {
		current := p.queued.Load()
		if size <= 0 || size > peerQueueBytes || current > peerQueueBytes-size ||
			!p.queued.CompareAndSwap(current, current+size) {
			if size <= 0 || size > peerQueueBytes || current > peerQueueBytes-size {
				return false
			}
			continue
		}
		break
	}
	copyOf := append([]byte(nil), frame...)
	timer := time.NewTimer(peerEnqueueTimeout)
	defer timer.Stop()
	select {
	case p.send <- copyOf:
		return true
	case <-timer.C:
		p.queued.Add(-size)
		return false
	case <-p.done:
		p.queued.Add(-size)
		return false
	}
}

func (p *peer) writeLoop() {
	ticker := time.NewTicker(pingInterval)
	defer ticker.Stop()
	for {
		select {
		case frame := <-p.send:
			p.queued.Add(-int64(len(frame)))
			_ = p.socket.SetWriteDeadline(time.Now().Add(writeTimeout))
			if err := p.socket.WriteMessage(websocket.BinaryMessage, frame); err != nil {
				p.close(websocket.CloseAbnormalClosure, "file-transfer write failed")
				return
			}
		case <-ticker.C:
			_ = p.socket.SetWriteDeadline(time.Now().Add(writeTimeout))
			if err := p.socket.WriteControl(websocket.PingMessage, nil, time.Now().Add(writeTimeout)); err != nil {
				p.close(websocket.CloseAbnormalClosure, "file-transfer ping failed")
				return
			}
		case <-p.done:
			return
		}
	}
}

func (p *peer) close(code int, reason string) {
	p.once.Do(func() {
		close(p.done)
		_ = p.socket.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(code, reason), time.Now().Add(time.Second))
		_ = p.socket.Close()
	})
}

func closeSocket(socket *websocket.Conn, code int, reason string) {
	if socket == nil {
		return
	}
	_ = socket.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(code, reason), time.Now().Add(time.Second))
	_ = socket.Close()
}
