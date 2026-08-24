// Package kv owns short-lived, reconstructible Valkey state.
package kv

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
)

type Valkey struct{ Client *redis.Client }

func Open(ctx context.Context, redisURL string) (*Valkey, error) {
	options, err := redis.ParseURL(redisURL)
	if err != nil {
		return nil, fmt.Errorf("parse Valkey URL: %w", err)
	}
	client := redis.NewClient(options)
	if err := client.Ping(ctx).Err(); err != nil {
		_ = client.Close()
		return nil, fmt.Errorf("ping Valkey: %w", err)
	}
	return &Valkey{Client: client}, nil
}
func (v *Valkey) Close() error {
	if v == nil || v.Client == nil {
		return nil
	}
	return v.Client.Close()
}
func (v *Valkey) Ping(ctx context.Context) error { return v.Client.Ping(ctx).Err() }

func (v *Valkey) Consume(ctx context.Context, scope, nonce string, ttl time.Duration) (bool, error) {
	return v.Client.SetNX(ctx, "proof:"+scope+":"+nonce, "1", ttl).Result()
}

type Lease struct {
	SessionID      uuid.UUID `json:"session_id"`
	HolderDeviceID uuid.UUID `json:"holder_device_id"`
	ID             uuid.UUID `json:"lease_id"`
	Version        int64     `json:"version"`
	ExpiresAt      time.Time `json:"expires_at"`
}

var ErrLeaseHeld = errors.New("control lease already held")

// ErrQuotaReached is retained for source compatibility with older callers;
// ReserveLiveSession no longer returns it because live subscriptions are
// unlimited.
var ErrQuotaReached = errors.New("live session quota reached")

var reserveLiveScript = redis.NewScript(`
local key=KEYS[1]
local now=tonumber(ARGV[1])
local expires=tonumber(ARGV[2])
local member=ARGV[3]
redis.call('ZREMRANGEBYSCORE',key,'-inf',now)
redis.call('ZADD',key,expires,member)
redis.call('PEXPIRE',key,120000)
return 1`)

// ReserveLiveSession records an active subscription for its short lease
// window.  There is intentionally no account-wide quota: the sorted set is
// only an expiry/reconnect registry and remains bounded by active clients.
// The variadic argument is accepted for source compatibility with older
// callers, but is ignored.
func (v *Valkey) ReserveLiveSession(ctx context.Context, userID, sessionID, deviceID uuid.UUID, _ ...int) (bool, error) {
	now := time.Now().UTC()
	member := sessionID.String() + ":" + deviceID.String()
	result, err := reserveLiveScript.Run(ctx, v.Client, []string{"live-sessions:" + userID.String()}, now.UnixMilli(), now.Add(60*time.Second).UnixMilli(), member).Int()
	if err != nil {
		return false, err
	}
	if result != 1 {
		return false, errors.New("live session reservation was not accepted")
	}
	return true, nil
}
func (v *Valkey) ReleaseLiveSession(ctx context.Context, userID, sessionID, deviceID uuid.UUID) error {
	return v.Client.ZRem(ctx, "live-sessions:"+userID.String(), sessionID.String()+":"+deviceID.String()).Err()
}

var acquireScript = redis.NewScript(`
local current=redis.call('GET',KEYS[1])
if current then
  local separator=string.find(current,'|',1,true)
  if separator and string.sub(current,1,separator-1)==ARGV[1] then
    redis.call('PEXPIRE',KEYS[1],ARGV[3])
    return current
  end
  return false
end
local version=redis.call('INCR',KEYS[2])
local value=ARGV[1]..'|'..ARGV[2]..'|'..version
redis.call('PSETEX',KEYS[1],ARGV[3],value)
return value`)

