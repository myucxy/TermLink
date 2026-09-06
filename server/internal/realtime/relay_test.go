package realtime

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"github.com/termlink/termlink/server/internal/store"
)

func TestPeerEnqueueAllowsBriefReplayBackpressure(t *testing.T) {
	p := &peer{
		hub:  &Hub{observer: noopObserver{}},
		send: make(chan []byte, 1),
		done: make(chan struct{}),
	}
	p.send <- []byte("queued")

	drained := make(chan struct{})
	go func() {
		time.Sleep(25 * time.Millisecond)
		<-p.send
		close(drained)
	}()

	p.enqueue([]byte("replay"))
	<-drained
	select {
	case frame := <-p.send:
		if string(frame) != "replay" {
			t.Fatalf("queued frame = %q, want replay", frame)
		}
	default:
		t.Fatal("replay frame was dropped during brief backpressure")
	}
	select {
	case <-p.done:
		t.Fatal("brief replay backpressure closed the peer")
	default:
	}
}

func TestPeerQueueByteBudgetIsHardBound(t *testing.T) {
	p := &peer{}
	if !p.reserveQueueBytes(peerQueueBytes - 1) {
		t.Fatal("initial queue reservation was rejected")
	}
	if p.reserveQueueBytes(2) {
		t.Fatal("queue accepted bytes beyond the hard budget")
	}
	p.releaseQueueBytes(peerQueueBytes - 1)
	if got := p.queuedBytes.Load(); got != 0 {
		t.Fatalf("queued bytes = %d, want 0", got)
	}
	if p.reserveQueueBytes(peerQueueBytes + 1) {
		t.Fatal("single reservation larger than the hard budget was accepted")
	}
}

func TestNegotiatedCapabilitiesAreBoundedAndOrdered(t *testing.T) {
	offered := []string{
		"terminal_typed_input_v1",
		"liveness_probe_v1",
		"unknown_future_capability",
		"lease_v1",
		"cumulative_ack",
		"flow_control_v1",
		"e2ee_x25519_chacha20poly1305",
		"terminal_presentation_metadata_v1",
		"host_history_v1",
	}
	got := negotiatedCapabilities(offered)
	want := []string{
		"cumulative_ack",
		"e2ee_x25519_chacha20poly1305",
		"flow_control_v1",
		"lease_v1",
		"terminal_presentation_metadata_v1",
		"terminal_typed_input_v1",
		"liveness_probe_v1",
		"host_history_v1",
	}
	if fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("negotiated capabilities = %v, want %v", got, want)
	}
}

func TestNegotiatedLivenessProbeRoundTripsThroughSingleWriter(t *testing.T) {
	serverSocket, clientSocket := newRelayWebSocketPair(t)
	hub := NewHubWithObserver(nil, nil, nil, nil)
	deviceID := uuid.New()
	p := &peer{
		hub:          hub,
		socket:       serverSocket,
		principal:    auth.Principal{Device: auth.Device{ID: deviceID}},
		role:         "android",
		send:         make(chan []byte, peerQueueFrames),
		controlSend:  make(chan []byte, 4),
		done:         make(chan struct{}),
		capabilities: map[string]bool{"liveness_probe_v1": true},
	}
	hub.peers[deviceID] = p
	go p.writeLoop()
	go p.readLoop()
	t.Cleanup(func() { p.close(websocket.CloseNormalClosure, "test complete") })

	probeID := uuid.NewString()
	request, err := json.Marshal(livenessControl{Type: "ping", ProbeID: probeID})
	if err != nil {
		t.Fatalf("marshal liveness request: %v", err)
	}
	if err := clientSocket.WriteMessage(websocket.TextMessage, request); err != nil {
		t.Fatalf("write liveness request: %v", err)
	}
	_ = clientSocket.SetReadDeadline(time.Now().Add(time.Second))
	messageType, response, err := clientSocket.ReadMessage()
	if err != nil {
		t.Fatalf("read liveness response: %v", err)
	}
	if messageType != websocket.TextMessage {
		t.Fatalf("liveness response type = %d, want text", messageType)
	}
	var pong livenessControl
	if err := json.Unmarshal(response, &pong); err != nil {
		t.Fatalf("decode liveness response: %v", err)
	}
	if pong.Type != "pong" || pong.ProbeID != probeID {
		t.Fatalf("liveness response = %#v, want matching pong", pong)
	}
}

func TestMalformedApplicationFrameDoesNotCloseHealthyRealtimeSocket(t *testing.T) {
	serverSocket, clientSocket := newRelayWebSocketPair(t)
	hub := NewHubWithObserver(nil, nil, nil, nil)
	deviceID := uuid.New()
	p := &peer{
		hub: hub, socket: serverSocket,
		principal: auth.Principal{Device: auth.Device{ID: deviceID}},
		role:      "android", send: make(chan []byte, peerQueueFrames),
		controlSend: make(chan []byte, 4), done: make(chan struct{}),
		capabilities: map[string]bool{"liveness_probe_v1": true},
	}
	hub.peers[deviceID] = p
	go p.writeLoop()
	go p.readLoop()
	t.Cleanup(func() { p.close(websocket.CloseNormalClosure, "test complete") })

	// Field tag zero is not a valid protobuf Envelope. The next authenticated
	// liveness exchange must still use this exact socket generation.
	if err := clientSocket.WriteMessage(websocket.BinaryMessage, []byte{0}); err != nil {
		t.Fatalf("write malformed application frame: %v", err)
	}
	probeID := uuid.NewString()
	request, err := json.Marshal(livenessControl{Type: "ping", ProbeID: probeID})
	if err != nil {
		t.Fatal(err)
	}
	if err := clientSocket.WriteMessage(websocket.TextMessage, request); err != nil {
		t.Fatalf("write liveness request after malformed frame: %v", err)
	}
	_ = clientSocket.SetReadDeadline(time.Now().Add(time.Second))
	messageType, response, err := clientSocket.ReadMessage()
	if err != nil {
		t.Fatalf("healthy socket closed after malformed application frame: %v", err)
	}
	if messageType != websocket.TextMessage {
		t.Fatalf("liveness response type = %d, want text", messageType)
	}
	var pong livenessControl
	if err := json.Unmarshal(response, &pong); err != nil {
		t.Fatalf("decode liveness response: %v", err)
	}
	if pong.Type != "pong" || pong.ProbeID != probeID {
		t.Fatalf("liveness response = %#v, want matching pong", pong)
	}
}

func TestLivenessControlRequiresNegotiatedCapabilityAndCanonicalID(t *testing.T) {
	p := &peer{done: make(chan struct{}), controlSend: make(chan []byte, 4)}
	valid, err := json.Marshal(livenessControl{Type: "ping", ProbeID: uuid.NewString()})
	if err != nil {
		t.Fatal(err)
	}
	if p.handleLivenessControl(valid) {
		t.Fatal("unnegotiated liveness control was accepted")
	}
	p.capabilities = map[string]bool{"liveness_probe_v1": true}
	invalid, err := json.Marshal(livenessControl{Type: "ping", ProbeID: "NOT-A-UUID"})
	if err != nil {
		t.Fatal(err)
	}
	if p.handleLivenessControl(invalid) {
		t.Fatal("non-canonical probe ID was accepted")
	}
	extraField, err := json.Marshal(map[string]string{
		"type": "ping", "probe_id": uuid.NewString(), "session_id": uuid.NewString(),
	})
	if err != nil {
		t.Fatal(err)
	}
	if p.handleLivenessControl(extraField) {
		t.Fatal("liveness control with an extra field was accepted")
	}
}

func TestRealtimeMessageDirectionACLIsExhaustive(t *testing.T) {
	all := []realtimewire.MessageType{
		realtimewire.Heartbeat, realtimewire.Goodbye,
		realtimewire.SessionUpsert, realtimewire.SessionRemoved,
		realtimewire.Subscribe, realtimewire.Unsubscribe,
		realtimewire.OutputChunk, realtimewire.OutputACK,
		realtimewire.FlowControl, realtimewire.Gap,
		realtimewire.InputEvent, realtimewire.InputAccepted,
		realtimewire.InputRejected, realtimewire.ResizeRequest,
		realtimewire.ResizeApplied, realtimewire.SnapshotOffer,
		realtimewire.SnapshotChunk, realtimewire.ResumeRequest,
		realtimewire.LeaseRequest, realtimewire.LeaseGranted,
		realtimewire.LeaseDenied, realtimewire.LeaseRevoked,
		realtimewire.ThemeChanged, realtimewire.SessionStarted,
		realtimewire.SessionInterrupted, realtimewire.SessionEnded,
		realtimewire.SessionTerminateRequest,
		realtimewire.HostHistoryRequest, realtimewire.HostHistoryResponse,
		realtimewire.HostHistoryControl, realtimewire.SessionTerminateResponse,
	}
	android := map[realtimewire.MessageType]bool{
		realtimewire.Heartbeat: true, realtimewire.Goodbye: true,
		realtimewire.Subscribe: true, realtimewire.Unsubscribe: true,
		realtimewire.OutputACK: true, realtimewire.FlowControl: true,
		realtimewire.InputEvent: true, realtimewire.ResizeRequest: true,
		realtimewire.ResumeRequest: true, realtimewire.LeaseRequest: true,
		realtimewire.SessionTerminateRequest: true,
		realtimewire.HostHistoryRequest:      true,
		realtimewire.HostHistoryControl:      true,
	}
	pc := map[realtimewire.MessageType]bool{
		realtimewire.Heartbeat: true, realtimewire.Goodbye: true,
		realtimewire.SessionUpsert: true, realtimewire.SessionRemoved: true,
		realtimewire.OutputChunk: true, realtimewire.Gap: true,
		realtimewire.InputAccepted: true, realtimewire.InputRejected: true,
		realtimewire.ResizeApplied: true, realtimewire.SnapshotOffer: true,
		realtimewire.SnapshotChunk: true, realtimewire.LeaseGranted: true,
		realtimewire.LeaseDenied: true, realtimewire.LeaseRevoked: true,
		realtimewire.ThemeChanged: true, realtimewire.SessionStarted: true,
		realtimewire.SessionInterrupted: true, realtimewire.SessionEnded: true,
		realtimewire.HostHistoryResponse:      true,
		realtimewire.SessionTerminateResponse: true,
	}
	for _, messageType := range all {
		if got := allowedMessage("android", messageType); got != android[messageType] {
			t.Errorf("android message %d allowed = %v, want %v", messageType, got, android[messageType])
		}
		if got := allowedMessage("pc", messageType); got != pc[messageType] {
			t.Errorf("pc message %d allowed = %v, want %v", messageType, got, pc[messageType])
		}
	}
	if allowedMessage("android", realtimewire.MessageUnspecified) ||
		allowedMessage("pc", realtimewire.MessageUnspecified) ||
		allowedMessage("unknown", realtimewire.OutputChunk) {
		t.Fatal("unspecified role/message was accepted")
	}
}

func TestEnvelopeACKMetadataContract(t *testing.T) {
	for _, messageType := range []realtimewire.MessageType{
		realtimewire.Subscribe, realtimewire.OutputACK, realtimewire.FlowControl,
	} {
		if !validEnvelopeACK(realtimewire.Envelope{MessageType: messageType, ACK: 42}) {
			t.Fatalf("message %d rejected its cumulative ACK", messageType)
		}
	}
	for _, messageType := range []realtimewire.MessageType{
		realtimewire.OutputChunk, realtimewire.InputEvent, realtimewire.ResizeRequest,
		realtimewire.LeaseRequest, realtimewire.SessionEnded,
		realtimewire.SessionTerminateRequest,
		realtimewire.HostHistoryRequest, realtimewire.HostHistoryResponse,
		realtimewire.HostHistoryControl, realtimewire.SessionTerminateResponse,
	} {
		if validEnvelopeACK(realtimewire.Envelope{MessageType: messageType, ACK: 42}) {
			t.Fatalf("message %d accepted a forbidden ACK", messageType)
		}
		if !validEnvelopeACK(realtimewire.Envelope{MessageType: messageType}) {
			t.Fatalf("message %d rejected ack=0", messageType)
		}
	}
}

func TestHostHistoryCapabilityIsOptionalAndNegotiatedWhenOffered(t *testing.T) {
	withoutHistory := clientHello{
		Role:             "android",
		ProtocolVersions: []uint32{realtimewire.ProtocolVersion},
		Capabilities:     append([]string(nil), requiredCapabilities...),
	}
	if !validHello(withoutHistory) {
		t.Fatal("host history capability became mandatory")
	}
	for _, capability := range negotiatedCapabilities(withoutHistory.Capabilities) {
		if capability == "host_history_v1" {
			t.Fatal("unoffered host history capability was negotiated")
		}
	}

	withHistory := withoutHistory
	withHistory.Capabilities = append(withHistory.Capabilities, "host_history_v1")
	if !validHello(withHistory) {
		t.Fatal("host history capability prevented a valid hello")
	}
	negotiated := negotiatedCapabilities(withHistory.Capabilities)
	if !containsRelayCapability(negotiated, "host_history_v1") {
		t.Fatalf("host history capability was not negotiated: %v", negotiated)
	}
}

func TestHostHistoryFramesRouteFromAndroidToPC(t *testing.T) {
	for _, messageType := range []realtimewire.MessageType{
		realtimewire.HostHistoryRequest,
		realtimewire.HostHistoryControl,
	} {
		t.Run(strconv.FormatUint(uint64(messageType), 10), func(t *testing.T) {
			hub := NewHubWithObserver(nil, nil, nil, nil)
			userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
			android, pc := activeRoutePeers(hub, userID, androidID, pcID)
			android.capabilities = map[string]bool{"host_history_v1": true}
			hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})

			frame := []byte("opaque-host-history-frame")
			envelope := realtimewire.Envelope{
				MessageType:       messageType,
				RecipientDeviceID: pcID[:],
				Sequence:          1,
			}
			if err := android.route(envelope, sessionID, frame); err != nil {
				t.Fatalf("Host History frame %d route: %v", messageType, err)
			}
			select {
			case received := <-pc.send:
				if string(received) != string(frame) {
					t.Fatalf("PC received host history frame %d = %q, want %q", messageType, received, frame)
				}
			default:
				t.Fatalf("PC did not receive host history frame %d", messageType)
			}
		})
	}
}

func TestOutOfBandPCResponsesBypassTerminalReplaySequence(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	pc.capabilities = map[string]bool{"host_history_v1": true}
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})

	outputOne := []byte("terminal-output-1")
	outputEnvelope := realtimewire.Envelope{
		RecipientDeviceID: androidID[:],
		MessageType:       realtimewire.OutputChunk,
		Sequence:          1,
	}
	if err := pc.routePCFrame(outputEnvelope, sessionID, androidID, outputOne); err != nil {
		t.Fatalf("initial terminal output route: %v", err)
	}
	if received := <-android.send; string(received) != string(outputOne) {
		t.Fatalf("initial terminal output = %q, want %q", received, outputOne)
	}

	for _, response := range []struct {
		messageType realtimewire.MessageType
		frame       []byte
	}{
		{messageType: realtimewire.HostHistoryResponse, frame: []byte("opaque-host-history-response")},
		{messageType: realtimewire.SessionTerminateResponse, frame: []byte("opaque-session-terminate-response")},
	} {
		envelope := realtimewire.Envelope{
			RecipientDeviceID: androidID[:],
			MessageType:       response.messageType,
			// Request-correlated response sequences are intentionally unrelated
			// to the ordered terminal output sequence.
			Sequence: 900,
		}
		if err := pc.routePCFrame(envelope, sessionID, androidID, response.frame); err != nil {
			t.Fatalf("out-of-band response %d route: %v", response.messageType, err)
		}
		if got := pcRouteHighWater(pc, sessionID, androidID); got != 1 {
			t.Fatalf("out-of-band response %d changed terminal high-water to %d, want 1", response.messageType, got)
		}
		if received := <-android.send; string(received) != string(response.frame) {
			t.Fatalf("Android response %d = %q, want %q", response.messageType, received, response.frame)
		}
	}

	outputTwo := []byte("terminal-output-2")
	outputEnvelope.Sequence = 2
	if err := pc.routePCFrame(outputEnvelope, sessionID, androidID, outputTwo); err != nil {
		t.Fatalf("terminal output after host history response: %v", err)
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 2 {
		t.Fatalf("terminal high-water after host history response = %d, want 2", got)
	}
	if received := <-android.send; string(received) != string(outputTwo) {
		t.Fatalf("terminal output after host history response = %q, want %q", received, outputTwo)
	}
}

