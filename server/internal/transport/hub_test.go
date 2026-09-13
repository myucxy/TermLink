package transport

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

type singleMailboxDelivery struct {
	delivery   DirectMailboxDelivery
	delivered  bool
	secondCall chan struct{}
}

func (s *singleMailboxDelivery) Receive(ctx context.Context) (DirectMailboxDelivery, error) {
	if !s.delivered {
		s.delivered = true
		return s.delivery, nil
	}
	select {
	case <-s.secondCall:
	default:
		close(s.secondCall)
	}
	<-ctx.Done()
	return DirectMailboxDelivery{}, ctx.Err()
}

func (*singleMailboxDelivery) Close() error { return nil }

func TestHubRoutesBothDirectionsAndCleansDisconnectedPeer(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	tickets := testTicketServiceWithRouting(t, resolver, true)
	connection := uuid.New()
	pcPrincipal := testPrincipal(account, pc, "PC_AGENT")
	androidPrincipal := testPrincipal(account, android, "ANDROID")
	if _, err := tickets.Issue(context.Background(), pcPrincipal, android, connection); err != nil {
		t.Fatal(err)
	}
	epoch := tickets.connections[connectionKey{accountID: account, connection: connection}].pathEpoch
	hub := NewHub(tickets)
	pcPeer := testSignalPeer(hub, pcPrincipal, signalQueueFrames)
	androidPeer := testSignalPeer(hub, androidPrincipal, signalQueueFrames)
	if !hub.add(pcPeer) || !hub.add(androidPeer) {
		t.Fatal("failed to add peers")
	}
	pcFrame := testSignalFrame(connection, pc, android, epoch, 1, SignalPathOffer, 32)
	if err := hub.route(pcPeer, pcFrame); err != nil {
		t.Fatalf("PC route: %v", err)
	}
	if got := <-androidPeer.send; string(got) != string(pcFrame) {
		t.Fatal("PC frame was not routed byte-identically")
	}
	multipairFrame := testSignalFrame(connection, pc, android, epoch, 2, SignalCheckBatchV2, 32)
	if err := hub.route(pcPeer, multipairFrame); err == nil {
		t.Fatal("multipair signal was accepted without negotiated capability")
	}
	pcPeer.multipair = true
	if err := hub.route(pcPeer, multipairFrame); err == nil {
		t.Fatal("multipair signal was accepted for a recipient without negotiated capability")
	}
	select {
	case <-androidPeer.send:
		t.Fatal("multipair signal was delivered to an unsupported recipient")
	default:
	}
	androidPeer.multipair = true
	if err := hub.route(pcPeer, multipairFrame); err != nil {
		t.Fatalf("negotiated multipair route: %v", err)
	}
	if got := <-androidPeer.send; string(got) != string(multipairFrame) {
		t.Fatal("multipair frame was not routed byte-identically")
	}
	directFrame := testSignalFrame(connection, pc, android, epoch, 3, SignalCutoverProposal, 32)
	if err := hub.route(pcPeer, directFrame); err == nil {
		t.Fatal("direct-data signal was accepted without negotiated capability")
	}
	pcPeer.directData = true
	if err := hub.route(pcPeer, directFrame); err == nil {
		t.Fatal("direct-data signal was accepted for an unsupported recipient")
	}
	androidPeer.directData = true
	for index, signalType := range []SignalType{
		SignalCutoverProposal, SignalCutoverAccept, SignalCutoverCommit, SignalFallback,
	} {
		directFrame = testSignalFrame(connection, pc, android, epoch, uint64(index+3), signalType, 32)
		if err := hub.route(pcPeer, directFrame); err != nil {
			t.Fatalf("negotiated direct-data signal %d: %v", signalType, err)
		}
		if got := <-androidPeer.send; string(got) != string(directFrame) {
			t.Fatalf("direct-data signal %d was not routed byte-identically", signalType)
		}
	}
	androidFrame := testSignalFrame(connection, android, pc, epoch, 1, SignalPathAnswer, 32)
	if err := hub.route(androidPeer, androidFrame); err != nil {
		t.Fatalf("Android route: %v", err)
	}
	if got := <-pcPeer.send; string(got) != string(androidFrame) {
		t.Fatal("Android frame was not routed byte-identically")
	}
	hub.remove(androidPeer)
	hub.mu.Lock()
	_, remains := hub.peers[android]
	hub.mu.Unlock()
	if remains {
		t.Fatal("disconnected peer remained registered")
	}
	if err := hub.route(pcPeer, testSignalFrame(connection, pc, android, epoch, 7, SignalFallback, 32)); err == nil {
		t.Fatal("direct-data control was accepted without an online negotiated recipient")
	}
	if err := hub.route(pcPeer, testSignalFrame(connection, pc, android, epoch, 7, SignalPathClose, 32)); err != nil {
		t.Fatalf("offline recipient should not fail sender route: %v", err)
	}
	select {
	case <-pcPeer.done:
		t.Fatal("sender signal socket closed when recipient was absent")
	default:
	}
}

