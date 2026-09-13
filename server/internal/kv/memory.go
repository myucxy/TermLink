package kv

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
)

const (
	memoryNonceLimit           = 131072
	memoryRateLimit            = 32768
	memoryLeaseLimit           = 32768
	memoryLiveReservationLimit = 131072
	memoryTombstoneLimit       = 65536
	memorySubscriberLimit      = 32
	memorySubscriberBuffer     = 1024
)

var (
	ErrCoordinatorClosed   = errors.New("coordination store is closed")
	ErrCoordinatorCapacity = errors.New("coordination store capacity reached")
)

type memoryExpiry struct {
	expiresAt time.Time
}

type memoryRate struct {
	count     int64
	expiresAt time.Time
}

type memoryLiveKey struct {
	userID    uuid.UUID
	sessionID uuid.UUID
	deviceID  uuid.UUID
}

type memoryLease struct {
	lease        Lease
	participants map[uuid.UUID]time.Time
}

type memoryTombstone struct {
	lease     Lease
	expiresAt time.Time
}

// MemoryCoordinator is the explicit single-node Coordinator. All maps are
// bounded and lazily prune expired entries. It is never constructed as a
// fallback for a failed Valkey connection.
type MemoryCoordinator struct {
	mu             sync.Mutex
	versions       LeaseVersionSource
	clock          func() time.Time
	closed         bool
	nonces         map[string]memoryExpiry
	rates          map[string]memoryRate
	live           map[memoryLiveKey]time.Time
	leases         map[uuid.UUID]*memoryLease
	tombstones     map[string]memoryTombstone
	subscribers    map[uint64]*memoryAuthoritySubscription
	nextSubscriber uint64
	lastCleanup    time.Time
}

func NewMemoryCoordinator(versions LeaseVersionSource) (*MemoryCoordinator, error) {
	if versions == nil {
		return nil, errors.New("durable lease version source is required")
	}
	return &MemoryCoordinator{
		versions:    versions,
		clock:       time.Now,
		nonces:      make(map[string]memoryExpiry),
		rates:       make(map[string]memoryRate),
		live:        make(map[memoryLiveKey]time.Time),
		leases:      make(map[uuid.UUID]*memoryLease),
		tombstones:  make(map[string]memoryTombstone),
		subscribers: make(map[uint64]*memoryAuthoritySubscription),
	}, nil
}

func (m *MemoryCoordinator) Ping(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.closed {
		return ErrCoordinatorClosed
	}
	return nil
}

func (m *MemoryCoordinator) Consume(ctx context.Context, scope, nonce string, ttl time.Duration) (bool, error) {
	if err := validMemoryOperation(ctx, ttl); err != nil || scope == "" || nonce == "" {
		if err != nil {
			return false, err
		}
		return false, errors.New("nonce scope and value are required")
	}
	now := m.now()
	key := scope + "\x00" + nonce
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return false, err
	}
	if current, ok := m.nonces[key]; ok && now.Before(current.expiresAt) {
		return false, nil
	}
	if _, exists := m.nonces[key]; !exists && len(m.nonces) >= memoryNonceLimit {
		return false, ErrCoordinatorCapacity
	}
	m.nonces[key] = memoryExpiry{expiresAt: now.Add(ttl)}
	return true, nil
}

func (m *MemoryCoordinator) Allow(ctx context.Context, key string, limit int64, window time.Duration) (bool, error) {
	if err := validMemoryOperation(ctx, window); err != nil {
		return false, err
	}
	if key == "" || limit < 1 {
		return false, errors.New("rate limit key and positive limit are required")
	}
	now := m.now()
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return false, err
	}
	current, exists := m.rates[key]
	if !exists || !now.Before(current.expiresAt) {
		if !exists && len(m.rates) >= memoryRateLimit {
			return false, ErrCoordinatorCapacity
		}
		m.rates[key] = memoryRate{count: 1, expiresAt: now.Add(window)}
		return true, nil
	}
	if current.count >= limit {
		return false, nil
	}
	current.count++
	m.rates[key] = current
	return true, nil
}

