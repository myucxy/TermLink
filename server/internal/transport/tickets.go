package transport

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"net/http"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
	"google.golang.org/protobuf/encoding/protowire"
)

const (
	ticketDomain = "TERMLINK-DIRECT-TICKET-V1\n"
	ticketTTL    = 20 * time.Minute
	// Renewal lead: a connection may request a fresh ticket once half of its
	// current lifetime has elapsed, and at most twice per minute per connection.
	ticketRenewalAfter      = ticketTTL / 2
	maxRenewalsPerMinute    = 2
	connectionReplayTTL     = 10 * time.Minute
	maxTicketBodyBytes      = 4 << 10
	maxTicketStates         = 4096
	maxSignalsPerDirection  = 256
	deviceTicketsPerMinute  = 12
	accountTicketsPerMinute = 60
	deviceSignalsPerWindow  = 96
	accountSignalsPerWindow = 512
	signalRateWindow        = 10 * time.Second
)

var (
	ErrTransportDenied      = errors.New("transport route denied")
	ErrTransportReplay      = errors.New("transport route replayed")
	ErrTransportRateLimited = errors.New("transport rate limited")
	ErrTransportInvalid     = errors.New("invalid transport request")
)

type TransportAuthority interface {
	AllocateDirectTransportEpoch(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) (uint64, error)
	AuthorizeDirectTransportSignal(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (string, error)
}

type RouteTicketService struct {
	authority                TransportAuthority
	state                    DirectStateStore
	directRoutingPermitted   bool
	routingPermittedOverride func() bool
	private                  ed25519.PrivateKey
	public                   ed25519.PublicKey
	keyID                    []byte
	clock                    func() time.Time
	random                   io.Reader

	mu                 sync.Mutex
	connections        map[connectionKey]*routeState
	pendingConnections map[connectionKey]struct{}
	deviceTicketRate   map[uuid.UUID]rateState
	accountTicketRate  map[uuid.UUID]rateState
	deviceSignalRate   map[uuid.UUID]rateState
	accountSignalRate  map[uuid.UUID]rateState
	connectionRenewals map[uuid.UUID][]time.Time
	stunServers        []string
}

// EnabledDeviceConfig is intentionally separate from the public client config.
// Production only registers this handler while the direct-transport feature is
// enabled, so a disabled deployment advertises no direct capability or key.
type EnabledDeviceConfig struct {
	ProtocolVersion        uint32             `json:"protocol_version"`
	Capabilities           []string           `json:"capabilities"`
	DirectRoutingPermitted bool               `json:"direct_routing_permitted"`
	TicketSigningKeys      []TicketSigningKey `json:"ticket_signing_keys"`
	STUNServers            []string           `json:"stun_servers"`
}

type TicketSigningKey struct {
	KeyID     string `json:"key_id"`
	Algorithm string `json:"algorithm"`
	PublicKey string `json:"public_key"`
}

type connectionKey struct {
	accountID  uuid.UUID
	connection uuid.UUID
}
type routeState struct {
	accountID    uuid.UUID
	initiator    uuid.UUID
	responder    uuid.UUID
	connectionID uuid.UUID
	pathEpoch    uint64
	issuedAt     time.Time
	expiresAt    time.Time
	replayUntil  time.Time
	lastSeq      map[uuid.UUID]uint64
	candidates   map[uuid.UUID]int
	signals      map[uuid.UUID]int
	nonces       map[uuid.UUID]map[[12]byte]struct{}
}
type rateState struct {
	started time.Time
	count   int
}

func NewRouteTicketService(authority TransportAuthority, seed []byte, stunServers ...string) (*RouteTicketService, error) {
	return newRouteTicketService(authority, nil, seed, false, stunServers...)
}

// NewRouteTicketServiceWithRouting constructs the single-node service with an
// explicit production data-routing gate. Callers must keep this false until
// both clients negotiate direct_transport_data_v1 and
// direct_session_cutover_v1.
func NewRouteTicketServiceWithRouting(authority TransportAuthority, seed []byte, directRoutingPermitted bool, stunServers ...string) (*RouteTicketService, error) {
	return newRouteTicketService(authority, nil, seed, directRoutingPermitted, stunServers...)
}

// NewSharedRouteTicketService is the production multi-replica constructor.
// The shared state dependency is mandatory and no operation falls back to
// process-local replay, rate, or signalling state.
func NewSharedRouteTicketService(authority TransportAuthority, state DirectStateStore, seed []byte, stunServers ...string) (*RouteTicketService, error) {
	return NewSharedRouteTicketServiceWithRouting(authority, state, seed, false, stunServers...)
}

// NewSharedRouteTicketServiceWithRouting is the multi-replica equivalent of
// NewRouteTicketServiceWithRouting.
func NewSharedRouteTicketServiceWithRouting(authority TransportAuthority, state DirectStateStore, seed []byte, directRoutingPermitted bool, stunServers ...string) (*RouteTicketService, error) {
	if state == nil {
		return nil, errors.New("direct transport shared state unavailable")
	}
	return newRouteTicketService(authority, state, seed, directRoutingPermitted, stunServers...)
}

// SetRoutingPermittedOverride replaces the static constructor-time routing
// permission with a runtime lookup. It must be called before the service is
// served; the production caller wires it to the database-backed portal policy
// so SUPER_ADMIN can toggle direct routing at runtime.
func (s *RouteTicketService) SetRoutingPermittedOverride(fn func() bool) {
	if s == nil {
		return
	}
	s.routingPermittedOverride = fn
}

// SetSTUNServers replaces the STUN server list handed to clients in the
// direct-transport device config. The management portal calls it when a
// SUPER_ADMIN updates the runtime setting; empty lists are rejected because
// a direct transport without discovery servers cannot gather candidates.
func (s *RouteTicketService) SetSTUNServers(stunServers []string) error {
	if s == nil {
		return nil
	}
	if len(stunServers) == 0 {
		return fmt.Errorf("direct transport requires at least one STUN server")
	}
	s.mu.Lock()
	s.stunServers = append([]string(nil), stunServers...)
	s.mu.Unlock()
	return nil
}

func (s *RouteTicketService) routingPermittedNow() bool {
	if s == nil {
		return false
	}
	if s.routingPermittedOverride != nil {
		return s.routingPermittedOverride()
	}
	return s.directRoutingPermitted
}

func newRouteTicketService(authority TransportAuthority, state DirectStateStore, seed []byte, directRoutingPermitted bool, stunServers ...string) (*RouteTicketService, error) {
	if authority == nil || len(seed) < ed25519.SeedSize {
		return nil, errors.New("transport ticket dependencies unavailable")
	}
	private := ed25519.NewKeyFromSeed(seed[:ed25519.SeedSize])
	public := append(ed25519.PublicKey(nil), private.Public().(ed25519.PublicKey)...)
	digest := sha256.Sum256(public)
	keyID := []byte("direct-v1-" + hex.EncodeToString(digest[:8]))
	return &RouteTicketService{
		authority: authority, state: state, directRoutingPermitted: directRoutingPermitted,
		private: private, public: public, keyID: keyID,
		clock: time.Now, random: rand.Reader,
		connections: map[connectionKey]*routeState{}, pendingConnections: map[connectionKey]struct{}{},
		deviceTicketRate: map[uuid.UUID]rateState{}, accountTicketRate: map[uuid.UUID]rateState{},
		deviceSignalRate: map[uuid.UUID]rateState{}, accountSignalRate: map[uuid.UUID]rateState{},
		connectionRenewals: map[uuid.UUID][]time.Time{},
		stunServers:        append([]string(nil), stunServers...),
	}, nil
}

func (s *RouteTicketService) TicketHandler() http.Handler { return http.HandlerFunc(s.issueTicketHTTP) }
func (s *RouteTicketService) RenewHandler() http.Handler  { return http.HandlerFunc(s.renewTicketHTTP) }
func (s *RouteTicketService) ConfigHandler() http.Handler { return http.HandlerFunc(s.configHTTP) }

type ticketRenewalRequest struct {
	TargetDeviceID string `json:"target_device_id"`
	ConnectionID   string `json:"connection_id"`
	TicketProtobuf string `json:"ticket_protobuf"`
}

func (s *RouteTicketService) renewTicketHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.Header().Set("Allow", http.MethodPost)
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, maxTicketBodyBytes)
	principal, ok := auth.PrincipalFromContext(r.Context())
	if !ok {
		writeTransportError(w, http.StatusUnauthorized, "UNAUTHENTICATED")
		return
	}
	var input ticketRenewalRequest
	if err := json.NewDecoder(r.Body).Decode(&input); err != nil {
		writeTransportError(w, http.StatusBadRequest, ErrTransportInvalid.Error())
		return
	}
	targetID, err := uuid.Parse(input.TargetDeviceID)
	connectionID, errConnection := uuid.Parse(input.ConnectionID)
	proof, errProof := base64.RawURLEncoding.DecodeString(input.TicketProtobuf)
	if err != nil || errConnection != nil || errProof != nil {
		writeTransportError(w, http.StatusBadRequest, ErrTransportInvalid.Error())
		return
	}
	ticket, err := s.Renew(r.Context(), principal, targetID, connectionID, proof)
	if err != nil {
		switch {
		case errors.Is(err, ErrDirectStateUnavailable):
			writeTransportError(w, http.StatusServiceUnavailable, "RENEWAL_UNAVAILABLE")
		case errors.Is(err, ErrTransportRateLimited):
			writeTransportError(w, http.StatusTooManyRequests, ErrTransportRateLimited.Error())
		case errors.Is(err, ErrTransportDenied), errors.Is(err, ErrTransportInvalid):
			writeTransportError(w, http.StatusForbidden, "RENEWAL_DENIED")
		default:
			writeTransportError(w, http.StatusInternalServerError, ErrTransportInvalid.Error())
		}
		return
	}
	writeTransportJSON(w, http.StatusOK, map[string]any{
		"ticket_protobuf": base64.RawURLEncoding.EncodeToString(ticket),
	})
}

