package kv

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
)

// Keep the fence slightly longer than PostgreSQL's 24-hour idempotency window.
// If a caller incorrectly reuses a key at the expiry boundary, replaying an
// old fence is fail-safe; it cannot delete the newer active lease.
const forceReleaseOperationTTL = 25 * time.Hour

// ForceReleaseForOperation atomically preserves the removed lease value under
// an operation-scoped tombstone. A retry with the same account and
// Idempotency-Key returns the original fencing token without touching a lease
// that may have been acquired after the first release.
func (v *Valkey) ForceReleaseForOperation(
	ctx context.Context,
	sessionID uuid.UUID,
	userID uuid.UUID,
	idempotencyKey string,
) (Lease, bool, error) {
	if userID == uuid.Nil || idempotencyKey == "" {
		return Lease{}, false, errors.New("force release operation identity required")
	}
	leaseKey := "lease:" + sessionID.String()
	tombstoneKey := forceReleaseOperationKey(sessionID, userID, idempotencyKey)
	value, err := forceReleaseForOperationScript.Run(
		ctx,
		v.Client,
		[]string{leaseKey, tombstoneKey},
		forceReleaseOperationTTL.Milliseconds(),
	).Text()
	if errors.Is(err, redis.Nil) {
		return Lease{}, false, nil
	}
	if err != nil {
		return Lease{}, false, err
	}
	lease, err := parseLeaseValue(sessionID, value)
	if err != nil {
		return Lease{}, false, err
	}
	return lease, true, nil
}

func forceReleaseOperationKey(sessionID, userID uuid.UUID, idempotencyKey string) string {
	operationDigest := sha256.Sum256([]byte(userID.String() + "\x00" + idempotencyKey))
	return fmt.Sprintf("lease-release:%s:%x", sessionID, operationDigest)
}

var forceReleaseForOperationScript = redis.NewScript(`
local previous=redis.call('GET',KEYS[2])
if previous then return previous end
local value=redis.call('GET',KEYS[1])
if not value then return nil end
redis.call('PSETEX',KEYS[2],ARGV[1],value)
redis.call('DEL',KEYS[1])
return value`)
