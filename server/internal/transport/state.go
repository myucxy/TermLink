package transport

import (
	"context"
	"errors"
	"math"
	"time"

	"github.com/google/uuid"
)

const (
	DirectTicketPendingTTL       = 15 * time.Second
	DirectTicketRateWindow       = time.Minute
	DirectSignalRateWindow       = 10 * time.Second
	DirectMailboxPresenceTTL     = 30 * time.Second
	DirectDevicePresenceTTL      = 60 * time.Second
	DirectTicketDeviceLimit      = 12
	DirectTicketAccountLimit     = 60
	DirectSignalDeviceLimit      = 96
	DirectSignalAccountLimit     = 512
	DirectRouteStateLimit        = 4096
	DirectSignalsPerDirection    = 256
	DirectCandidatesPerDirection = 32
)

var (
	ErrDirectStateInvalid     = errors.New("invalid direct transport state request")
	ErrDirectStateUnavailable = errors.New("direct transport state unavailable")
)

// DirectStateStore owns only short-lived, reconstructible transport-control
// state. Durable path epochs and all account/device/grant/policy authorization
// remain in PostgreSQL and must be checked by callers before FinalizeTicket or
// AuthorizeAndPublish mutates protocol state.
//
// Implementations must fail closed. In particular, an unavailable state store
// must never fall back to process-local state or affect the realtime Relay.
type DirectStateStore interface {
	ReserveTicket(context.Context, DirectTicketReservation) (DirectTicketReserveResult, error)
	FinalizeTicket(context.Context, DirectTicketFinalization) (DirectTicketFinalizeStatus, error)
	AbortTicket(context.Context, DirectTicketReservation) (bool, error)

	RegisterMailbox(context.Context, DirectMailbox) (DirectMailboxRegistration, error)
	RefreshMailbox(context.Context, DirectMailbox) (bool, error)
	RemoveMailbox(context.Context, DirectMailbox) (bool, error)
	SubscribeMailbox(context.Context, DirectMailbox) (DirectMailboxSubscription, error)

	RegisterPresence(context.Context, DirectPresence) (DirectPresenceRegistration, error)
	RefreshPresence(context.Context, DirectPresence) (bool, error)
	RemovePresence(context.Context, DirectPresence) (bool, error)

	ConsumeSignalBudget(context.Context, DirectSignalBudgetRequest) (DirectSignalBudgetStatus, error)
	AuthorizeAndPublish(context.Context, DirectSignalPublishRequest) (DirectSignalPublishResult, error)
}

type DirectTicketReservation struct {
	AccountID     uuid.UUID
	ConnectionID  uuid.UUID
	InitiatorID   uuid.UUID
	ResponderID   uuid.UUID
	ReservationID uuid.UUID
}

func (r DirectTicketReservation) Validate() error {
	if r.AccountID == uuid.Nil || r.ConnectionID == uuid.Nil || r.ConnectionID.Version() != 4 ||
		r.InitiatorID == uuid.Nil || r.ResponderID == uuid.Nil || r.InitiatorID == r.ResponderID ||
		r.ReservationID == uuid.Nil {
		return ErrDirectStateInvalid
	}
	return nil
}

type DirectTicketReserveStatus uint8

const (
	DirectTicketReserveAccepted DirectTicketReserveStatus = iota + 1
	DirectTicketReserveReplay
	DirectTicketReserveRateLimited
	DirectTicketReserveCapacity
)

type DirectTicketReserveResult struct {
	Status    DirectTicketReserveStatus
	ServerNow time.Time
}

type DirectTicketFinalization struct {
	Reservation DirectTicketReservation
	PathEpoch   uint64
	IssuedAt    time.Time
	ExpiresAt   time.Time
	ReplayUntil time.Time
}