func (m *MemoryCoordinator) ReserveLiveSession(ctx context.Context, userID, sessionID, deviceID uuid.UUID, _ ...int) (bool, error) {
	if err := validIDs(ctx, userID, sessionID, deviceID); err != nil {
		return false, err
	}
	now := m.now()
	key := memoryLiveKey{userID: userID, sessionID: sessionID, deviceID: deviceID}
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return false, err
	}
	if _, exists := m.live[key]; !exists && len(m.live) >= memoryLiveReservationLimit {
		return false, ErrCoordinatorCapacity
	}
	m.live[key] = now.Add(60 * time.Second)
	return true, nil
}

func (m *MemoryCoordinator) ReserveLiveSessions(ctx context.Context, userID uuid.UUID, sessionIDs []uuid.UUID, deviceID uuid.UUID) error {
	if len(sessionIDs) == 0 {
		return nil
	}
	if len(sessionIDs) > maxLiveSessionReservationBatch || userID == uuid.Nil || deviceID == uuid.Nil {
		return errors.New("invalid live session reservation batch")
	}
	for _, sessionID := range sessionIDs {
		if sessionID == uuid.Nil {
			return errors.New("live session reservation contains invalid session")
		}
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	now := m.now()
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return err
	}
	additional := 0
	for _, sessionID := range sessionIDs {
		if _, exists := m.live[memoryLiveKey{userID: userID, sessionID: sessionID, deviceID: deviceID}]; !exists {
			additional++
		}
	}
	if len(m.live)+additional > memoryLiveReservationLimit {
		return ErrCoordinatorCapacity
	}
	expiresAt := now.Add(60 * time.Second)
	for _, sessionID := range sessionIDs {
		m.live[memoryLiveKey{userID: userID, sessionID: sessionID, deviceID: deviceID}] = expiresAt
	}
	return nil
}

func (m *MemoryCoordinator) ReleaseLiveSession(ctx context.Context, userID, sessionID, deviceID uuid.UUID) error {
	if err := validIDs(ctx, userID, sessionID, deviceID); err != nil {
		return err
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.closed {
		return ErrCoordinatorClosed
	}
	delete(m.live, memoryLiveKey{userID: userID, sessionID: sessionID, deviceID: deviceID})
	return nil
}

func (m *MemoryCoordinator) Acquire(ctx context.Context, sessionID, holderID uuid.UUID, duration time.Duration) (Lease, error) {
	if err := validMemoryOperation(ctx, duration); err != nil || sessionID == uuid.Nil || holderID == uuid.Nil {
		if err != nil {
			return Lease{}, err
		}
		return Lease{}, errors.New("lease session and holder are required")
	}
	now := m.now()
	m.mu.Lock()
	if err := m.prepareLocked(now); err != nil {
		m.mu.Unlock()
		return Lease{}, err
	}
	if current := m.liveLeaseLocked(sessionID, now); current != nil {
		result := m.joinLeaseLocked(current, holderID, now, duration)
		m.mu.Unlock()
		return result, nil
	}
	if len(m.leases) >= memoryLeaseLimit {
		m.mu.Unlock()
		return Lease{}, ErrCoordinatorCapacity
	}
	m.mu.Unlock()

	version, err := m.versions.NextControlLeaseVersion(ctx)
	if err != nil {
		return Lease{}, fmt.Errorf("allocate durable lease version: %w", err)
	}
	if version < 1 {
		return Lease{}, errors.New("durable lease version is invalid")
	}
	now = m.now()
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return Lease{}, err
	}
	if current := m.liveLeaseLocked(sessionID, now); current != nil {
		return m.joinLeaseLocked(current, holderID, now, duration), nil
	}
	if len(m.leases) >= memoryLeaseLimit {
		return Lease{}, ErrCoordinatorCapacity
	}
	expiresAt := now.Add(duration)
	lease := Lease{SessionID: sessionID, HolderDeviceID: holderID, ID: uuid.New(), Version: version, ExpiresAt: expiresAt}
	m.leases[sessionID] = &memoryLease{lease: lease, participants: map[uuid.UUID]time.Time{holderID: expiresAt}}
	return lease, nil
}