func TestSharedMailboxNeverDeliversMultipairToUnsupportedRecipient(t *testing.T) {
	account, senderID, recipientID := uuid.New(), uuid.New(), uuid.New()
	connection, generation := uuid.New(), uuid.New()
	frame := testSignalFrame(connection, senderID, recipientID, 1, 1, SignalCheckBatchV2, 32)

	for _, test := range []struct {
		name      string
		supported bool
		delivered bool
	}{
		{name: "unsupported", supported: false, delivered: false},
		{name: "supported", supported: true, delivered: true},
	} {
		t.Run(test.name, func(t *testing.T) {
			hub := NewHub(nil)
			peer := testSignalPeer(hub, testPrincipal(account, recipientID, "ANDROID"), 1)
			peer.multipair = test.supported
			peer.presence.PeerGeneration = generation
			hub.peers[recipientID] = peer
			hub.deliverSharedSignal(DirectMailboxDelivery{
				RecipientID: recipientID, PeerGeneration: generation, Frame: frame,
			})

			select {
			case got := <-peer.send:
				if !test.delivered || string(got) != string(frame) {
					t.Fatalf("mailbox delivered multipair=%t frame=%x", test.delivered, got)
				}
			default:
				if test.delivered {
					t.Fatal("mailbox dropped multipair for a supported recipient")
				}
			}
		})
	}
}

func TestSharedMailboxNeverDeliversDirectDataControlToUnsupportedRecipient(t *testing.T) {
	account, recipientID, generation := uuid.New(), uuid.New(), uuid.New()
	frame := testSignalFrame(uuid.New(), uuid.New(), recipientID, 1, 1, SignalCutoverCommit, 32)

	for _, test := range []struct {
		name       string
		supported  bool
		wantQueued bool
	}{
		{name: "unsupported"},
		{name: "supported", supported: true, wantQueued: true},
	} {
		t.Run(test.name, func(t *testing.T) {
			hub := NewHub(nil)
			peer := testSignalPeer(hub, testPrincipal(account, recipientID, "ANDROID"), 1)
			peer.multipair = true
			peer.directData = test.supported
			peer.presence.PeerGeneration = generation
			hub.peers[recipientID] = peer
			hub.deliverSharedSignal(DirectMailboxDelivery{
				RecipientID: recipientID, PeerGeneration: generation, Frame: frame,
			})

			select {
			case got := <-peer.send:
				if !test.wantQueued || string(got) != string(frame) {
					t.Fatalf("mailbox delivered direct data=%t frame=%x", test.wantQueued, got)
				}
			default:
				if test.wantQueued {
					t.Fatal("mailbox dropped direct-data control for a supported recipient")
				}
			}
		})
	}
}

func TestSlowSignallingConsumerIsIsolatedFromSender(t *testing.T) {
	hub := NewHub(nil)
	sender := testSignalPeer(hub, testPrincipal(uuid.New(), uuid.New(), "PC_AGENT"), 1)
	recipient := testSignalPeer(hub, testPrincipal(uuid.New(), uuid.New(), "ANDROID"), 1)
	recipient.enqueue([]byte("first"))
	recipient.enqueue([]byte("second"))
	select {
	case <-recipient.done:
	default:
		t.Fatal("slow recipient signalling socket was not closed")
	}
	select {
	case <-sender.done:
		t.Fatal("unrelated sender was closed with slow recipient")
	default:
	}
}

func TestHubRejectsOversizeAndRateLimitWithoutGlobalShutdown(t *testing.T) {
	hub := NewHub(nil)
	peer := testSignalPeer(hub, testPrincipal(uuid.New(), uuid.New(), "PC_AGENT"), 1)
	if peer.allowFrame(MaxSignalBytes + 1) {
		t.Fatal("oversize signal accepted")
	}
	for count := 0; count < peerWindowFrames; count++ {
		if !peer.allowFrame(1) {
			t.Fatalf("frame %d unexpectedly rate limited", count)
		}
	}
	if peer.allowFrame(1) {
		t.Fatal("frame rate limit not enforced")
	}
	hub.mu.Lock()
	closed := hub.closed
	hub.mu.Unlock()
	if closed {
		t.Fatal("peer validation failure closed signalling hub")
	}
}

func TestHubCloseIsIndependentAndBounded(t *testing.T) {
	hub := NewHub(nil)
	peer := testSignalPeer(hub, testPrincipal(uuid.New(), uuid.New(), "PC_AGENT"), 1)
	if !hub.add(peer) {
		t.Fatal("add peer")
	}
	hub.remove(peer)
	if err := hub.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if hub.add(testSignalPeer(hub, peer.principal, 1)) {
		t.Fatal("closed hub accepted peer")
	}
}

