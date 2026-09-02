package kv

import (
	"context"
	"encoding/hex"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
	"github.com/termlink/termlink/server/internal/transport"
)

const (
	DefaultDirectTransportNamespace = "termlink:direct:v1"
	directMailboxMessagePrefix      = "DTS1"
	directMailboxMessageHeaderBytes = len(directMailboxMessagePrefix) + 2*36
)

// DirectTransportState is the standalone-Valkey implementation of
// transport.DirectStateStore. It intentionally accepts *redis.Client rather
// than redis.UniversalClient: the Lua operations touch account, device,
// instance and global-index keys atomically and therefore do not support Redis
// Cluster cross-slot execution.
//
// Construction performs no network I/O. Production must give this state layer
// a client backed by the independent Direct Valkey deployment; it must never
// silently reuse process-local state when a command fails.
type DirectTransportState struct {
	client    *redis.Client
	namespace string
}

func NewDirectTransportState(client *redis.Client, namespace string) (*DirectTransportState, error) {
	if client == nil {
		return nil, fmt.Errorf("%w: direct Valkey client is required", transport.ErrDirectStateInvalid)
	}
	if namespace == "" {
		namespace = DefaultDirectTransportNamespace
	}
	if !validDirectNamespace(namespace) {
		return nil, fmt.Errorf("%w: invalid direct Valkey namespace", transport.ErrDirectStateInvalid)
	}
	return &DirectTransportState{client: client, namespace: namespace}, nil
}

func validDirectNamespace(namespace string) bool {
	if len(namespace) == 0 || len(namespace) > 96 || strings.TrimSpace(namespace) != namespace {
		return false
	}
	for _, character := range namespace {
		if character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
			character >= '0' && character <= '9' || character == ':' || character == '-' || character == '_' {
			continue
		}
		return false
	}
	return true
}

var reserveDirectTicketScript = redis.NewScript(`
local clock=redis.call('TIME')
local now=tonumber(clock[1])*1000+math.floor(tonumber(clock[2])/1000)

local deviceCount=redis.call('INCR',KEYS[3])
if deviceCount==1 then redis.call('PEXPIRE',KEYS[3],ARGV[7]) end
local accountCount=redis.call('INCR',KEYS[4])
if accountCount==1 then redis.call('PEXPIRE',KEYS[4],ARGV[7]) end
if deviceCount>tonumber(ARGV[8]) or accountCount>tonumber(ARGV[9]) then
  return {3,now}
end

redis.call('ZREMRANGEBYSCORE',KEYS[2],'-inf',now)
if redis.call('EXISTS',KEYS[1])==1 then return {2,now} end
if redis.call('ZCARD',KEYS[2])>=tonumber(ARGV[10]) then return {4,now} end

redis.call('HSET',KEYS[1],
  'schema','1','state','pending','reservation_id',ARGV[1],
  'account_id',ARGV[2],'connection_id',ARGV[3],
  'initiator_id',ARGV[4],'responder_id',ARGV[5])
redis.call('PEXPIRE',KEYS[1],ARGV[6])
redis.call('ZADD',KEYS[2],now+tonumber(ARGV[6]),KEYS[1])
return {1,now}`)

func (s *DirectTransportState) ReserveTicket(ctx context.Context, request transport.DirectTicketReservation) (transport.DirectTicketReserveResult, error) {
	if err := request.Validate(); err != nil {
		return transport.DirectTicketReserveResult{}, err
	}
	result, err := reserveDirectTicketScript.Run(ctx, s.client, []string{
		s.routeKey(request.AccountID, request.ConnectionID), s.routeIndexKey(),
		s.ticketDeviceRateKey(request.InitiatorID), s.ticketAccountRateKey(request.AccountID),
	}, request.ReservationID.String(), request.AccountID.String(), request.ConnectionID.String(),
		request.InitiatorID.String(), request.ResponderID.String(), transport.DirectTicketPendingTTL.Milliseconds(),
		transport.DirectTicketRateWindow.Milliseconds(), transport.DirectTicketDeviceLimit,
		transport.DirectTicketAccountLimit, transport.DirectRouteStateLimit).Slice()
	if err != nil {
		return transport.DirectTicketReserveResult{}, directStateUnavailable("reserve ticket", err)
	}
	code, now, err := directStatusAndTime(result)
	if err != nil || code < int64(transport.DirectTicketReserveAccepted) || code > int64(transport.DirectTicketReserveCapacity) {
		return transport.DirectTicketReserveResult{}, directStateUnavailable("decode reserve ticket result", err)
	}
	return transport.DirectTicketReserveResult{
		Status: transport.DirectTicketReserveStatus(code), ServerNow: time.UnixMilli(now).UTC(),
	}, nil
}