func (m *MemoryCoordinator) joinLeaseLocked(current *memoryLease, deviceID uuid.UUID, now time.Time, duration time.Duration) Lease {
	expiresAt := now.Add(duration)
	current.participants[deviceID] = expiresAt
	if expiresAt.After(current.lease.ExpiresAt) {
		current.lease.ExpiresAt = expiresAt
	}
	result := current.lease
	result.ExpiresAt = expiresAt
	return result
}

func (m *MemoryCoordinator) Release(ctx context.Context, lease Lease) (bool, error) {
	if err := ctx.Err(); err != nil {
		return false, err
	}
	now := m.now()
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return false, err
	}
	current := m.liveLeaseLocked(lease.SessionID, now)
	if current == nil || !sameLease(current.lease, lease) {
		return false, nil
	}
	delete(m.leases, lease.SessionID)
	return true, nil
}

func (m *MemoryCoordinator) ReleaseForDevice(ctx context.Context, lease Lease, deviceID uuid.UUID) (bool, bool, error) {
	if err := ctx.Err(); err != nil {
		return false, false, err
	}
	now := m.now()
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return false, false, err
	}
	current := m.liveLeaseLocked(lease.SessionID, now)
	if current == nil || !sameLease(current.lease, lease) {
		return false, false, nil
	}
	if _, exists := current.participants[deviceID]; !exists {
		return false, false, nil
	}
	delete(current.participants, deviceID)
	if len(current.participants) == 0 {
		delete(m.leases, lease.SessionID)
		return true, true, nil
	}
	m.updateLeaseExpiryLocked(current)
	return true, false, nil
}

func (m *MemoryCoordinator) ForceRelease(ctx context.Context, sessionID uuid.UUID) (Lease, bool, error) {
	if err := ctx.Err(); err != nil {
		return Lease{}, false, err
	}
	now := m.now()
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return Lease{}, false, err
	}
	current := m.liveLeaseLocked(sessionID, now)
	if current == nil {
		return Lease{}, false, nil
	}
	lease := current.lease
	delete(m.leases, sessionID)
	return lease, true, nil
}

func (m *MemoryCoordinator) ForceReleaseForOperation(ctx context.Context, sessionID, userID uuid.UUID, idempotencyKey string) (Lease, bool, error) {
	if err := ctx.Err(); err != nil {
		return Lease{}, false, err
	}
	if sessionID == uuid.Nil || userID == uuid.Nil || idempotencyKey == "" {
		return Lease{}, false, errors.New("force release operation identity required")
	}
	now := m.now()
	key := forceReleaseOperationKey(sessionID, userID, idempotencyKey)
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return Lease{}, false, err
	}
	if previous, ok := m.tombstones[key]; ok && now.Before(previous.expiresAt) {
		return previous.lease, true, nil
	}
	current := m.liveLeaseLocked(sessionID, now)
	if current == nil {
		return Lease{}, false, nil
	}
	if len(m.tombstones) >= memoryTombstoneLimit {
		return Lease{}, false, ErrCoordinatorCapacity
	}
	lease := current.lease
	m.tombstones[key] = memoryTombstone{lease: lease, expiresAt: now.Add(forceReleaseOperationTTL)}
	delete(m.leases, sessionID)
	return lease, true, nil
}

func (m *MemoryCoordinator) GetLease(ctx context.Context, sessionID uuid.UUID) (Lease, error) {
	if err := ctx.Err(); err != nil {
		return Lease{}, err
	}
	now := m.now()
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return Lease{}, err
	}
	current := m.liveLeaseLocked(sessionID, now)
	if current == nil {
		return Lease{}, redis.Nil
	}
	return current.lease, nil
}

