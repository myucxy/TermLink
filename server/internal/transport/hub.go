package transport

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
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
	signalSubprotocol   = "termlink.transport.v1"
	maxSignalHelloBytes = 4 << 10
	signalHelloTimeout  = 5 * time.Second
	signalPongTimeout   = 45 * time.Second
	signalPingInterval  = 15 * time.Second
	signalQueueFrames   = 32
	signalQueueBytes    = 512 << 10
	peerWindow          = 10 * time.Second
	peerWindowFrames    = 96
	peerWindowBytes     = 2 << 20
)

type Hub struct {
	tickets      *RouteTicketService
	state        DirectStateStore
	mailbox      DirectMailbox
	mu           sync.Mutex
	peers        map[uuid.UUID]*signalPeer
	subscription DirectMailboxSubscription
	cancel       context.CancelFunc
	wg           sync.WaitGroup
	closed       bool
	ready        atomic.Bool
	degraded     atomic.Bool
}

type signalPeer struct {
	hub       *Hub
	socket    *websocket.Conn
	principal auth.Principal
	send      chan []byte
	done      chan struct{}
	once      sync.Once
	queued    atomic.Int64
	rateMu    sync.Mutex
	windowAt  time.Time
	frames    int
	bytes     int
	presence  DirectPresence
	multipair bool
}

type signalClientHello struct {
	Type             string   `json:"type"`
	Role             string   `json:"role"`
	ProtocolVersions []uint32 `json:"protocol_versions"`
	Capabilities     []string `json:"capabilities"`
}

func NewHub(tickets *RouteTicketService) *Hub {
	hub := &Hub{tickets: tickets, peers: map[uuid.UUID]*signalPeer{}}
	hub.ready.Store(true)
	return hub
}

// NewSharedHub constructs the production multi-replica signalling lane. Start
// must succeed before routes are registered; construction itself performs no
// network I/O and cannot affect the realtime Relay.
func NewSharedHub(tickets *RouteTicketService, state DirectStateStore) (*Hub, error) {
	if tickets == nil || state == nil {
		return nil, errors.New("direct signalling shared dependencies unavailable")
	}
	return &Hub{
		tickets: tickets, state: state, peers: map[uuid.UUID]*signalPeer{},
		mailbox: DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()},
	}, nil
}

func (h *Hub) Start(ctx context.Context) error {
	if h.state == nil {
		h.ready.Store(true)
		return nil
	}
	h.mu.Lock()
	if h.closed || h.subscription != nil {
		h.mu.Unlock()
		return errors.New("direct signalling hub already started or closed")
	}
	h.mu.Unlock()
	subscription, err := h.state.SubscribeMailbox(ctx, h.mailbox)
	if err != nil {
		return err
	}
	registration, err := h.state.RegisterMailbox(ctx, h.mailbox)
	if err != nil || !registration.Registered {
		_ = subscription.Close()
		if err != nil {
			return err
		}
		return ErrDirectStateUnavailable
	}
	runCtx, cancel := context.WithCancel(ctx)
	h.mu.Lock()
	if h.closed {
		h.mu.Unlock()
		cancel()
		_ = subscription.Close()
		_, _ = h.state.RemoveMailbox(context.Background(), h.mailbox)
		return errors.New("direct signalling hub closed while starting")
	}
	h.subscription, h.cancel = subscription, cancel
	h.mu.Unlock()
	h.ready.Store(true)
	h.wg.Add(2)
	go h.mailboxLoop(runCtx)
	go h.refreshLoop(runCtx)
	return nil
}

func (h *Hub) Handler() http.Handler { return http.HandlerFunc(h.serveHTTP) }