var finalizeDirectTicketScript = redis.NewScript(`
local clock=redis.call('TIME')
local now=tonumber(clock[1])*1000+math.floor(tonumber(clock[2])/1000)
local issued=tonumber(ARGV[7])
local expires=tonumber(ARGV[8])
local replay=tonumber(ARGV[9])
if not issued or not expires or not replay or issued<now-30000 or issued>now+30000 or
   expires<=now or expires<=issued or
   expires-issued>120000 or replay<expires or replay-issued>600000 then
  return 3
end

local state=redis.call('HGET',KEYS[1],'state')
local same=redis.call('HGET',KEYS[1],'reservation_id')==ARGV[1] and
  redis.call('HGET',KEYS[1],'account_id')==ARGV[2] and
  redis.call('HGET',KEYS[1],'connection_id')==ARGV[3] and
  redis.call('HGET',KEYS[1],'initiator_id')==ARGV[4] and
  redis.call('HGET',KEYS[1],'responder_id')==ARGV[5]
if state=='active' and same and redis.call('HGET',KEYS[1],'path_epoch')==ARGV[6] and
   redis.call('HGET',KEYS[1],'issued_at_ms')==ARGV[7] and
   redis.call('HGET',KEYS[1],'expires_at_ms')==ARGV[8] and
   redis.call('HGET',KEYS[1],'replay_until_ms')==ARGV[9] then
  return 1
end
if state~='pending' or not same then return 2 end

redis.call('HSET',KEYS[1],
  'state','active','path_epoch',ARGV[6],
  'issued_at_ms',ARGV[7],'expires_at_ms',ARGV[8],'replay_until_ms',ARGV[9])
redis.call('PEXPIREAT',KEYS[1],ARGV[9])
redis.call('ZADD',KEYS[2],replay,KEYS[1])
return 1`)

func (s *DirectTransportState) FinalizeTicket(ctx context.Context, request transport.DirectTicketFinalization) (transport.DirectTicketFinalizeStatus, error) {
	if err := request.Validate(); err != nil {
		return 0, err
	}
	r := request.Reservation
	result, err := finalizeDirectTicketScript.Run(ctx, s.client, []string{
		s.routeKey(r.AccountID, r.ConnectionID), s.routeIndexKey(),
	}, r.ReservationID.String(), r.AccountID.String(), r.ConnectionID.String(), r.InitiatorID.String(),
		r.ResponderID.String(), strconv.FormatUint(request.PathEpoch, 10), request.IssuedAt.UTC().UnixMilli(),
		request.ExpiresAt.UTC().UnixMilli(), request.ReplayUntil.UTC().UnixMilli()).Int64()
	if err != nil {
		return 0, directStateUnavailable("finalize ticket", err)
	}
	if result < int64(transport.DirectTicketFinalizeAccepted) || result > int64(transport.DirectTicketFinalizeExpired) {
		return 0, directStateUnavailable("decode finalize ticket result", nil)
	}
	return transport.DirectTicketFinalizeStatus(result), nil
}

var abortDirectTicketScript = redis.NewScript(`
if redis.call('HGET',KEYS[1],'state')~='pending' or
   redis.call('HGET',KEYS[1],'reservation_id')~=ARGV[1] then return 0 end
redis.call('DEL',KEYS[1])
redis.call('ZREM',KEYS[2],KEYS[1])
return 1`)

