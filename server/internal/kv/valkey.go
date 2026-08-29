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

type OpenOptions struct {
	PoolSize     int
	MinIdleConns int
	DialTimeout  time.Duration
	ReadTimeout  time.Duration
	WriteTimeout time.Duration
	PoolTimeout  time.Duration
}

func Open(ctx context.Context, redisURL string) (*Valkey, error) {
	return OpenWithOptions(ctx, redisURL, OpenOptions{})
}

func OpenWithOptions(ctx context.Context, redisURL string, config OpenOptions) (*Valkey, error) {
	options, err := redis.ParseURL(redisURL)
	if err != nil {
		return nil, fmt.Errorf("parse Valkey URL: %w", err)
	}
	if config.PoolSize > 0 {
		options.PoolSize = config.PoolSize
	}
	if config.MinIdleConns >= 0 {
		options.MinIdleConns = config.MinIdleConns
	}
	if config.DialTimeout > 0 {
		options.DialTimeout = config.DialTimeout
	}
	if config.ReadTimeout > 0 {
		options.ReadTimeout = config.ReadTimeout
	}
	if config.WriteTimeout > 0 {
		options.WriteTimeout = config.WriteTimeout
	}
	if config.PoolTimeout > 0 {
		options.PoolTimeout = config.PoolTimeout
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

const maxLiveSessionReservationBatch = 256

var reserveLiveSessionsScript = redis.NewScript(`
local key=KEYS[1]
local now=tonumber(ARGV[1])
local expires=tonumber(ARGV[2])
local device=ARGV[3]
redis.call('ZREMRANGEBYSCORE',key,'-inf',now)
for index=4,#ARGV do
  redis.call('ZADD',key,expires,ARGV[index]..':'..device)
end
redis.call('PEXPIRE',key,120000)
return #ARGV-3`)

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

// ReserveLiveSessions refreshes a bounded set for one account/device in one
// Valkey operation. It is used only by the Relay's already-bounded authority
// refresh and preserves the same member format and expiry as the single-item
// operation.
func (v *Valkey) ReserveLiveSessions(ctx context.Context, userID uuid.UUID, sessionIDs []uuid.UUID, deviceID uuid.UUID) error {
	if len(sessionIDs) == 0 {
		return nil
	}
	if len(sessionIDs) > maxLiveSessionReservationBatch {
		return errors.New("live session reservation batch exceeds limit")
	}
	now := time.Now().UTC()
	arguments := make([]any, 0, len(sessionIDs)+3)
	arguments = append(arguments, now.UnixMilli(), now.Add(60*time.Second).UnixMilli(), deviceID.String())
	for _, sessionID := range sessionIDs {
		if sessionID == uuid.Nil {
			return errors.New("live session reservation contains invalid session")
		}
		arguments = append(arguments, sessionID.String())
	}
	reserved, err := reserveLiveSessionsScript.Run(ctx, v.Client, []string{"live-sessions:" + userID.String()}, arguments...).Int()
	if err != nil {
		return err
	}
	if reserved != len(sessionIDs) {
		return errors.New("live session reservation batch was not accepted")
	}
	return nil
}
func (v *Valkey) ReleaseLiveSession(ctx context.Context, userID, sessionID, deviceID uuid.UUID) error {
	return v.Client.ZRem(ctx, "live-sessions:"+userID.String(), sessionID.String()+":"+deviceID.String()).Err()
}

var acquireScript = redis.NewScript(`
local current=redis.call('GET',KEYS[1])
local clock=redis.call('TIME')
local now=tonumber(clock[1])*1000+math.floor(tonumber(clock[2])/1000)
local expires=now+tonumber(ARGV[3])
if current then
  redis.call('ZREMRANGEBYSCORE',KEYS[3],'-inf',now)
  if redis.call('ZCARD',KEYS[3])==0 then
    local separator=string.find(current,'|',1,true)
    local legacy_holder=separator and string.sub(current,1,separator-1) or ''
    local legacy_ttl=redis.call('PTTL',KEYS[1])
    if legacy_holder~='' and legacy_ttl>0 then
      redis.call('ZADD',KEYS[3],now+legacy_ttl,legacy_holder)
    end
  end
  redis.call('ZADD',KEYS[3],expires,ARGV[1])
  local latest=redis.call('ZRANGE',KEYS[3],-1,-1,'WITHSCORES')
  if latest[2] and tonumber(latest[2])>expires then
    expires=tonumber(latest[2])
  end
  redis.call('PEXPIREAT',KEYS[1],expires)
  redis.call('PEXPIREAT',KEYS[3],expires)
  return current
end
local version=redis.call('INCR',KEYS[2])
local value=ARGV[1]..'|'..ARGV[2]..'|'..version
redis.call('PSETEX',KEYS[1],ARGV[3],value)
redis.call('DEL',KEYS[3])
redis.call('ZADD',KEYS[3],expires,ARGV[1])
redis.call('PEXPIREAT',KEYS[3],expires)
return value`)

// Acquire atomically creates or joins the session's shared control lease. A
// join preserves the first holder's compatibility field, lease ID, and fencing
// version while registering the joining Android as an independently releasable
// participant. Authorization and the account boundary are enforced by the API
// and durable store before this account-scoped primitive is used.
func (v *Valkey) Acquire(ctx context.Context, sessionID, holderID uuid.UUID, duration time.Duration) (Lease, error) {
	key := "lease:" + sessionID.String()
	leaseID := uuid.New()
	now := time.Now().UTC()
	value, err := acquireScript.Run(ctx, v.Client, []string{key, key + ":version", key + ":participants"}, holderID.String(), leaseID.String(), duration.Milliseconds()).Text()
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

var releaseScript = redis.NewScript(`
if redis.call('GET',KEYS[1])~=ARGV[1] then return 0 end
redis.call('DEL',KEYS[1],KEYS[2])
return 1`)

// Release performs a fenced hard release. It is reserved for rollback and
// administrative cleanup; an Android's ordinary release uses ReleaseForDevice
// so it cannot revoke other participants in the shared lease.
func (v *Valkey) Release(ctx context.Context, lease Lease) (bool, error) {
	value := lease.HolderDeviceID.String() + "|" + lease.ID.String() + "|" + fmt.Sprint(lease.Version)
	key := "lease:" + lease.SessionID.String()
	result, err := releaseScript.Run(ctx, v.Client, []string{key, key + ":participants"}, value).Int()
	return result == 1, err
}

var releaseForDeviceScript = redis.NewScript(`
local current=redis.call('GET',KEYS[1])
if not current or current~=ARGV[1] then return 0 end
local clock=redis.call('TIME')
local now=tonumber(clock[1])*1000+math.floor(tonumber(clock[2])/1000)
redis.call('ZREMRANGEBYSCORE',KEYS[2],'-inf',now)
local removed=redis.call('ZREM',KEYS[2],ARGV[2])
local remaining=redis.call('ZCARD',KEYS[2])
if remaining==0 then
  local separator=string.find(current,'|',1,true)
  local legacy_holder=separator and string.sub(current,1,separator-1) or ''
  if removed==0 and legacy_holder~=ARGV[2] then return 0 end
  redis.call('DEL',KEYS[1],KEYS[2])
  return 2
end
if removed==0 then return 0 end
local latest=redis.call('ZRANGE',KEYS[2],-1,-1,'WITHSCORES')
redis.call('PEXPIREAT',KEYS[1],latest[2])
redis.call('PEXPIREAT',KEYS[2],latest[2])
return 1`)

// ReleaseForDevice removes one Android from a shared lease. accepted reports
// whether the exact lease included that device; released is true only when it
// was the last live participant and the shared lease was deleted.
func (v *Valkey) ReleaseForDevice(ctx context.Context, lease Lease, deviceID uuid.UUID) (accepted, released bool, err error) {
	value := lease.HolderDeviceID.String() + "|" + lease.ID.String() + "|" + fmt.Sprint(lease.Version)
	key := "lease:" + lease.SessionID.String()
	result, err := releaseForDeviceScript.Run(ctx, v.Client, []string{key, key + ":participants"}, value, deviceID.String()).Int()
	if err != nil {
		return false, false, err
	}
	return result == 1 || result == 2, result == 2, nil
}

func (v *Valkey) ForceRelease(ctx context.Context, sessionID uuid.UUID) (Lease, bool, error) {
	key := "lease:" + sessionID.String()
	value, err := forceReleaseScript.Run(ctx, v.Client, []string{key, key + ":participants"}).Text()
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
redis.call('DEL',KEYS[1],KEYS[2])
return value`)

var getLeaseScript = redis.NewScript(`
local value=redis.call('GET',KEYS[1])
if not value then return nil end
return {value,redis.call('PTTL',KEYS[1])}`)

func (v *Valkey) GetLease(ctx context.Context, sessionID uuid.UUID) (Lease, error) {
	key := "lease:" + sessionID.String()
	result, err := getLeaseScript.Run(ctx, v.Client, []string{key}).Slice()
	if err != nil {
		return Lease{}, err
	}
	if len(result) != 2 {
		return Lease{}, errors.New("invalid lease state")
	}
	value, ok := result[0].(string)
	if !ok {
		return Lease{}, errors.New("invalid lease state")
	}
	ttlMilliseconds, ok := result[1].(int64)
	if !ok {
		return Lease{}, errors.New("invalid lease TTL")
	}
	lease, err := parseLeaseValue(sessionID, value)
	if err != nil {
		return Lease{}, err
	}
	lease.ExpiresAt = time.Now().UTC().Add(time.Duration(ttlMilliseconds) * time.Millisecond)
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

var renewScript = redis.NewScript(`
if redis.call('GET',KEYS[1])~=ARGV[1] then return 0 end
local clock=redis.call('TIME')
local now=tonumber(clock[1])*1000+math.floor(tonumber(clock[2])/1000)
local expires=now+tonumber(ARGV[3])
redis.call('ZREMRANGEBYSCORE',KEYS[2],'-inf',now)
redis.call('ZADD',KEYS[2],expires,ARGV[2])
local latest=redis.call('ZRANGE',KEYS[2],-1,-1,'WITHSCORES')
if latest[2] and tonumber(latest[2])>expires then
  expires=tonumber(latest[2])
end
redis.call('PEXPIREAT',KEYS[1],expires)
redis.call('PEXPIREAT',KEYS[2],expires)
return 1`)

func (v *Valkey) Renew(ctx context.Context, lease Lease, duration time.Duration) (Lease, error) {
	return v.RenewForDevice(ctx, lease, lease.HolderDeviceID, duration)
}

// RenewForDevice refreshes the shared lease and the calling Android's
// participant lifetime without changing the protocol-visible first holder.
func (v *Valkey) RenewForDevice(ctx context.Context, lease Lease, deviceID uuid.UUID, duration time.Duration) (Lease, error) {
	value := lease.HolderDeviceID.String() + "|" + lease.ID.String() + "|" + fmt.Sprint(lease.Version)
	key := "lease:" + lease.SessionID.String()
	now := time.Now().UTC()
	result, err := renewScript.Run(ctx, v.Client, []string{key, key + ":participants"}, value, deviceID.String(), duration.Milliseconds()).Int()
	if err != nil {
		return Lease{}, err
	}
	if result != 1 {
		return Lease{}, ErrLeaseHeld
	}
	lease.ExpiresAt = now.Add(duration)
	return lease, nil
}