func (h *Hub) serveHTTP(w http.ResponseWriter, r *http.Request) {
	principal, ok := signalPrincipalFromContext(r.Context())
	if !ok || h.tickets == nil {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodGet || !slices.Contains(websocket.Subprotocols(r), signalSubprotocol) {
		http.Error(w, "transport subprotocol required", http.StatusUpgradeRequired)
		return
	}
	h.mu.Lock()
	closed := h.closed
	h.mu.Unlock()
	if closed || !h.ready.Load() {
		http.Error(w, "transport signalling shutting down", http.StatusServiceUnavailable)
		return
	}
	upgrader := websocket.Upgrader{
		Subprotocols: []string{signalSubprotocol}, ReadBufferSize: 8 << 10, WriteBufferSize: 8 << 10,
		CheckOrigin: func(request *http.Request) bool { return request.Header.Get("Origin") == "" },
	}
	socket, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	socket.SetReadLimit(MaxSignalBytes)
	_ = socket.SetReadDeadline(time.Now().Add(signalHelloTimeout))
	messageType, helloBytes, err := socket.ReadMessage()
	if err != nil || messageType != websocket.TextMessage || len(helloBytes) > maxSignalHelloBytes {
		closeSignalSocket(socket, websocket.CloseProtocolError, "valid transport hello required")
		return
	}
	var hello signalClientHello
	if json.Unmarshal(helloBytes, &hello) != nil || !validSignalHello(hello, principal.Device.Type) {
		closeSignalSocket(socket, websocket.ClosePolicyViolation, "unsupported transport client")
		return
	}
	peer := &signalPeer{
		hub: h, socket: socket, principal: principal,
		send: make(chan []byte, signalQueueFrames), done: make(chan struct{}), windowAt: time.Now(),
		multipair: slices.Contains(hello.Capabilities, "direct_transport_shadow_multipair_v2"),
	}
	if !h.add(peer) {
		closeSignalSocket(socket, websocket.CloseTryAgainLater, "transport signalling shutting down")
		return
	}
	defer h.remove(peer)
	welcomeCapabilities := []string{"direct_transport_shadow_v1"}
	if peer.multipair {
		welcomeCapabilities = append(welcomeCapabilities, "direct_transport_shadow_multipair_v2")
	}
	welcome := map[string]any{
		"type": "SERVER_WELCOME", "protocol_version": 1,
		"capabilities":          welcomeCapabilities,
		"heartbeat_interval_ms": signalPingInterval.Milliseconds(), "max_signal_frame_bytes": MaxSignalBytes,
	}
	if err := socket.WriteJSON(welcome); err != nil {
		return
	}
	_ = socket.SetReadDeadline(time.Now().Add(signalPongTimeout))
	socket.SetPongHandler(func(string) error { return socket.SetReadDeadline(time.Now().Add(signalPongTimeout)) })
	go peer.writeLoop()
	peer.readLoop()
}

func validSignalHello(hello signalClientHello, deviceType string) bool {
	if hello.Type != "CLIENT_HELLO" || !slices.Contains(hello.ProtocolVersions, uint32(1)) || !slices.Contains(hello.Capabilities, "direct_transport_v1") {
		return false
	}
	return hello.Role == "android" && deviceType == "ANDROID" || hello.Role == "pc" && store.IsPCAgentDeviceType(deviceType)
}

func (h *Hub) add(peer *signalPeer) bool {
	if h.state != nil {
		if !h.ready.Load() {
			return false
		}
		peer.presence = DirectPresence{
			AccountID: peer.principal.UserID, DeviceID: peer.principal.Device.ID,
			Mailbox: h.mailbox, PeerGeneration: uuid.New(), SupportsMultipairV2: peer.multipair,
		}
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		registration, err := h.state.RegisterPresence(ctx, peer.presence)
		cancel()
		if err != nil || !registration.Registered {
			if err != nil {
				h.degrade()
			}
			return false
		}
	}
	h.mu.Lock()
	if h.closed || !h.ready.Load() {
		h.mu.Unlock()
		if h.state != nil {
			ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			_, _ = h.state.RemovePresence(ctx, peer.presence)
			cancel()
		}
		return false
	}
	old := h.peers[peer.principal.Device.ID]
	h.peers[peer.principal.Device.ID] = peer
	h.mu.Unlock()
	// A close control frame may block until its write deadline. Never hold the
	// hub routing lock across socket I/O; the old peer's deferred remove is
	// identity-fenced and cannot delete this replacement.
	if old != nil {
		old.close(websocket.CloseNormalClosure, "transport connection replaced")
	}
	return true
}

func (h *Hub) remove(peer *signalPeer) {
	peer.close(websocket.CloseNormalClosure, "transport connection closed")
	h.mu.Lock()
	if h.peers[peer.principal.Device.ID] == peer {
		delete(h.peers, peer.principal.Device.ID)
	}
	h.mu.Unlock()
	if h.state != nil && peer.presence.PeerGeneration != uuid.Nil {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		_, _ = h.state.RemovePresence(ctx, peer.presence)
		cancel()
	}
}

func (p *signalPeer) readLoop() {
	defer p.close(websocket.CloseNormalClosure, "transport reader stopped")
	for {
		messageType, frame, err := p.socket.ReadMessage()
		if err != nil {
			return
		}
		if messageType != websocket.BinaryMessage || !p.allowFrame(len(frame)) {
			p.close(websocket.ClosePolicyViolation, "transport frame rejected")
			return
		}
		if err := p.hub.route(p, frame); err != nil {
			p.close(websocket.ClosePolicyViolation, "transport route rejected")
			return
		}
	}
}

func (h *Hub) route(sender *signalPeer, frame []byte) error {
	envelope, err := UnmarshalSignal(frame)
	if err != nil || !bytes.Equal(envelope.SenderDeviceID, sender.principal.Device.ID[:]) {
		return ErrTransportDenied
	}
	if envelope.SignalType > SignalPathClose && !sender.multipair {
		return ErrTransportDenied
	}
	recipientID, err := uuid.FromBytes(envelope.RecipientDeviceID)
	if err != nil {
		return ErrTransportDenied
	}
	if envelope.SignalType > SignalPathClose {
		h.mu.Lock()
		recipient := h.peers[recipientID]
		h.mu.Unlock()
		if recipient != nil && !recipient.multipair {
			return ErrTransportDenied
		}
	}
	authorityContext, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if h.state != nil {
		if !h.ready.Load() {
			return ErrDirectStateUnavailable
		}
		if err := h.tickets.AuthorizeAndPublishSignal(authorityContext, sender.principal, sender.presence, envelope, frame); err != nil {
			if errors.Is(err, ErrDirectStateUnavailable) {
				h.degrade()
			}
			return err
		}
		return nil
	}
	if err := h.tickets.AuthorizeSignal(authorityContext, sender.principal, envelope); err != nil {
		return err
	}
	h.mu.Lock()
	recipient := h.peers[recipientID]
	h.mu.Unlock()
	if recipient != nil {
		if envelope.SignalType > SignalPathClose && !recipient.multipair {
			return ErrTransportDenied
		}
		recipient.enqueue(frame)
	}
	return nil
}

func (h *Hub) mailboxLoop(ctx context.Context) {
	defer h.wg.Done()
	for {
		h.mu.Lock()
		subscription := h.subscription
		h.mu.Unlock()
		if subscription == nil {
			return
		}
		delivery, err := subscription.Receive(ctx)
		if err != nil {
			if ctx.Err() == nil {
				h.degrade()
			}
			return
		}
		h.mu.Lock()
		peer := h.peers[delivery.RecipientID]
		if peer != nil && peer.presence.PeerGeneration != delivery.PeerGeneration {
			peer = nil
		}
		h.mu.Unlock()
		if peer != nil && !peer.multipair {
			envelope, decodeErr := UnmarshalSignal(delivery.Frame)
			if decodeErr != nil || envelope.SignalType > SignalPathClose {
				peer = nil
			}
		}
		if peer != nil {
			peer.enqueue(delivery.Frame)
		}
	}
}

func (h *Hub) refreshLoop(ctx context.Context) {
	defer h.wg.Done()
	ticker := time.NewTicker(10 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ticker.C:
			refreshCtx, cancel := context.WithTimeout(ctx, 2*time.Second)
			mailboxOK, err := h.state.RefreshMailbox(refreshCtx, h.mailbox)
			if err == nil && mailboxOK {
				h.mu.Lock()
				presences := make([]DirectPresence, 0, len(h.peers))
				for _, peer := range h.peers {
					presences = append(presences, peer.presence)
				}
				h.mu.Unlock()
				for _, presence := range presences {
					ok, refreshErr := h.state.RefreshPresence(refreshCtx, presence)
					if refreshErr != nil || !ok {
						err = ErrDirectStateUnavailable
						break
					}
				}
			} else if err == nil {
				err = ErrDirectStateUnavailable
			}
			cancel()
			if err != nil {
				h.degrade()
				return
			}
		case <-ctx.Done():
			return
		}
	}
}