func (s *DirectTransportState) AbortTicket(ctx context.Context, request transport.DirectTicketReservation) (bool, error) {
	if err := request.Validate(); err != nil {
		return false, err
	}
	result, err := abortDirectTicketScript.Run(ctx, s.client, []string{
		s.routeKey(request.AccountID, request.ConnectionID), s.routeIndexKey(),
	}, request.ReservationID.String()).Int64()
	if err != nil {
		return false, directStateUnavailable("abort ticket", err)
	}
	return result == 1, nil
}

var registerDirectMailboxScript = redis.NewScript(`
local previous=redis.call('GET',KEYS[1])
redis.call('PSETEX',KEYS[1],ARGV[2],ARGV[1])
if previous and previous~=ARGV[1] then return {1,1} end
return {1,0}`)

func (s *DirectTransportState) RegisterMailbox(ctx context.Context, mailbox transport.DirectMailbox) (transport.DirectMailboxRegistration, error) {
	if err := mailbox.Validate(); err != nil {
		return transport.DirectMailboxRegistration{}, err
	}
	result, err := registerDirectMailboxScript.Run(ctx, s.client, []string{s.instanceKey(mailbox.InstanceID)},
		mailbox.Generation.String(), transport.DirectMailboxPresenceTTL.Milliseconds()).Slice()
	if err != nil {
		return transport.DirectMailboxRegistration{}, directStateUnavailable("register mailbox", err)
	}
	registered, replaced, err := directPair(result)
	if err != nil {
		return transport.DirectMailboxRegistration{}, directStateUnavailable("decode register mailbox result", err)
	}
	return transport.DirectMailboxRegistration{Registered: registered == 1, Replaced: replaced == 1}, nil
}

var refreshDirectMailboxScript = redis.NewScript(`
if redis.call('GET',KEYS[1])~=ARGV[1] then return 0 end
redis.call('PEXPIRE',KEYS[1],ARGV[2])
return 1`)

func (s *DirectTransportState) RefreshMailbox(ctx context.Context, mailbox transport.DirectMailbox) (bool, error) {
	if err := mailbox.Validate(); err != nil {
		return false, err
	}
	result, err := refreshDirectMailboxScript.Run(ctx, s.client, []string{s.instanceKey(mailbox.InstanceID)},
		mailbox.Generation.String(), transport.DirectMailboxPresenceTTL.Milliseconds()).Int64()
	if err != nil {
		return false, directStateUnavailable("refresh mailbox", err)
	}
	return result == 1, nil
}

var removeDirectMailboxScript = redis.NewScript(`
if redis.call('GET',KEYS[1])~=ARGV[1] then return 0 end
redis.call('DEL',KEYS[1])
return 1`)

func (s *DirectTransportState) RemoveMailbox(ctx context.Context, mailbox transport.DirectMailbox) (bool, error) {
	if err := mailbox.Validate(); err != nil {
		return false, err
	}
	result, err := removeDirectMailboxScript.Run(ctx, s.client, []string{s.instanceKey(mailbox.InstanceID)}, mailbox.Generation.String()).Int64()
	if err != nil {
		return false, directStateUnavailable("remove mailbox", err)
	}
	return result == 1, nil
}

var registerDirectPresenceScript = redis.NewScript(`
if redis.call('GET',KEYS[2])~=ARGV[4] then return {0,0} end
local oldAccount=redis.call('HGET',KEYS[1],'account_id')
local oldInstance=redis.call('HGET',KEYS[1],'instance_id')
local oldMailbox=redis.call('HGET',KEYS[1],'mailbox_generation')
local oldPeer=redis.call('HGET',KEYS[1],'peer_generation')
local oldMultipair=redis.call('HGET',KEYS[1],'multipair_v2')
if oldAccount and oldAccount~=ARGV[1] then return {0,0} end
local replaced=0
if oldPeer and (oldAccount~=ARGV[1] or oldInstance~=ARGV[3] or oldMailbox~=ARGV[4] or
                oldPeer~=ARGV[5] or oldMultipair~=ARGV[6]) then
  replaced=1
end
redis.call('HSET',KEYS[1],
  'schema','2','account_id',ARGV[1],'device_id',ARGV[2],
  'instance_id',ARGV[3],'mailbox_generation',ARGV[4],'peer_generation',ARGV[5],
  'multipair_v2',ARGV[6])
redis.call('PEXPIRE',KEYS[1],ARGV[7])
return {1,replaced}`)

