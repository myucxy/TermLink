package transport

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
)

type controlledMailboxSubscription struct {
	results chan mailboxReceiveResult
	closed  chan struct{}
	once    sync.Once
}

func newControlledMailboxSubscription() *controlledMailboxSubscription {
	return &controlledMailboxSubscription{
		results: make(chan mailboxReceiveResult, 4),
		closed:  make(chan struct{}),
	}
}

func (s *controlledMailboxSubscription) Receive(ctx context.Context) (DirectMailboxDelivery, error) {
	select {
	case result := <-s.results:
		return result.delivery, result.err
	case <-s.closed:
		return DirectMailboxDelivery{}, context.Canceled
	case <-ctx.Done():
		return DirectMailboxDelivery{}, ctx.Err()
	}
}

func (s *controlledMailboxSubscription) Close() error {
	s.once.Do(func() { close(s.closed) })
	return nil
}

type recoveringSharedState struct {
	*sharedStateStub
	mu               sync.Mutex
	subscriptions    []*controlledMailboxSubscription
	order            []string
	blockFromCall    int
	subscribeEntered chan int
	subscribeRelease chan struct{}
	mailboxOK        bool
	mailboxErr       error
	mailboxResults   []bool
	mailboxCalls     int
	presenceOK       bool
	presenceErr      error
	removeCalls      int
}

func newRecoveringSharedState() *recoveringSharedState {
	return &recoveringSharedState{
		sharedStateStub:  newSharedStateStub(),
		subscribeEntered: make(chan int, 8),
		subscribeRelease: make(chan struct{}),
		mailboxOK:        true,
		presenceOK:       true,
	}
}

func (s *recoveringSharedState) SubscribeMailbox(ctx context.Context, _ DirectMailbox) (DirectMailboxSubscription, error) {
	s.mu.Lock()
	call := len(s.subscriptions) + 1
	s.order = append(s.order, "subscribe")
	blocked := s.blockFromCall > 0 && call >= s.blockFromCall
	s.mu.Unlock()
	if blocked {
		select {
		case s.subscribeEntered <- call:
		default:
		}
		select {
		case <-s.subscribeRelease:
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	subscription := newControlledMailboxSubscription()
	s.mu.Lock()
	s.subscriptions = append(s.subscriptions, subscription)
	s.mu.Unlock()
	return subscription, nil
}

func (s *recoveringSharedState) RegisterMailbox(context.Context, DirectMailbox) (DirectMailboxRegistration, error) {
	s.mu.Lock()
	s.order = append(s.order, "register")
	s.mu.Unlock()
	return DirectMailboxRegistration{Registered: true}, nil
}

func (s *recoveringSharedState) RefreshMailbox(context.Context, DirectMailbox) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.mailboxCalls < len(s.mailboxResults) {
		result := s.mailboxResults[s.mailboxCalls]
		s.mailboxCalls++
		return result, s.mailboxErr
	}
	s.mailboxCalls++
	return s.mailboxOK, s.mailboxErr
}

func (s *recoveringSharedState) RemoveMailbox(context.Context, DirectMailbox) (bool, error) {
	s.mu.Lock()
	s.order = append(s.order, "remove")
	s.removeCalls++
	s.mu.Unlock()
	return true, nil
}

func (s *recoveringSharedState) RefreshPresence(context.Context, DirectPresence) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.presenceOK, s.presenceErr
}

func (s *recoveringSharedState) subscription(index int) *controlledMailboxSubscription {
	s.mu.Lock()
	defer s.mu.Unlock()
	if index >= len(s.subscriptions) {
		return nil
	}
	return s.subscriptions[index]
}

func (s *recoveringSharedState) subscribeCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.subscriptions)
}