func (h *Hub) degrade() {
	if !h.degraded.CompareAndSwap(false, true) {
		return
	}
	h.ready.Store(false)
	h.mu.Lock()
	peers := make([]*signalPeer, 0, len(h.peers))
	for _, peer := range h.peers {
		peers = append(peers, peer)
	}
	subscription, cancel := h.subscription, h.cancel
	h.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	if subscription != nil {
		_ = subscription.Close()
	}
	for _, peer := range peers {
		peer.close(websocket.CloseTryAgainLater, "direct signalling temporarily unavailable")
	}
}

func (p *signalPeer) allowFrame(size int) bool {
	if size <= 0 || size > MaxSignalBytes {
		return false
	}
	p.rateMu.Lock()
	defer p.rateMu.Unlock()
	now := time.Now()
	if now.Sub(p.windowAt) >= peerWindow {
		p.windowAt, p.frames, p.bytes = now, 0, 0
	}
	p.frames++
	p.bytes += size
	return p.frames <= peerWindowFrames && p.bytes <= peerWindowBytes
}

func (p *signalPeer) enqueue(frame []byte) {
	copyFrame := append([]byte(nil), frame...)
	if p.queued.Add(int64(len(copyFrame))) > signalQueueBytes {
		p.queued.Add(-int64(len(copyFrame)))
		p.close(websocket.CloseTryAgainLater, "slow transport signalling consumer")
		return
	}
	select {
	case p.send <- copyFrame:
	case <-p.done:
		p.queued.Add(-int64(len(copyFrame)))
	default:
		p.queued.Add(-int64(len(copyFrame)))
		p.close(websocket.CloseTryAgainLater, "slow transport signalling consumer")
	}
}

