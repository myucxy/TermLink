// Package presence tracks short-lived authenticated connections.
// Device identity and revocation remain durable in PostgreSQL; this registry
// only answers whether a device has a live server connection.
package presence

import (
	"sync"
	"time"

	"github.com/google/uuid"
)

const DefaultTTL = 45 * time.Second

type Entry struct {
	UserID     uuid.UUID
	DeviceID   uuid.UUID
	Connection uuid.UUID
	Role       string
	LastSeenAt time.Time
}

type Registry struct {
	mu      sync.Mutex
	ttl     time.Duration
	clock   func() time.Time
	entries map[uuid.UUID]Entry
}

func New(ttl time.Duration) *Registry {
	if ttl <= 0 {
		ttl = DefaultTTL
	}
	return &Registry{ttl: ttl, clock: time.Now, entries: make(map[uuid.UUID]Entry)}
}

func (r *Registry) Register(userID, deviceID, connectionID uuid.UUID, role string) {
	if r == nil || userID == uuid.Nil || deviceID == uuid.Nil || connectionID == uuid.Nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	now := r.clock()
	r.pruneLocked(now)
	r.entries[connectionID] = Entry{
		UserID: userID, DeviceID: deviceID, Connection: connectionID,
		Role: role, LastSeenAt: now,
	}
}

func (r *Registry) Touch(connectionID uuid.UUID) bool {
	if r == nil || connectionID == uuid.Nil {
		return false
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	now := r.clock()
	r.pruneLocked(now)
	entry, ok := r.entries[connectionID]
	if !ok {
		return false
	}
	entry.LastSeenAt = now
	r.entries[connectionID] = entry
	return true
}

func (r *Registry) Remove(connectionID uuid.UUID) {
	if r == nil || connectionID == uuid.Nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.entries, connectionID)
}

func (r *Registry) Online(userID, deviceID uuid.UUID) (time.Time, bool) {
	if r == nil || userID == uuid.Nil || deviceID == uuid.Nil {
		return time.Time{}, false
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.pruneLocked(r.clock())
	// ponytail: bounded O(n) scan; add a device index only if presence volume
	// becomes measurable in production.
	var latest time.Time
	for _, entry := range r.entries {
		if entry.UserID == userID && entry.DeviceID == deviceID && entry.LastSeenAt.After(latest) {
			latest = entry.LastSeenAt
		}
	}
	return latest, !latest.IsZero()
}

// OnlineDevice reports the newest live connection for a device when the
// account is not known to the caller (for example, the admin connection list).
func (r *Registry) OnlineDevice(deviceID uuid.UUID) (time.Time, bool) {
	if r == nil || deviceID == uuid.Nil {
		return time.Time{}, false
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.pruneLocked(r.clock())
	var latest time.Time
	for _, entry := range r.entries {
		if entry.DeviceID == deviceID && entry.LastSeenAt.After(latest) {
			latest = entry.LastSeenAt
		}
	}
	return latest, !latest.IsZero()
}

// OnlineDeviceCount returns distinct devices with a live connection. Roles
// accept both realtime labels (pc/android) and durable device types.
func (r *Registry) OnlineDeviceCount(userID uuid.UUID, roles ...string) int {
	if r == nil || userID == uuid.Nil {
		return 0
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.pruneLocked(r.clock())
	allowed := make(map[string]struct{}, len(roles))
	for _, role := range roles {
		allowed[role] = struct{}{}
	}
	devices := make(map[uuid.UUID]struct{})
	for _, entry := range r.entries {
		if entry.UserID != userID || (len(allowed) > 0 && !presenceRoleAllowed(allowed, entry.Role)) {
			continue
		}
		devices[entry.DeviceID] = struct{}{}
	}
	return len(devices)
}

func presenceRoleAllowed(allowed map[string]struct{}, role string) bool {
	if _, ok := allowed[role]; ok {
		return true
	}
	if role == "WINDOWS_PC" || role == "PC_AGENT" {
		_, ok := allowed["pc"]
		return ok
	}
	if role == "ANDROID" {
		_, ok := allowed["android"]
		return ok
	}
	return false
}

func (r *Registry) pruneLocked(now time.Time) {
	cutoff := now.Add(-r.ttl)
	for id, entry := range r.entries {
		if !entry.LastSeenAt.After(cutoff) {
			delete(r.entries, id)
		}
	}
}