func (s *RouteTicketService) configHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.Header().Set("Allow", http.MethodGet)
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	writeTransportJSON(w, http.StatusOK, s.EnabledConfig(s.stunServers))
}

func (s *RouteTicketService) EnabledConfig(stunServers []string) EnabledDeviceConfig {
	capabilities := []string{
		"direct_signaling_v1",
		"direct_transport_shadow_v1",
		"direct_transport_shadow_multipair_v2",
	}
	if s.routingPermittedNow() {
		capabilities = append(
			capabilities,
			"direct_transport_data_v1",
			"direct_session_cutover_v1",
		)
	}
	return EnabledDeviceConfig{
		ProtocolVersion:        1,
		Capabilities:           capabilities,
		DirectRoutingPermitted: s.routingPermittedNow(),
		TicketSigningKeys: []TicketSigningKey{{
			KeyID: string(s.keyID), Algorithm: "Ed25519", PublicKey: base64.RawURLEncoding.EncodeToString(s.public),
		}},
		STUNServers: append([]string(nil), stunServers...),
	}
}

func (s *RouteTicketService) DirectRoutingPermitted() bool {
	return s != nil && s.routingPermittedNow()
}

type ticketRequest struct {
	TargetDeviceID   string   `json:"target_device_id"`
	ConnectionID     string   `json:"connection_id"`
	RequestedCarrier []string `json:"requested_carriers"`
}