// IsLeaseParticipantActive checks one exact shared-lease fence without
// renewing the lease or participant. Expired participants are pruned while
// the coordinator lock is held so release, renewal, and authorization observe
// one consistent state.
func (m *MemoryCoordinator) IsLeaseParticipantActive(ctx context.Context, lease Lease, deviceID uuid.UUID) (bool, error) {
	if err := ctx.Err(); err != nil {
		return false, err
	}
	if lease.SessionID == uuid.Nil || lease.HolderDeviceID == uuid.Nil || lease.ID == uuid.Nil || lease.Version < 1 || deviceID == uuid.Nil {
		return false, errors.New("invalid lease participant query")
	}
	now := m.now()
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return false, err
	}
	current := m.liveLeaseLocked(lease.SessionID, now)
	if current == nil || !sameLease(current.lease, lease) {
		return false, nil
	}
	expiresAt, exists := current.participants[deviceID]
	return exists && now.Before(expiresAt), nil
}

func (m *MemoryCoordinator) Renew(ctx context.Context, lease Lease, duration time.Duration) (Lease, error) {
	return m.RenewForDevice(ctx, lease, lease.HolderDeviceID, duration)
}

func (m *MemoryCoordinator) RenewForDevice(ctx context.Context, lease Lease, deviceID uuid.UUID, duration time.Duration) (Lease, error) {
	if err := validMemoryOperation(ctx, duration); err != nil {
		return Lease{}, err
	}
	now := m.now()
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.prepareLocked(now); err != nil {
		return Lease{}, err
	}
	current := m.liveLeaseLocked(lease.SessionID, now)
	if current == nil || !sameLease(current.lease, lease) {
		return Lease{}, ErrLeaseHeld
	}
	expiresAt := now.Add(duration)
	current.participants[deviceID] = expiresAt
	m.updateLeaseExpiryLocked(current)
	result := current.lease
	result.ExpiresAt = expiresAt
	return result, nil
}

func (m *MemoryCoordinator) Publish(ctx context.Context, channel string, id uuid.UUID) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if channel == "" || id == uuid.Nil {
		return errors.New("authority notification is invalid")
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.closed {
		return ErrCoordinatorClosed
	}
	selected := make([]*memoryAuthoritySubscription, 0, len(m.subscribers))
	for _, subscriber := range m.subscribers {
		if _, ok := subscriber.channels[channel]; !ok {
			continue
		}
		if len(subscriber.messages) == cap(subscriber.messages) {
			return ErrCoordinatorCapacity
		}
		selected = append(selected, subscriber)
	}
	message := AuthorityMessage{Channel: channel, ID: id}
	for _, subscriber := range selected {
		subscriber.messages <- message
	}
	return nil
}