func TestUnnegotiatedHostHistoryFramesAreRejectedWithoutClosingPeer(t *testing.T) {
	tests := []struct {
		name      string
		role      string
		message   realtimewire.MessageType
		direction realtimewire.Direction
	}{
		{name: "android request", role: "android", message: realtimewire.HostHistoryRequest, direction: realtimewire.AndroidToPC},
		{name: "android control", role: "android", message: realtimewire.HostHistoryControl, direction: realtimewire.AndroidToPC},
		{name: "pc response", role: "pc", message: realtimewire.HostHistoryResponse, direction: realtimewire.PCToAndroid},
		{name: "pc termination response", role: "pc", message: realtimewire.SessionTerminateResponse, direction: realtimewire.PCToAndroid},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			hub := NewHubWithObserver(nil, nil, nil, nil)
			userID, senderID, recipientID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
			senderSocket, senderClient := newRelayWebSocketPair(t)
			sender := testPeer(hub, senderID)
			sender.socket = senderSocket
			sender.role = test.role
			sender.principal.UserID = userID
			sender.capabilities = capabilitySet(requiredCapabilities)
			recipient := testPeer(hub, recipientID)
			recipient.role = "android"
			if test.role == "android" {
				recipient.role = "pc"
			}
			recipient.principal.UserID = userID
			hub.peers[senderID] = sender
			hub.peers[recipientID] = recipient
			route := store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: recipientID}
			if test.role == "pc" {
				route.PCDeviceID = senderID
				hub.subscribe(recipient, sessionID, route)
			} else {
				hub.subscribe(sender, sessionID, route)
			}
			go sender.readLoop()
			defer func() {
				_ = senderClient.Close()
				select {
				case <-sender.done:
				case <-time.After(time.Second):
					t.Error("sender read loop did not stop after test socket close")
				}
			}()

			historyFrame := relayTestEnvelope(t, test.message, test.direction, senderID, recipientID, sessionID, 1)
			if err := senderClient.WriteMessage(websocket.BinaryMessage, historyFrame); err != nil {
				t.Fatalf("write unnegotiated host history frame: %v", err)
			}
			heartbeatFrame := relayTestEnvelope(t, realtimewire.Heartbeat, test.direction, senderID, recipientID, sessionID, 1)
			if err := senderClient.WriteMessage(websocket.BinaryMessage, heartbeatFrame); err != nil {
				t.Fatalf("write post-rejection heartbeat: %v", err)
			}
			select {
			case received := <-recipient.send:
				if string(received) != string(heartbeatFrame) {
					t.Fatalf("recipient received unnegotiated host history frame %q, want heartbeat %q", received, heartbeatFrame)
				}
			case <-time.After(time.Second):
				t.Fatal("post-rejection heartbeat was not routed")
			}
			select {
			case <-sender.done:
				t.Fatal("unnegotiated host history frame closed the authenticated peer")
			default:
			}
		})
	}
}

func containsRelayCapability(capabilities []string, wanted string) bool {
	for _, capability := range capabilities {
		if capability == wanted {
			return true
		}
	}
	return false
}

func relayTestEnvelope(t *testing.T, messageType realtimewire.MessageType, direction realtimewire.Direction, senderID, recipientID, sessionID uuid.UUID, sequence uint64) []byte {
	t.Helper()
	envelope := realtimewire.Envelope{
		ProtocolVersion:   realtimewire.ProtocolVersion,
		MessageType:       messageType,
		SessionID:         sessionID[:],
		SenderDeviceID:    senderID[:],
		Direction:         direction,
		Sequence:          sequence,
		SentAtMS:          time.Now().UnixMilli(),
		KeyID:             []byte("host-history-key"),
		Nonce:             []byte("0123456789ab"),
		Ciphertext:        []byte("0123456789abcdef"),
		RecipientDeviceID: recipientID[:],
	}
	frame, err := envelope.Marshal()
	if err != nil {
		t.Fatalf("marshal relay test envelope: %v", err)
	}
	return frame
}

func TestAndroidControlMessagePermissionPolicy(t *testing.T) {
	tests := []struct {
		name        string
		messageType realtimewire.MessageType
		permission  string
		want        bool
	}{
		{name: "terminate view", messageType: realtimewire.SessionTerminateRequest, permission: "VIEW"},
		{name: "terminate control", messageType: realtimewire.SessionTerminateRequest, permission: "CONTROL"},
		{name: "terminate manage", messageType: realtimewire.SessionTerminateRequest, permission: "MANAGE", want: true},
		{name: "input view", messageType: realtimewire.InputEvent, permission: "VIEW"},
		{name: "input control", messageType: realtimewire.InputEvent, permission: "CONTROL", want: true},
		{name: "input manage", messageType: realtimewire.InputEvent, permission: "MANAGE", want: true},
		{name: "resize view", messageType: realtimewire.ResizeRequest, permission: "VIEW"},
		{name: "resize control", messageType: realtimewire.ResizeRequest, permission: "CONTROL", want: true},
		{name: "resize manage", messageType: realtimewire.ResizeRequest, permission: "MANAGE", want: true},
		{name: "lease view", messageType: realtimewire.LeaseRequest, permission: "VIEW"},
		{name: "lease control", messageType: realtimewire.LeaseRequest, permission: "CONTROL", want: true},
		{name: "lease manage", messageType: realtimewire.LeaseRequest, permission: "MANAGE", want: true},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if got := allowedRoutePermission(test.messageType, test.permission); got != test.want {
				t.Fatalf("allowedRoutePermission(%d, %q) = %v, want %v", test.messageType, test.permission, got, test.want)
			}
		})
	}
}

func TestExpectedControlRouteDenialsDoNotCloseAuthenticatedPeer(t *testing.T) {
	for _, class := range []string{
		"control_lease_inactive",
		"control_lease_holder_mismatch",
		"pc_connection_unavailable",
	} {
		t.Run(class, func(t *testing.T) {
			if routeDenialIsFatal(denyRoute(class, store.ErrDenied)) {
				t.Fatalf("route denial %q unexpectedly closes the device connection", class)
			}
		})
	}
}

func TestProtocolAndAuthorizationRouteDenialsRemainFatal(t *testing.T) {
	for _, err := range []error{
		denyRoute("recipient_mismatch", store.ErrDenied),
		denyRoute("route_permission_denied", store.ErrDenied),
		denyRoute("sender_connection_replaced", store.ErrDenied),
		store.ErrDenied,
	} {
		if !routeDenialIsFatal(err) {
			t.Fatalf("security-sensitive route denial %q did not close the connection", routeDenialClass(err))
		}
	}
}

func TestReplacedPeerCannotRemoveReplacementSubscription(t *testing.T) {
	observer := &subscriptionObserver{}
	hub := NewHubWithObserver(nil, nil, nil, observer)
	deviceID := uuid.New()
	sessionID := uuid.New()
	oldPeer := testPeer(hub, deviceID)
	newPeer := testPeer(hub, deviceID)

	hub.peers[deviceID] = oldPeer
	hub.subscribe(oldPeer, sessionID, store.SessionRouteAuthorization{PCDeviceID: uuid.New()})
	hub.peers[deviceID] = newPeer
	hub.subscribe(newPeer, sessionID, store.SessionRouteAuthorization{PCDeviceID: uuid.New()})

	if hub.isCurrent(oldPeer) {
		t.Fatal("replaced peer remained current")
	}
	if err := oldPeer.route(realtimewire.Envelope{}, sessionID, nil); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("replaced peer route error = %v, want ErrDenied", err)
	}
	hub.unsubscribe(oldPeer, sessionID)

	if got := hub.subscriptions[sessionID][deviceID]; got != newPeer {
		t.Fatalf("replacement subscription = %p, want %p", got, newPeer)
	}
	if observer.delta != 1 {
		t.Fatalf("subscription metric delta = %d, want 1", observer.delta)
	}
}

func TestPCPeerAcceptsRetransmittedOutputSequence(t *testing.T) {
	peer := testPeer(NewHubWithObserver(nil, nil, nil, nil), uuid.New())
	peer.role = "pc"
	sessionID := uuid.New()

	if !peer.acceptSequence(sessionID, 42) {
		t.Fatal("initial PC output sequence was rejected")
	}
	if !peer.acceptSequence(sessionID, 41) {
		t.Fatal("retransmitted PC output sequence was rejected")
	}
	if got := peer.lastSequences[sessionID]; got != 42 {
		t.Fatalf("PC high-water sequence = %d, want 42", got)
	}
}

func TestPCPeerRejectsSameSequenceCiphertextReplacement(t *testing.T) {
	peer := testPeer(NewHubWithObserver(nil, nil, nil, nil), uuid.New())
	peer.role = "pc"
	sessionID := uuid.New()
	recipientID := uuid.New()
	peer.beginPCReplayEpoch(sessionID, recipientID, 41)
	first := []byte("opaque-frame-a")
	if !peer.acceptPCSequence(sessionID, recipientID, 42, first) {
		t.Fatal("initial PC frame was rejected")
	}
	if !peer.acceptPCSequence(sessionID, recipientID, 42, first) {
		t.Fatal("identical PC replay was rejected")
	}
	if peer.acceptPCSequence(sessionID, recipientID, 42, []byte("opaque-frame-b")) {
		t.Fatal("same-sequence ciphertext replacement was accepted")
	}
	independentRecipient := uuid.New()
	peer.beginPCReplayEpoch(sessionID, independentRecipient, 41)
	if !peer.acceptPCSequence(sessionID, independentRecipient, 42, []byte("opaque-frame-b")) {
		t.Fatal("same logical sequence for an independent recipient was rejected")
	}
}

func TestPCPeerAcceptsOrderedReplayBeyondDigestWindowAfterVisibleSubscribeACK(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID := uuid.New()
	pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New()
	pc := testPeer(hub, pcID)
	pc.role = "pc"
	pc.principal.UserID = userID
	android := testPeer(hub, androidID)
	android.role = "android"
	android.principal.UserID = userID
	hub.peers[pcID] = pc
	hub.peers[androidID] = android
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})

	for sequence := uint64(1); sequence <= maxPCReplayFrames+256; sequence++ {
		if !pc.acceptPCSequence(sessionID, androidID, sequence, replayTestFrame(sequence)) {
			t.Fatalf("initial sequence %d was rejected", sequence)
		}
	}

	// A successfully authorized SUBSCRIBE is the ACK visible to this PC. Its
	// route-specific epoch permits replay from ACK+1 even though sequence 1 was
	// evicted from the finite digest cache long ago.
	hub.sendSubscriptionToPC(android, pcID, sessionID, 0, []byte("subscribe-ack-0"))
	if !pc.acceptPCSequence(sessionID, androidID, 1, replayTestFrame(1)) {
		t.Fatal("ordered replay beyond the digest window was rejected")
	}
	select {
	case <-pc.done:
		t.Fatal("legal old replay closed the entire PC")
	default:
	}
	if pc.acceptPCSequence(sessionID, androidID, 1, []byte("tampered-ciphertext")) {
		t.Fatal("same sequence with different serialized frame was accepted inside replay epoch")
	}
	if !pc.acceptPCSequence(sessionID, androidID, 2, replayTestFrame(2)) {
		t.Fatal("next ordered frame in replay epoch was rejected")
	}

	// A later visible cumulative ACK advances the epoch without requiring the
	// relay to retain every historical frame digest.
	hub.sendSubscriptionToPC(android, pcID, sessionID, 99, []byte("subscribe-ack-99"))
	if !pc.acceptPCSequence(sessionID, androidID, 100, replayTestFrame(100)) {
		t.Fatal("advanced replay epoch did not start at ACK+1")
	}
	if pc.acceptPCSequence(sessionID, androidID, 100, []byte("tampered-after-advance")) {
		t.Fatal("advanced replay epoch allowed ciphertext replacement")
	}
}

func TestPCReplayDigestEvictionIsIsolatedByRecipientAndSession(t *testing.T) {
	pc := testPeer(NewHubWithObserver(nil, nil, nil, nil), uuid.New())
	pc.role = "pc"
	busySession, otherSession := uuid.New(), uuid.New()
	busyRecipient, otherRecipient := uuid.New(), uuid.New()

	protected := []struct {
		name        string
		sessionID   uuid.UUID
		recipientID uuid.UUID
		frame       []byte
	}{
		{name: "recipient", sessionID: busySession, recipientID: otherRecipient, frame: []byte("recipient-frame")},
		{name: "session", sessionID: otherSession, recipientID: busyRecipient, frame: []byte("session-frame")},
	}
	for _, route := range protected {
		pc.beginPCReplayEpoch(route.sessionID, route.recipientID, 6)
		if !pc.acceptPCSequence(route.sessionID, route.recipientID, 7, route.frame) {
			t.Fatalf("initial %s route frame was rejected", route.name)
		}
	}
	for sequence := uint64(1); sequence <= maxPCReplayFrames+256; sequence++ {
		if !pc.acceptPCSequence(busySession, busyRecipient, sequence, replayTestFrame(sequence)) {
			t.Fatalf("busy route sequence %d was rejected", sequence)
		}
	}
	busyState := pc.pcReplayRoutes[pcRouteKey{sessionID: busySession, recipientID: busyRecipient}]
	if busyState == nil {
		t.Fatal("busy route replay state was not created")
	}
	if len(busyState.digests) != maxPCReplayFrames || len(busyState.order) != maxPCReplayFrames {
		t.Fatalf("busy route digest/order size=%d/%d, want bounded %d/%d",
			len(busyState.digests), len(busyState.order), maxPCReplayFrames, maxPCReplayFrames)
	}
	for _, route := range protected {
		if !pc.acceptPCSequence(route.sessionID, route.recipientID, 7, route.frame) {
			t.Fatalf("busy route evicted %s route digest", route.name)
		}
		if pc.acceptPCSequence(route.sessionID, route.recipientID, 7, []byte("replacement")) {
			t.Fatalf("%s route accepted ciphertext replacement after unrelated eviction", route.name)
		}
	}
}

func replayTestFrame(sequence uint64) []byte {
	return []byte(fmt.Sprintf("opaque-frame-%d", sequence))
}

func pcRouteHighWater(pc *peer, sessionID, recipientID uuid.UUID) uint64 {
	pc.pcReplayMu.Lock()
	defer pc.pcReplayMu.Unlock()
	state := pc.pcReplayRoutes[pcRouteKey{sessionID: sessionID, recipientID: recipientID}]
	if state == nil {
		return 0
	}
	return state.highWater
}

func liveReservationOwner(hub *Hub, identity *peer, sessionID uuid.UUID) *peer {
	key := reservationKey(identity, sessionID)
	shard := hub.reservationShard(key)
	shard.mu.Lock()
	defer shard.mu.Unlock()
	return shard.owners[key]
}