func (s *RouteTicketService) issueTicketHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.Header().Set("Allow", http.MethodPost)
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	principal, ok := auth.PrincipalFromContext(r.Context())
	if !ok {
		writeTransportError(w, http.StatusUnauthorized, "UNAUTHENTICATED")
		return
	}
	var input ticketRequest
	decoder := json.NewDecoder(io.LimitReader(r.Body, maxTicketBodyBytes+1))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&input); err != nil || decoder.Decode(&struct{}{}) != io.EOF {
		writeTransportError(w, http.StatusBadRequest, "INVALID_TRANSPORT_REQUEST")
		return
	}
	target, targetErr := uuid.Parse(input.TargetDeviceID)
	connectionID, connectionErr := uuid.Parse(input.ConnectionID)
	if targetErr != nil || connectionErr != nil || target.String() != input.TargetDeviceID || connectionID.String() != input.ConnectionID || connectionID.Version() != 4 || len(input.RequestedCarrier) != 1 || input.RequestedCarrier[0] != "UDP_QUIC" {
		writeTransportError(w, http.StatusBadRequest, "INVALID_TRANSPORT_REQUEST")
		return
	}
	ticket, err := s.Issue(r.Context(), principal, target, connectionID)
	if err != nil {
		switch {
		case errors.Is(err, ErrTransportRateLimited):
			writeTransportError(w, http.StatusTooManyRequests, "TRANSPORT_RATE_LIMITED")
		case errors.Is(err, ErrTransportReplay):
			writeTransportError(w, http.StatusConflict, "TRANSPORT_CONNECTION_REPLAYED")
		case errors.Is(err, ErrTransportDenied), errors.Is(err, store.ErrNotFound):
			writeTransportError(w, http.StatusForbidden, "TRANSPORT_ROUTE_DENIED")
		default:
			writeTransportError(w, http.StatusServiceUnavailable, "TRANSPORT_UNAVAILABLE")
		}
		return
	}
	writeTransportJSON(w, http.StatusCreated, map[string]any{
		"protocol_version": 1,
		"ticket_protobuf":  base64.RawURLEncoding.EncodeToString(ticket),
	})
}

