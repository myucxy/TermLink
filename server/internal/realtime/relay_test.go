package realtime

import (
	"errors"
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