func TestAndroidPeerRejectsReplayedInputSequence(t *testing.T) {
	peer := testPeer(NewHubWithObserver(nil, nil, nil, nil), uuid.New())
	peer.role = "android"
	sessionID := uuid.New()

	if !peer.acceptSequence(sessionID, 42) {
		t.Fatal("initial Android input sequence was rejected")
	}
	if peer.acceptSequence(sessionID, 42) {
		t.Fatal("duplicate Android input sequence was accepted")
	}
	if peer.acceptSequence(sessionID, 41) {
		t.Fatal("older Android input sequence was accepted")
	}
	if !peer.acceptSequence(sessionID, 43) {
		t.Fatal("newer Android input sequence was rejected")
	}
}

func TestPCOutputUsesValidatedSubscriptionWithoutAuthorityQuery(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID := uuid.New()
	pcID := uuid.New()
	androidID := uuid.New()
	sessionID := uuid.New()
	pc := testPeer(hub, pcID)
	pc.role = "pc"
	pc.principal.UserID = userID
	android := testPeer(hub, androidID)
	android.role = "android"
	android.principal.UserID = userID
	hub.peers[pcID] = pc
	hub.peers[androidID] = android
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{
		Permission: "VIEW",
		PCDeviceID: pcID,
	})

	frame := []byte("opaque-production-frame")
	err := pc.route(realtimewire.Envelope{
		RecipientDeviceID: androidID[:],
		MessageType:       realtimewire.OutputChunk,
	}, sessionID, frame)
	if err != nil {
		t.Fatal(err)
	}
	select {
	case received := <-android.send:
		if string(received) != string(frame) {
			t.Fatalf("routed frame = %q, want %q", received, frame)
		}
	default:
		t.Fatal("validated subscriber did not receive PC output")
	}
}

func TestConnectedAndroidWithoutSessionSubscriptionReceivesNoPCOutput(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID := uuid.New(), uuid.New(), uuid.New()
	subscribedSessionID, unsubscribedSessionID := uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	hub.subscribe(android, subscribedSessionID, store.SessionRouteAuthorization{
		Permission: "VIEW",
		PCDeviceID: pcID,
	})

	frame := []byte("output-for-unsubscribed-session")
	envelope := realtimewire.Envelope{
		RecipientDeviceID: androidID[:],
		MessageType:       realtimewire.OutputChunk,
		Sequence:          1,
	}
	if err := pc.routePCFrame(envelope, unsubscribedSessionID, androidID, frame); err != nil {
		t.Fatalf("unsubscribed PC output was rejected: %v", err)
	}
	select {
	case received := <-android.send:
		t.Fatalf("unsubscribed session delivered PC output %q", received)
	default:
	}
	if _, ok := hub.subscriptions[unsubscribedSessionID]; ok {
		t.Fatal("PC output created a subscription for the unsubscribed session")
	}
}

func TestFlowControlAloneCannotEstablishSubscriptionOrRoute(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)

	envelope := realtimewire.Envelope{
		RecipientDeviceID: pcID[:],
		MessageType:       realtimewire.FlowControl,
		ACK:               7,
	}
	err := android.route(envelope, sessionID, []byte("flow-control-without-subscribe"))
	if !errors.Is(err, store.ErrDenied) {
		t.Fatalf("FLOW_CONTROL without SUBSCRIBE error = %v, want ErrDenied", err)
	}
	if got := subscriptionCountForPeer(hub, android); got != 0 {
		t.Fatalf("FLOW_CONTROL created %d subscription(s), want 0", got)
	}
	if _, ok := hub.subscriptions[sessionID]; ok {
		t.Fatal("FLOW_CONTROL created a session route")
	}
	select {
	case received := <-pc.send:
		t.Fatalf("FLOW_CONTROL without subscription reached PC as %q", received)
	default:
	}
}

func TestAuthorizedSubscribeACKFencesTailFirstReplayAtSPlusOne(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	authority := newCountingRouteAuthority()
	hub.store = authority
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	route := store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID}
	authority.set(userID, androidID, sessionID, route, nil)

	const ack uint64 = 40
	for sequence := uint64(1); sequence <= ack+2; sequence++ {
		envelope := realtimewire.Envelope{
			RecipientDeviceID: androidID[:],
			MessageType:       realtimewire.OutputChunk,
			Sequence:          sequence,
		}
		if err := pc.routePCFrame(envelope, sessionID, androidID, replayTestFrame(sequence)); err != nil {
			t.Fatalf("pre-subscription sequence %d: %v", sequence, err)
		}
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != ack+2 {
		t.Fatalf("pre-subscription high-water = %d, want %d", got, ack+2)
	}
	select {
	case received := <-android.send:
		t.Fatalf("pre-subscription replay reached Android as %q", received)
	default:
	}

	subscribeFrame := []byte("authorized-subscribe-ack-40")
	subscribe := realtimewire.Envelope{
		RecipientDeviceID: pcID[:],
		MessageType:       realtimewire.Subscribe,
		ACK:               ack,
	}
	if err := android.route(subscribe, sessionID, subscribeFrame); err != nil {
		t.Fatalf("authorized SUBSCRIBE: %v", err)
	}
	hub.mu.RLock()
	gotACK, ackPresent := android.subscriptionACKs[sessionID]
	_, subscribed := hub.subscriptions[sessionID][androidID]
	hub.mu.RUnlock()
	if !subscribed || !ackPresent || gotACK != ack {
		t.Fatalf("subscription state = subscribed:%v ack:%d present:%v, want subscribed with ACK %d", subscribed, gotACK, ackPresent, ack)
	}
	select {
	case received := <-pc.send:
		if string(received) != string(subscribeFrame) {
			t.Fatalf("visible SUBSCRIBE frame = %q, want %q", received, subscribeFrame)
		}
	default:
		t.Fatal("authorized SUBSCRIBE was not made visible to PC")
	}

	outOfOrder := realtimewire.Envelope{
		RecipientDeviceID: androidID[:],
		MessageType:       realtimewire.OutputChunk,
		Sequence:          ack + 2,
	}
	if err := pc.routePCFrame(outOfOrder, sessionID, androidID, replayTestFrame(ack+2)); !errors.Is(err, errPCSequenceRejected) {
		t.Fatalf("out-of-order S+2 replay error = %v, want sequence rejection", err)
	}
	select {
	case received := <-android.send:
		t.Fatalf("out-of-order replay reached Android as %q", received)
	default:
	}

	for _, sequence := range []uint64{ack + 1, ack + 2} {
		envelope := realtimewire.Envelope{
			RecipientDeviceID: androidID[:],
			MessageType:       realtimewire.OutputChunk,
			Sequence:          sequence,
		}
		if err := pc.routePCFrame(envelope, sessionID, androidID, replayTestFrame(sequence)); err != nil {
			t.Fatalf("ordered replay S+%d: %v", sequence-ack, err)
		}
		select {
		case received := <-android.send:
			want := replayTestFrame(sequence)
			if string(received) != string(want) {
				t.Fatalf("ordered replay S+%d frame = %q, want %q", sequence-ack, received, want)
			}
		default:
			t.Fatalf("ordered replay S+%d did not reach Android", sequence-ack)
		}
	}

	tampered := realtimewire.Envelope{
		RecipientDeviceID: androidID[:],
		MessageType:       realtimewire.OutputChunk,
		Sequence:          ack + 1,
	}
	if err := pc.routePCFrame(tampered, sessionID, androidID, []byte("tampered-S-plus-1")); !errors.Is(err, errPCSequenceRejected) {
		t.Fatalf("tampered S+1 replay error = %v, want sequence rejection", err)
	}
}

