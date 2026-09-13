package kv

import (
	"context"
	"time"

	"github.com/google/uuid"
)

// LeaseVersionSource allocates durable, monotonically increasing fencing
// versions. Single-node state remains reconstructible, but a process restart
// must never make a stale lease appear newer than its replacement.
type LeaseVersionSource interface {
	NextControlLeaseVersion(context.Context) (int64, error)
}

// AuthorityMessage invalidates one cached realtime authorization decision.
// Payloads are UUIDs rather than arbitrary text so every backend has the same
// validation boundary.
type AuthorityMessage struct {
	Channel string
	ID      uuid.UUID
}

type AuthoritySubscription interface {
	Receive(context.Context) (AuthorityMessage, error)
	Close() error
}

// Coordinator is the short-lived server state required by authentication,
// control leasing, realtime subscriptions, rate limiting, and transactional
// authority notifications. Implementations are selected explicitly at startup;
// a failed external backend must never silently fall back to local memory.
type Coordinator interface {
	Consume(context.Context, string, string, time.Duration) (bool, error)
	Allow(context.Context, string, int64, time.Duration) (bool, error)

	ReserveLiveSession(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, ...int) (bool, error)
	ReserveLiveSessions(context.Context, uuid.UUID, []uuid.UUID, uuid.UUID) error
	ReleaseLiveSession(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) error

	Acquire(context.Context, uuid.UUID, uuid.UUID, time.Duration) (Lease, error)
	Release(context.Context, Lease) (bool, error)
	ReleaseForDevice(context.Context, Lease, uuid.UUID) (bool, bool, error)
	ForceRelease(context.Context, uuid.UUID) (Lease, bool, error)
	ForceReleaseForOperation(context.Context, uuid.UUID, uuid.UUID, string) (Lease, bool, error)
	GetLease(context.Context, uuid.UUID) (Lease, error)
	IsLeaseParticipantActive(context.Context, Lease, uuid.UUID) (bool, error)
	Renew(context.Context, Lease, time.Duration) (Lease, error)
	RenewForDevice(context.Context, Lease, uuid.UUID, time.Duration) (Lease, error)

	Publish(context.Context, string, uuid.UUID) error
	Subscribe(context.Context, ...string) (AuthoritySubscription, error)
	Ping(context.Context) error
	Close() error
}

var _ Coordinator = (*Valkey)(nil)
