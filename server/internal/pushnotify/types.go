// Package pushnotify sends generic APNs hints. Device authority and delivery
// leases remain owned by the repository; opening a notification grants no action.
package pushnotify

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
)

const (
	EnvironmentSandbox    = "sandbox"
	EnvironmentProduction = "production"
	KindAINeedsInput      = "ai.needs_input"
	KindAICompleted       = "ai.completed"
	KindUpgradeSucceeded  = "upgrade.succeeded"
	KindUpgradeFailed     = "upgrade.failed"
)

type Status string

const (
	Sent         Status = "sent"
	Retry        Status = "retry"
	InvalidToken Status = "invalid_token"
	Permanent    Status = "permanent"
)

// Result contains only allowlisted diagnostic codes, never provider response
// bodies, device tokens, key material or request URLs. RetryAfter is the earliest
// next attempt; a repository must persist it while releasing the current lease.
// InvalidatedAt is APNs's token invalidation time. A repository must compare it
// with the registration generation/time before disabling any current token.
type Result struct {
	Status        Status
	Code          string
	RetryAfter    time.Duration
	InvalidatedAt *time.Time
}

// Delivery is returned only after Prepare rechecks account/device/grant/PC
// authority, registration generation, and event expiry under the current lease.
// Attempt starts at one and counts claimed attempts for bounded retry backoff.
type Delivery struct {
	SoundEnabled   bool
	ID             uuid.UUID
	RegistrationID uuid.UUID
	Generation     uuid.UUID
	AccountID      uuid.UUID
	DeviceID       uuid.UUID
	PCID           uuid.UUID
	SessionID      uuid.UUID
	TokenCipher    []byte
	Environment    string
	Topic          string
	Kind           string
	ExpiresAt      time.Time
	Attempt        int
}

// Message contains plaintext only transiently after Prepare and decryption. It
// must not be logged, serialized to a queue, or returned by any public API.
type Message struct {
	SoundEnabled bool
	ID           uuid.UUID
	PCID         uuid.UUID
	SessionID    uuid.UUID
	Token        string
	Topic        string
	Environment  string
	Kind         string
	ExpiresAt    time.Time
}

type Sender interface {
	Send(context.Context, Message) Result
}

// Queue keeps authority, generations and retry state transactionally. A nil
// Prepare result means the repository has already settled an unauthorized,
// expired or superseded event. Finish must match both event ID and lease and
// must not invalidate a newer token generation from an old APNs result.
type Queue interface {
	ClaimPushNotifications(context.Context, uuid.UUID, int, time.Duration) ([]uuid.UUID, error)
	PreparePushNotification(context.Context, uuid.UUID, uuid.UUID) (*Delivery, error)
	FinishPushNotification(context.Context, uuid.UUID, uuid.UUID, Result) error
}

type Config struct {
	TeamID      string
	KeyID       string
	Topic       string
	Environment string
	PrivateKey  []byte
}

var (
	ErrConfiguration = errors.New("PUSH_CONFIGURATION_INVALID")
	ErrToken         = errors.New("PUSH_TOKEN_INVALID")
	ErrCipher        = errors.New("PUSH_TOKEN_CIPHER_INVALID")
	ErrQueue         = errors.New("PUSH_QUEUE_UNAVAILABLE")
)