func TestPCRouteCommitsWhileSubscriberIsTemporarilyUnavailable(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	pc := testPeer(hub, pcID)
	pc.role = "pc"
	pc.principal.UserID = userID
	hub.peers[pcID] = pc
	pc.beginPCReplayEpoch(sessionID, androidID, 305)

	envelope := realtimewire.Envelope{
		RecipientDeviceID: androidID[:],
		MessageType:       realtimewire.OutputChunk,
		Sequence:          306,
	}
	err := pc.routePCFrame(envelope, sessionID, androidID, []byte("output-306"))
	if err != nil {
		t.Fatalf("temporarily unavailable subscriber rejected PC output: %v", err)
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 306 {
		t.Fatalf("offline route high-water = %d, want 306", got)
	}
	// Advancing the high-water mark keeps later live output continuous. The
	// next Android SUBSCRIBE/ACK opens a replay epoch across these frames.
	envelope.Sequence = 307
	if err := pc.routePCFrame(envelope, sessionID, androidID, []byte("output-307")); err != nil {
		t.Fatalf("continuous offline PC output was rejected: %v", err)
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 307 {
		t.Fatalf("continuous offline route high-water = %d, want 307", got)
	}
}

func TestPCRouteCommitsOnlyAfterCurrentSubscriberEnqueue(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	pc.beginPCReplayEpoch(sessionID, androidID, 305)

	envelope := realtimewire.Envelope{
		RecipientDeviceID: androidID[:],
		MessageType:       realtimewire.OutputChunk,
		Sequence:          306,
	}
	frame := []byte("output-306")
	if err := pc.routePCFrame(envelope, sessionID, androidID, frame); err != nil {
		t.Fatalf("route current subscriber: %v", err)
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 306 {
		t.Fatalf("successful route high-water = %d, want 306", got)
	}
	select {
	case received := <-android.send:
		if string(received) != string(frame) {
			t.Fatalf("queued frame = %q, want %q", received, frame)
		}
	default:
		t.Fatal("success was recorded before the frame entered the subscriber queue")
	}
}

func TestPCRouteCommitsForClosedOrReplacedSubscriber(t *testing.T) {
	for _, test := range []struct {
		name    string
		prepare func(*Hub, *peer, uuid.UUID)
	}{
		{
			name: "closed enqueue",
			prepare: func(_ *Hub, android *peer, _ uuid.UUID) {
				android.close(websocket.CloseNormalClosure, "test target closed")
			},
		},
		{
			name: "android replacement",
			prepare: func(hub *Hub, _ *peer, androidID uuid.UUID) {
				replacement := testPeer(hub, androidID)
				replacement.role = "android"
				hub.peers[androidID] = replacement
			},
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			hub := NewHubWithObserver(nil, nil, nil, nil)
			userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
			android, pc := activeRoutePeers(hub, userID, androidID, pcID)
			hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
			pc.beginPCReplayEpoch(sessionID, androidID, 305)
			test.prepare(hub, android, androidID)

			envelope := realtimewire.Envelope{
				RecipientDeviceID: androidID[:],
				MessageType:       realtimewire.OutputChunk,
				Sequence:          306,
			}
			err := pc.routePCFrame(envelope, sessionID, androidID, []byte("output-306"))
			if err != nil {
				t.Fatalf("transient subscriber transition rejected PC output: %v", err)
			}
			if got := pcRouteHighWater(pc, sessionID, androidID); got != 306 {
				t.Fatalf("transient route high-water = %d, want 306", got)
			}
		})
	}
}

func TestReplacementPCAppliesACKAndRequiresContinuousOutput(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, replacement := activeRoutePeers(hub, userID, androidID, pcID)
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	replacement.beginPCReplayEpoch(sessionID, androidID, 305)

	envelope := realtimewire.Envelope{RecipientDeviceID: androidID[:], MessageType: realtimewire.OutputChunk, Sequence: 307}
	if err := replacement.routePCFrame(envelope, sessionID, androidID, []byte("output-307")); !errors.Is(err, errPCSequenceRejected) {
		t.Fatalf("ACK+2 route error = %v, want sequence rejection", err)
	}
	if got := pcRouteHighWater(replacement, sessionID, androidID); got != 305 {
		t.Fatalf("ACK+2 changed high-water to %d, want 305", got)
	}

	envelope.Sequence = 306
	if err := replacement.routePCFrame(envelope, sessionID, androidID, []byte("output-306")); err != nil {
		t.Fatalf("ACK+1 route: %v", err)
	}
	envelope.Sequence = 307
	if err := replacement.routePCFrame(envelope, sessionID, androidID, []byte("output-307")); err != nil {
		t.Fatalf("continuous ACK+2 route: %v", err)
	}
	if got := pcRouteHighWater(replacement, sessionID, androidID); got != 307 {
		t.Fatalf("continuous route high-water = %d, want 307", got)
	}
	for _, want := range []string{"output-306", "output-307"} {
		select {
		case received := <-android.send:
			if string(received) != want {
				t.Fatalf("queued frame = %q, want %q", received, want)
			}
		default:
			t.Fatalf("missing queued frame %q", want)
		}
	}
}

func TestExplicitGapIsOnlyForwardSequenceJumpException(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	pc.beginPCReplayEpoch(sessionID, androidID, 305)

	envelope := realtimewire.Envelope{RecipientDeviceID: androidID[:], MessageType: realtimewire.OutputChunk, Sequence: 309}
	if err := pc.routePCFrame(envelope, sessionID, androidID, []byte("skipped-output")); !errors.Is(err, errPCSequenceRejected) {
		t.Fatalf("ordinary sequence jump error = %v, want sequence rejection", err)
	}
	envelope.MessageType = realtimewire.Gap
	if err := pc.routePCFrame(envelope, sessionID, androidID, []byte("explicit-gap")); err != nil {
		t.Fatalf("explicit Gap route: %v", err)
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 309 {
		t.Fatalf("Gap route high-water = %d, want 309", got)
	}
}

func TestGapCannotReplaceAcceptedCiphertextAtSameSequence(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})

	envelope := realtimewire.Envelope{RecipientDeviceID: androidID[:], MessageType: realtimewire.OutputChunk, Sequence: 1}
	if err := pc.routePCFrame(envelope, sessionID, androidID, []byte("original-output")); err != nil {
		t.Fatalf("original route: %v", err)
	}
	<-android.send
	pc.beginPCReplayEpoch(sessionID, androidID, 0)
	envelope.MessageType = realtimewire.Gap
	if err := pc.routePCFrame(envelope, sessionID, androidID, []byte("different-gap-ciphertext")); !errors.Is(err, errPCSequenceRejected) {
		t.Fatalf("same-sequence Gap replacement error = %v, want sequence rejection", err)
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 1 {
		t.Fatalf("rejected Gap changed high-water to %d, want 1", got)
	}
}

func TestSubscriptionActivationFencesConcurrentACKPlusTwo(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	pc.send = make(chan []byte, 1)
	prefill := []byte("queued-before-subscribe")
	if !pc.enqueue(prefill) {
		t.Fatal("failed to prefill PC queue")
	}
	pc.pcReplayMu.Lock()
	pc.pcReplayRoute(pcRouteKey{sessionID: sessionID, recipientID: androidID}).highWater = 306
	pc.pcReplayMu.Unlock()

	subscribeFrame := []byte("subscribe-ack-305")
	activationDone := make(chan error, 1)
	go func() {
		activationDone <- hub.activateSubscription(
			android,
			sessionID,
			store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID},
			305,
			subscribeFrame,
		)
	}()

	deadline := time.Now().Add(time.Second)
	for {
		hub.mu.RLock()
		published := hub.subscriptions[sessionID][androidID] == android && android.subscriptionACKs[sessionID] == 305
		hub.mu.RUnlock()
		if published && pc.queuedBytes.Load() >= int64(len(prefill)+len(subscribeFrame)) {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("subscription activation did not reach its blocked enqueue")
		}
		time.Sleep(time.Millisecond)
	}

	routeDone := make(chan error, 1)
	go func() {
		envelope := realtimewire.Envelope{RecipientDeviceID: androidID[:], MessageType: realtimewire.OutputChunk, Sequence: 307}
		routeDone <- pc.routePCFrame(envelope, sessionID, androidID, []byte("output-307"))
	}()
	queued := <-pc.send
	pc.releaseQueueBytes(int64(len(queued)))
	if string(queued) != string(prefill) {
		t.Fatalf("prefill frame = %q, want %q", queued, prefill)
	}
	if err := <-activationDone; err != nil {
		t.Fatalf("activate subscription: %v", err)
	}
	if err := <-routeDone; !errors.Is(err, errPCSequenceRejected) {
		t.Fatalf("concurrent ACK+2 route error = %v, want sequence rejection", err)
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 306 {
		t.Fatalf("concurrent ACK+2 changed high-water to %d, want 306", got)
	}
	queued = <-pc.send
	pc.releaseQueueBytes(int64(len(queued)))
	if string(queued) != string(subscribeFrame) {
		t.Fatalf("queued subscription = %q, want %q", queued, subscribeFrame)
	}
}

func TestPCRouteDoesNotCommitWhenSenderIsReplacedDuringEnqueue(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	android.send = make(chan []byte, 1)
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	pc.beginPCReplayEpoch(sessionID, androidID, 305)
	prefill := []byte("queued-before-output")
	if !android.enqueue(prefill) {
		t.Fatal("failed to prefill Android queue")
	}
	frame := []byte("output-306")
	routeDone := make(chan error, 1)
	go func() {
		envelope := realtimewire.Envelope{RecipientDeviceID: androidID[:], MessageType: realtimewire.OutputChunk, Sequence: 306}
		routeDone <- pc.routePCFrame(envelope, sessionID, androidID, frame)
	}()
	deadline := time.Now().Add(time.Second)
	for pcBytes := int64(len(prefill) + len(frame)); android.queuedBytes.Load() < pcBytes; {
		if time.Now().After(deadline) {
			t.Fatal("PC route did not block in subscriber enqueue")
		}
		time.Sleep(time.Millisecond)
	}

	replacement := testPeer(hub, pcID)
	replacement.role = "pc"
	replacement.principal.UserID = userID
	if !hub.add(replacement) {
		t.Fatal("failed to install replacement PC")
	}
	queued := <-android.send
	android.releaseQueueBytes(int64(len(queued)))
	if string(queued) != string(prefill) {
		t.Fatalf("prefill frame = %q, want %q", queued, prefill)
	}
	err := <-routeDone
	if got := routeDenialClass(err); got != "subscriber_replaced" {
		t.Fatalf("old PC route denial = %q (%v), want subscriber_replaced", got, err)
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 305 {
		t.Fatalf("replaced PC committed high-water %d, want 305", got)
	}
	queued = <-android.send
	android.releaseQueueBytes(int64(len(queued)))
	if string(queued) != string(frame) {
		t.Fatalf("enqueued output = %q, want %q", queued, frame)
	}
}

func TestOldSubscribeCleanupCannotReleaseReplacementReservation(t *testing.T) {
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.leases = leasing
	userID, deviceID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	oldAndroid := testPeer(hub, deviceID)
	oldAndroid.role = "android"
	oldAndroid.principal.UserID = userID
	if !hub.add(oldAndroid) {
		t.Fatal("failed to add old Android")
	}
	if err := hub.reserveLiveSession(context.Background(), oldAndroid, sessionID); err != nil {
		t.Fatalf("old reserve: %v", err)
	}

	replacement := testPeer(hub, deviceID)
	replacement.role = "android"
	replacement.principal.UserID = userID
	if !hub.add(replacement) {
		t.Fatal("failed to add replacement Android")
	}
	if err := hub.reserveLiveSession(context.Background(), replacement, sessionID); err != nil {
		t.Fatalf("replacement reserve: %v", err)
	}
	route := store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID}
	if err := hub.activateSubscription(replacement, sessionID, route, 305, []byte("replacement-subscribe")); err != nil {
		t.Fatalf("replacement activate: %v", err)
	}

	if err := hub.activateSubscription(oldAndroid, sessionID, route, 305, []byte("old-subscribe")); routeDenialClass(err) != "subscription_reservation_replaced" {
		t.Fatalf("old activation error = %v, want reservation generation rejection", err)
	}
	// This is the failed SUBSCRIBE cleanup path in route(). It must not call
	// the identity-scoped backend release after ownership moved to replacement.
	hub.releaseLiveSession(context.Background(), oldAndroid, sessionID)
	if !leasing.reservationActive(userID, sessionID, deviceID) {
		t.Fatal("old cleanup deleted replacement reservation")
	}
	if got := leasing.releaseCalls(); got != 0 {
		t.Fatalf("backend releases = %d, want 0", got)
	}
}

func TestStaleSingleReserveCannotTouchReplacementReservation(t *testing.T) {
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.leases = leasing
	userID, deviceID, sessionID := uuid.New(), uuid.New(), uuid.New()
	oldAndroid := testPeer(hub, deviceID)
	oldAndroid.role = "android"
	oldAndroid.principal.UserID = userID
	if !hub.add(oldAndroid) {
		t.Fatal("failed to add old Android")
	}
	replacement := testPeer(hub, deviceID)
	replacement.role = "android"
	replacement.principal.UserID = userID
	if !hub.add(replacement) {
		t.Fatal("failed to add replacement Android")
	}
	if err := hub.reserveLiveSession(context.Background(), replacement, sessionID); err != nil {
		t.Fatalf("replacement reserve: %v", err)
	}
	reservesBefore, releasesBefore := leasing.reserveCalls(), leasing.releaseCalls()

	if err := hub.reserveLiveSession(context.Background(), oldAndroid, sessionID); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("stale reserve error = %v, want ErrDenied", err)
	}
	if got := leasing.reserveCalls(); got != reservesBefore {
		t.Fatalf("stale peer called backend reserve: got %d, want %d", got, reservesBefore)
	}
	if got := leasing.releaseCalls(); got != releasesBefore {
		t.Fatalf("stale peer called backend release: got %d, want %d", got, releasesBefore)
	}
	if !leasing.reservationActive(userID, sessionID, deviceID) {
		t.Fatal("stale reserve deleted replacement reservation")
	}
}

func TestStaleBatchReserveCannotTouchReplacementReservations(t *testing.T) {
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.leases = leasing
	userID, deviceID := uuid.New(), uuid.New()
	sessionIDs := []uuid.UUID{uuid.New(), uuid.New(), uuid.New()}
	oldAndroid := testPeer(hub, deviceID)
	oldAndroid.role = "android"
	oldAndroid.principal.UserID = userID
	if !hub.add(oldAndroid) {
		t.Fatal("failed to add old Android")
	}
	replacement := testPeer(hub, deviceID)
	replacement.role = "android"
	replacement.principal.UserID = userID
	if !hub.add(replacement) {
		t.Fatal("failed to add replacement Android")
	}
	if err := hub.reserveLiveSessions(context.Background(), replacement, sessionIDs, leasing); err != nil {
		t.Fatalf("replacement batch reserve: %v", err)
	}
	reservesBefore, batchesBefore, releasesBefore := leasing.reserveCalls(), leasing.reserveBatchCalls(), leasing.releaseCalls()

	if err := hub.reserveLiveSessions(context.Background(), oldAndroid, sessionIDs, leasing); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("stale batch reserve error = %v, want ErrDenied", err)
	}
	if got := leasing.reserveCalls(); got != reservesBefore {
		t.Fatalf("stale peer changed backend reserve count: got %d, want %d", got, reservesBefore)
	}
	if got := leasing.reserveBatchCalls(); got != batchesBefore {
		t.Fatalf("stale peer called backend batch reserve: got %d, want %d", got, batchesBefore)
	}
	if got := leasing.releaseCalls(); got != releasesBefore {
		t.Fatalf("stale peer called backend release: got %d, want %d", got, releasesBefore)
	}
	for _, sessionID := range sessionIDs {
		if !leasing.reservationActive(userID, sessionID, deviceID) {
			t.Fatalf("stale batch deleted replacement reservation for session %s", sessionID)
		}
	}
}

func TestSingleReserveReplacementDuringIOClearsOnlyStaleOwner(t *testing.T) {
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.leases = leasing
	userID, deviceID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	oldAndroid := testPeer(hub, deviceID)
	oldAndroid.role = "android"
	oldAndroid.principal.UserID = userID
	if !hub.add(oldAndroid) {
		t.Fatal("failed to add old Android")
	}
	if err := hub.reserveLiveSession(context.Background(), oldAndroid, sessionID); err != nil {
		t.Fatalf("initial old reserve: %v", err)
	}
	hub.subscribe(oldAndroid, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})

	reserveStarted, resumeReserve := leasing.blockNextReserve()
	reserveDone := make(chan error, 1)
	go func() {
		reserveDone <- hub.reserveLiveSession(context.Background(), oldAndroid, sessionID)
	}()
	select {
	case <-reserveStarted:
	case <-time.After(time.Second):
		t.Fatal("old reserve did not enter backend")
	}
	replacement := testPeer(hub, deviceID)
	replacement.role = "android"
	replacement.principal.UserID = userID
	if !hub.add(replacement) {
		t.Fatal("failed to add replacement Android during reserve I/O")
	}
	close(resumeReserve)
	if err := <-reserveDone; !errors.Is(err, store.ErrDenied) {
		t.Fatalf("old post-check error = %v, want ErrDenied", err)
	}
	leasing.clearReserveBlock()

	if leasing.reservationActive(userID, sessionID, deviceID) {
		t.Fatal("stale post-check left backend member active")
	}
	if owner := liveReservationOwner(hub, oldAndroid, sessionID); owner != nil {
		t.Fatalf("stale post-check retained owner %p, want nil", owner)
	}
	if got := leasing.releaseCalls(); got != 1 {
		t.Fatalf("stale post-check releases = %d, want 1", got)
	}

	if err := hub.reserveLiveSession(context.Background(), replacement, sessionID); err != nil {
		t.Fatalf("replacement reserve after cleanup: %v", err)
	}
	if err := hub.activateSubscription(
		replacement,
		sessionID,
		store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID},
		0,
		[]byte("replacement-subscribe"),
	); err != nil {
		t.Fatalf("replacement activation after cleanup: %v", err)
	}
	if owner := liveReservationOwner(hub, replacement, sessionID); owner != replacement {
		t.Fatalf("replacement owner = %p, want %p", owner, replacement)
	}
	hub.remove(oldAndroid)
	if got := leasing.releaseCalls(); got != 1 {
		t.Fatalf("old remove repeated backend release: got %d, want 1", got)
	}
	if !leasing.reservationActive(userID, sessionID, deviceID) {
		t.Fatal("old remove deleted replacement backend member")
	}
}

func TestBatchReserveReplacementDuringIOClearsOnlyStaleOwners(t *testing.T) {
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.leases = leasing
	userID, deviceID, pcID := uuid.New(), uuid.New(), uuid.New()
	sessionIDs := []uuid.UUID{uuid.New(), uuid.New(), uuid.New()}
	oldAndroid := testPeer(hub, deviceID)
	oldAndroid.role = "android"
	oldAndroid.principal.UserID = userID
	if !hub.add(oldAndroid) {
		t.Fatal("failed to add old Android")
	}
	if err := hub.reserveLiveSessions(context.Background(), oldAndroid, sessionIDs, leasing); err != nil {
		t.Fatalf("initial old batch reserve: %v", err)
	}
	for _, sessionID := range sessionIDs {
		hub.subscribe(oldAndroid, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	}

	reserveStarted, resumeReserve := leasing.blockNextReserve()
	reserveDone := make(chan error, 1)
	go func() {
		reserveDone <- hub.reserveLiveSessions(context.Background(), oldAndroid, sessionIDs, leasing)
	}()
	select {
	case <-reserveStarted:
	case <-time.After(time.Second):
		t.Fatal("old batch reserve did not enter backend")
	}
	replacement := testPeer(hub, deviceID)
	replacement.role = "android"
	replacement.principal.UserID = userID
	if !hub.add(replacement) {
		t.Fatal("failed to add replacement Android during batch I/O")
	}
	close(resumeReserve)
	if err := <-reserveDone; !errors.Is(err, store.ErrDenied) {
		t.Fatalf("old batch post-check error = %v, want ErrDenied", err)
	}
	leasing.clearReserveBlock()

	if got := leasing.releaseCalls(); got != len(sessionIDs) {
		t.Fatalf("batch post-check releases = %d, want %d", got, len(sessionIDs))
	}
	for _, sessionID := range sessionIDs {
		if leasing.reservationActive(userID, sessionID, deviceID) {
			t.Fatalf("batch post-check left backend member active for %s", sessionID)
		}
		if owner := liveReservationOwner(hub, oldAndroid, sessionID); owner != nil {
			t.Fatalf("batch post-check retained owner %p for %s", owner, sessionID)
		}
	}

	if err := hub.reserveLiveSessions(context.Background(), replacement, sessionIDs, leasing); err != nil {
		t.Fatalf("replacement batch reserve after cleanup: %v", err)
	}
	for _, sessionID := range sessionIDs {
		if err := hub.activateSubscription(
			replacement,
			sessionID,
			store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID},
			0,
			[]byte("replacement-subscribe"),
		); err != nil {
			t.Fatalf("replacement activation for %s: %v", sessionID, err)
		}
		if owner := liveReservationOwner(hub, replacement, sessionID); owner != replacement {
			t.Fatalf("replacement owner = %p, want %p for %s", owner, replacement, sessionID)
		}
	}
	hub.remove(oldAndroid)
	if got := leasing.releaseCalls(); got != len(sessionIDs) {
		t.Fatalf("old remove repeated batch releases: got %d, want %d", got, len(sessionIDs))
	}
	for _, sessionID := range sessionIDs {
		if !leasing.reservationActive(userID, sessionID, deviceID) {
			t.Fatalf("old remove deleted replacement member for %s", sessionID)
		}
	}
}

func TestThreeGenerationSingleReserveClearsOriginalOwner(t *testing.T) {
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.leases = leasing
	userID, deviceID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	oldOwner := testPeer(hub, deviceID)
	oldOwner.role = "android"
	oldOwner.principal.UserID = userID
	if !hub.add(oldOwner) {
		t.Fatal("failed to add original owner O")
	}
	if err := hub.reserveLiveSession(context.Background(), oldOwner, sessionID); err != nil {
		t.Fatalf("original owner reserve: %v", err)
	}
	hub.subscribe(oldOwner, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})

	reserving := testPeer(hub, deviceID)
	reserving.role = "android"
	reserving.principal.UserID = userID
	if !hub.add(reserving) {
		t.Fatal("failed to replace O with reserving generation R")
	}
	reserveStarted, resumeReserve := leasing.blockNextReserve()
	reserveDone := make(chan error, 1)
	go func() {
		reserveDone <- hub.reserveLiveSession(context.Background(), reserving, sessionID)
	}()
	select {
	case <-reserveStarted:
	case <-time.After(time.Second):
		t.Fatal("R did not enter backend reserve")
	}
	latest := testPeer(hub, deviceID)
	latest.role = "android"
	latest.principal.UserID = userID
	if !hub.add(latest) {
		t.Fatal("failed to replace R with latest generation Q")
	}
	close(resumeReserve)
	if err := <-reserveDone; !errors.Is(err, store.ErrDenied) {
		t.Fatalf("R post-check error = %v, want ErrDenied", err)
	}
	leasing.clearReserveBlock()

	if leasing.reservationActive(userID, sessionID, deviceID) {
		t.Fatal("R rollback left backend member active")
	}
	if owner := liveReservationOwner(hub, oldOwner, sessionID); owner != nil {
		t.Fatalf("R rollback retained stale O owner %p", owner)
	}
	if got := leasing.releaseCalls(); got != 1 {
		t.Fatalf("R rollback releases = %d, want 1", got)
	}

	if err := hub.reserveLiveSession(context.Background(), latest, sessionID); err != nil {
		t.Fatalf("Q reserve after R rollback: %v", err)
	}
	if err := hub.activateSubscription(
		latest,
		sessionID,
		store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID},
		0,
		[]byte("q-subscribe"),
	); err != nil {
		t.Fatalf("Q activation: %v", err)
	}
	hub.releaseLiveSession(context.Background(), oldOwner, sessionID)
	hub.releaseLiveSession(context.Background(), reserving, sessionID)
	hub.remove(oldOwner)
	hub.remove(reserving)
	if got := leasing.releaseCalls(); got != 1 {
		t.Fatalf("O/R cleanup released Q member: got %d releases, want 1", got)
	}
	if !leasing.reservationActive(userID, sessionID, deviceID) {
		t.Fatal("O/R cleanup deleted Q backend member")
	}
	if owner := liveReservationOwner(hub, latest, sessionID); owner != latest {
		t.Fatalf("owner after O/R cleanup = %p, want Q %p", owner, latest)
	}
}

