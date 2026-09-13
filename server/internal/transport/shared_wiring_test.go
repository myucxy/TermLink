package transport

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

type sharedStateStub struct {
	mu          sync.Mutex
	reserved    map[connectionKey]uuid.UUID
	routes      map[connectionKey]DirectTicketReservation
	finalized   map[connectionKey]uint64
	finalizeErr error
	aborts      int
	publishes   int
}

type invalidSubscriberState struct {
	*sharedStateStub
	count int64
}

func (s *invalidSubscriberState) AuthorizeAndPublish(context.Context, DirectSignalPublishRequest) (DirectSignalPublishResult, error) {
	return DirectSignalPublishResult{Status: DirectSignalPublishAccepted, SubscriberCount: s.count}, nil
}

func newSharedStateStub() *sharedStateStub {
	return &sharedStateStub{
		reserved: map[connectionKey]uuid.UUID{}, routes: map[connectionKey]DirectTicketReservation{},
		finalized: map[connectionKey]uint64{},
	}
}

func (s *sharedStateStub) ReserveTicket(_ context.Context, request DirectTicketReservation) (DirectTicketReserveResult, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	key := connectionKey{accountID: request.AccountID, connection: request.ConnectionID}
	if _, exists := s.reserved[key]; exists {
		return DirectTicketReserveResult{Status: DirectTicketReserveReplay}, nil
	}
	s.reserved[key] = request.ReservationID
	s.routes[key] = request
	return DirectTicketReserveResult{Status: DirectTicketReserveAccepted}, nil
}

func (s *sharedStateStub) FinalizeTicket(_ context.Context, request DirectTicketFinalization) (DirectTicketFinalizeStatus, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.finalizeErr != nil {
		return 0, s.finalizeErr
	}
	key := connectionKey{accountID: request.Reservation.AccountID, connection: request.Reservation.ConnectionID}
	if s.reserved[key] != request.Reservation.ReservationID {
		return DirectTicketFinalizeStale, nil
	}
	s.finalized[key] = request.PathEpoch
	return DirectTicketFinalizeAccepted, nil
}

func (s *sharedStateStub) AbortTicket(_ context.Context, request DirectTicketReservation) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	key := connectionKey{accountID: request.AccountID, connection: request.ConnectionID}
	if s.reserved[key] != request.ReservationID {
		return false, nil
	}
	delete(s.reserved, key)
	s.aborts++
	return true, nil
}

func (*sharedStateStub) RegisterMailbox(context.Context, DirectMailbox) (DirectMailboxRegistration, error) {
	return DirectMailboxRegistration{Registered: true}, nil
}
func (*sharedStateStub) RefreshMailbox(context.Context, DirectMailbox) (bool, error) {
	return true, nil
}
func (*sharedStateStub) RemoveMailbox(context.Context, DirectMailbox) (bool, error) { return true, nil }
func (*sharedStateStub) SubscribeMailbox(context.Context, DirectMailbox) (DirectMailboxSubscription, error) {
	return nil, errors.New("not used")
}
func (*sharedStateStub) RegisterPresence(context.Context, DirectPresence) (DirectPresenceRegistration, error) {
	return DirectPresenceRegistration{Registered: true}, nil
}
func (*sharedStateStub) RefreshPresence(context.Context, DirectPresence) (bool, error) {
	return true, nil
}
func (*sharedStateStub) RemovePresence(context.Context, DirectPresence) (bool, error) {
	return true, nil
}
func (*sharedStateStub) ConsumeSignalBudget(context.Context, DirectSignalBudgetRequest) (DirectSignalBudgetStatus, error) {
	return DirectSignalBudgetAccepted, nil
}
func (s *sharedStateStub) ResolveSignalResponder(_ context.Context, request DirectSignalRouteRequest) (uuid.UUID, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	key := connectionKey{accountID: request.AccountID, connection: request.ConnectionID}
	route, exists := s.routes[key]
	if !exists || s.finalized[key] != request.PathEpoch ||
		!((route.InitiatorID == request.SenderID && route.ResponderID == request.RecipientID) ||
			(route.ResponderID == request.SenderID && route.InitiatorID == request.RecipientID)) {
		return uuid.Nil, nil
	}
	return route.ResponderID, nil
}
func (s *sharedStateStub) AuthorizeAndPublish(context.Context, DirectSignalPublishRequest) (DirectSignalPublishResult, error) {
	s.mu.Lock()
	s.publishes++
	s.mu.Unlock()
	return DirectSignalPublishResult{Status: DirectSignalPublishAccepted, SubscriberCount: 1}, nil
}

func TestSharedTicketServicesFenceConcurrentConnectionAcrossInstances(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	authority := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	state := newSharedStateStub()
	first, err := NewSharedRouteTicketService(authority, state, make([]byte, 32))
	if err != nil {
		t.Fatal(err)
	}
	second, err := NewSharedRouteTicketService(authority, state, make([]byte, 32))
	if err != nil {
		t.Fatal(err)
	}
	connection := uuid.New()
	principal := testPrincipal(account, pc, "PC_AGENT")
	start := make(chan struct{})
	results := make(chan error, 2)
	for _, service := range []*RouteTicketService{first, second} {
		go func(service *RouteTicketService) {
			<-start
			_, issueErr := service.Issue(context.Background(), principal, android, connection)
			results <- issueErr
		}(service)
	}
	close(start)
	success, replay := 0, 0
	for range 2 {
		switch err := <-results; {
		case err == nil:
			success++
		case errors.Is(err, ErrTransportReplay):
			replay++
		default:
			t.Fatalf("unexpected issue error: %v", err)
		}
	}
	if success != 1 || replay != 1 {
		t.Fatalf("shared issue success/replay=%d/%d", success, replay)
	}
}