func TestSharedHubRecoversAfterMailboxFailureWithoutStartupContext(t *testing.T) {
	state := newRecoveringSharedState()
	state.blockFromCall = 2
	hub, err := NewSharedHub(&RouteTicketService{}, state)
	if err != nil {
		t.Fatal(err)
	}
	lifecycle, stop := context.WithCancel(context.Background())
	defer stop()
	startup, cancelStartup := context.WithTimeout(context.Background(), time.Second)
	if err := hub.Start(startup, lifecycle); err != nil {
		t.Fatal(err)
	}
	cancelStartup()
	first := state.subscription(0)
	if first == nil {
		t.Fatal("initial mailbox subscription was not installed")
	}
	first.results <- mailboxReceiveResult{err: errors.New("mailbox receive failed")}
	select {
	case call := <-state.subscribeEntered:
		if call != 2 {
			t.Fatalf("recovery subscribe call=%d, want 2", call)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("shared Hub did not begin recovery")
	}
	if hub.ready.Load() {
		t.Fatal("shared Hub remained ready while its mailbox was unavailable")
	}
	close(state.subscribeRelease)
	waitForHubCondition(t, 2*time.Second, func() bool {
		return hub.ready.Load() && state.subscribeCount() == 2
	}, "shared Hub did not recover")

	state.mu.Lock()
	order := append([]string(nil), state.order...)
	state.mu.Unlock()
	if len(order) < 5 || order[0] != "subscribe" || order[1] != "register" ||
		order[2] != "remove" || order[3] != "subscribe" || order[4] != "register" {
		t.Fatalf("mailbox recovery order=%v, want old mailbox fenced before resubscribe/register", order)
	}
	shutdown, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if err := hub.Close(shutdown); err != nil {
		t.Fatal(err)
	}
}

func TestSharedHubCloseFencesBlockedRecovery(t *testing.T) {
	state := newRecoveringSharedState()
	state.blockFromCall = 2
	hub, err := NewSharedHub(&RouteTicketService{}, state)
	if err != nil {
		t.Fatal(err)
	}
	lifecycle, stop := context.WithCancel(context.Background())
	defer stop()
	if err := hub.Start(context.Background(), lifecycle); err != nil {
		t.Fatal(err)
	}
	state.subscription(0).results <- mailboxReceiveResult{err: errors.New("mailbox receive failed")}
	select {
	case <-state.subscribeEntered:
	case <-time.After(2 * time.Second):
		t.Fatal("shared Hub did not enter blocked recovery")
	}
	shutdown, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	if err := hub.Close(shutdown); err != nil {
		t.Fatalf("Close did not cancel blocked recovery: %v", err)
	}
	if hub.ready.Load() {
		t.Fatal("closed shared Hub became ready again")
	}
	count := state.subscribeCount()
	time.Sleep(300 * time.Millisecond)
	if state.subscribeCount() != count {
		t.Fatal("closed shared Hub performed a post-close recovery attempt")
	}
}

func TestSharedHubPresenceFenceRetiresOnlyCurrentPeer(t *testing.T) {
	state := newRecoveringSharedState()
	state.presenceOK = false
	hub, err := NewSharedHub(&RouteTicketService{}, state)
	if err != nil {
		t.Fatal(err)
	}
	hub.ready.Store(true)
	peer := testSignalPeer(hub, testPrincipal(uuid.New(), uuid.New(), "ANDROID"), 1)
	peer.presence = DirectPresence{
		AccountID: peer.principal.UserID, DeviceID: peer.principal.Device.ID,
		Mailbox: hub.mailbox, PeerGeneration: uuid.New(), SupportsMultipairV2: true,
	}
	hub.peers[peer.principal.Device.ID] = peer
	if err := hub.refreshSharedState(context.Background()); err != nil {
		t.Fatalf("presence ownership fence degraded the Hub: %v", err)
	}
	select {
	case <-peer.done:
	case <-time.After(time.Second):
		t.Fatal("stale presence peer was not retired")
	}
	if !hub.ready.Load() {
		t.Fatal("presence ownership fence made the whole Hub unavailable")
	}
}

func TestSharedHubPresenceRefreshRechecksMailboxFence(t *testing.T) {
	for _, name := range []string{"mailbox deleted", "mailbox generation replaced"} {
		t.Run(name, func(t *testing.T) {
			state := newRecoveringSharedState()
			state.mailboxResults = []bool{true, false}
			state.presenceOK = false
			hub, err := NewSharedHub(&RouteTicketService{}, state)
			if err != nil {
				t.Fatal(err)
			}
			hub.ready.Store(true)
			peer := testSignalPeer(hub, testPrincipal(uuid.New(), uuid.New(), "ANDROID"), 1)
			peer.presence = DirectPresence{
				AccountID: peer.principal.UserID, DeviceID: peer.principal.Device.ID,
				Mailbox: hub.mailbox, PeerGeneration: uuid.New(), SupportsMultipairV2: true,
			}
			hub.peers[peer.principal.Device.ID] = peer
			if err := hub.refreshSharedState(context.Background()); !errors.Is(err, ErrDirectStateUnavailable) {
				t.Fatalf("refresh error=%v, want mailbox fence failure", err)
			}
			select {
			case <-peer.done:
				t.Fatal("mailbox fence was misclassified as a stale peer generation")
			default:
			}
		})
	}
}

type slowPresenceRefreshState struct {
	*recoveringSharedState
	delay time.Duration
}

func (s *slowPresenceRefreshState) RefreshPresence(ctx context.Context, _ DirectPresence) (bool, error) {
	select {
	case <-time.After(s.delay):
		return true, nil
	case <-ctx.Done():
		return false, ctx.Err()
	}
}

func TestSharedHubPresenceRefreshUsesOnePassDeadline(t *testing.T) {
	state := &slowPresenceRefreshState{recoveringSharedState: newRecoveringSharedState(), delay: 35 * time.Millisecond}
	hub, err := NewSharedHub(&RouteTicketService{}, state)
	if err != nil {
		t.Fatal(err)
	}
	for range 2 {
		peer := testSignalPeer(hub, testPrincipal(uuid.New(), uuid.New(), "ANDROID"), 1)
		peer.presence = DirectPresence{
			AccountID: peer.principal.UserID, DeviceID: peer.principal.Device.ID,
			Mailbox: hub.mailbox, PeerGeneration: uuid.New(), SupportsMultipairV2: true,
		}
		hub.peers[peer.principal.Device.ID] = peer
	}
	started := time.Now()
	err = hub.refreshSharedStateWithin(context.Background(), 50*time.Millisecond)
	elapsed := time.Since(started)
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("refresh error=%v, want one-pass deadline", err)
	}
	if elapsed > 150*time.Millisecond {
		t.Fatalf("refresh pass exceeded bounded deadline: %s", elapsed)
	}
}

func waitForHubCondition(t *testing.T, timeout time.Duration, condition func() bool, message string) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for !condition() {
		if time.Now().After(deadline) {
			t.Fatal(message)
		}
		time.Sleep(5 * time.Millisecond)
	}
}