func (s *RouteTicketService) Issue(ctx context.Context, principal auth.Principal, targetID, connectionID uuid.UUID) ([]byte, error) {
	if connectionID.Version() != 4 {
		return nil, ErrTransportInvalid
	}
	if principal.UserID == uuid.Nil || principal.Device.ID == uuid.Nil ||
		principal.Device.UserID != principal.UserID || principal.Device.ID == targetID ||
		principal.Device.TrustState != "TRUSTED" {
		return nil, ErrTransportDenied
	}
	if s.state != nil {
		return s.issueShared(ctx, principal, targetID, connectionID)
	}
	now := s.clock().UTC()
	key := connectionKey{accountID: principal.UserID, connection: connectionID}
	s.mu.Lock()
	s.cleanupLocked(now)
	if len(s.connections)+len(s.pendingConnections) >= maxTicketStates || !allowRateLocked(s.deviceTicketRate, principal.Device.ID, now, time.Minute, deviceTicketsPerMinute) || !allowRateLocked(s.accountTicketRate, principal.UserID, now, time.Minute, accountTicketsPerMinute) {
		s.mu.Unlock()
		return nil, ErrTransportRateLimited
	}
	_, active := s.connections[key]
	_, pending := s.pendingConnections[key]
	if active || pending {
		s.mu.Unlock()
		return nil, ErrTransportReplay
	}
	s.pendingConnections[key] = struct{}{}
	s.mu.Unlock()
	finishPending := func(state *routeState) {
		s.mu.Lock()
		delete(s.pendingConnections, key)
		if state != nil {
			s.connections[key] = state
		}
		s.mu.Unlock()
	}
	epoch, err := s.authority.AllocateDirectTransportEpoch(
		ctx, principal.UserID, principal.Device.ID, targetID,
	)
	if err != nil {
		finishPending(nil)
		if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) {
			return nil, ErrTransportDenied
		}
		return nil, err
	}
	now = s.clock().UTC()
	ticketID := uuid.New()
	serverNonce := make([]byte, 16)
	if _, err := io.ReadFull(s.random, serverNonce); err != nil {
		finishPending(nil)
		return nil, err
	}
	expiresAt := now.Add(ticketTTL)
	claims := marshalTicketClaims(ticketID, principal.UserID, principal.Device.ID, targetID, connectionID, epoch, now, expiresAt, serverNonce, s.keyID)
	signature := ed25519.Sign(s.private, append([]byte(ticketDomain), claims...))
	ticket := append(append([]byte(nil), claims...), appendBytesField(nil, 13, signature)...)
	state := &routeState{
		accountID: principal.UserID, initiator: principal.Device.ID, responder: targetID,
		connectionID: connectionID, pathEpoch: epoch, issuedAt: now, expiresAt: expiresAt,
		replayUntil: now.Add(connectionReplayTTL), lastSeq: map[uuid.UUID]uint64{},
		candidates: map[uuid.UUID]int{}, signals: map[uuid.UUID]int{},
		nonces: map[uuid.UUID]map[[12]byte]struct{}{},
	}
	finishPending(state)
	return ticket, nil
}

func (s *RouteTicketService) issueShared(ctx context.Context, principal auth.Principal, targetID, connectionID uuid.UUID) ([]byte, error) {
	reservation := DirectTicketReservation{
		AccountID: principal.UserID, ConnectionID: connectionID,
		InitiatorID: principal.Device.ID, ResponderID: targetID, ReservationID: uuid.New(),
	}
	reserved, err := s.state.ReserveTicket(ctx, reservation)
	if err != nil {
		return nil, err
	}
	switch reserved.Status {
	case DirectTicketReserveReplay:
		return nil, ErrTransportReplay
	case DirectTicketReserveRateLimited, DirectTicketReserveCapacity:
		return nil, ErrTransportRateLimited
	case DirectTicketReserveAccepted:
	default:
		return nil, ErrDirectStateUnavailable
	}
	abort := true
	defer func() {
		if abort {
			abortCtx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			defer cancel()
			_, _ = s.state.AbortTicket(abortCtx, reservation)
		}
	}()
	epoch, err := s.authority.AllocateDirectTransportEpoch(ctx, principal.UserID, principal.Device.ID, targetID)
	if err != nil {
		if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) {
			return nil, ErrTransportDenied
		}
		return nil, err
	}
	now := s.clock().UTC()
	ticketID := uuid.New()
	serverNonce := make([]byte, 16)
	if _, err := io.ReadFull(s.random, serverNonce); err != nil {
		return nil, err
	}
	expiresAt := now.Add(ticketTTL)
	claims := marshalTicketClaims(ticketID, principal.UserID, principal.Device.ID, targetID, connectionID, epoch, now, expiresAt, serverNonce, s.keyID)
	signature := ed25519.Sign(s.private, append([]byte(ticketDomain), claims...))
	ticket := append(append([]byte(nil), claims...), appendBytesField(nil, 13, signature)...)
	status, err := s.state.FinalizeTicket(ctx, DirectTicketFinalization{
		Reservation: reservation, PathEpoch: epoch, IssuedAt: now, ExpiresAt: expiresAt,
		ReplayUntil: now.Add(connectionReplayTTL),
	})
	if err != nil || status != DirectTicketFinalizeAccepted {
		if err != nil {
			return nil, err
		}
		return nil, ErrTransportReplay
	}
	abort = false
	return ticket, nil
}

