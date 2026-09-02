package transport

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type sharedStateStub struct {
	mu          sync.Mutex
	reserved    map[connectionKey]uuid.UUID
	finalized   map[connectionKey]uint64
	finalizeErr error
	aborts      int
}

type invalidSubscriberState struct {
	*sharedStateStub
	count int64
}

func (s *invalidSubscriberState) AuthorizeAndPublish(context.Context, DirectSignalPublishRequest) (DirectSignalPublishResult, error) {
	return DirectSignalPublishResult{Status: DirectSignalPublishAccepted, SubscriberCount: s.count}, nil
}

func newSharedStateStub() *sharedStateStub {
	return &sharedStateStub{reserved: map[connectionKey]uuid.UUID{}, finalized: map[connectionKey]uint64{}}
}

func (s *sharedStateStub) ReserveTicket(_ context.Context, request DirectTicketReservation) (DirectTicketReserveResult, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	key := connectionKey{accountID: request.AccountID, connection: request.ConnectionID}
	if _, exists := s.reserved[key]; exists {
		return DirectTicketReserveResult{Status: DirectTicketReserveReplay}, nil
	}
	s.reserved[key] = request.ReservationID
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
func (*sharedStateStub) AuthorizeAndPublish(context.Context, DirectSignalPublishRequest) (DirectSignalPublishResult, error) {
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
			presence := DirectPresence{
				AccountID: account, DeviceID: pc,
				Mailbox: DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}, PeerGeneration: uuid.New(),
			}
			frame := testSignalFrame(uuid.New(), pc, android, 1, 1, SignalPathOffer, 32)
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

var _ DirectStateStore = (*sharedStateStub)(nil)
var _ DirectStateStore = (*invalidSubscriberState)(nil)