func (s *DirectTransportState) RegisterPresence(ctx context.Context, presence transport.DirectPresence) (transport.DirectPresenceRegistration, error) {
	if err := presence.Validate(); err != nil {
		return transport.DirectPresenceRegistration{}, err
	}
	result, err := registerDirectPresenceScript.Run(ctx, s.client, []string{
		s.presenceKey(presence.DeviceID), s.instanceKey(presence.Mailbox.InstanceID),
	}, presence.AccountID.String(), presence.DeviceID.String(), presence.Mailbox.InstanceID.String(),
		presence.Mailbox.Generation.String(), presence.PeerGeneration.String(),
		directBoolFlag(presence.SupportsMultipairV2),
		transport.DirectDevicePresenceTTL.Milliseconds()).Slice()
	if err != nil {
		return transport.DirectPresenceRegistration{}, directStateUnavailable("register presence", err)
	}
	registered, replaced, err := directPair(result)
	if err != nil {
		return transport.DirectPresenceRegistration{}, directStateUnavailable("decode register presence result", err)
	}
	return transport.DirectPresenceRegistration{Registered: registered == 1, Replaced: replaced == 1}, nil
}

var refreshDirectPresenceScript = redis.NewScript(`
if redis.call('GET',KEYS[2])~=ARGV[4] or
   redis.call('HGET',KEYS[1],'schema')~='2' or
   redis.call('HGET',KEYS[1],'account_id')~=ARGV[1] or
   redis.call('HGET',KEYS[1],'device_id')~=ARGV[2] or
   redis.call('HGET',KEYS[1],'instance_id')~=ARGV[3] or
   redis.call('HGET',KEYS[1],'mailbox_generation')~=ARGV[4] or
   redis.call('HGET',KEYS[1],'peer_generation')~=ARGV[5] or
   redis.call('HGET',KEYS[1],'multipair_v2')~=ARGV[6] then return 0 end
redis.call('PEXPIRE',KEYS[1],ARGV[7])
return 1`)

func (s *DirectTransportState) RefreshPresence(ctx context.Context, presence transport.DirectPresence) (bool, error) {
	if err := presence.Validate(); err != nil {
		return false, err
	}
	result, err := refreshDirectPresenceScript.Run(ctx, s.client, []string{
		s.presenceKey(presence.DeviceID), s.instanceKey(presence.Mailbox.InstanceID),
	}, presence.AccountID.String(), presence.DeviceID.String(), presence.Mailbox.InstanceID.String(),
		presence.Mailbox.Generation.String(), presence.PeerGeneration.String(),
		directBoolFlag(presence.SupportsMultipairV2),
		transport.DirectDevicePresenceTTL.Milliseconds()).Int64()
	if err != nil {
		return false, directStateUnavailable("refresh presence", err)
	}
	return result == 1, nil
}

var removeDirectPresenceScript = redis.NewScript(`
if redis.call('HGET',KEYS[1],'schema')~='2' or
   redis.call('HGET',KEYS[1],'account_id')~=ARGV[1] or
   redis.call('HGET',KEYS[1],'device_id')~=ARGV[2] or
   redis.call('HGET',KEYS[1],'instance_id')~=ARGV[3] or
   redis.call('HGET',KEYS[1],'mailbox_generation')~=ARGV[4] or
   redis.call('HGET',KEYS[1],'peer_generation')~=ARGV[5] or
   redis.call('HGET',KEYS[1],'multipair_v2')~=ARGV[6] then return 0 end
redis.call('DEL',KEYS[1])
return 1`)

