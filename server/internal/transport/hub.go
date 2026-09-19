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
	"github.com/termlink/termlink/server/internal/presence"
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
	sharedRefreshPeriod = 10 * time.Second
	sharedOpTimeout     = 2 * time.Second
	sharedRetryMinimum  = 250 * time.Millisecond
	sharedRetryMaximum  = 5 * time.Second
	peerAddShardCount   = 64
)

type Hub struct {
	tickets      *RouteTicketService
	state        DirectStateStore
	mailbox      DirectMailbox
	mu           sync.Mutex
	peers        map[uuid.UUID]*signalPeer
	subscription DirectMailboxSubscription
	lifecycle    context.CancelFunc
	startup      context.CancelFunc
	startupDone  chan struct{}
	faultWake    chan struct{}
	wg           sync.WaitGroup
	starting     bool
	started      bool
	closed       bool
	ready        atomic.Bool
	presence     *presence.Registry
	// A shared presence registration performs network I/O before the local
	// peer map can be updated. Serialize only equal-device replacements so two
	// concurrent WebSocket handshakes cannot commit generations to shared state
	// in one order and publish them locally in the opposite order.
	peerAddShards [peerAddShardCount]sync.Mutex
}

type signalPeer struct {
	hub        *Hub
	socket     *websocket.Conn
	principal  auth.Principal
	send       chan []byte
	done       chan struct{}
	once       sync.Once
	queued     atomic.Int64
	rateMu     sync.Mutex
	windowAt   time.Time
	frames     int
	bytes      int
	presence   DirectPresence
	multipair  bool
	directData bool
	presenceID uuid.UUID
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
		mailbox:   DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()},
		faultWake: make(chan struct{}, 1),
	}, nil
}

// Start bounds initial shared-state I/O with startupCtx while all maintenance
// and recovery work remains owned by lifecycleCtx until Close. Keeping those
// contexts separate prevents a successful startup timeout from silently
// cancelling the mailbox consumer.
func (h *Hub) Start(startupCtx, lifecycleCtx context.Context) error {
	if h.state == nil {
		h.ready.Store(true)
		return nil
	}
	if startupCtx == nil || lifecycleCtx == nil {
		return errors.New("direct signalling contexts are required")
	}
	attemptCtx, attemptCancel := context.WithCancel(startupCtx)
	startupDone := make(chan struct{})
	h.mu.Lock()
	if h.closed || h.starting || h.started {
		h.mu.Unlock()
		attemptCancel()
		return errors.New("direct signalling hub already started or closed")
	}
	h.starting = true
	h.startup = attemptCancel
	h.startupDone = startupDone
	h.mu.Unlock()
	defer func() {
		attemptCancel()
		h.mu.Lock()
		h.starting = false
		h.startup = nil
		if h.startupDone == startupDone {
			close(startupDone)
			h.startupDone = nil
		}
		h.mu.Unlock()
	}()

	subscription, err := h.openSharedCycle(attemptCtx)
	if err != nil {
		return err
	}
	runCtx, lifecycleCancel := context.WithCancel(lifecycleCtx)
	h.mu.Lock()
	if h.closed || lifecycleCtx.Err() != nil {
		h.mu.Unlock()
		lifecycleCancel()
		_ = subscription.Close()
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), sharedOpTimeout)
		_, _ = h.state.RemoveMailbox(cleanupCtx, h.mailbox)
		cleanupCancel()
		return errors.New("direct signalling hub closed while starting")
	}
	h.subscription = subscription
	h.lifecycle = lifecycleCancel
	h.started = true
	h.ready.Store(true)
	h.wg.Add(1)
	h.mu.Unlock()
	go h.superviseSharedState(runCtx, subscription)
	return nil
}

func (h *Hub) openSharedCycle(ctx context.Context) (DirectMailboxSubscription, error) {
	// Subscribe before publishing mailbox availability so no signal can be
	// accepted into a mailbox which has no active consumer.
	subscription, err := h.state.SubscribeMailbox(ctx, h.mailbox)
	if err != nil {
		return nil, err
	}
	registration, err := h.state.RegisterMailbox(ctx, h.mailbox)
	if err != nil || !registration.Registered {
		_ = subscription.Close()
		if err != nil {
			return nil, err
		}
		return nil, ErrDirectStateUnavailable
	}
	return subscription, nil
}

