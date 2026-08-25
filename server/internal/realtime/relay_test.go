package realtime

import (
	"errors"
	"fmt"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
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
		"unknown_future_capability",
		"lease_v1",
		"cumulative_ack",
		"flow_control_v1",
		"e2ee_x25519_chacha20poly1305",
		"terminal_presentation_metadata_v1",
	}
	got := negotiatedCapabilities(offered)
	want := []string{
		"cumulative_ack",
		"e2ee_x25519_chacha20poly1305",
		"flow_control_v1",
		"lease_v1",
		"terminal_presentation_metadata_v1",
		"terminal_typed_input_v1",
	}
	if fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("negotiated capabilities = %v, want %v", got, want)
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
	}
	android := map[realtimewire.MessageType]bool{
		realtimewire.Heartbeat: true, realtimewire.Goodbye: true,
		realtimewire.Subscribe: true, realtimewire.Unsubscribe: true,
		realtimewire.OutputACK: true, realtimewire.FlowControl: true,
		realtimewire.InputEvent: true, realtimewire.ResizeRequest: true,
		realtimewire.ResumeRequest: true, realtimewire.LeaseRequest: true,
		realtimewire.SessionTerminateRequest: true,
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
	} {
		if validEnvelopeACK(realtimewire.Envelope{MessageType: messageType, ACK: 42}) {
			t.Fatalf("message %d accepted a forbidden ACK", messageType)
		}
		if !validEnvelopeACK(realtimewire.Envelope{MessageType: messageType}) {
			t.Fatalf("message %d rejected ack=0", messageType)
		}
	}
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
	if !peer.acceptPCSequence(sessionID, uuid.New(), 42, []byte("opaque-frame-b")) {
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

func testPeer(hub *Hub, deviceID uuid.UUID) *peer {
	return &peer{
		hub:                hub,
		principal:          auth.Principal{Device: auth.Device{ID: deviceID}},
		send:               make(chan []byte, 4),
		done:               make(chan struct{}),
		lastSequences:      map[uuid.UUID]uint64{},
		subscriptions:      map[uuid.UUID]bool{},
		subscriptionRoutes: map[uuid.UUID]store.SessionRouteAuthorization{},
		subscriptionFrames: map[uuid.UUID][]byte{},
		subscriptionACKs:   map[uuid.UUID]uint64{},
		pcReplayRoutes:     map[pcRouteKey]*pcReplayRoute{},
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