func TestThreeGenerationBatchReserveClearsOriginalOwners(t *testing.T) {
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.leases = leasing
	userID, deviceID, pcID := uuid.New(), uuid.New(), uuid.New()
	sessionIDs := []uuid.UUID{uuid.New(), uuid.New(), uuid.New()}
	oldOwner := testPeer(hub, deviceID)
	oldOwner.role = "android"
	oldOwner.principal.UserID = userID
	if !hub.add(oldOwner) {
		t.Fatal("failed to add original owner O")
	}
	if err := hub.reserveLiveSessions(context.Background(), oldOwner, sessionIDs, leasing); err != nil {
		t.Fatalf("original owner batch reserve: %v", err)
	}
	for _, sessionID := range sessionIDs {
		hub.subscribe(oldOwner, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	}

	reserving := testPeer(hub, deviceID)
	reserving.role = "android"
	reserving.principal.UserID = userID
	if !hub.add(reserving) {
		t.Fatal("failed to replace O with reserving generation R")
	}
	reserveStarted, resumeReserve := leasing.blockNextReserve()
	reserveDone := make(chan error, 1)
	go func() {
		reserveDone <- hub.reserveLiveSessions(context.Background(), reserving, sessionIDs, leasing)
	}()
	select {
	case <-reserveStarted:
	case <-time.After(time.Second):
		t.Fatal("R did not enter backend batch reserve")
	}
	latest := testPeer(hub, deviceID)
	latest.role = "android"
	latest.principal.UserID = userID
	if !hub.add(latest) {
		t.Fatal("failed to replace R with latest generation Q")
	}
	close(resumeReserve)
	if err := <-reserveDone; !errors.Is(err, store.ErrDenied) {
		t.Fatalf("R batch post-check error = %v, want ErrDenied", err)
	}
	leasing.clearReserveBlock()

	if got := leasing.releaseCalls(); got != len(sessionIDs) {
		t.Fatalf("R batch rollback releases = %d, want %d", got, len(sessionIDs))
	}
	for _, sessionID := range sessionIDs {
		if leasing.reservationActive(userID, sessionID, deviceID) {
			t.Fatalf("R rollback left backend member active for %s", sessionID)
		}
		if owner := liveReservationOwner(hub, oldOwner, sessionID); owner != nil {
			t.Fatalf("R rollback retained stale O owner %p for %s", owner, sessionID)
		}
	}

	if err := hub.reserveLiveSessions(context.Background(), latest, sessionIDs, leasing); err != nil {
		t.Fatalf("Q batch reserve after R rollback: %v", err)
	}
	for _, sessionID := range sessionIDs {
		if err := hub.activateSubscription(
			latest,
			sessionID,
			store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID},
			0,
			[]byte("q-subscribe"),
		); err != nil {
			t.Fatalf("Q activation for %s: %v", sessionID, err)
		}
		hub.releaseLiveSession(context.Background(), oldOwner, sessionID)
		hub.releaseLiveSession(context.Background(), reserving, sessionID)
	}
	hub.remove(oldOwner)
	hub.remove(reserving)
	if got := leasing.releaseCalls(); got != len(sessionIDs) {
		t.Fatalf("O/R cleanup released Q members: got %d, want %d", got, len(sessionIDs))
	}
	for _, sessionID := range sessionIDs {
		if !leasing.reservationActive(userID, sessionID, deviceID) {
			t.Fatalf("O/R cleanup deleted Q member for %s", sessionID)
		}
		if owner := liveReservationOwner(hub, latest, sessionID); owner != latest {
			t.Fatalf("owner after O/R cleanup = %p, want Q %p for %s", owner, latest, sessionID)
		}
	}
}

func TestOldRemoveCannotRaceReplacementReserveAndReleaseIt(t *testing.T) {
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.leases = leasing
	userID, deviceID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	oldAndroid := testPeer(hub, deviceID)
	oldAndroid.role = "android"
	oldAndroid.principal.UserID = userID
	if !hub.add(oldAndroid) {
		t.Fatal("failed to add old Android")
	}
	if err := hub.reserveLiveSession(context.Background(), oldAndroid, sessionID); err != nil {
		t.Fatalf("old reserve: %v", err)
	}
	hub.subscribe(oldAndroid, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})

	replacement := testPeer(hub, deviceID)
	replacement.role = "android"
	replacement.principal.UserID = userID
	if !hub.add(replacement) {
		t.Fatal("failed to add replacement Android")
	}
	reserveStarted, resumeReserve := leasing.blockNextReserve()
	reserveDone := make(chan error, 1)
	go func() {
		reserveDone <- hub.reserveLiveSession(context.Background(), replacement, sessionID)
	}()
	select {
	case <-reserveStarted:
	case <-time.After(time.Second):
		t.Fatal("replacement reserve did not enter backend")
	}

	removeDone := make(chan struct{})
	go func() {
		hub.remove(oldAndroid)
		close(removeDone)
	}()
	close(resumeReserve)
	if err := <-reserveDone; err != nil {
		t.Fatalf("replacement reserve: %v", err)
	}
	leasing.clearReserveBlock()
	select {
	case <-removeDone:
	case <-time.After(time.Second):
		t.Fatal("old remove did not finish")
	}
	if err := hub.activateSubscription(
		replacement,
		sessionID,
		store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID},
		305,
		[]byte("replacement-subscribe"),
	); err != nil {
		t.Fatalf("replacement activate: %v", err)
	}
	if !leasing.reservationActive(userID, sessionID, deviceID) {
		t.Fatal("old remove deleted replacement reservation")
	}
	if got := leasing.releaseCalls(); got != 0 {
		t.Fatalf("backend releases = %d, want 0", got)
	}
}

func TestCurrentUnsubscribeAndFinalRemoveReleaseReservation(t *testing.T) {
	for _, test := range []struct {
		name    string
		release func(*Hub, *peer, uuid.UUID, uuid.UUID)
	}{
		{
			name: "unsubscribe",
			release: func(hub *Hub, android *peer, sessionID, pcID uuid.UUID) {
				if err := android.route(androidRouteEnvelope(realtimewire.Unsubscribe, pcID), sessionID, []byte("unsubscribe")); err != nil {
					t.Fatalf("unsubscribe route: %v", err)
				}
			},
		},
		{
			name: "final disconnect",
			release: func(hub *Hub, android *peer, _, _ uuid.UUID) {
				hub.remove(android)
			},
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			leasing := &fakeLiveSessionStore{}
			hub := NewHubWithObserver(nil, nil, nil, nil)
			hub.leases = leasing
			userID, deviceID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
			android := testPeer(hub, deviceID)
			android.role = "android"
			android.principal.UserID = userID
			if !hub.add(android) {
				t.Fatal("failed to add Android")
			}
			if err := hub.reserveLiveSession(context.Background(), android, sessionID); err != nil {
				t.Fatalf("reserve: %v", err)
			}
			if err := hub.activateSubscription(
				android,
				sessionID,
				store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID},
				0,
				[]byte("subscribe"),
			); err != nil {
				t.Fatalf("activate: %v", err)
			}
			test.release(hub, android, sessionID, pcID)
			if leasing.reservationActive(userID, sessionID, deviceID) {
				t.Fatal("current owner release left reservation active")
			}
			if got := leasing.releaseCalls(); got != 1 {
				t.Fatalf("backend releases = %d, want 1", got)
			}
		})
	}
}

func TestExpiredPCRouteReleasesReplayLockBeforeReservationCleanup(t *testing.T) {
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.leases = leasing
	userID, deviceID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	oldAndroid := testPeer(hub, deviceID)
	oldAndroid.role = "android"
	oldAndroid.principal.UserID = userID
	pc := testPeer(hub, pcID)
	pc.role = "pc"
	pc.principal.UserID = userID
	if !hub.add(oldAndroid) || !hub.add(pc) {
		t.Fatal("failed to add initial peers")
	}
	if err := hub.reserveLiveSession(context.Background(), oldAndroid, sessionID); err != nil {
		t.Fatalf("old reserve: %v", err)
	}
	expiredAt := time.Now().Add(-time.Second)
	hub.subscribe(oldAndroid, sessionID, store.SessionRouteAuthorization{
		Permission:     "VIEW",
		PCDeviceID:     pcID,
		GrantExpiresAt: &expiredAt,
	})

	replacement := testPeer(hub, deviceID)
	replacement.role = "android"
	replacement.principal.UserID = userID
	if !hub.add(replacement) {
		t.Fatal("failed to add replacement Android")
	}
	if err := hub.reserveLiveSession(context.Background(), replacement, sessionID); err != nil {
		t.Fatalf("replacement reserve: %v", err)
	}

	expiredReached := make(chan struct{})
	allowCleanup := make(chan struct{})
	hub.beforeExpiredReservationCleanup = func() {
		close(expiredReached)
		<-allowCleanup
	}
	routeDone := make(chan error, 1)
	go func() {
		envelope := realtimewire.Envelope{RecipientDeviceID: deviceID[:], MessageType: realtimewire.OutputChunk, Sequence: 1}
		routeDone <- pc.routePCFrame(envelope, sessionID, deviceID, []byte("expired-output"))
	}()
	select {
	case <-expiredReached:
	case <-time.After(time.Second):
		t.Fatal("PC route did not reach expired cleanup seam")
	}

	activationDone := make(chan error, 1)
	go func() {
		activationDone <- hub.activateSubscription(
			replacement,
			sessionID,
			store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID},
			0,
			[]byte("replacement-subscribe"),
		)
	}()
	shard := hub.reservationShard(reservationKey(replacement, sessionID))
	deadline := time.Now().Add(time.Second)
	for {
		if !shard.mu.TryLock() {
			break
		}
		shard.mu.Unlock()
		if time.Now().After(deadline) {
			t.Fatal("replacement activation did not acquire reservation shard")
		}
		time.Sleep(time.Millisecond)
	}
	close(allowCleanup)

	select {
	case err := <-routeDone:
		if got := routeDenialClass(err); got != "subscription_authority_expired" {
			t.Fatalf("expired route denial = %q (%v)", got, err)
		}
	case <-time.After(time.Second):
		t.Fatal("expired route deadlocked replay lock against reservation shard")
	}
	select {
	case err := <-activationDone:
		if err != nil {
			t.Fatalf("replacement activation: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("replacement activation deadlocked reservation shard against replay lock")
	}
	if !leasing.reservationActive(userID, sessionID, deviceID) {
		t.Fatal("expired old route cleanup deleted replacement reservation")
	}
}

func TestPCOutputRejectsSubscriptionOwnedByAnotherPC(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID := uuid.New()
	pcID := uuid.New()
	androidID := uuid.New()
	sessionID := uuid.New()
	pc := testPeer(hub, pcID)
	pc.role = "pc"
	pc.principal.UserID = userID
	android := testPeer(hub, androidID)
	android.principal.UserID = userID
	hub.peers[pcID] = pc
	hub.peers[androidID] = android
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{
		Permission: "VIEW",
		PCDeviceID: uuid.New(),
	})

	err := pc.route(realtimewire.Envelope{
		RecipientDeviceID: androidID[:],
		MessageType:       realtimewire.OutputChunk,
	}, sessionID, []byte("must-not-route"))
	if !errors.Is(err, store.ErrDenied) {
		t.Fatalf("wrong-owner route error = %v, want ErrDenied", err)
	}
	select {
	case <-android.send:
		t.Fatal("wrong PC owner routed output to subscriber")
	default:
	}
}

func TestReplacementPCImmediatelyReceivesExistingAndroidSubscriptions(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID := uuid.New()
	pcID := uuid.New()
	androidID := uuid.New()
	sessionID := uuid.New()
	android := testPeer(hub, androidID)
	android.role = "android"
	android.principal.UserID = userID
	hub.peers[androidID] = android
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{
		Permission: "CONTROL",
		PCDeviceID: pcID,
	})
	subscribeFrame := []byte("signed-subscribe-ack-73")
	if !hub.rememberSubscriptionFrame(android, sessionID, 73, subscribeFrame) {
		t.Fatal("authorized Android subscription was not cached")
	}

	replacement := testPeer(hub, pcID)
	replacement.role = "pc"
	replacement.principal.UserID = userID
	hub.peers[pcID] = replacement
	hub.replaySubscriptionsToPC(replacement)

	select {
	case received := <-replacement.send:
		if string(received) != string(subscribeFrame) {
			t.Fatalf("replayed subscription = %q, want %q", received, subscribeFrame)
		}
	default:
		t.Fatal("replacement PC did not immediately receive the existing subscription")
	}

	outputFrame := []byte("output-after-pc-reconnect")
	if err := replacement.route(realtimewire.Envelope{
		RecipientDeviceID: androidID[:],
		MessageType:       realtimewire.OutputChunk,
	}, sessionID, outputFrame); err != nil {
		t.Fatalf("replacement PC could not route output: %v", err)
	}
	select {
	case received := <-android.send:
		if string(received) != string(outputFrame) {
			t.Fatalf("routed output = %q, want %q", received, outputFrame)
		}
	default:
		t.Fatal("Android did not receive output after PC reconnect")
	}
}