func (s *DirectTransportState) RemovePresence(ctx context.Context, presence transport.DirectPresence) (bool, error) {
	if err := presence.Validate(); err != nil {
		return false, err
	}
	result, err := removeDirectPresenceScript.Run(ctx, s.client, []string{s.presenceKey(presence.DeviceID)},
		presence.AccountID.String(), presence.DeviceID.String(), presence.Mailbox.InstanceID.String(),
		presence.Mailbox.Generation.String(), presence.PeerGeneration.String(),
		directBoolFlag(presence.SupportsMultipairV2)).Int64()
	if err != nil {
		return false, directStateUnavailable("remove presence", err)
	}
	return result == 1, nil
}

var consumeDirectSignalBudgetScript = redis.NewScript(`
if redis.call('GET',KEYS[4])~=ARGV[4] or
   redis.call('HGET',KEYS[3],'schema')~='2' or
   redis.call('HGET',KEYS[3],'account_id')~=ARGV[1] or
   redis.call('HGET',KEYS[3],'device_id')~=ARGV[2] or
   redis.call('HGET',KEYS[3],'instance_id')~=ARGV[3] or
   redis.call('HGET',KEYS[3],'mailbox_generation')~=ARGV[4] or
   redis.call('HGET',KEYS[3],'peer_generation')~=ARGV[5] or
   redis.call('HGET',KEYS[3],'multipair_v2')~=ARGV[9] then return 3 end
local deviceCount=redis.call('INCR',KEYS[1])
if deviceCount==1 then redis.call('PEXPIRE',KEYS[1],ARGV[6]) end
local accountCount=redis.call('INCR',KEYS[2])
if accountCount==1 then redis.call('PEXPIRE',KEYS[2],ARGV[6]) end
if deviceCount>tonumber(ARGV[7]) or accountCount>tonumber(ARGV[8]) then return 2 end
return 1`)

func (s *DirectTransportState) ConsumeSignalBudget(ctx context.Context, request transport.DirectSignalBudgetRequest) (transport.DirectSignalBudgetStatus, error) {
	if err := request.Validate(); err != nil {
		return 0, err
	}
	presence := request.Sender
	result, err := consumeDirectSignalBudgetScript.Run(ctx, s.client, []string{
		s.signalDeviceRateKey(presence.DeviceID), s.signalAccountRateKey(presence.AccountID),
		s.presenceKey(presence.DeviceID), s.instanceKey(presence.Mailbox.InstanceID),
	}, presence.AccountID.String(), presence.DeviceID.String(), presence.Mailbox.InstanceID.String(),
		presence.Mailbox.Generation.String(), presence.PeerGeneration.String(),
		transport.DirectSignalRateWindow.Milliseconds(), transport.DirectSignalDeviceLimit,
		transport.DirectSignalAccountLimit, directBoolFlag(presence.SupportsMultipairV2)).Int64()
	if err != nil {
		return 0, directStateUnavailable("consume signal budget", err)
	}
	if result < int64(transport.DirectSignalBudgetAccepted) || result > int64(transport.DirectSignalBudgetStalePresence) {
		return 0, directStateUnavailable("decode signal budget result", nil)
	}
	return transport.DirectSignalBudgetStatus(result), nil
}

