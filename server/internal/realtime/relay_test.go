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

func TestReplacedPeerCannotRemoveReplacementSubscription(t *testing.T) {
	observer := &subscriptionObserver{}
	hub := NewHubWithObserver(nil, nil, nil, observer)
	deviceID := uuid.New()
	sessionID := uuid.New()
	oldPeer := testPeer(hub, deviceID)
	newPeer := testPeer(hub, deviceID)

	hub.peers[deviceID] = oldPeer
	hub.subscribe(oldPeer, sessionID)
	hub.peers[deviceID] = newPeer
	hub.subscribe(newPeer, sessionID)

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

func testPeer(hub *Hub, deviceID uuid.UUID) *peer {
	return &peer{
		hub:           hub,
		principal:     auth.Principal{Device: auth.Device{ID: deviceID}},
		done:          make(chan struct{}),
		subscriptions: map[uuid.UUID]bool{},
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