func (f DirectTicketFinalization) Validate() error {
	if err := f.Reservation.Validate(); err != nil || f.PathEpoch == 0 || f.PathEpoch > math.MaxInt64 {
		return ErrDirectStateInvalid
	}
	issued := f.IssuedAt.UTC()
	expires := f.ExpiresAt.UTC()
	replay := f.ReplayUntil.UTC()
	if issued.IsZero() || !expires.After(issued) || expires.Sub(issued) > 120*time.Second ||
		replay.Before(expires) || replay.Sub(issued) > 10*time.Minute {
		return ErrDirectStateInvalid
	}
	return nil
}

type DirectTicketFinalizeStatus uint8

const (
	DirectTicketFinalizeAccepted DirectTicketFinalizeStatus = iota + 1
	DirectTicketFinalizeStale
	DirectTicketFinalizeExpired
)

type DirectMailbox struct {
	InstanceID uuid.UUID
	Generation uuid.UUID
}

func (m DirectMailbox) Validate() error {
	if m.InstanceID == uuid.Nil || m.Generation == uuid.Nil {
		return ErrDirectStateInvalid
	}
	return nil
}

type DirectMailboxRegistration struct {
	Registered bool
	Replaced   bool
}

type DirectPresence struct {
	AccountID           uuid.UUID
	DeviceID            uuid.UUID
	Mailbox             DirectMailbox
	PeerGeneration      uuid.UUID
	SupportsMultipairV2 bool
}

func (p DirectPresence) Validate() error {
	if p.AccountID == uuid.Nil || p.DeviceID == uuid.Nil || p.PeerGeneration == uuid.Nil {
		return ErrDirectStateInvalid
	}
	return p.Mailbox.Validate()
}

type DirectPresenceRegistration struct {
	Registered bool
	Replaced   bool
}

type DirectSignalBudgetRequest struct {
	Sender DirectPresence
}

func (r DirectSignalBudgetRequest) Validate() error { return r.Sender.Validate() }

type DirectSignalBudgetStatus uint8

const (
	DirectSignalBudgetAccepted DirectSignalBudgetStatus = iota + 1
	DirectSignalBudgetRateLimited
	DirectSignalBudgetStalePresence
)

type DirectSignalPublishRequest struct {
	AccountID      uuid.UUID
	ConnectionID   uuid.UUID
	Sender         DirectPresence
	RecipientID    uuid.UUID
	PathEpoch      uint64
	SignalSequence uint64
	SignalType     SignalType
	SentAt         time.Time
	Nonce          [12]byte
	Frame          []byte
}

func (r DirectSignalPublishRequest) Validate() error {
	if r.AccountID == uuid.Nil || r.ConnectionID == uuid.Nil || r.ConnectionID.Version() != 4 ||
		r.RecipientID == uuid.Nil || r.RecipientID == r.Sender.DeviceID || r.Sender.AccountID != r.AccountID ||
		r.PathEpoch == 0 || r.PathEpoch > math.MaxInt64 || r.SignalSequence == 0 || r.SignalSequence > math.MaxInt64 ||
		r.SignalType < SignalPathOffer || r.SignalType > SignalNominationResultV2 || r.SentAt.IsZero() ||
		len(r.Frame) == 0 || len(r.Frame) > MaxSignalBytes ||
		(r.SignalType > SignalPathClose && !r.Sender.SupportsMultipairV2) {
		return ErrDirectStateInvalid
	}
	return r.Sender.Validate()
}

type DirectSignalPublishStatus uint8

const (
	DirectSignalPublishAccepted DirectSignalPublishStatus = iota + 1
	DirectSignalPublishDenied
	DirectSignalPublishReplay
	DirectSignalPublishRateLimited
	DirectSignalPublishStalePresence
)

type DirectSignalPublishResult struct {
	Status          DirectSignalPublishStatus
	SubscriberCount int64
	ServerNow       time.Time
}

type DirectMailboxDelivery struct {
	RecipientID    uuid.UUID
	PeerGeneration uuid.UUID
	Frame          []byte
}

type DirectMailboxSubscription interface {
	Receive(context.Context) (DirectMailboxDelivery, error)
	Close() error
}