func (h *Hub) Handler() http.Handler { return http.HandlerFunc(h.serveHTTP) }

// SetPresence attaches the process-local online registry shared with the
// authenticated realtime Relay.
func (h *Hub) SetPresence(registry *presence.Registry) {
	if h == nil {
		return
	}
	h.mu.Lock()
	h.presence = registry
	h.mu.Unlock()
}

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
		send: make(chan []byte, signalQueueFrames), done: make(chan struct{}), windowAt: time.Now(), presenceID: uuid.New(),
		multipair: slices.Contains(hello.Capabilities, "direct_transport_shadow_multipair_v2"),
	}
	peer.directData = h.tickets.DirectRoutingPermitted() &&
		slices.Contains(hello.Capabilities, "direct_transport_data_v1") &&
		slices.Contains(hello.Capabilities, "direct_session_cutover_v1")
	if !h.add(peer) {
		closeSignalSocket(socket, websocket.CloseTryAgainLater, "transport signalling shutting down")
		return
	}
	defer func() {
		h.remove(peer)
		h.mu.Lock()
		registry := h.presence
		h.mu.Unlock()
		if registry != nil {
			registry.Remove(peer.presenceID)
		}
	}()
	h.mu.Lock()
	registry := h.presence
	h.mu.Unlock()
	if registry != nil {
		registry.Register(peer.principal.UserID, peer.principal.Device.ID, peer.presenceID, peer.principal.Device.Type)
	}
	welcomeCapabilities := []string{"direct_transport_shadow_v1"}
	if peer.multipair {
		welcomeCapabilities = append(welcomeCapabilities, "direct_transport_shadow_multipair_v2")
	}
	if peer.directData {
		welcomeCapabilities = append(
			welcomeCapabilities,
			"direct_transport_data_v1",
			"direct_session_cutover_v1",
		)
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
	socket.SetPongHandler(func(string) error {
		if err := socket.SetReadDeadline(time.Now().Add(signalPongTimeout)); err != nil {
			return err
		}
		h.mu.Lock()
		registry := h.presence
		h.mu.Unlock()
		if registry != nil {
			registry.Touch(peer.presenceID)
		}
		return nil
	})
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
	shard := &h.peerAddShards[int(peer.principal.Device.ID[0])%len(h.peerAddShards)]
	shard.Lock()
	defer shard.Unlock()

	if h.state != nil {
		if !h.ready.Load() {
			return false
		}
		peer.presence = DirectPresence{
			AccountID: peer.principal.UserID, DeviceID: peer.principal.Device.ID,
			Mailbox: h.mailbox, PeerGeneration: uuid.New(), SupportsMultipairV2: peer.multipair,
			SupportsDirectDataV1: peer.directData,
		}
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		registration, err := h.state.RegisterPresence(ctx, peer.presence)
		cancel()
		if err != nil || !registration.Registered {
			h.markSharedFault()
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
	// The socket close which retires a replaced connection can race with one
	// final ReadMessage. Fence that frame against the authoritative local
	// generation before it can consume sequence state or reach the recipient.
	h.mu.Lock()
	current := h.peers[sender.principal.Device.ID] == sender
	h.mu.Unlock()
	if !current {
		return ErrTransportDenied
	}
	envelope, err := UnmarshalSignal(frame)
	if err != nil || !bytes.Equal(envelope.SenderDeviceID, sender.principal.Device.ID[:]) {
		return ErrTransportDenied
	}
	if envelope.SignalType > SignalPathClose && !sender.multipair {
		return ErrTransportDenied
	}
	if isDirectDataControlSignal(envelope.SignalType) && !sender.directData {
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
	if isDirectDataControlSignal(envelope.SignalType) {
		h.mu.Lock()
		recipient := h.peers[recipientID]
		h.mu.Unlock()
		if (h.state == nil && recipient == nil) || (recipient != nil && !recipient.directData) {
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
				h.markSharedFault()
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
		if isDirectDataControlSignal(envelope.SignalType) && !recipient.directData {
			return ErrTransportDenied
		}
		recipient.enqueue(frame)
	}
	return nil
}

type mailboxReceiveResult struct {
	delivery DirectMailboxDelivery
	err      error
}

// superviseSharedState is the sole owner of the active mailbox subscription.
// A Direct state failure immediately becomes fail-closed, but the independent
// supervisor remains alive and restores the shadow lane without touching the
// authoritative Relay.
func (h *Hub) superviseSharedState(ctx context.Context, subscription DirectMailboxSubscription) {
	defer h.wg.Done()
	retry := 0
	for {
		h.runSharedCycle(ctx, subscription)
		h.clearSharedSubscription(subscription)
		peers := h.beginSharedFault()
		fenced := h.fenceSharedMailbox(ctx) == nil
		closeSignalPeers(peers, websocket.CloseTryAgainLater, "direct signalling temporarily unavailable")
		if ctx.Err() != nil {
			return
		}

		for {
			delay := h.sharedRetryDelay(retry)
			retry++
			timer := time.NewTimer(delay)
			select {
			case <-ctx.Done():
				if !timer.Stop() {
					<-timer.C
				}
				return
			case <-timer.C:
			}

			attemptCtx, cancel := context.WithTimeout(ctx, sharedOpTimeout)
			if !fenced {
				_, fenceErr := h.state.RemoveMailbox(attemptCtx, h.mailbox)
				if fenceErr != nil {
					cancel()
					continue
				}
				fenced = true
			}
			candidate, openErr := h.openSharedCycle(attemptCtx)
			cancel()
			if openErr != nil {
				// RegisterMailbox may have committed even when its response was
				// lost. Fence again before the next subscribe/register attempt.
				fenced = false
				continue
			}
			h.mu.Lock()
			if h.closed || ctx.Err() != nil {
				h.mu.Unlock()
				_ = candidate.Close()
				return
			}
			for {
				select {
				case <-h.faultWake:
					continue
				default:
					goto faultWakeDrained
				}
			}
		faultWakeDrained:
			h.subscription = candidate
			h.ready.Store(true)
			h.mu.Unlock()
			subscription = candidate
			retry = 0
			break
		}
	}
}

func (h *Hub) runSharedCycle(ctx context.Context, subscription DirectMailboxSubscription) error {
	cycleCtx, cancel := context.WithCancel(ctx)
	received := make(chan mailboxReceiveResult, 1)
	receiverDone := make(chan struct{})
	go func() {
		defer close(receiverDone)
		for {
			delivery, err := subscription.Receive(cycleCtx)
			select {
			case received <- mailboxReceiveResult{delivery: delivery, err: err}:
			case <-cycleCtx.Done():
				return
			}
			if err != nil {
				return
			}
		}
	}()
	defer func() {
		cancel()
		_ = subscription.Close()
		<-receiverDone
	}()

	ticker := time.NewTicker(sharedRefreshPeriod)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-h.faultWake:
			return ErrDirectStateUnavailable
		case result := <-received:
			if result.err != nil {
				return result.err
			}
			h.deliverSharedSignal(result.delivery)
		case <-ticker.C:
			if err := h.refreshSharedState(ctx); err != nil {
				return err
			}
		}
	}
}

func (h *Hub) deliverSharedSignal(delivery DirectMailboxDelivery) {
	h.mu.Lock()
	peer := h.peers[delivery.RecipientID]
	if peer != nil && peer.presence.PeerGeneration != delivery.PeerGeneration {
		peer = nil
	}
	h.mu.Unlock()
	if peer != nil && (!peer.multipair || !peer.directData) {
		envelope, err := UnmarshalSignal(delivery.Frame)
		if err != nil ||
			(envelope.SignalType > SignalPathClose && !peer.multipair) ||
			(isDirectDataControlSignal(envelope.SignalType) && !peer.directData) {
			peer = nil
		}
	}
	if peer != nil {
		peer.enqueue(delivery.Frame)
	}
}

func isDirectDataControlSignal(signalType SignalType) bool {
	return signalType >= SignalCutoverProposal && signalType <= SignalFallback
}

func (h *Hub) refreshSharedState(ctx context.Context) error {
	return h.refreshSharedStateWithin(ctx, sharedOpTimeout)
}

func (h *Hub) refreshSharedStateWithin(ctx context.Context, timeout time.Duration) error {
	refreshCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	mailboxOK, err := h.state.RefreshMailbox(refreshCtx, h.mailbox)
	if err != nil {
		return err
	}
	if !mailboxOK {
		return ErrDirectStateUnavailable
	}

	h.mu.Lock()
	peers := make([]*signalPeer, 0, len(h.peers))
	for _, peer := range h.peers {
		peers = append(peers, peer)
	}
	h.mu.Unlock()
	for _, peer := range peers {
		ok, refreshErr := h.state.RefreshPresence(refreshCtx, peer.presence)
		h.mu.Lock()
		current := h.peers[peer.principal.Device.ID] == peer
		h.mu.Unlock()
		if !current {
			continue
		}
		if refreshErr != nil {
			return refreshErr
		}
		if !ok {
			mailboxOK, mailboxErr := h.state.RefreshMailbox(refreshCtx, h.mailbox)
			if mailboxErr != nil {
				return mailboxErr
			}
			if !mailboxOK {
				return ErrDirectStateUnavailable
			}
			// A generation mismatch is a device-level ownership fence, not a
			// shared-store outage, but RefreshPresence also returns false when
			// the mailbox fence is missing. Rechecking the mailbox above keeps
			// those two outcomes distinct.
			peer.close(websocket.CloseTryAgainLater, "transport presence replaced")
		}
	}
	if err := refreshCtx.Err(); err != nil {
		return err
	}
	return nil
}

func (h *Hub) markSharedFault() {
	peers := h.beginSharedFault()
	closeSignalPeers(peers, websocket.CloseTryAgainLater, "direct signalling temporarily unavailable")
}

func (h *Hub) beginSharedFault() []*signalPeer {
	h.mu.Lock()
	if h.closed || !h.ready.Swap(false) {
		h.mu.Unlock()
		return nil
	}
	peers := make([]*signalPeer, 0, len(h.peers))
	for _, peer := range h.peers {
		peers = append(peers, peer)
	}
	select {
	case h.faultWake <- struct{}{}:
	default:
	}
	h.mu.Unlock()
	return peers
}

func (h *Hub) fenceSharedMailbox(ctx context.Context) error {
	if ctx.Err() != nil {
		ctx = context.Background()
	}
	fenceCtx, cancel := context.WithTimeout(ctx, sharedOpTimeout)
	defer cancel()
	_, err := h.state.RemoveMailbox(fenceCtx, h.mailbox)
	return err
}

func (h *Hub) clearSharedSubscription(subscription DirectMailboxSubscription) {
	h.mu.Lock()
	if h.subscription == subscription {
		h.subscription = nil
	}
	h.mu.Unlock()
}

func (h *Hub) sharedRetryDelay(attempt int) time.Duration {
	shift := attempt
	if shift > 4 {
		shift = 4
	}
	delay := sharedRetryMinimum << shift
	if delay > sharedRetryMaximum {
		delay = sharedRetryMaximum
	}
	// A stable per-instance offset prevents replicas from reconnecting in
	// lockstep while keeping unit tests deterministic.
	jitter := time.Duration(h.mailbox.InstanceID[0]) * time.Millisecond
	if delay+jitter > sharedRetryMaximum {
		return sharedRetryMaximum
	}
	return delay + jitter
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
	startupCancel, lifecycleCancel := h.startup, h.lifecycle
	startupDone := h.startupDone
	h.mu.Unlock()
	if startupCancel != nil {
		startupCancel()
	}
	if lifecycleCancel != nil {
		lifecycleCancel()
	}
	closeSignalPeers(peers, websocket.CloseGoingAway, "transport signalling shutting down")
	if startupDone != nil {
		select {
		case <-startupDone:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	wait := make(chan struct{})
	go func() { h.wg.Wait(); close(wait) }()
	select {
	case <-wait:
	case <-ctx.Done():
		return ctx.Err()
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
		removeCtx, cancel := context.WithTimeout(ctx, sharedOpTimeout)
		_, _ = h.state.RemoveMailbox(removeCtx, h.mailbox)
		cancel()
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	return nil
}

func closeSignalPeers(peers []*signalPeer, code int, reason string) {
	var wait sync.WaitGroup
	wait.Add(len(peers))
	for _, peer := range peers {
		go func(peer *signalPeer) {
			defer wait.Done()
			peer.close(code, reason)
		}(peer)
	}
	wait.Wait()
}

func closeSignalSocket(socket *websocket.Conn, code int, reason string) {
	if socket == nil {
		return
	}
	_ = socket.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(code, reason), time.Now().Add(time.Second))
	_ = socket.Close()
}