// AuthorizeAndPublishSignal performs the per-frame PostgreSQL authority check
// before the shared store atomically advances replay state and publishes to the
// recipient mailbox. Failures are confined to the signalling lane.
func (s *RouteTicketService) AuthorizeAndPublishSignal(ctx context.Context, principal auth.Principal, presence DirectPresence, envelope SignalEnvelope, frame []byte) error {
	if isDirectDataControlSignal(envelope.SignalType) && !s.DirectRoutingPermitted() {
		return ErrTransportDenied
	}
	if s.state == nil || presence.AccountID != principal.UserID || presence.DeviceID != principal.Device.ID {
		return ErrTransportDenied
	}
	connectionID, err := uuid.FromBytes(envelope.ConnectionID)
	if err != nil {
		return ErrTransportDenied
	}
	recipientID, err := uuid.FromBytes(envelope.RecipientDeviceID)
	if err != nil {
		return ErrTransportDenied
	}
	budget, err := s.state.ConsumeSignalBudget(ctx, DirectSignalBudgetRequest{Sender: presence})
	if err != nil {
		return err
	}
	if budget == DirectSignalBudgetRateLimited {
		return ErrTransportRateLimited
	}
	if budget != DirectSignalBudgetAccepted {
		return ErrTransportDenied
	}
	responderID, err := s.resolveSignalResponder(ctx, principal.UserID, envelope, principal.Device.ID, recipientID)
	if err != nil {
		return err
	}
	if responderID == uuid.Nil || !s.authorizeSignalKey(ctx, principal.UserID, principal.Device.ID, recipientID, responderID, envelope.KeyID) {
		return ErrTransportDenied
	}
	var nonce [12]byte
	copy(nonce[:], envelope.Nonce)
	result, err := s.state.AuthorizeAndPublish(ctx, DirectSignalPublishRequest{
		AccountID: principal.UserID, ConnectionID: connectionID, Sender: presence,
		RecipientID: recipientID, PathEpoch: envelope.PathEpoch, SignalSequence: envelope.SignalSeq,
		SignalType: envelope.SignalType, SentAt: time.UnixMilli(envelope.SentAtMS), Nonce: nonce,
		Frame: append([]byte(nil), frame...),
	})
	if err != nil {
		return err
	}
	switch result.Status {
	case DirectSignalPublishAccepted:
		if result.SubscriberCount != 1 {
			return ErrDirectStateUnavailable
		}
		return nil
	case DirectSignalPublishReplay:
		return ErrTransportReplay
	case DirectSignalPublishRateLimited:
		return ErrTransportRateLimited
	default:
		return ErrTransportDenied
	}
}