var authorizeAndPublishDirectSignalScript = redis.NewScript(`
local function decimal_increment(value)
  if string.len(value)==0 then return nil end
  if string.len(value)>1 and string.sub(value,1,1)=='0' then return nil end
  local result={}
  local carry=1
  for index=string.len(value),1,-1 do
    local digit=string.byte(value,index)-48
    if digit<0 or digit>9 then return nil end
    if carry==1 then
      digit=digit+1
      if digit==10 then digit=0 else carry=0 end
    end
    result[index]=string.char(48+digit)
  end
  if carry==1 then return '1'..table.concat(result) end
  return table.concat(result)
end

if redis.call('GET',KEYS[3])~=ARGV[6] or
   redis.call('HGET',KEYS[2],'schema')~='2' or
   redis.call('HGET',KEYS[2],'account_id')~=ARGV[1] or
   redis.call('HGET',KEYS[2],'device_id')~=ARGV[3] or
   redis.call('HGET',KEYS[2],'instance_id')~=ARGV[5] or
   redis.call('HGET',KEYS[2],'mailbox_generation')~=ARGV[6] or
   redis.call('HGET',KEYS[2],'peer_generation')~=ARGV[7] or
   redis.call('HGET',KEYS[2],'multipair_v2')~=ARGV[18] then return {5,0,0} end

if redis.call('HGET',KEYS[1],'state')~='active' or
   redis.call('HGET',KEYS[1],'account_id')~=ARGV[1] or
   redis.call('HGET',KEYS[1],'connection_id')~=ARGV[2] or
   redis.call('HGET',KEYS[1],'path_epoch')~=ARGV[8] then return {2,0,0} end
local initiator=redis.call('HGET',KEYS[1],'initiator_id')
local responder=redis.call('HGET',KEYS[1],'responder_id')
if not ((initiator==ARGV[3] and responder==ARGV[4]) or
        (responder==ARGV[3] and initiator==ARGV[4])) then return {2,0,0} end

local clock=redis.call('TIME')
local now=tonumber(clock[1])*1000+math.floor(tonumber(clock[2])/1000)
local issued=tonumber(redis.call('HGET',KEYS[1],'issued_at_ms'))
local expires=tonumber(redis.call('HGET',KEYS[1],'expires_at_ms'))
local sent=tonumber(ARGV[10])
if not issued or not expires or not sent or now>=expires or sent<issued-30000 or sent>now+30000 then
  return {2,0,now}
end

local lastField='last_seq:'..ARGV[3]
local last=redis.call('HGET',KEYS[1],lastField) or '0'
local expected=decimal_increment(last)
if not expected or ARGV[9]~=expected then return {3,0,now} end
local nonceField='nonce:'..ARGV[3]..':'..ARGV[11]
if redis.call('HEXISTS',KEYS[1],nonceField)==1 then return {3,0,now} end

local signalField='signals:'..ARGV[3]
local signalCount=tonumber(redis.call('HGET',KEYS[1],signalField) or '0')
if signalCount>=tonumber(ARGV[16]) then return {4,0,now} end
local signalType=tonumber(ARGV[12])
local candidateField='candidates:'..ARGV[3]
local candidateCount=tonumber(redis.call('HGET',KEYS[1],candidateField) or '0')
local candidate=signalType==1 or signalType==2 or signalType==3
if candidate and candidateCount>=tonumber(ARGV[17]) then return {4,0,now} end

local recipientAccount=redis.call('HGET',KEYS[4],'account_id')
local recipientDevice=redis.call('HGET',KEYS[4],'device_id')
if recipientAccount and (recipientAccount~=ARGV[1] or recipientDevice~=ARGV[4]) then return {2,0,now} end
local recipientInstance=redis.call('HGET',KEYS[4],'instance_id')
local recipientMailbox=redis.call('HGET',KEYS[4],'mailbox_generation')
local recipientPeer=redis.call('HGET',KEYS[4],'peer_generation')
local recipientMultipair=redis.call('HGET',KEYS[4],'multipair_v2')
if signalType>10 and recipientAccount and
   (redis.call('HGET',KEYS[4],'schema')~='2' or recipientMultipair~='1') then return {2,0,now} end

redis.call('HSET',KEYS[1],lastField,ARGV[9],nonceField,'1')
redis.call('HINCRBY',KEYS[1],signalField,1)
if candidate then redis.call('HINCRBY',KEYS[1],candidateField,1) end

local subscribers=0
if recipientAccount and recipientInstance and recipientMailbox and recipientPeer and
   redis.call('GET',ARGV[15]..recipientInstance)==recipientMailbox then
  local channel=ARGV[14]..recipientInstance..':'..recipientMailbox
  local message='DTS1'..ARGV[4]..recipientPeer..ARGV[13]
  subscribers=redis.call('PUBLISH',channel,message)
end
return {1,subscribers,now}`)