// Acquire atomically creates a lease or resumes the current holder's lease.
// A resume preserves the lease ID and fencing version while restoring its full
// duration. Competing holders cannot consume versions or replace the lease.
func (v *Valkey) Acquire(ctx context.Context, sessionID, holderID uuid.UUID, duration time.Duration) (Lease, error) {
	key := "lease:" + sessionID.String()
	leaseID := uuid.New()
	now := time.Now().UTC()
	value, err := acquireScript.Run(ctx, v.Client, []string{key, key + ":version"}, holderID.String(), leaseID.String(), duration.Milliseconds()).Text()
	if errors.Is(err, redis.Nil) {
		return Lease{}, ErrLeaseHeld
	}
	if err != nil {
		return Lease{}, err
	}
	lease, err := parseLeaseValue(sessionID, value)
	if err != nil {
		return Lease{}, err
	}
	lease.ExpiresAt = now.Add(duration)
	return lease, nil
}

var releaseScript = redis.NewScript(`if redis.call('GET',KEYS[1])==ARGV[1] then return redis.call('DEL',KEYS[1]) else return 0 end`)

func (v *Valkey) Release(ctx context.Context, lease Lease) (bool, error) {
	value := lease.HolderDeviceID.String() + "|" + lease.ID.String() + "|" + fmt.Sprint(lease.Version)
	result, err := releaseScript.Run(ctx, v.Client, []string{"lease:" + lease.SessionID.String()}, value).Int()
	return result == 1, err
}

func (v *Valkey) ForceRelease(ctx context.Context, sessionID uuid.UUID) (Lease, bool, error) {
	key := "lease:" + sessionID.String()
	value, err := forceReleaseScript.Run(ctx, v.Client, []string{key}).Text()
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

// Reading and deleting must be one Redis operation. A GET followed by DEL can
// erase a replacement lease acquired between those commands.
var forceReleaseScript = redis.NewScript(`
local value=redis.call('GET',KEYS[1])
if not value then return nil end
redis.call('DEL',KEYS[1])
return value`)

func (v *Valkey) GetLease(ctx context.Context, sessionID uuid.UUID) (Lease, error) {
	key := "lease:" + sessionID.String()
	value, err := v.Client.Get(ctx, key).Result()
	if err != nil {
		return Lease{}, err
	}
	lease, err := parseLeaseValue(sessionID, value)
	if err != nil {
		return Lease{}, err
	}
	ttl, err := v.Client.PTTL(ctx, key).Result()
	if err != nil {
		return Lease{}, err
	}
	lease.ExpiresAt = time.Now().UTC().Add(ttl)
	return lease, nil
}

func parseLeaseValue(sessionID uuid.UUID, value string) (Lease, error) {
	parts := strings.Split(value, "|")
	if len(parts) != 3 {
		return Lease{}, errors.New("invalid lease state")
	}
	holder, err := uuid.Parse(parts[0])
	if err != nil {
		return Lease{}, err
	}
	id, err := uuid.Parse(parts[1])
	if err != nil {
		return Lease{}, err
	}
	version, err := strconv.ParseInt(parts[2], 10, 64)
	if err != nil {
		return Lease{}, err
	}
	return Lease{SessionID: sessionID, HolderDeviceID: holder, ID: id, Version: version}, nil
}

var renewScript = redis.NewScript(`if redis.call('GET',KEYS[1])==ARGV[1] then return redis.call('PEXPIRE',KEYS[1],ARGV[2]) else return 0 end`)

func (v *Valkey) Renew(ctx context.Context, lease Lease, duration time.Duration) (Lease, error) {
	value := lease.HolderDeviceID.String() + "|" + lease.ID.String() + "|" + fmt.Sprint(lease.Version)
	result, err := renewScript.Run(ctx, v.Client, []string{"lease:" + lease.SessionID.String()}, value, duration.Milliseconds()).Int()
	if err != nil {
		return Lease{}, err
	}
	if result != 1 {
		return Lease{}, ErrLeaseHeld
	}
	lease.ExpiresAt = time.Now().UTC().Add(duration)
	return lease, nil
}
