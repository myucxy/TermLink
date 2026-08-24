package e2eserver

import (
	"bytes"
	"crypto/sha256"
	"encoding/json"
	"net/http"
	"slices"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/myucxy/TermLink/technical-validation/e2e/internal/realtimewire"
)

const (
	realtimeSubprotocol = "termlink.realtime.v1"
	maxHelloBytes       = 16 << 10
	helloTimeout        = 5 * time.Second
	pongTimeout         = 45 * time.Second
	pingInterval        = 15 * time.Second
	peerQueueFrames     = 128
	peerQueueBytes      = 8 << 20
	peerEnqueueTimeout  = 500 * time.Millisecond
	maxReconnectHold    = 30 * time.Second
)

var requiredCapabilities = []string{
	"cumulative_ack", "e2ee_x25519_chacha20poly1305", "flow_control_v1", "lease_v1",
}

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
	server   *Server
	socket   *websocket.Conn
	deviceID uuid.UUID
	role     string
	send     chan []byte
	done     chan struct{}
	once     sync.Once

	mu            sync.Mutex
	lastSequences map[uuid.UUID]uint64
	pcLast        map[pcRouteKey]uint64
	pcReplay      map[pcReplayKey][32]byte
	pcReplayOrder []pcReplayKey
	subscriptions map[uuid.UUID]bool
	queuedBytes   atomic.Int64
}

type pcRouteKey struct {
	sessionID   uuid.UUID
	recipientID uuid.UUID
}
type pcReplayKey struct {
	sessionID   uuid.UUID
	recipientID uuid.UUID
	sequence    uint64
}

func (s *Server) disconnectAndroidRealtime(w http.ResponseWriter, r *http.Request) {
	holdMillis := int64(0)
	if values, present := r.URL.Query()["hold_ms"]; present {
		if len(values) != 1 {
			writeError(w, r, http.StatusBadRequest, "INVALID_HOLD_MS")
			return
		}
		parsed, err := strconv.ParseInt(values[0], 10, 64)
		if err != nil || parsed < 0 || parsed > maxReconnectHold.Milliseconds() {
			writeError(w, r, http.StatusBadRequest, "INVALID_HOLD_MS")
			return
		}
		holdMillis = parsed
	}

	now := s.now().UTC()
	blockedUntil := now.Add(time.Duration(holdMillis) * time.Millisecond)
	s.mu.Lock()
	// Set the block before taking the peer snapshot. Registration performs its
	// final block check while holding the same mutex, so an Android peer is
	// either included below or rejected before it enters the peer catalog.
	s.androidReconnectBlockedUntil = blockedUntil
	peers := make([]*peer, 0)
	for _, devicePeers := range s.peers {
		for p := range devicePeers {
			if p.role == "android" {
				peers = append(peers, p)
			}
		}
	}
	s.mu.Unlock()

	// websocket.Close can block on the network stack. Never perform it while
	// holding the Server mutex used by routing and catalog requests.
	for _, p := range peers {
		p.close()
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"disconnected_android_peers": len(peers),
		"hold_ms":                    holdMillis,
	})
}