func (s *DirectTransportState) AuthorizeAndPublish(ctx context.Context, request transport.DirectSignalPublishRequest) (transport.DirectSignalPublishResult, error) {
	if err := request.Validate(); err != nil {
		return transport.DirectSignalPublishResult{}, err
	}
	sender := request.Sender
	nonce := hex.EncodeToString(request.Nonce[:])
	result, err := authorizeAndPublishDirectSignalScript.Run(ctx, s.client, []string{
		s.routeKey(request.AccountID, request.ConnectionID), s.presenceKey(sender.DeviceID),
		s.instanceKey(sender.Mailbox.InstanceID), s.presenceKey(request.RecipientID),
	}, request.AccountID.String(), request.ConnectionID.String(), sender.DeviceID.String(), request.RecipientID.String(),
		sender.Mailbox.InstanceID.String(), sender.Mailbox.Generation.String(),
		sender.PeerGeneration.String(), strconv.FormatUint(request.PathEpoch, 10),
		strconv.FormatUint(request.SignalSequence, 10), request.SentAt.UTC().UnixMilli(), nonce,
		uint32(request.SignalType), request.Frame, s.mailboxChannelPrefix(), s.instanceKeyPrefix(),
		transport.DirectSignalsPerDirection, transport.DirectCandidatesPerDirection,
		directBoolFlag(sender.SupportsMultipairV2)).Slice()
	if err != nil {
		return transport.DirectSignalPublishResult{}, directStateUnavailable("authorize and publish signal", err)
	}
	if len(result) != 3 {
		return transport.DirectSignalPublishResult{}, directStateUnavailable("decode signal publish result", nil)
	}
	status, statusErr := directInt64(result[0])
	subscribers, subscribersErr := directInt64(result[1])
	now, nowErr := directInt64(result[2])
	if statusErr != nil || subscribersErr != nil || nowErr != nil ||
		status < int64(transport.DirectSignalPublishAccepted) || status > int64(transport.DirectSignalPublishStalePresence) {
		return transport.DirectSignalPublishResult{}, directStateUnavailable("decode signal publish result", firstDirectError(statusErr, subscribersErr, nowErr))
	}
	return transport.DirectSignalPublishResult{
		Status: transport.DirectSignalPublishStatus(status), SubscriberCount: subscribers,
		ServerNow: time.UnixMilli(now).UTC(),
	}, nil
}

func (s *DirectTransportState) SubscribeMailbox(ctx context.Context, mailbox transport.DirectMailbox) (transport.DirectMailboxSubscription, error) {
	if err := mailbox.Validate(); err != nil {
		return nil, err
	}
	pubsub := s.client.Subscribe(ctx, s.mailboxChannel(mailbox))
	if _, err := pubsub.Receive(ctx); err != nil {
		_ = pubsub.Close()
		return nil, directStateUnavailable("subscribe mailbox", err)
	}
	return &directMailboxSubscription{pubsub: pubsub}, nil
}

type directMailboxSubscription struct{ pubsub *redis.PubSub }

func (s *directMailboxSubscription) Receive(ctx context.Context) (transport.DirectMailboxDelivery, error) {
	message, err := s.pubsub.ReceiveMessage(ctx)
	if err != nil {
		return transport.DirectMailboxDelivery{}, directStateUnavailable("receive mailbox", err)
	}
	return decodeDirectMailboxDelivery([]byte(message.Payload))
}

func (s *directMailboxSubscription) Close() error { return s.pubsub.Close() }

