package realtime

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
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

func TestRefreshSubscriptionsBatchReservationErrorFailsClosed(t *testing.T) {
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
	if got := subscriptionCountForPeer(hub, android); got != 0 {
		t.Fatalf("subscriptions after reservation batch error = %d, want 0", got)
	}
	if leasing.reserveBatchCalls() != 1 || leasing.releaseCalls() != 4 {
		t.Fatalf("reservation batches/releases = %d/%d, want 1/4", leasing.reserveBatchCalls(), leasing.releaseCalls())
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

func TestRefreshSubscriptionsBatchErrorFailsClosed(t *testing.T) {
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
	if got := subscriptionCountForPeer(hub, android); got != 0 {
		t.Fatalf("subscriptions after batch error = %d, want 0", got)
	}
	if leasing.releaseCalls() != 4 {
		t.Fatalf("live-session releases after batch error = %d, want 4", leasing.releaseCalls())
	}
	batchCalls, singleCalls, _ := authority.counts()
	if batchCalls != 1 || singleCalls != 0 {
		t.Fatalf("authority calls batch/single = %d/%d, want 1/0", batchCalls, singleCalls)
	}
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
	mu             sync.Mutex
	releases       int
	reserves       int
	reserveBatches int
	reserveErr     error
	leases         map[uuid.UUID]kv.Lease
}

func (s *fakeLiveSessionStore) ReserveLiveSession(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, ...int) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.reserves++
	return s.reserveErr == nil, s.reserveErr
}

func (s *fakeLiveSessionStore) ReserveLiveSessions(_ context.Context, _ uuid.UUID, sessionIDs []uuid.UUID, _ uuid.UUID) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.reserveBatches++
	s.reserves += len(sessionIDs)
	return s.reserveErr
}

func (s *fakeLiveSessionStore) ReleaseLiveSession(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) error {
	s.mu.Lock()
	s.releases++
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
		hub:                hub,
		principal:          auth.Principal{Device: auth.Device{ID: deviceID}},
		send:               make(chan []byte, 4),
		done:               make(chan struct{}),
		lastSequences:      map[uuid.UUID]uint64{},
		subscriptions:      map[uuid.UUID]bool{},
		subscriptionRoutes: map[uuid.UUID]cachedSessionRoute{},
		subscriptionFrames: map[uuid.UUID][]byte{},
		subscriptionACKs:   map[uuid.UUID]uint64{},
		controlPolicies:    map[uuid.UUID]cachedControlPolicy{},
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