func TestReplacementPCReceivesLatestSubscriberControlWithoutACKRegression(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	authority := newCountingRouteAuthority()
	hub.store = authority
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	authority.set(userID, androidID, sessionID, store.SessionRouteAuthorization{
		Permission: "VIEW",
		PCDeviceID: pcID,
	}, nil)
	android, oldPC := activeRoutePeers(hub, userID, androidID, pcID)

	subscribeFrame := []byte("signed-subscribe-ack-0")
	subscribe := androidRouteEnvelope(realtimewire.Subscribe, pcID)
	subscribe.ACK = 0
	if err := android.route(subscribe, sessionID, subscribeFrame); err != nil {
		t.Fatalf("initial subscribe: %v", err)
	}
	if got := <-oldPC.send; string(got) != string(subscribeFrame) {
		t.Fatalf("initial subscription frame = %q, want %q", got, subscribeFrame)
	}

	outputACKFrame := []byte("signed-output-ack-100")
	outputACK := androidRouteEnvelope(realtimewire.OutputACK, pcID)
	outputACK.ACK = 100
	if err := android.route(outputACK, sessionID, outputACKFrame); err != nil {
		t.Fatalf("output ACK 100: %v", err)
	}
	if got := <-oldPC.send; string(got) != string(outputACKFrame) {
		t.Fatalf("live output ACK frame = %q, want %q", got, outputACKFrame)
	}

	flowFrame := []byte("signed-flow-control-ack-100-new-epoch")
	wantFlowFrame := append([]byte(nil), flowFrame...)
	flow := androidRouteEnvelope(realtimewire.FlowControl, pcID)
	flow.ACK = 100
	if err := android.route(flow, sessionID, flowFrame); err != nil {
		t.Fatalf("flow control ACK 100: %v", err)
	}
	if got := <-oldPC.send; string(got) != string(wantFlowFrame) {
		t.Fatalf("live flow-control frame = %q, want %q", got, wantFlowFrame)
	}
	flowFrame[0] = 'X'

	regressive := androidRouteEnvelope(realtimewire.OutputACK, pcID)
	regressive.ACK = 99
	if err := android.route(regressive, sessionID, []byte("signed-output-ack-99")); !errors.Is(err, errCumulativeACKRegressed) {
		t.Fatalf("regressive ACK error = %v, want errCumulativeACKRegressed", err)
	}
	select {
	case frame := <-oldPC.send:
		t.Fatalf("regressive ACK reached PC: %q", frame)
	default:
	}

	replacement := testPeer(hub, pcID)
	replacement.role = "pc"
	replacement.principal.UserID = userID
	replacement.subscriptionReplayReady = false
	if !hub.add(replacement) {
		t.Fatal("failed to install replacement PC")
	}
	hub.replaySubscriptionsToPC(replacement)

	if got := <-replacement.send; string(got) != string(subscribeFrame) {
		t.Fatalf("replacement first frame = %q, want original SUBSCRIBE %q", got, subscribeFrame)
	}
	if got := <-replacement.send; string(got) != string(wantFlowFrame) {
		t.Fatalf("replacement latest control = %q, want byte-identical %q", got, wantFlowFrame)
	}
	select {
	case frame := <-replacement.send:
		t.Fatalf("replacement received stale or regressive control: %q", frame)
	default:
	}
	if got := pcRouteHighWater(replacement, sessionID, androidID); got != 100 {
		t.Fatalf("replacement replay high-water = %d, want latest visible ACK 100", got)
	}
}

func TestReplacementPCSerializesConcurrentReplayAndSubscriberControl(t *testing.T) {
	for iteration := 0; iteration < 50; iteration++ {
		hub := NewHubWithObserver(nil, nil, nil, nil)
		authority := newCountingRouteAuthority()
		hub.store = authority
		userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
		authority.set(userID, androidID, sessionID, store.SessionRouteAuthorization{
			Permission: "VIEW",
			PCDeviceID: pcID,
		}, nil)
		android, oldPC := activeRoutePeers(hub, userID, androidID, pcID)

		subscribeFrame := []byte("signed-subscribe-ack-0")
		subscribe := androidRouteEnvelope(realtimewire.Subscribe, pcID)
		if err := android.route(subscribe, sessionID, subscribeFrame); err != nil {
			t.Fatalf("iteration %d initial subscribe: %v", iteration, err)
		}
		<-oldPC.send

		replacement := testPeer(hub, pcID)
		replacement.role = "pc"
		replacement.principal.UserID = userID
		replacement.subscriptionReplayReady = false
		if !hub.add(replacement) {
			t.Fatalf("iteration %d failed to install replacement PC", iteration)
		}

		controlFrame := []byte("signed-flow-control-ack-128")
		control := androidRouteEnvelope(realtimewire.FlowControl, pcID)
		control.ACK = 128
		replayDone := make(chan struct{})
		controlResult := make(chan error, 1)

		// Hold the common serialization gate until both operations are ready.
		// Either may win after release; both orders must produce exactly one
		// SUBSCRIBE followed by exactly one latest control frame.
		replacement.pcReplayMu.Lock()
		go func() {
			hub.replaySubscriptionsToPC(replacement)
			close(replayDone)
		}()
		go func() {
			controlResult <- android.route(control, sessionID, controlFrame)
		}()
		replacement.pcReplayMu.Unlock()

		if err := <-controlResult; err != nil {
			t.Fatalf("iteration %d concurrent control: %v", iteration, err)
		}
		<-replayDone
		if got := <-replacement.send; string(got) != string(subscribeFrame) {
			t.Fatalf("iteration %d first frame = %q, want SUBSCRIBE %q", iteration, got, subscribeFrame)
		}
		if got := <-replacement.send; string(got) != string(controlFrame) {
			t.Fatalf("iteration %d second frame = %q, want control %q", iteration, got, controlFrame)
		}
		select {
		case frame := <-replacement.send:
			t.Fatalf("iteration %d received duplicate replay frame: %q", iteration, frame)
		default:
		}
		if got := pcRouteHighWater(replacement, sessionID, androidID); got != 128 {
			t.Fatalf("iteration %d replay high-water = %d, want 128", iteration, got)
		}
	}
}

func TestLiveSubscriberACKAdvancesFenceWithoutOpeningReplayEpoch(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	pc := testPeer(hub, uuid.New())
	sessionID, androidID := uuid.New(), uuid.New()
	state := pc.pcReplayRoute(pcRouteKey{sessionID: sessionID, recipientID: androidID})
	state.highWater = 200

	pc.advancePCVisibleACKLocked(sessionID, androidID, 128)
	if state.highWater != 200 {
		t.Fatalf("lagging live ACK lowered high-water to %d, want 200", state.highWater)
	}
	if state.epoch != nil {
		t.Fatal("lagging live ACK unexpectedly opened a replay epoch")
	}

	pc.advancePCVisibleACKLocked(sessionID, androidID, 250)
	if state.highWater != 250 {
		t.Fatalf("advanced live ACK high-water = %d, want 250", state.highWater)
	}
	if state.epoch != nil {
		t.Fatal("advanced live ACK unexpectedly opened a replay epoch")
	}
}

func TestUnsubscribeRemovesCachedSubscriptionReplay(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID := uuid.New()
	pcID := uuid.New()
	android := testPeer(hub, uuid.New())
	android.role = "android"
	android.principal.UserID = userID
	sessionID := uuid.New()
	hub.peers[android.principal.Device.ID] = android
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{PCDeviceID: pcID})
	if !hub.rememberSubscriptionFrame(android, sessionID, 4, []byte("subscribe")) {
		t.Fatal("authorized Android subscription was not cached")
	}
	if !hub.unsubscribe(android, sessionID) {
		t.Fatal("subscription was not removed")
	}

	replacement := testPeer(hub, pcID)
	replacement.role = "pc"
	replacement.principal.UserID = userID
	hub.peers[pcID] = replacement
	hub.replaySubscriptionsToPC(replacement)
	select {
	case frame := <-replacement.send:
		t.Fatalf("unsubscribed frame was replayed: %q", frame)
	default:
	}
}

func TestDisableAccountClosesOnlyMatchingPeers(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	lockedUser := uuid.New()
	otherUser := uuid.New()
	lockedPC := testPeer(hub, uuid.New())
	lockedAndroid := testPeer(hub, uuid.New())
	otherAndroid := testPeer(hub, uuid.New())
	lockedPC.principal.UserID = lockedUser
	lockedAndroid.principal.UserID = lockedUser
	otherAndroid.principal.UserID = otherUser
	hub.peers[lockedPC.principal.Device.ID] = lockedPC
	hub.peers[lockedAndroid.principal.Device.ID] = lockedAndroid
	hub.peers[otherAndroid.principal.Device.ID] = otherAndroid

	hub.DisableAccount(lockedUser)
	for name, peer := range map[string]*peer{"pc": lockedPC, "android": lockedAndroid} {
		select {
		case <-peer.done:
		default:
			t.Fatalf("locked account %s peer remained open", name)
		}
	}
	select {
	case <-otherAndroid.done:
		t.Fatal("unrelated account peer was closed")
	default:
	}
}

func TestRefreshDeviceClosesOnlyCurrentDevice(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	target := testPeer(hub, uuid.New())
	other := testPeer(hub, uuid.New())
	hub.peers[target.principal.Device.ID] = target
	hub.peers[other.principal.Device.ID] = other

	hub.RefreshDevice(target.principal.Device.ID)
	select {
	case <-target.done:
	default:
		t.Fatal("authorization-changed device remained open")
	}
	select {
	case <-other.done:
		t.Fatal("unrelated device was closed")
	default:
	}
}

func TestAndroidSubscribedRouteReusesInitialAuthorityForInputResizeAndACK(t *testing.T) {
	authority := newCountingRouteAuthority()
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store = authority
	hub.leases = leasing
	userID, androidID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	authority.set(userID, androidID, sessionID, store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID}, nil)
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	leasing.setLease(sessionID, androidID)

	if err := android.route(androidRouteEnvelope(realtimewire.Subscribe, pcID), sessionID, []byte("subscribe")); err != nil {
		t.Fatalf("subscribe route: %v", err)
	}
	<-pc.send
	for index, messageType := range []realtimewire.MessageType{
		realtimewire.InputEvent,
		realtimewire.ResizeRequest,
		realtimewire.OutputACK,
		realtimewire.FlowControl,
		realtimewire.ResumeRequest,
	} {
		frame := []byte(fmt.Sprintf("hot-frame-%d", index))
		if err := android.route(androidRouteEnvelope(messageType, pcID), sessionID, frame); err != nil {
			t.Fatalf("route message %d: %v", messageType, err)
		}
		if got := <-pc.send; string(got) != string(frame) {
			t.Fatalf("PC frame = %q, want %q", got, frame)
		}
	}
	if got := authority.callsFor(userID, androidID, sessionID); got != 1 {
		t.Fatalf("SessionRoute calls = %d, want only the initial subscription lookup", got)
	}
}

func TestAndroidRouteCacheInvalidatesOnUnsubscribeAndRevoke(t *testing.T) {
	for _, test := range []struct {
		name       string
		invalidate func(*Hub, *peer, uuid.UUID, uuid.UUID)
	}{
		{
			name: "unsubscribe",
			invalidate: func(_ *Hub, android *peer, sessionID, pcID uuid.UUID) {
				if err := android.route(androidRouteEnvelope(realtimewire.Unsubscribe, pcID), sessionID, []byte("unsubscribe")); err != nil {
					t.Fatalf("unsubscribe route: %v", err)
				}
			},
		},
		{
			name: "revoke",
			invalidate: func(hub *Hub, android *peer, _, _ uuid.UUID) {
				hub.Revoke(android.principal.Device.ID)
			},
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			authority := newCountingRouteAuthority()
			leasing := &fakeLiveSessionStore{}
			hub := NewHubWithObserver(nil, nil, nil, nil)
			hub.store, hub.leases = authority, leasing
			userID, androidID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
			authority.set(userID, androidID, sessionID, store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID}, nil)
			android, pc := activeRoutePeers(hub, userID, androidID, pcID)
			if err := android.route(androidRouteEnvelope(realtimewire.Subscribe, pcID), sessionID, []byte("subscribe")); err != nil {
				t.Fatalf("subscribe route: %v", err)
			}
			<-pc.send

			test.invalidate(hub, android, sessionID, pcID)
			if test.name == "unsubscribe" {
				<-pc.send
			}
			if err := android.route(androidRouteEnvelope(realtimewire.OutputACK, pcID), sessionID, []byte("denied")); !errors.Is(err, store.ErrDenied) {
				t.Fatalf("post-invalidation route error = %v, want ErrDenied", err)
			}
			if got := authority.callsFor(userID, androidID, sessionID); got != 1 {
				t.Fatalf("post-invalidation SessionRoute calls = %d, want 1", got)
			}
		})
	}
}

func TestAndroidExpiredRouteRevalidatesAndFailsClosed(t *testing.T) {
	authority := newCountingRouteAuthority()
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store, hub.leases = authority, leasing
	userID, androidID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	authority.set(userID, androidID, sessionID, store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID}, nil)
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	if err := android.route(androidRouteEnvelope(realtimewire.Subscribe, pcID), sessionID, []byte("subscribe")); err != nil {
		t.Fatalf("subscribe route: %v", err)
	}
	<-pc.send

	authority.set(userID, androidID, sessionID, store.SessionRouteAuthorization{}, store.ErrDenied)
	hub.mu.Lock()
	cached := android.subscriptionRoutes[sessionID]
	cached.validUntil = time.Now().Add(-time.Second)
	android.subscriptionRoutes[sessionID] = cached
	hub.mu.Unlock()
	if err := android.route(androidRouteEnvelope(realtimewire.OutputACK, pcID), sessionID, []byte("expired")); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("expired route error = %v, want ErrDenied", err)
	}
	if got := authority.callsFor(userID, androidID, sessionID); got != 2 {
		t.Fatalf("SessionRoute calls after cache expiry = %d, want relookup count 2", got)
	}
	if _, subscribed := hub.subscriptionRoute(android, sessionID); subscribed {
		t.Fatal("authority-denied route remained subscribed")
	}
	if leasing.releaseCalls() != 1 {
		t.Fatalf("live-session releases = %d, want 1", leasing.releaseCalls())
	}
	if err := android.route(androidRouteEnvelope(realtimewire.OutputACK, pcID), sessionID, []byte("still-denied")); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("route after failed revalidation = %v, want ErrDenied", err)
	}
	if got := authority.callsFor(userID, androidID, sessionID); got != 2 {
		t.Fatalf("unsubscribed route performed another lookup: %d", got)
	}
}

func TestAndroidRouteTransientFailureUsesBoundedVerifiedRouteGrace(t *testing.T) {
	authority := newCountingRouteAuthority()
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store, hub.leases = authority, leasing
	userID, androidID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	route := store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID}
	authority.set(userID, androidID, sessionID, route, nil)
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	if err := android.route(androidRouteEnvelope(realtimewire.Subscribe, pcID), sessionID, []byte("subscribe")); err != nil {
		t.Fatalf("subscribe route: %v", err)
	}
	<-pc.send
	authority.set(userID, androidID, sessionID, store.SessionRouteAuthorization{}, errors.New("database unavailable"))

	hub.mu.Lock()
	cached := android.subscriptionRoutes[sessionID]
	cached.validUntil = time.Now().Add(-time.Second)
	android.subscriptionRoutes[sessionID] = cached
	hub.mu.Unlock()
	if err := android.route(androidRouteEnvelope(realtimewire.OutputACK, pcID), sessionID, []byte("within-grace")); err != nil {
		t.Fatalf("transient route within grace: %v", err)
	}
	if got := string(<-pc.send); got != "within-grace" {
		t.Fatalf("transient route frame = %q", got)
	}

	hub.mu.Lock()
	cached = android.subscriptionRoutes[sessionID]
	cached.failureUntil = time.Now().Add(-time.Second)
	android.subscriptionRoutes[sessionID] = cached
	hub.mu.Unlock()
	if err := android.route(androidRouteEnvelope(realtimewire.OutputACK, pcID), sessionID, []byte("after-grace")); err == nil {
		t.Fatal("transient route remained authorized after failure grace")
	}
	if _, subscribed := hub.subscriptionRoute(android, sessionID); subscribed {
		t.Fatal("expired transient route remained subscribed")
	}
}