func decodeDirectMailboxDelivery(message []byte) (transport.DirectMailboxDelivery, error) {
	if len(message) <= directMailboxMessageHeaderBytes || string(message[:len(directMailboxMessagePrefix)]) != directMailboxMessagePrefix {
		return transport.DirectMailboxDelivery{}, directStateUnavailable("decode mailbox delivery", nil)
	}
	offset := len(directMailboxMessagePrefix)
	recipient, recipientErr := uuid.Parse(string(message[offset : offset+36]))
	offset += 36
	peer, peerErr := uuid.Parse(string(message[offset : offset+36]))
	offset += 36
	if recipientErr != nil || peerErr != nil || recipient == uuid.Nil || peer == uuid.Nil {
		return transport.DirectMailboxDelivery{}, directStateUnavailable("decode mailbox delivery", firstDirectError(recipientErr, peerErr))
	}
	return transport.DirectMailboxDelivery{
		RecipientID: recipient, PeerGeneration: peer, Frame: append([]byte(nil), message[offset:]...),
	}, nil
}

func directStatusAndTime(result []any) (int64, int64, error) {
	if len(result) != 2 {
		return 0, 0, fmt.Errorf("unexpected result length %d", len(result))
	}
	status, err := directInt64(result[0])
	if err != nil {
		return 0, 0, err
	}
	now, err := directInt64(result[1])
	return status, now, err
}

func directPair(result []any) (int64, int64, error) {
	if len(result) != 2 {
		return 0, 0, fmt.Errorf("unexpected result length %d", len(result))
	}
	first, err := directInt64(result[0])
	if err != nil {
		return 0, 0, err
	}
	second, err := directInt64(result[1])
	return first, second, err
}

func directInt64(value any) (int64, error) {
	switch item := value.(type) {
	case int64:
		return item, nil
	case string:
		return strconv.ParseInt(item, 10, 64)
	case []byte:
		return strconv.ParseInt(string(item), 10, 64)
	default:
		return 0, fmt.Errorf("unexpected integer type %T", value)
	}
}

func directStateUnavailable(operation string, cause error) error {
	if cause == nil {
		return fmt.Errorf("%w: %s", transport.ErrDirectStateUnavailable, operation)
	}
	return fmt.Errorf("%w: %s: %w", transport.ErrDirectStateUnavailable, operation, cause)
}

func firstDirectError(items ...error) error {
	for _, item := range items {
		if item != nil {
			return item
		}
	}
	return nil
}

func directBoolFlag(value bool) string {
	if value {
		return "1"
	}
	return "0"
}

func (s *DirectTransportState) routeKey(accountID, connectionID uuid.UUID) string {
	return s.namespace + ":route:" + accountID.String() + ":" + connectionID.String()
}

func (s *DirectTransportState) routeIndexKey() string { return s.namespace + ":route-index" }
func (s *DirectTransportState) ticketDeviceRateKey(deviceID uuid.UUID) string {
	return s.namespace + ":rate:ticket:device:" + deviceID.String()
}
func (s *DirectTransportState) ticketAccountRateKey(accountID uuid.UUID) string {
	return s.namespace + ":rate:ticket:account:" + accountID.String()
}
func (s *DirectTransportState) signalDeviceRateKey(deviceID uuid.UUID) string {
	return s.namespace + ":rate:signal:device:" + deviceID.String()
}
func (s *DirectTransportState) signalAccountRateKey(accountID uuid.UUID) string {
	return s.namespace + ":rate:signal:account:" + accountID.String()
}
func (s *DirectTransportState) presenceKey(deviceID uuid.UUID) string {
	return s.namespace + ":presence:" + deviceID.String()
}
func (s *DirectTransportState) instanceKey(instanceID uuid.UUID) string {
	return s.instanceKeyPrefix() + instanceID.String()
}
func (s *DirectTransportState) instanceKeyPrefix() string    { return s.namespace + ":instance:" }
func (s *DirectTransportState) mailboxChannelPrefix() string { return s.namespace + ":mailbox:" }
func (s *DirectTransportState) mailboxChannel(mailbox transport.DirectMailbox) string {
	return s.mailboxChannelPrefix() + mailbox.InstanceID.String() + ":" + mailbox.Generation.String()
}

var _ transport.DirectStateStore = (*DirectTransportState)(nil)