func (s *Server) serveRealtime(w http.ResponseWriter, r *http.Request) {
	if !slices.Contains(websocket.Subprotocols(r), realtimeSubprotocol) {
		http.Error(w, "realtime subprotocol required", http.StatusUpgradeRequired)
		return
	}
	device, ok := s.currentDevice(r)
	if !ok {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if device.Type == "ANDROID" {
		now := s.now().UTC()
		s.mu.RLock()
		blockedUntil := s.androidReconnectBlockedUntil
		blocked := now.Before(blockedUntil)
		s.mu.RUnlock()
		if blocked {
			retryAfter := int((blockedUntil.Sub(now) + time.Second - 1) / time.Second)
			w.Header().Set("Retry-After", strconv.Itoa(retryAfter))
			writeError(w, r, http.StatusServiceUnavailable, "ANDROID_REALTIME_BLOCKED")
			return
		}
	}
	upgrader := websocket.Upgrader{
		Subprotocols: []string{realtimeSubprotocol}, ReadBufferSize: 16 << 10, WriteBufferSize: 16 << 10,
		CheckOrigin: func(req *http.Request) bool { return req.Header.Get("Origin") == "" },
	}
	socket, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	socket.SetReadLimit(realtimewire.MaxFrameBytes)
	_ = socket.SetReadDeadline(time.Now().Add(helloTimeout))
	messageType, data, err := socket.ReadMessage()
	if err != nil || messageType != websocket.TextMessage || len(data) > maxHelloBytes {
		_ = socket.Close()
		return
	}
	var hello clientHello
	if err := json.Unmarshal(data, &hello); err != nil || !validHello(hello) {
		_ = socket.Close()
		return
	}
	if hello.Role == "pc" && device.Type != "WINDOWS_PC" || hello.Role == "android" && device.Type != "ANDROID" {
		_ = socket.Close()
		return
	}
	p := &peer{
		server: s, socket: socket, deviceID: device.ID, role: hello.Role,
		send: make(chan []byte, peerQueueFrames), done: make(chan struct{}),
		lastSequences: map[uuid.UUID]uint64{}, pcLast: map[pcRouteKey]uint64{},
		pcReplay: map[pcReplayKey][32]byte{}, subscriptions: map[uuid.UUID]bool{},
	}
	s.mu.Lock()
	if p.role == "android" && s.now().UTC().Before(s.androidReconnectBlockedUntil) {
		s.mu.Unlock()
		p.close()
		return
	}
	if s.peers[p.deviceID] == nil {
		s.peers[p.deviceID] = map[*peer]struct{}{}
	}
	s.peers[p.deviceID][p] = struct{}{}
	s.mu.Unlock()
	defer s.removePeer(p)

	_ = socket.SetWriteDeadline(time.Now().Add(10 * time.Second))
	if err := socket.WriteJSON(serverWelcome{
		Type: "welcome", ProtocolVersion: realtimewire.ProtocolVersion,
		HeartbeatSeconds: int(pingInterval.Seconds()), ConnectionID: uuid.NewString(),
	}); err != nil {
		p.close()
		return
	}
	_ = socket.SetReadDeadline(time.Now().Add(pongTimeout))
	socket.SetPongHandler(func(string) error {
		_ = socket.SetReadDeadline(time.Now().Add(pongTimeout))
		p.touchPresence()
		return nil
	})
	go p.writeLoop()
	p.readLoop()
}

func validHello(hello clientHello) bool {
	if hello.Role != "pc" && hello.Role != "android" || !slices.Contains(hello.ProtocolVersions, realtimewire.ProtocolVersion) {
		return false
	}
	for _, required := range requiredCapabilities {
		if !slices.Contains(hello.Capabilities, required) {
			return false
		}
	}
	return true
}

func (s *Server) removePeer(p *peer) {
	p.close()
	s.mu.Lock()
	if devicePeers := s.peers[p.deviceID]; devicePeers != nil {
		delete(devicePeers, p)
		if len(devicePeers) == 0 {
			delete(s.peers, p.deviceID)
		}
	}
	for sessionID := range p.subscriptions {
		if subscribers := s.subscriptions[sessionID]; subscribers != nil && subscribers[p.deviceID] == p {
			delete(subscribers, p.deviceID)
			if len(subscribers) == 0 {
				delete(s.subscriptions, sessionID)
			}
		}
	}
	s.mu.Unlock()
}

func (p *peer) close() {
	p.once.Do(func() {
		close(p.done)
		_ = p.socket.Close()
	})
}

func (p *peer) readLoop() {
	defer p.close()
	for {
		messageType, frame, err := p.socket.ReadMessage()
		if err != nil {
			return
		}
		if messageType != websocket.BinaryMessage {
			p.close()
			return
		}
		envelope, err := realtimewire.Unmarshal(frame)
		if err != nil || !bytes.Equal(envelope.SenderDeviceID, p.deviceID[:]) ||
			(p.role == "pc" && envelope.Direction != realtimewire.PCToAndroid) ||
			(p.role == "android" && envelope.Direction != realtimewire.AndroidToPC) ||
			!allowedMessage(p.role, envelope.MessageType) || !validEnvelopeACK(envelope) {
			p.close()
			return
		}
		sessionID, err := uuid.FromBytes(envelope.SessionID)
		if err != nil {
			p.close()
			return
		}
		recipient, err := uuid.FromBytes(envelope.RecipientDeviceID)
		if err != nil || recipient == p.deviceID {
			p.close()
			return
		}
		if !p.acceptSequence(sessionID, recipient, envelope.Sequence, frame) {
			p.close()
			return
		}
		if !p.route(envelope, sessionID, recipient, frame) {
			p.close()
			return
		}
	}
}

func allowedMessage(role string, messageType realtimewire.MessageType) bool {
	if role == "android" {
		switch messageType {
		case realtimewire.Heartbeat, realtimewire.Goodbye, realtimewire.Subscribe, realtimewire.Unsubscribe,
			realtimewire.OutputACK, realtimewire.FlowControl, realtimewire.InputEvent, realtimewire.ResizeRequest,
			realtimewire.ResumeRequest, realtimewire.LeaseRequest:
			return true
		}
	}
	if role == "pc" {
		switch messageType {
		case realtimewire.Heartbeat, realtimewire.Goodbye, realtimewire.SessionUpsert, realtimewire.SessionRemoved,
			realtimewire.OutputChunk, realtimewire.Gap, realtimewire.InputAccepted, realtimewire.InputRejected,
			realtimewire.ResizeApplied, realtimewire.SnapshotOffer, realtimewire.SnapshotChunk, realtimewire.LeaseGranted,
			realtimewire.LeaseDenied, realtimewire.LeaseRevoked, realtimewire.ThemeChanged, realtimewire.SessionStarted,
			realtimewire.SessionInterrupted, realtimewire.SessionEnded:
			return true
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

func (p *peer) acceptSequence(sessionID, recipientID uuid.UUID, sequence uint64, frame []byte) bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.role == "android" {
		last := p.lastSequences[sessionID]
		if sequence <= last {
			return false
		}
		p.lastSequences[sessionID] = sequence
		return true
	}
	route := pcRouteKey{sessionID: sessionID, recipientID: recipientID}
	last := p.pcLast[route]
	digest := sha256.Sum256(frame)
	key := pcReplayKey{sessionID: sessionID, recipientID: recipientID, sequence: sequence}
	if sequence <= last {
		previous, ok := p.pcReplay[key]
		return ok && bytes.Equal(previous[:], digest[:])
	}
	p.pcLast[route] = sequence
	p.pcReplay[key] = digest
	p.pcReplayOrder = append(p.pcReplayOrder, key)
	for len(p.pcReplayOrder) > 4096 {
		delete(p.pcReplay, p.pcReplayOrder[0])
		p.pcReplayOrder = p.pcReplayOrder[1:]
	}
	return true
}

func (p *peer) route(envelope realtimewire.Envelope, sessionID, recipient uuid.UUID, frame []byte) bool {
	s := p.server
	s.mu.RLock()
	session, sessionExists := s.sessions[sessionID]
	recipientDevice, recipientExists := s.devices[recipient]
	pc, pcExists := s.pcs[session.PCID]
	s.mu.RUnlock()
	if !sessionExists || !pcExists || !recipientExists || recipientDevice.ID == p.deviceID {
		return false
	}
	if p.role == "pc" {
		if pc.DeviceID != p.deviceID || recipientDevice.Type != "ANDROID" {
			return false
		}
		s.mu.RLock()
		target := s.subscriptions[sessionID][recipient]
		s.mu.RUnlock()
		if target == nil {
			// A subscriber may disappear between the Agent's snapshot and its
			// output frame. Dropping that opaque frame is idempotent.
			return true
		}
		target.enqueue(frame)
		return true
	}
	if recipient != pc.DeviceID || recipientDevice.Type != "WINDOWS_PC" {
		return false
	}
	if envelope.MessageType == realtimewire.Unsubscribe {
		s.mu.Lock()
		wasSubscribed := s.subscriptions[sessionID][p.deviceID] == p
		if wasSubscribed {
			delete(s.subscriptions[sessionID], p.deviceID)
			delete(p.subscriptions, sessionID)
			if len(s.subscriptions[sessionID]) == 0 {
				delete(s.subscriptions, sessionID)
			}
		}
		s.mu.Unlock()
		if wasSubscribed {
			s.sendToDevice(recipient, frame)
		}
		return true
	}
	if envelope.MessageType == realtimewire.Subscribe {
		s.mu.Lock()
		if s.subscriptions[sessionID] == nil {
			s.subscriptions[sessionID] = map[uuid.UUID]*peer{}
		}
		s.subscriptions[sessionID][p.deviceID] = p
		p.subscriptions[sessionID] = true
		s.mu.Unlock()
		s.sendToDevice(recipient, frame)
		return true
	}
	if envelope.MessageType == realtimewire.OutputACK || envelope.MessageType == realtimewire.FlowControl || envelope.MessageType == realtimewire.ResumeRequest {
		s.mu.RLock()
		subscribed := s.subscriptions[sessionID][p.deviceID] == p
		s.mu.RUnlock()
		if !subscribed {
			return false
		}
	}
	if envelope.MessageType == realtimewire.InputEvent || envelope.MessageType == realtimewire.ResizeRequest {
		s.mu.Lock()
		lease, active := s.activeLeaseLocked(sessionID, s.now().UTC())
		s.mu.Unlock()
		if !active || lease.HolderDeviceID != p.deviceID {
			return false
		}
	}
	s.sendToDevice(recipient, frame)
	return true
}

func (s *Server) sendToDevice(deviceID uuid.UUID, frame []byte) {
	s.mu.RLock()
	targets := make([]*peer, 0, len(s.peers[deviceID]))
	for target := range s.peers[deviceID] {
		targets = append(targets, target)
	}
	s.mu.RUnlock()
	for _, target := range targets {
		target.enqueue(frame)
	}
}

func (p *peer) enqueue(frame []byte) {
	copyFrame := append([]byte(nil), frame...)
	if len(copyFrame) == 0 || len(copyFrame) > peerQueueBytes || !p.reserveBytes(int64(len(copyFrame))) {
		p.close()
		return
	}
	timer := time.NewTimer(peerEnqueueTimeout)
	defer timer.Stop()
	select {
	case p.send <- copyFrame:
	case <-p.done:
		p.releaseBytes(int64(len(copyFrame)))
	case <-timer.C:
		p.releaseBytes(int64(len(copyFrame)))
		p.close()
	}
}

func (p *peer) reserveBytes(size int64) bool {
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

func (p *peer) releaseBytes(size int64) {
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
			if len(frame) == 0 {
				continue
			}
			p.releaseBytes(int64(len(frame)))
			_ = p.socket.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := p.socket.WriteMessage(websocket.BinaryMessage, frame); err != nil {
				p.close()
				return
			}
		case <-ticker.C:
			_ = p.socket.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := p.socket.WriteControl(websocket.PingMessage, nil, time.Now().Add(10*time.Second)); err != nil {
				p.close()
				return
			}
		case <-p.done:
			return
		}
	}
}

func (p *peer) touchPresence() {
	now := p.server.now().UTC()
	p.server.mu.Lock()
	if p.role == "pc" {
		for id, pc := range p.server.pcs {
			if pc.DeviceID == p.deviceID {
				copyTime := now
				pc.LastSeenAt = &copyTime
				p.server.pcs[id] = pc
			}
		}
	}
	p.server.mu.Unlock()
}