func TestAndroidRouteCacheRejectsWrongSessionPCAndPCConnection(t *testing.T) {
	authority := newCountingRouteAuthority()
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store, hub.leases = authority, leasing
	userID, androidID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	authority.set(userID, androidID, sessionID, store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID}, nil)
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	if err := android.route(androidRouteEnvelope(realtimewire.Subscribe, pcID), sessionID, []byte("subscribe")); err != nil {
		t.Fatalf("subscribe route: %v", err)
	}
	<-pc.send

	if err := android.route(androidRouteEnvelope(realtimewire.OutputACK, pcID), uuid.New(), []byte("wrong-session")); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("wrong-session error = %v, want ErrDenied", err)
	}
	if err := android.route(androidRouteEnvelope(realtimewire.OutputACK, uuid.New()), sessionID, []byte("wrong-pc")); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("wrong-PC error = %v, want ErrDenied", err)
	}
	pc.role = "android"
	if err := android.route(androidRouteEnvelope(realtimewire.OutputACK, pcID), sessionID, []byte("wrong-role")); err != nil {
		t.Fatalf("valid cached route should tolerate an offline/non-PC recipient connection: %v", err)
	}
	select {
	case frame := <-pc.send:
		t.Fatalf("frame reached invalid PC connection: %q", frame)
	default:
	}
	if got := authority.callsFor(userID, androidID, sessionID); got != 1 {
		t.Fatalf("binding denials caused unexpected SessionRoute calls: %d", got)
	}
}

func TestRefreshSubscriptionsUsesOneBatchAuthorityQuery(t *testing.T) {
	authority := newBatchedRouteAuthority()
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store, hub.leases = authority, leasing
	userID, androidID, pcID := uuid.New(), uuid.New(), uuid.New()
	android := activeSubscriptionPeer(hub, userID, androidID)
	const subscriptionCount = 12
	for range subscriptionCount {
		sessionID := uuid.New()
		route := store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID}
		authority.setRoute(sessionID, route)
		hub.subscribe(android, sessionID, route)
	}

	android.refreshSubscriptions()
	batchCalls, singleCalls, maxBatch := authority.counts()
	if batchCalls != 1 || singleCalls != 0 {
		t.Fatalf("authority calls batch/single = %d/%d, want 1/0", batchCalls, singleCalls)
	}
	if maxBatch != subscriptionCount {
		t.Fatalf("largest authority batch = %d, want %d", maxBatch, subscriptionCount)
	}
	if leasing.reserveCalls() != subscriptionCount {
		t.Fatalf("live-session renewals = %d, want %d", leasing.reserveCalls(), subscriptionCount)
	}
	if leasing.reserveBatchCalls() != 1 {
		t.Fatalf("live-session reservation batches = %d, want 1", leasing.reserveBatchCalls())
	}
	if got := subscriptionCountForPeer(hub, android); got != subscriptionCount {
		t.Fatalf("subscriptions after valid refresh = %d, want %d", got, subscriptionCount)
	}
}

func TestRefreshSubscriptionsBatchReservationErrorUsesBoundedVerifiedRouteGrace(t *testing.T) {
	authority := newBatchedRouteAuthority()
	leasing := &fakeLiveSessionStore{reserveErr: errors.New("Valkey unavailable")}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store, hub.leases = authority, leasing
	android := activeSubscriptionPeer(hub, uuid.New(), uuid.New())
	for range 4 {
		sessionID := uuid.New()
		route := store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: uuid.New()}
		authority.setRoute(sessionID, route)
		hub.subscribe(android, sessionID, route)
	}

	android.refreshSubscriptions()
	if got := subscriptionCountForPeer(hub, android); got != 4 {
		t.Fatalf("subscriptions during transient reservation error = %d, want 4", got)
	}
	if leasing.reserveBatchCalls() != 1 || leasing.releaseCalls() != 0 {
		t.Fatalf("reservation batches/releases = %d/%d, want 1/0", leasing.reserveBatchCalls(), leasing.releaseCalls())
	}
	expireSubscriptionFailureGrace(hub, android)
	android.refreshSubscriptions()
	if got := subscriptionCountForPeer(hub, android); got != 0 {
		t.Fatalf("subscriptions after reservation failure grace = %d, want 0", got)
	}
	if leasing.reserveBatchCalls() != 2 || leasing.releaseCalls() != 4 {
		t.Fatalf("reservation batches/releases after grace = %d/%d, want 2/4", leasing.reserveBatchCalls(), leasing.releaseCalls())
	}
}

func TestPeerEnqueueHealthyPathAllocatesOnlyFrameCopy(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	peer := testPeer(hub, uuid.New())
	frame := []byte("opaque terminal frame")
	allocations := testing.AllocsPerRun(1000, func() {
		peer.enqueue(frame)
		queued := <-peer.send
		peer.releaseQueueBytes(int64(len(queued)))
	})
	if allocations > 1 {
		t.Fatalf("healthy enqueue allocations = %.2f, want at most one owned frame copy", allocations)
	}
	if queued := peer.queuedBytes.Load(); queued != 0 {
		t.Fatalf("healthy enqueue retained %d queued bytes", queued)
	}
}

func BenchmarkPeerEnqueueHealthyPath(b *testing.B) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	peer := testPeer(hub, uuid.New())
	frame := []byte("opaque terminal frame")
	b.ReportAllocs()
	b.SetBytes(int64(len(frame)))
	for b.Loop() {
		peer.enqueue(frame)
		queued := <-peer.send
		peer.releaseQueueBytes(int64(len(queued)))
	}
}

func BenchmarkPCOutputCachedRoute(b *testing.B) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	route := store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID}
	hub.subscribe(android, sessionID, route)
	frame := []byte("opaque terminal output frame")
	envelope := realtimewire.Envelope{RecipientDeviceID: androidID[:]}
	b.ReportAllocs()
	b.SetBytes(int64(len(frame)))
	for b.Loop() {
		if err := pc.route(envelope, sessionID, frame); err != nil {
			b.Fatal(err)
		}
		queued := <-android.send
		android.releaseQueueBytes(int64(len(queued)))
	}
}

func TestRefreshSubscriptionsBatchMissingAndExpiredResultsUnsubscribe(t *testing.T) {
	authority := newBatchedRouteAuthority()
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store, hub.leases = authority, leasing
	userID, androidID, pcID := uuid.New(), uuid.New(), uuid.New()
	android := activeSubscriptionPeer(hub, userID, androidID)
	validID, expiredID, missingID := uuid.New(), uuid.New(), uuid.New()
	validRoute := store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID}
	expiredAt := time.Now().Add(-time.Second)
	expiredRoute := store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID, GrantExpiresAt: &expiredAt}
	for sessionID, route := range map[uuid.UUID]store.SessionRouteAuthorization{
		validID: validRoute, expiredID: expiredRoute, missingID: validRoute,
	} {
		hub.subscribe(android, sessionID, route)
	}
	authority.setRoute(validID, validRoute)
	authority.setRoute(expiredID, expiredRoute)

	android.refreshSubscriptions()
	if _, ok := hub.subscriptionRoute(android, validID); !ok {
		t.Fatal("valid batch result was unsubscribed")
	}
	for _, sessionID := range []uuid.UUID{expiredID, missingID} {
		if _, ok := hub.subscriptionRoute(android, sessionID); ok {
			t.Fatalf("unauthorized session %s remained subscribed", sessionID)
		}
	}
	if leasing.releaseCalls() != 2 {
		t.Fatalf("live-session releases = %d, want 2", leasing.releaseCalls())
	}
}

func TestRefreshSubscriptionsBatchErrorUsesBoundedVerifiedRouteGrace(t *testing.T) {
	authority := newBatchedRouteAuthority()
	authority.err = errors.New("authority database unavailable")
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store, hub.leases = authority, leasing
	android := activeSubscriptionPeer(hub, uuid.New(), uuid.New())
	for range 4 {
		hub.subscribe(android, uuid.New(), store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: uuid.New()})
	}

	android.refreshSubscriptions()
	if got := subscriptionCountForPeer(hub, android); got != 4 {
		t.Fatalf("subscriptions during transient authority error = %d, want 4", got)
	}
	if leasing.releaseCalls() != 0 {
		t.Fatalf("live-session releases during transient authority error = %d, want 0", leasing.releaseCalls())
	}
	batchCalls, singleCalls, _ := authority.counts()
	if batchCalls != 1 || singleCalls != 0 {
		t.Fatalf("authority calls batch/single = %d/%d, want 1/0", batchCalls, singleCalls)
	}
	expireSubscriptionFailureGrace(hub, android)
	android.refreshSubscriptions()
	if got := subscriptionCountForPeer(hub, android); got != 0 {
		t.Fatalf("subscriptions after authority failure grace = %d, want 0", got)
	}
	if leasing.releaseCalls() != 4 {
		t.Fatalf("live-session releases after authority failure grace = %d, want 4", leasing.releaseCalls())
	}
}

func expireSubscriptionFailureGrace(hub *Hub, android *peer) {
	hub.mu.Lock()
	for sessionID, cached := range android.subscriptionRoutes {
		cached.validUntil = time.Now().Add(-time.Second)
		cached.failureUntil = time.Now().Add(-time.Second)
		android.subscriptionRoutes[sessionID] = cached
	}
	hub.mu.Unlock()
}

func TestRefreshSubscriptionsBoundsAuthorityBatchSize(t *testing.T) {
	authority := newBatchedRouteAuthority()
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store, hub.leases = authority, leasing
	android := activeSubscriptionPeer(hub, uuid.New(), uuid.New())
	for range store.MaxSessionRouteBatchSize + 1 {
		sessionID := uuid.New()
		route := store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: uuid.New()}
		authority.setRoute(sessionID, route)
		hub.subscribe(android, sessionID, route)
	}

	android.refreshSubscriptions()
	batchCalls, singleCalls, maxBatch := authority.counts()
	if batchCalls != 2 || singleCalls != 0 {
		t.Fatalf("bounded authority calls batch/single = %d/%d, want 2/0", batchCalls, singleCalls)
	}
	if maxBatch > store.MaxSessionRouteBatchSize {
		t.Fatalf("authority batch size = %d, limit = %d", maxBatch, store.MaxSessionRouteBatchSize)
	}
}

func TestRefreshSubscriptionsSafelyFallsBackToSingleRouteStore(t *testing.T) {
	authority := newCountingRouteAuthority()
	leasing := &fakeLiveSessionStore{}
	hub := NewHubWithObserver(nil, nil, nil, nil)
	hub.store, hub.leases = authority, leasing
	userID, androidID, pcID := uuid.New(), uuid.New(), uuid.New()
	android := activeSubscriptionPeer(hub, userID, androidID)
	sessionIDs := []uuid.UUID{uuid.New(), uuid.New(), uuid.New()}
	for _, sessionID := range sessionIDs {
		route := store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID}
		authority.set(userID, androidID, sessionID, route, nil)
		hub.subscribe(android, sessionID, route)
	}

	android.refreshSubscriptions()
	for _, sessionID := range sessionIDs {
		if got := authority.callsFor(userID, androidID, sessionID); got != 1 {
			t.Fatalf("fallback SessionRoute calls for %s = %d, want 1", sessionID, got)
		}
	}
	if got := subscriptionCountForPeer(hub, android); got != len(sessionIDs) {
		t.Fatalf("fallback subscriptions = %d, want %d", got, len(sessionIDs))
	}
}

func TestControlPolicyModesEnforceCurrentLeaseHolder(t *testing.T) {
	for _, test := range []struct {
		mode              store.ControlMode
		secondDeviceInput bool
	}{
		{mode: store.ControlModeSingle},
		{mode: store.ControlModeTakeover},
		{mode: store.ControlModeCollaborative, secondDeviceInput: true},
	} {
		t.Run(string(test.mode), func(t *testing.T) {
			hub, policyStore, leases, pc, first, second, sessionID := controlPolicyFixture(t, test.mode)
			if err := first.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("first")); err != nil {
				t.Fatalf("lease holder input denied: %v", err)
			}
			if got := string(<-pc.send); got != "first" {
				t.Fatalf("holder frame = %q", got)
			}
			err := second.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("second"))
			if test.secondDeviceInput {
				if err != nil {
					t.Fatalf("collaborative participant input denied: %v", err)
				}
				if got := string(<-pc.send); got != "second" {
					t.Fatalf("collaborative frame = %q", got)
				}
			} else if !errors.Is(err, store.ErrDenied) {
				t.Fatalf("non-holder input error = %v, want ErrDenied", err)
			}
			lease, leaseErr := leases.GetLease(context.Background(), sessionID)
			if leaseErr != nil || lease.HolderDeviceID != first.principal.Device.ID || lease.Version != 1 {
				t.Fatalf("mode %s silently changed lease fence: lease=%+v err=%v", test.mode, lease, leaseErr)
			}
			if calls := policyStore.callCount(); calls != 2 {
				t.Fatalf("policy calls = %d, want one per subscribed Android", calls)
			}
			_ = hub
		})
	}
}

func TestTakeoverWithoutExplicitDecisionCannotReplaceHolder(t *testing.T) {
	_, _, leases, pc, first, second, sessionID := controlPolicyFixture(t, store.ControlModeTakeover)
	original, _ := leases.GetLease(context.Background(), sessionID)
	for range 2 {
		if err := second.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("implicit-takeover")); !errors.Is(err, store.ErrDenied) {
			t.Fatalf("implicit takeover error = %v, want ErrDenied", err)
		}
	}
	retained, _ := leases.GetLease(context.Background(), sessionID)
	if retained.ID != original.ID || retained.Version != original.Version || retained.HolderDeviceID != first.principal.Device.ID {
		t.Fatalf("implicit takeover replaced fence: original=%+v retained=%+v", original, retained)
	}
}

func TestCollaborativeInputsAreSerializedAndDeviceSequencesRemainIndependent(t *testing.T) {
	_, _, _, pc, first, second, sessionID := controlPolicyFixture(t, store.ControlModeCollaborative)
	pc.send = make(chan []byte, 16)
	const inputsPerDevice = 5
	var wait sync.WaitGroup
	for deviceIndex, android := range []*peer{first, second} {
		wait.Add(1)
		go func(deviceIndex int, android *peer) {
			defer wait.Done()
			for inputIndex := 1; inputIndex <= inputsPerDevice; inputIndex++ {
				sequence := uint64(inputIndex)
				if !android.acceptSequence(sessionID, sequence) {
					t.Errorf("device %d input sequence %d rejected", deviceIndex, sequence)
					return
				}
				frame := []byte(fmt.Sprintf("device-%d-input-%d", deviceIndex, inputIndex))
				if err := android.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, frame); err != nil {
					t.Errorf("device %d input %d route: %v", deviceIndex, inputIndex, err)
					return
				}
			}
		}(deviceIndex, android)
	}
	wait.Wait()
	seen := map[string]bool{}
	for range inputsPerDevice * 2 {
		seen[string(<-pc.send)] = true
	}
	if len(seen) != inputsPerDevice*2 {
		t.Fatalf("serialized collaborative frames = %d, want %d unique", len(seen), inputsPerDevice*2)
	}
	if first.acceptSequence(sessionID, inputsPerDevice) {
		t.Fatal("duplicate device input sequence was accepted")
	}
}

func TestControlPolicyChangeImmediatelyInvalidatesCache(t *testing.T) {
	hub, policies, _, pc, first, second, sessionID := controlPolicyFixture(t, store.ControlModeSingle)
	if err := second.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("single-denied")); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("SINGLE non-holder error = %v", err)
	}
	policy := policies.policy
	policy.Mode = store.ControlModeCollaborative
	policy.PolicyVersion++
	policies.set(policy, nil)
	if err := second.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("still-cached")); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("unpublished policy change unexpectedly bypassed cache: %v", err)
	}
	hub.RefreshControlPolicy(policy.PCID)
	if err := second.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("collaborative")); err != nil {
		t.Fatalf("published policy change was not applied: %v", err)
	}
	if got := string(<-pc.send); got != "collaborative" {
		t.Fatalf("post-policy-change frame = %q", got)
	}
	if policies.callCount() != 2 {
		t.Fatalf("policy query count = %d, want initial and invalidation refresh", policies.callCount())
	}
	_ = first
}