func TestSharedTicketFinalizeFailureAbortsAndReturnsNoTicket(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	authority := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	state := newSharedStateStub()
	state.finalizeErr = ErrDirectStateUnavailable
	service, err := NewSharedRouteTicketService(authority, state, make([]byte, 32))
	if err != nil {
		t.Fatal(err)
	}
	ticket, issueErr := service.Issue(context.Background(), testPrincipal(account, pc, "PC_AGENT"), android, uuid.New())
	if ticket != nil || !errors.Is(issueErr, ErrDirectStateUnavailable) {
		t.Fatalf("ticket/error=%v/%v, want unavailable and no ticket", ticket, issueErr)
	}
	if state.aborts != 1 {
		t.Fatalf("abort count=%d, want 1", state.aborts)
	}
}

func TestSharedSignalAcceptedWithInvalidSubscriberCountFailsClosed(t *testing.T) {
	for _, count := range []int64{0, 2} {
		t.Run(fmt.Sprintf("subscriber count %d", count), func(t *testing.T) {
			account, pc, android := uuid.New(), uuid.New(), uuid.New()
			authority := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
				pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
			}}}
			state := &invalidSubscriberState{sharedStateStub: newSharedStateStub(), count: count}
			service, err := NewSharedRouteTicketService(authority, state, make([]byte, 32))
			if err != nil {
				t.Fatal(err)
			}
			principal := testPrincipal(account, pc, "PC_AGENT")
			connection := uuid.New()
			if _, err := service.Issue(context.Background(), principal, android, connection); err != nil {
				t.Fatal(err)
			}
			epoch := state.finalized[connectionKey{accountID: account, connection: connection}]
			presence := DirectPresence{
				AccountID: account, DeviceID: pc,
				Mailbox: DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}, PeerGeneration: uuid.New(),
			}
			frame := testSignalFrame(connection, pc, android, epoch, 1, SignalPathOffer, 32)
			envelope, err := UnmarshalSignal(frame)
			if err != nil {
				t.Fatal(err)
			}
			if err := service.AuthorizeAndPublishSignal(context.Background(), principal, presence, envelope, frame); !errors.Is(err, ErrDirectStateUnavailable) {
				t.Fatalf("accepted publish with subscriber count %d error=%v, want state unavailable", count, err)
			}
		})
	}
}

func TestSharedSignalBindsBothDirectionsToResponderCurrentKeyBeforePublish(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc:      {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED", KeyID: "pc-current-key"},
		android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED", KeyID: "android-current-key"},
	}}}
	state := newSharedStateStub()
	service, err := NewSharedRouteTicketService(resolver, state, make([]byte, 32))
	if err != nil {
		t.Fatal(err)
	}
	connection := uuid.New()
	pcPrincipal := testPrincipal(account, pc, "PC_AGENT")
	androidPrincipal := testPrincipal(account, android, "ANDROID")
	if _, err := service.Issue(context.Background(), pcPrincipal, android, connection); err != nil {
		t.Fatal(err)
	}
	epoch := state.finalized[connectionKey{accountID: account, connection: connection}]
	pcPresence := DirectPresence{
		AccountID: account, DeviceID: pc,
		Mailbox: DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}, PeerGeneration: uuid.New(),
	}
	androidPresence := DirectPresence{
		AccountID: account, DeviceID: android,
		Mailbox: DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}, PeerGeneration: uuid.New(),
	}

	assertSignal := func(principal auth.Principal, presence DirectPresence, sender, recipient uuid.UUID, sequence uint64, keyID string, want error, wantPublishes int) {
		t.Helper()
		frame := testSignalFrameWithKey(connection, sender, recipient, epoch, sequence, SignalCandidateUpdate, 32, []byte(keyID))
		envelope, parseErr := UnmarshalSignal(frame)
		if parseErr != nil {
			t.Fatal(parseErr)
		}
		authorizeErr := service.AuthorizeAndPublishSignal(context.Background(), principal, presence, envelope, frame)
		if want == nil && authorizeErr != nil || want != nil && !errors.Is(authorizeErr, want) {
			t.Fatalf("key %q authorization error=%v, want %v", keyID, authorizeErr, want)
		}
		if state.publishes != wantPublishes {
			t.Fatalf("key %q publish count=%d, want %d", keyID, state.publishes, wantPublishes)
		}
	}

	assertSignal(pcPrincipal, pcPresence, pc, android, 1, "pc-current-key", ErrTransportDenied, 0)
	assertSignal(pcPrincipal, pcPresence, pc, android, 1, "android-current-key", nil, 1)
	assertSignal(androidPrincipal, androidPresence, android, pc, 1, "pc-current-key", ErrTransportDenied, 1)
	assertSignal(androidPrincipal, androidPresence, android, pc, 1, "android-current-key", nil, 2)
	rotated := resolver.devices[account][android]
	rotated.KeyID = "android-rotated-key"
	resolver.devices[account][android] = rotated
	assertSignal(pcPrincipal, pcPresence, pc, android, 2, "android-current-key", ErrTransportDenied, 2)
	assertSignal(pcPrincipal, pcPresence, pc, android, 2, "android-rotated-key", nil, 3)
}

var _ DirectStateStore = (*sharedStateStub)(nil)
var _ DirectStateStore = (*invalidSubscriberState)(nil)