func (s *RouteTicketService) AuthorizeSignal(ctx context.Context, principal auth.Principal, envelope SignalEnvelope) error {
	if isDirectDataControlSignal(envelope.SignalType) && !s.DirectRoutingPermitted() {
		return ErrTransportDenied
	}
	connectionID, err := uuid.FromBytes(envelope.ConnectionID)
	if err != nil {
		return ErrTransportDenied
	}
	recipientID, err := uuid.FromBytes(envelope.RecipientDeviceID)
	if err != nil {
		return ErrTransportDenied
	}
	now := s.clock().UTC()
	responderID, err := s.resolveSignalResponder(ctx, principal.UserID, envelope, principal.Device.ID, recipientID)
	if err != nil {
		return err
	}
	s.mu.Lock()
	s.cleanupLocked(now)
	allowed := allowRateLocked(s.deviceSignalRate, principal.Device.ID, now, signalRateWindow, deviceSignalsPerWindow) &&
		allowRateLocked(s.accountSignalRate, principal.UserID, now, signalRateWindow, accountSignalsPerWindow)
	s.mu.Unlock()
	if !allowed {
		return ErrTransportRateLimited
	}
	if responderID == uuid.Nil || !s.authorizeSignalKey(ctx, principal.UserID, principal.Device.ID, recipientID, responderID, envelope.KeyID) {
		return ErrTransportDenied
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	state := s.connections[connectionKey{accountID: principal.UserID, connection: connectionID}]
	sentAt := time.UnixMilli(envelope.SentAtMS)
	if state == nil || !now.Before(state.expiresAt) || sentAt.Before(state.issuedAt.Add(-30*time.Second)) || sentAt.After(now.Add(30*time.Second)) || state.pathEpoch != envelope.PathEpoch || !state.containsDirection(principal.Device.ID, recipientID) {
		return ErrTransportDenied
	}
	if !IsNextSignalSequence(state.lastSeq[principal.Device.ID], envelope.SignalSeq) {
		return ErrTransportReplay
	}
	if state.signals[principal.Device.ID] >= maxSignalsPerDirection {
		return ErrTransportRateLimited
	}
	var nonce [12]byte
	copy(nonce[:], envelope.Nonce)
	if state.nonces[principal.Device.ID] == nil {
		state.nonces[principal.Device.ID] = map[[12]byte]struct{}{}
	}
	if _, reused := state.nonces[principal.Device.ID][nonce]; reused {
		return ErrTransportReplay
	}
	if envelope.SignalType == SignalPathOffer || envelope.SignalType == SignalPathAnswer || envelope.SignalType == SignalCandidateUpdate {
		state.candidates[principal.Device.ID]++
		if state.candidates[principal.Device.ID] > 32 {
			return ErrTransportRateLimited
		}
	}
	state.lastSeq[principal.Device.ID] = envelope.SignalSeq
	state.signals[principal.Device.ID]++
	state.nonces[principal.Device.ID][nonce] = struct{}{}
	return nil
}

func (s *RouteTicketService) resolveSignalResponder(
	ctx context.Context,
	accountID uuid.UUID,
	envelope SignalEnvelope,
	senderID, recipientID uuid.UUID,
) (uuid.UUID, error) {
	connectionID, err := uuid.FromBytes(envelope.ConnectionID)
	if err != nil {
		return uuid.Nil, nil
	}
	if s.state != nil {
		return s.state.ResolveSignalResponder(ctx, DirectSignalRouteRequest{
			AccountID: accountID, ConnectionID: connectionID, SenderID: senderID,
			RecipientID: recipientID, PathEpoch: envelope.PathEpoch,
		})
	}
	now := s.clock().UTC()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	state := s.connections[connectionKey{accountID: accountID, connection: connectionID}]
	if state == nil || !now.Before(state.expiresAt) || state.pathEpoch != envelope.PathEpoch ||
		!state.containsDirection(senderID, recipientID) {
		return uuid.Nil, nil
	}
	return state.responder, nil
}

func (s *RouteTicketService) authorizeSignalKey(
	ctx context.Context,
	accountID, senderID, recipientID, responderID uuid.UUID,
	keyID []byte,
) bool {
	expected, err := s.authority.AuthorizeDirectTransportSignal(
		ctx, accountID, senderID, recipientID, responderID,
	)
	expectedBytes := []byte(expected)
	return err == nil && len(expectedBytes) >= 1 && len(expectedBytes) <= 64 && utf8.ValidString(expected) &&
		len(keyID) == len(expectedBytes) && subtle.ConstantTimeCompare(keyID, expectedBytes) == 1
}

// IsNextSignalSequence applies the signalling replay policy without overflowing
// at the protocol's signed 64-bit storage boundary.
func IsNextSignalSequence(previous, next uint64) bool {
	return previous < math.MaxInt64 && next == previous+1
}

func (s *RouteTicketService) cleanupLocked(now time.Time) {
	cleanupRateLocked(s.deviceTicketRate, now, time.Minute)
	cleanupRateLocked(s.accountTicketRate, now, time.Minute)
	cleanupRateLocked(s.deviceSignalRate, now, signalRateWindow)
	cleanupRateLocked(s.accountSignalRate, now, signalRateWindow)
	for key, state := range s.connections {
		if !now.Before(state.replayUntil) {
			delete(s.connections, key)
		}
	}
}

func allowRateLocked(states map[uuid.UUID]rateState, id uuid.UUID, now time.Time, window time.Duration, limit int) bool {
	state := states[id]
	if state.started.IsZero() || now.Sub(state.started) >= window {
		state = rateState{started: now}
	}
	state.count++
	states[id] = state
	return state.count <= limit
}

func cleanupRateLocked(states map[uuid.UUID]rateState, now time.Time, window time.Duration) {
	for id, state := range states {
		if now.Sub(state.started) >= window {
			delete(states, id)
		}
	}
}

func (r *routeState) containsDirection(sender, recipient uuid.UUID) bool {
	return r.initiator == sender && r.responder == recipient || r.responder == sender && r.initiator == recipient
}

func complementaryDeviceTypes(first, second string) bool {
	return first == "ANDROID" && store.IsPCAgentDeviceType(second) || second == "ANDROID" && store.IsPCAgentDeviceType(first)
}

func marshalTicketClaims(ticketID, accountID, initiatorID, responderID, connectionID uuid.UUID, epoch uint64, issuedAt, expiresAt time.Time, nonce, keyID []byte) []byte {
	var result []byte
	result = appendVarintField(result, 1, 1)
	result = appendBytesField(result, 2, ticketID[:])
	result = appendBytesField(result, 3, accountID[:])
	result = appendBytesField(result, 4, initiatorID[:])
	result = appendBytesField(result, 5, responderID[:])
	result = appendBytesField(result, 6, connectionID[:])
	result = appendVarintField(result, 7, epoch)
	result = appendVarintField(result, 8, uint64(issuedAt.UnixMilli()))
	result = appendVarintField(result, 9, uint64(expiresAt.UnixMilli()))
	// DirectPathTicketV1.allowed_carriers is a repeated enum. Proto3 generated
	// clients serialize repeated scalar values in packed form, and the ticket
	// signature covers the exact canonical protobuf bytes. Keep the server's
	// hand-written encoder byte-identical to Java/C# generated serializers.
	result = appendBytesField(result, 10, protowire.AppendVarint(nil, 1))
	result = appendBytesField(result, 11, nonce)
	result = appendBytesField(result, 12, keyID)
	return result
}

func appendVarintField(dst []byte, number protowire.Number, value uint64) []byte {
	dst = protowire.AppendTag(dst, number, protowire.VarintType)
	return protowire.AppendVarint(dst, value)
}
func appendBytesField(dst []byte, number protowire.Number, value []byte) []byte {
	dst = protowire.AppendTag(dst, number, protowire.BytesType)
	return protowire.AppendBytes(dst, value)
}

// Renew issues a fresh ticket for an existing direct-transport connection so
// an established data path can outlive the original ticket TTL without
// tearing down (audit #2). The caller presents its current ticket as proof:
// the signature is verified with this server's public key, the claims must
// match the authenticated principal and the requested peer, and the live
// connection state must still exist with an unexpired ticket. Renewal never
// advances the path epoch and never changes the connection identity; shared
// (multi-replica) state stores do not support renewal yet, so those
// deployments answer with ErrDirectStateUnavailable and clients keep the
// legacy reissue behavior.
func (s *RouteTicketService) Renew(ctx context.Context, principal auth.Principal, targetID, connectionID uuid.UUID, proof []byte) ([]byte, error) {
	if s.state != nil {
		return nil, ErrDirectStateUnavailable
	}
	claims, err := parseSignedTicket(proof, s.public, s.keyID)
	if err != nil {
		return nil, ErrTransportInvalid
	}
	if claims.accountID != principal.UserID || principal.Device.UserID != principal.UserID ||
		principal.Device.TrustState != "TRUSTED" {
		return nil, ErrTransportDenied
	}
	var peer uuid.UUID
	switch principal.Device.ID {
	case claims.initiator:
		peer = claims.responder
	case claims.responder:
		peer = claims.initiator
	default:
		return nil, ErrTransportDenied
	}
	if targetID != peer || claims.connectionID != connectionID || claims.epoch == 0 {
		return nil, ErrTransportDenied
	}
	now := s.clock().UTC()
	if !now.Before(claims.expiresAt) {
		// Expired tickets renew through a fresh Issue, never in place.
		return nil, ErrTransportDenied
	}
	s.mu.Lock()
	s.cleanupLocked(now)
	state := s.connections[connectionKey{accountID: principal.UserID, connection: connectionID}]
	if state == nil || state.pathEpoch != claims.epoch || !now.Before(state.expiresAt) ||
		state.accountID != claims.accountID || !state.containsDirection(claims.initiator, claims.responder) {
		s.mu.Unlock()
		return nil, ErrTransportDenied
	}
	if len(s.connectionRenewals[connectionID]) >= maxRenewalsPerMinute {
		s.mu.Unlock()
		return nil, ErrTransportRateLimited
	}
	ticketID := uuid.New()
	nonce := make([]byte, 16)
	if _, err := io.ReadFull(s.random, nonce); err != nil {
		s.mu.Unlock()
		return nil, err
	}
	expiresAt := now.Add(ticketTTL)
	claimsBytes := marshalTicketClaims(ticketID, state.accountID, state.initiator, state.responder, connectionID, state.pathEpoch, now, expiresAt, nonce, s.keyID)
	signature := ed25519.Sign(s.private, append([]byte(ticketDomain), claimsBytes...))
	ticket := append(append([]byte(nil), claimsBytes...), appendBytesField(nil, 13, signature)...)
	state.issuedAt = now
	state.expiresAt = expiresAt
	s.connectionRenewals[connectionID] = appendRenewal(s.connectionRenewals[connectionID], now)
	s.mu.Unlock()
	return ticket, nil
}

func appendRenewal(history []time.Time, now time.Time) []time.Time {
	window := now.Add(-time.Minute)
	kept := history[:0]
	for _, at := range history {
		if at.After(window) {
			kept = append(kept, at)
		}
	}
	return append(kept, now)
}

type signedTicketClaims struct {
	ticketID                        uuid.UUID
	accountID, initiator, responder uuid.UUID
	connectionID                    uuid.UUID
	epoch                           uint64
	issuedAt, expiresAt             time.Time
	nonce, keyID                    []byte
}

// parseSignedTicket verifies the server signature over the canonical claim
// prefix (fields 1-12; field 13 carries the signature) and decodes the
// claims. The original encoder emits fields in ascending order, so
// re-marshaling fields 1-12 reproduces the signed byte prefix exactly.
func parseSignedTicket(ticket []byte, public ed25519.PublicKey, keyID []byte) (*signedTicketClaims, error) {
	raw := map[int][]byte{}
	segments := map[int][]byte{}
	rest := ticket
	for len(rest) > 0 {
		number, typ, consumed := protowire.ConsumeTag(rest)
		if consumed < 0 {
			return nil, errors.New("ticket field tag is invalid")
		}
		fieldStart := rest
		rest = rest[consumed:]
		var value []byte
		var width int
		switch typ {
		case protowire.VarintType:
			_, width = protowire.ConsumeVarint(rest)
			value = append([]byte(nil), rest[:width]...)
		case protowire.BytesType:
			value, width = protowire.ConsumeBytes(rest)
		default:
			return nil, errors.New("ticket field type is invalid")
		}
		if width < 0 {
			return nil, errors.New("ticket field value is invalid")
		}
		if _, duplicate := raw[int(number)]; !duplicate {
			raw[int(number)] = append([]byte(nil), value...)
			// The signed canonical prefix spans tag+value for every field,
			// so the segment must capture both.
			segments[int(number)] = append([]byte(nil), fieldStart[:consumed+width]...)
		}
		rest = rest[width:]
	}
	if raw[13] == nil {
		return nil, fmt.Errorf("ticket signature is missing (fields=%d)", len(raw))
	}
	var canonical []byte
	for number := 1; number <= 12; number++ {
		segment, ok := segments[number]
		if !ok {
			return nil, errors.New("ticket claim is missing")
		}
		canonical = append(canonical, segment...)
	}
	if !ed25519.Verify(public, append([]byte(ticketDomain), canonical...), raw[13]) {
		return nil, errors.New("ticket signature is invalid")
	}
	decodeVarint := func(raw []byte) (uint64, error) {
		value, consumed := protowire.ConsumeVarint(raw)
		if consumed != len(raw) {
			return 0, errors.New("ticket varint claim is malformed")
		}
		return value, nil
	}
	decodeFixed := func(raw []byte, what string) (uuid.UUID, error) {
		if len(raw) != 16 {
			return uuid.Nil, fmt.Errorf("ticket %s claim is malformed", what)
		}
		return uuid.FromBytes(raw)
	}
	claims := &signedTicketClaims{}
	var err error
	if claims.ticketID, err = decodeFixed(raw[2], "ticket_id"); err != nil {
		return nil, err
	}
	if claims.accountID, err = decodeFixed(raw[3], "account"); err != nil {
		return nil, err
	}
	if claims.initiator, err = decodeFixed(raw[4], "initiator"); err != nil {
		return nil, err
	}
	if claims.responder, err = decodeFixed(raw[5], "responder"); err != nil {
		return nil, err
	}
	if claims.connectionID, err = decodeFixed(raw[6], "connection"); err != nil {
		return nil, err
	}
	if claims.epoch, err = decodeVarint(raw[7]); err != nil {
		return nil, err
	}
	issuedAt, errIssued := decodeVarint(raw[8])
	expiresAt, errExpires := decodeVarint(raw[9])
	if errIssued != nil || errExpires != nil {
		return nil, errors.New("ticket lifetime claims are malformed")
	}
	claims.issuedAt = time.UnixMilli(int64(issuedAt))
	claims.expiresAt = time.UnixMilli(int64(expiresAt))
	claims.nonce = append([]byte(nil), raw[11]...)
	claims.keyID = append([]byte(nil), raw[12]...)
	return claims, nil
}

func writeTransportJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}
func writeTransportError(w http.ResponseWriter, status int, code string) {
	writeTransportJSON(w, status, map[string]any{"error": map[string]string{
		"code": code, "message_key": "errors." + strings.ToLower(code),
	}})
}