func TestControlPolicyFailureRevocationCrossAccountAndLocalPriorityFailClosed(t *testing.T) {
	t.Run("policy store failure", func(t *testing.T) {
		_, policies, _, pc, first, _, sessionID := controlPolicyFixture(t, store.ControlModeSingle)
		policies.set(store.SessionControlPolicy{}, errors.New("policy unavailable"))
		if err := first.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("denied")); !errors.Is(err, store.ErrDenied) {
			t.Fatalf("policy failure error = %v, want ErrDenied", err)
		}
	})
	t.Run("revoked device", func(t *testing.T) {
		hub, _, _, pc, first, _, sessionID := controlPolicyFixture(t, store.ControlModeSingle)
		hub.Revoke(first.principal.Device.ID)
		if err := first.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("revoked")); !errors.Is(err, store.ErrDenied) {
			t.Fatalf("revoked input error = %v, want ErrDenied", err)
		}
	})
	t.Run("cross account PC", func(t *testing.T) {
		_, _, _, pc, first, _, sessionID := controlPolicyFixture(t, store.ControlModeSingle)
		pc.principal.UserID = uuid.New()
		if err := first.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("cross-account")); !errors.Is(err, store.ErrDenied) {
			t.Fatalf("cross-account input error = %v, want ErrDenied", err)
		}
	})
	t.Run("local input released fence", func(t *testing.T) {
		_, _, leases, pc, first, _, sessionID := controlPolicyFixture(t, store.ControlModeSingle)
		leases.deleteLease(sessionID)
		if err := first.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("after-local-input")); !errors.Is(err, store.ErrDenied) {
			t.Fatalf("input after local-priority release = %v, want ErrDenied", err)
		}
	})
}

func TestControlPolicyTransientFailureUsesBoundedVerifiedPolicyGrace(t *testing.T) {
	hub, policies, _, pc, first, _, sessionID := controlPolicyFixture(t, store.ControlModeSingle)
	if err := first.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("prime")); err != nil {
		t.Fatalf("prime policy cache: %v", err)
	}
	<-pc.send
	policies.set(store.SessionControlPolicy{}, errors.New("database unavailable"))
	hub.mu.Lock()
	cached := first.controlPolicies[sessionID]
	cached.validUntil = time.Now().Add(-time.Second)
	first.controlPolicies[sessionID] = cached
	hub.mu.Unlock()
	if err := first.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("within-grace")); err != nil {
		t.Fatalf("transient policy within grace: %v", err)
	}
	if got := string(<-pc.send); got != "within-grace" {
		t.Fatalf("transient policy frame = %q", got)
	}
	hub.mu.Lock()
	cached = first.controlPolicies[sessionID]
	cached.failureUntil = time.Now().Add(-time.Second)
	first.controlPolicies[sessionID] = cached
	hub.mu.Unlock()
	if err := first.route(androidRouteEnvelope(realtimewire.InputEvent, pc.principal.Device.ID), sessionID, []byte("after-grace")); !errors.Is(err, store.ErrDenied) {
		t.Fatalf("transient policy after grace = %v, want ErrDenied", err)
	}
}

func controlPolicyFixture(t *testing.T, mode store.ControlMode) (*Hub, *fakeControlPolicyStore, *fakeLiveSessionStore, *peer, *peer, *peer, uuid.UUID) {
	t.Helper()
	hub := NewHubWithObserver(nil, nil, nil, nil)
	policies := &fakeControlPolicyStore{}
	hub.policyStore = policies
	leasing := &fakeLiveSessionStore{}
	hub.leases = leasing
	userID, pcID, firstID, secondID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New(), uuid.New()
	pc := testPeer(hub, pcID)
	pc.role, pc.principal.UserID = "pc", userID
	first := testPeer(hub, firstID)
	first.role, first.principal.UserID = "android", userID
	second := testPeer(hub, secondID)
	second.role, second.principal.UserID = "android", userID
	hub.peers[pcID], hub.peers[firstID], hub.peers[secondID] = pc, first, second
	route := store.SessionRouteAuthorization{Permission: "CONTROL", PCDeviceID: pcID}
	hub.subscribe(first, sessionID, route)
	hub.subscribe(second, sessionID, route)
	policy := store.SessionControlPolicy{
		Mode: mode, LocalPriority: true, PolicyVersion: 1,
		PCID: uuid.New(), PCDeviceID: pcID,
	}
	policies.set(policy, nil)
	leasing.setLease(sessionID, firstID)
	return hub, policies, leasing, pc, first, second, sessionID
}

type fakeControlPolicyStore struct {
	mu     sync.Mutex
	policy store.SessionControlPolicy
	err    error
	calls  int
}

func (s *fakeControlPolicyStore) set(policy store.SessionControlPolicy, err error) {
	s.mu.Lock()
	s.policy, s.err = policy, err
	s.mu.Unlock()
}

func (s *fakeControlPolicyStore) SessionControlPolicy(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) (store.SessionControlPolicy, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.calls++
	return s.policy, s.err
}

func (s *fakeControlPolicyStore) callCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.calls
}

func activeSubscriptionPeer(hub *Hub, userID, androidID uuid.UUID) *peer {
	android := testPeer(hub, androidID)
	android.role = "android"
	android.principal.UserID = userID
	hub.peers[androidID] = android
	return android
}

func subscriptionCountForPeer(hub *Hub, android *peer) int {
	hub.mu.RLock()
	defer hub.mu.RUnlock()
	return len(android.subscriptions)
}

func activeRoutePeers(hub *Hub, userID, androidID, pcID uuid.UUID) (*peer, *peer) {
	android := testPeer(hub, androidID)
	android.role = "android"
	android.principal.UserID = userID
	pc := testPeer(hub, pcID)
	pc.role = "pc"
	pc.principal.UserID = userID
	hub.peers[androidID] = android
	hub.peers[pcID] = pc
	return android, pc
}

func androidRouteEnvelope(messageType realtimewire.MessageType, pcID uuid.UUID) realtimewire.Envelope {
	return realtimewire.Envelope{MessageType: messageType, RecipientDeviceID: pcID[:]}
}

type routeAuthorityKey struct {
	userID, deviceID, sessionID uuid.UUID
}

type routeAuthorityResult struct {
	route store.SessionRouteAuthorization
	err   error
}

type countingRouteAuthority struct {
	mu      sync.Mutex
	results map[routeAuthorityKey]routeAuthorityResult
	calls   map[routeAuthorityKey]int
}

type batchedRouteAuthority struct {
	mu          sync.Mutex
	routes      map[uuid.UUID]store.SessionRouteAuthorization
	err         error
	batchCalls  int
	singleCalls int
	maxBatch    int
}

func newBatchedRouteAuthority() *batchedRouteAuthority {
	return &batchedRouteAuthority{routes: map[uuid.UUID]store.SessionRouteAuthorization{}}
}

func (s *batchedRouteAuthority) setRoute(sessionID uuid.UUID, route store.SessionRouteAuthorization) {
	s.mu.Lock()
	s.routes[sessionID] = route
	s.mu.Unlock()
}

func (s *batchedRouteAuthority) SessionRoute(_ context.Context, _, _, sessionID uuid.UUID) (store.SessionRouteAuthorization, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.singleCalls++
	if s.err != nil {
		return store.SessionRouteAuthorization{}, s.err
	}
	route, ok := s.routes[sessionID]
	if !ok {
		return store.SessionRouteAuthorization{}, store.ErrDenied
	}
	return route, nil
}

func (s *batchedRouteAuthority) SessionRoutes(_ context.Context, _, _ uuid.UUID, sessionIDs []uuid.UUID) (map[uuid.UUID]store.SessionRouteAuthorization, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.batchCalls++
	if len(sessionIDs) > s.maxBatch {
		s.maxBatch = len(sessionIDs)
	}
	if s.err != nil {
		return nil, s.err
	}
	result := make(map[uuid.UUID]store.SessionRouteAuthorization, len(sessionIDs))
	for _, sessionID := range sessionIDs {
		if route, ok := s.routes[sessionID]; ok {
			result[sessionID] = route
		}
	}
	return result, nil
}

func (*batchedRouteAuthority) TouchPCPresence(context.Context, uuid.UUID, uuid.UUID) error {
	return nil
}

func (*batchedRouteAuthority) TouchDevicePresence(context.Context, uuid.UUID, uuid.UUID) error {
	return nil
}

func (s *batchedRouteAuthority) counts() (batch, single, maxBatch int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.batchCalls, s.singleCalls, s.maxBatch
}

func newCountingRouteAuthority() *countingRouteAuthority {
	return &countingRouteAuthority{results: map[routeAuthorityKey]routeAuthorityResult{}, calls: map[routeAuthorityKey]int{}}
}

func (s *countingRouteAuthority) set(userID, deviceID, sessionID uuid.UUID, route store.SessionRouteAuthorization, err error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.results[routeAuthorityKey{userID: userID, deviceID: deviceID, sessionID: sessionID}] = routeAuthorityResult{route: route, err: err}
}

func (s *countingRouteAuthority) SessionRoute(_ context.Context, userID, deviceID, sessionID uuid.UUID) (store.SessionRouteAuthorization, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	key := routeAuthorityKey{userID: userID, deviceID: deviceID, sessionID: sessionID}
	s.calls[key]++
	result, ok := s.results[key]
	if !ok {
		return store.SessionRouteAuthorization{}, store.ErrDenied
	}
	return result.route, result.err
}

func (*countingRouteAuthority) TouchPCPresence(context.Context, uuid.UUID, uuid.UUID) error {
	return nil
}

func (*countingRouteAuthority) TouchDevicePresence(context.Context, uuid.UUID, uuid.UUID) error {
	return nil
}

func (s *countingRouteAuthority) callsFor(userID, deviceID, sessionID uuid.UUID) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.calls[routeAuthorityKey{userID: userID, deviceID: deviceID, sessionID: sessionID}]
}

type fakeLiveSessionStore struct {
	mu              sync.Mutex
	releases        int
	reserves        int
	reserveBatches  int
	reserveErr      error
	leases          map[uuid.UUID]kv.Lease
	reservations    map[liveReservationKey]bool
	reserveStarted  chan struct{}
	reserveContinue chan struct{}
}

func (s *fakeLiveSessionStore) ReserveLiveSession(_ context.Context, userID, sessionID, deviceID uuid.UUID, _ ...int) (bool, error) {
	s.mu.Lock()
	s.reserves++
	err := s.reserveErr
	if err == nil {
		if s.reservations == nil {
			s.reservations = map[liveReservationKey]bool{}
		}
		s.reservations[liveReservationKey{userID: userID, sessionID: sessionID, deviceID: deviceID}] = true
	}
	started, resume := s.reserveStarted, s.reserveContinue
	s.mu.Unlock()
	if started != nil {
		select {
		case started <- struct{}{}:
		default:
		}
	}
	if resume != nil {
		<-resume
	}
	return err == nil, err
}

func (s *fakeLiveSessionStore) ReserveLiveSessions(_ context.Context, userID uuid.UUID, sessionIDs []uuid.UUID, deviceID uuid.UUID) error {
	s.mu.Lock()
	s.reserveBatches++
	s.reserves += len(sessionIDs)
	if s.reserveErr == nil {
		if s.reservations == nil {
			s.reservations = map[liveReservationKey]bool{}
		}
		for _, sessionID := range sessionIDs {
			s.reservations[liveReservationKey{userID: userID, sessionID: sessionID, deviceID: deviceID}] = true
		}
	}
	err := s.reserveErr
	started, resume := s.reserveStarted, s.reserveContinue
	s.mu.Unlock()
	if started != nil {
		select {
		case started <- struct{}{}:
		default:
		}
	}
	if resume != nil {
		<-resume
	}
	return err
}

func (s *fakeLiveSessionStore) ReleaseLiveSession(_ context.Context, userID, sessionID, deviceID uuid.UUID) error {
	s.mu.Lock()
	s.releases++
	delete(s.reservations, liveReservationKey{userID: userID, sessionID: sessionID, deviceID: deviceID})
	s.mu.Unlock()
	return nil
}

func (s *fakeLiveSessionStore) GetLease(_ context.Context, sessionID uuid.UUID) (kv.Lease, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	lease, ok := s.leases[sessionID]
	if !ok {
		return kv.Lease{}, errors.New("lease not found")
	}
	return lease, nil
}

func (s *fakeLiveSessionStore) releaseCalls() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.releases
}

func (s *fakeLiveSessionStore) reserveCalls() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.reserves
}

func (s *fakeLiveSessionStore) reserveBatchCalls() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.reserveBatches
}

func (s *fakeLiveSessionStore) reservationActive(userID, sessionID, deviceID uuid.UUID) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.reservations[liveReservationKey{userID: userID, sessionID: sessionID, deviceID: deviceID}]
}

func (s *fakeLiveSessionStore) blockNextReserve() (<-chan struct{}, chan<- struct{}) {
	s.mu.Lock()
	defer s.mu.Unlock()
	started := make(chan struct{}, 1)
	resume := make(chan struct{})
	s.reserveStarted = started
	s.reserveContinue = resume
	return started, resume
}

func (s *fakeLiveSessionStore) clearReserveBlock() {
	s.mu.Lock()
	s.reserveStarted = nil
	s.reserveContinue = nil
	s.mu.Unlock()
}

func (s *fakeLiveSessionStore) setLease(sessionID, holderID uuid.UUID) kv.Lease {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.leases == nil {
		s.leases = map[uuid.UUID]kv.Lease{}
	}
	lease := kv.Lease{SessionID: sessionID, HolderDeviceID: holderID, ID: uuid.New(), Version: 1, ExpiresAt: time.Now().Add(time.Minute)}
	s.leases[sessionID] = lease
	return lease
}

func (s *fakeLiveSessionStore) deleteLease(sessionID uuid.UUID) {
	s.mu.Lock()
	delete(s.leases, sessionID)
	s.mu.Unlock()
}

func testPeer(hub *Hub, deviceID uuid.UUID) *peer {
	return &peer{
		hub:                     hub,
		principal:               auth.Principal{Device: auth.Device{ID: deviceID}},
		send:                    make(chan []byte, 4),
		done:                    make(chan struct{}),
		lastSequences:           map[uuid.UUID]uint64{},
		subscriptionReplayReady: true,
		subscriptions:           map[uuid.UUID]bool{},
		subscriptionRoutes:      map[uuid.UUID]cachedSessionRoute{},
		subscriptionFrames:      map[uuid.UUID][]byte{},
		subscriptionACKs:        map[uuid.UUID]uint64{},
		subscriptionControls:    map[uuid.UUID]retainedSubscriberControl{},
		controlPolicies:         map[uuid.UUID]cachedControlPolicy{},
		pcReplayRoutes:          map[pcRouteKey]*pcReplayRoute{},
	}
}

type subscriptionObserver struct{ delta int }

func (*subscriptionObserver) ConnectionOpened(string) {}
func (*subscriptionObserver) ConnectionClosed(string) {}
func (o *subscriptionObserver) SubscriptionsChanged(delta int) {
	o.delta += delta
}
func (*subscriptionObserver) EnvelopeRouted(string, string, string, int, time.Duration) {}
func (*subscriptionObserver) SlowConsumerDisconnected()                                 {}