func (m *MemoryCoordinator) Subscribe(ctx context.Context, channels ...string) (AuthoritySubscription, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	channelSet := make(map[string]struct{}, len(channels))
	for _, channel := range channels {
		if channel == "" {
			return nil, errors.New("authority notification channel is empty")
		}
		channelSet[channel] = struct{}{}
	}
	if len(channelSet) == 0 {
		return nil, errors.New("authority notification channels are required")
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.closed {
		return nil, ErrCoordinatorClosed
	}
	if len(m.subscribers) >= memorySubscriberLimit {
		return nil, ErrCoordinatorCapacity
	}
	m.nextSubscriber++
	subscriber := &memoryAuthoritySubscription{
		owner: m, id: m.nextSubscriber, channels: channelSet,
		messages: make(chan AuthorityMessage, memorySubscriberBuffer), done: make(chan struct{}),
	}
	m.subscribers[subscriber.id] = subscriber
	return subscriber, nil
}

func (m *MemoryCoordinator) Close() error {
	m.mu.Lock()
	if m.closed {
		m.mu.Unlock()
		return nil
	}
	m.closed = true
	subscribers := make([]*memoryAuthoritySubscription, 0, len(m.subscribers))
	for _, subscriber := range m.subscribers {
		subscribers = append(subscribers, subscriber)
	}
	m.subscribers = make(map[uint64]*memoryAuthoritySubscription)
	m.mu.Unlock()
	for _, subscriber := range subscribers {
		subscriber.closeDone()
	}
	return nil
}

type memoryAuthoritySubscription struct {
	owner    *MemoryCoordinator
	id       uint64
	channels map[string]struct{}
	messages chan AuthorityMessage
	done     chan struct{}
	once     sync.Once
}

func (s *memoryAuthoritySubscription) Receive(ctx context.Context) (AuthorityMessage, error) {
	select {
	case <-ctx.Done():
		return AuthorityMessage{}, ctx.Err()
	case <-s.done:
		return AuthorityMessage{}, ErrCoordinatorClosed
	case message := <-s.messages:
		return message, nil
	}
}

func (s *memoryAuthoritySubscription) Close() error {
	s.once.Do(func() {
		s.owner.mu.Lock()
		delete(s.owner.subscribers, s.id)
		s.owner.mu.Unlock()
		close(s.done)
	})
	return nil
}

func (s *memoryAuthoritySubscription) closeDone() {
	s.once.Do(func() { close(s.done) })
}

func (m *MemoryCoordinator) now() time.Time { return m.clock().UTC() }

func (m *MemoryCoordinator) prepareLocked(now time.Time) error {
	if m.closed {
		return ErrCoordinatorClosed
	}
	if m.lastCleanup.IsZero() || now.Sub(m.lastCleanup) >= time.Second || now.Before(m.lastCleanup) {
		m.cleanupLocked(now)
		m.lastCleanup = now
	}
	return nil
}

func (m *MemoryCoordinator) cleanupLocked(now time.Time) {
	for key, value := range m.nonces {
		if !now.Before(value.expiresAt) {
			delete(m.nonces, key)
		}
	}
	for key, value := range m.rates {
		if !now.Before(value.expiresAt) {
			delete(m.rates, key)
		}
	}
	for key, expiresAt := range m.live {
		if !now.Before(expiresAt) {
			delete(m.live, key)
		}
	}
	for sessionID := range m.leases {
		m.liveLeaseLocked(sessionID, now)
	}
	for key, value := range m.tombstones {
		if !now.Before(value.expiresAt) {
			delete(m.tombstones, key)
		}
	}
}

func (m *MemoryCoordinator) liveLeaseLocked(sessionID uuid.UUID, now time.Time) *memoryLease {
	current := m.leases[sessionID]
	if current == nil {
		return nil
	}
	for deviceID, expiresAt := range current.participants {
		if !now.Before(expiresAt) {
			delete(current.participants, deviceID)
		}
	}
	if len(current.participants) == 0 {
		delete(m.leases, sessionID)
		return nil
	}
	m.updateLeaseExpiryLocked(current)
	return current
}

func (m *MemoryCoordinator) updateLeaseExpiryLocked(current *memoryLease) {
	var latest time.Time
	for _, expiresAt := range current.participants {
		if expiresAt.After(latest) {
			latest = expiresAt
		}
	}
	current.lease.ExpiresAt = latest
}

func sameLease(left, right Lease) bool {
	return left.SessionID == right.SessionID && left.HolderDeviceID == right.HolderDeviceID &&
		left.ID == right.ID && left.Version == right.Version
}

func validMemoryOperation(ctx context.Context, duration time.Duration) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if duration <= 0 {
		return errors.New("positive duration is required")
	}
	return nil
}

func validIDs(ctx context.Context, ids ...uuid.UUID) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	for _, id := range ids {
		if id == uuid.Nil {
			return errors.New("non-zero identifiers are required")
		}
	}
	return nil
}

var _ Coordinator = (*MemoryCoordinator)(nil)