func (p *signalPeer) writeLoop() {
	ticker := time.NewTicker(signalPingInterval)
	defer ticker.Stop()
	for {
		select {
		case frame := <-p.send:
			p.queued.Add(-int64(len(frame)))
			_ = p.socket.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := p.socket.WriteMessage(websocket.BinaryMessage, frame); err != nil {
				p.close(websocket.CloseAbnormalClosure, "transport write failed")
				return
			}
		case <-ticker.C:
			if err := p.socket.WriteControl(websocket.PingMessage, nil, time.Now().Add(10*time.Second)); err != nil {
				p.close(websocket.CloseAbnormalClosure, "transport ping failed")
				return
			}
		case <-p.done:
			return
		}
	}
}

func (p *signalPeer) close(code int, reason string) {
	p.once.Do(func() {
		close(p.done)
		closeSignalSocket(p.socket, code, reason)
	})
}

func (h *Hub) Close(ctx context.Context) error {
	h.mu.Lock()
	h.closed = true
	h.ready.Store(false)
	peers := make([]*signalPeer, 0, len(h.peers))
	for _, peer := range h.peers {
		peers = append(peers, peer)
	}
	subscription, cancel := h.subscription, h.cancel
	h.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	if subscription != nil {
		_ = subscription.Close()
	}
	for _, peer := range peers {
		peer.close(websocket.CloseGoingAway, "transport signalling shutting down")
	}
	for {
		h.mu.Lock()
		remaining := len(h.peers)
		h.mu.Unlock()
		if remaining == 0 {
			break
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(10 * time.Millisecond):
		}
	}
	if h.state != nil {
		removeCtx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		_, _ = h.state.RemoveMailbox(removeCtx, h.mailbox)
		cancel()
	}
	wait := make(chan struct{})
	go func() { h.wg.Wait(); close(wait) }()
	select {
	case <-wait:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func closeSignalSocket(socket *websocket.Conn, code int, reason string) {
	if socket == nil {
		return
	}
	_ = socket.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(code, reason), time.Now().Add(time.Second))
	_ = socket.Close()
}