func TestReplacingSignalPeerCannotRemoveItsSuccessor(t *testing.T) {
	hub := NewHub(nil)
	principal := testPrincipal(uuid.New(), uuid.New(), "PC_AGENT")
	old := testSignalPeer(hub, principal, 1)
	replacement := testSignalPeer(hub, principal, 1)
	if !hub.add(old) || !hub.add(replacement) {
		t.Fatal("add replacement peer")
	}
	select {
	case <-old.done:
	default:
		t.Fatal("replaced peer remained open")
	}
	hub.remove(old)
	hub.mu.Lock()
	current := hub.peers[principal.Device.ID]
	hub.mu.Unlock()
	if current != replacement {
		t.Fatal("old peer removal deleted its live replacement")
	}
	hub.remove(replacement)
}

func TestReplacedSignalPeerCannotRouteAfterGenerationChanges(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	tickets := testTicketService(t, resolver)
	connection := uuid.New()
	principal := testPrincipal(account, pc, "PC_AGENT")
	if _, err := tickets.Issue(context.Background(), principal, android, connection); err != nil {
		t.Fatal(err)
	}
	epoch := tickets.connections[connectionKey{accountID: account, connection: connection}].pathEpoch
	hub := NewHub(tickets)
	old := testSignalPeer(hub, principal, 1)
	replacement := testSignalPeer(hub, principal, 1)
	recipient := testSignalPeer(hub, testPrincipal(account, android, "ANDROID"), 1)
	if !hub.add(old) || !hub.add(recipient) || !hub.add(replacement) {
		t.Fatal("add peers")
	}
	frame := testSignalFrame(connection, pc, android, epoch, 1, SignalPathOffer, 32)
	if err := hub.route(old, frame); !errors.Is(err, ErrTransportDenied) {
		t.Fatalf("stale generation route error=%v, want denied", err)
	}
	select {
	case <-recipient.send:
		t.Fatal("stale generation reached recipient")
	default:
	}
	if err := hub.route(replacement, frame); err != nil {
		t.Fatalf("current generation route: %v", err)
	}
	if got := <-recipient.send; string(got) != string(frame) {
		t.Fatal("current generation frame was not routed byte-identically")
	}
}

type orderedPresenceState struct {
	*sharedStateStub
	mu             sync.Mutex
	registrations  []DirectPresence
	firstCommitted chan struct{}
	releaseFirst   chan struct{}
	secondEntered  chan struct{}
}

func (s *orderedPresenceState) RegisterPresence(_ context.Context, presence DirectPresence) (DirectPresenceRegistration, error) {
	s.mu.Lock()
	index := len(s.registrations)
	s.registrations = append(s.registrations, presence)
	s.mu.Unlock()
	if index == 0 {
		close(s.firstCommitted)
		<-s.releaseFirst
	} else if index == 1 {
		close(s.secondEntered)
	}
	return DirectPresenceRegistration{Registered: true, Replaced: index > 0}, nil
}

func TestConcurrentSameDeviceAddsKeepLocalAndSharedGenerationAligned(t *testing.T) {
	state := &orderedPresenceState{
		sharedStateStub: newSharedStateStub(), firstCommitted: make(chan struct{}),
		releaseFirst: make(chan struct{}), secondEntered: make(chan struct{}),
	}
	account, device := uuid.New(), uuid.New()
	principal := testPrincipal(account, device, "PC_AGENT")
	hub := &Hub{
		state: state, mailbox: DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()},
		peers: map[uuid.UUID]*signalPeer{}, faultWake: make(chan struct{}, 1),
	}
	hub.ready.Store(true)
	first := testSignalPeer(hub, principal, 1)
	second := testSignalPeer(hub, principal, 1)
	firstDone, secondDone := make(chan bool, 1), make(chan bool, 1)
	go func() { firstDone <- hub.add(first) }()
	<-state.firstCommitted
	go func() { secondDone <- hub.add(second) }()

	select {
	case <-state.secondEntered:
		t.Fatal("same-device replacement entered shared registration before its predecessor committed locally")
	case <-time.After(25 * time.Millisecond):
	}
	close(state.releaseFirst)
	if !<-firstDone || !<-secondDone {
		t.Fatal("concurrent peer add failed")
	}
	select {
	case <-state.secondEntered:
	default:
		t.Fatal("replacement did not register after predecessor completed")
	}

	state.mu.Lock()
	registered := state.registrations[len(state.registrations)-1]
	state.mu.Unlock()
	hub.mu.Lock()
	current := hub.peers[device]
	hub.mu.Unlock()
	if current != second || registered.PeerGeneration != second.presence.PeerGeneration {
		t.Fatalf("local/shared generations diverged: current=%p replacement=%p shared=%s local=%s", current, second, registered.PeerGeneration, second.presence.PeerGeneration)
	}
}

func testSignalPeer(hub *Hub, principal auth.Principal, capacity int) *signalPeer {
	return &signalPeer{hub: hub, principal: principal, send: make(chan []byte, capacity), done: make(chan struct{})}
}
