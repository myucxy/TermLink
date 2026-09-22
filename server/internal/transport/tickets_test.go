package transport

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/json"
	"errors"
	"math"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
	"google.golang.org/protobuf/encoding/protowire"
)

type memoryDeviceResolver struct {
	devices map[uuid.UUID]map[uuid.UUID]store.DeviceRecord
	mu      sync.Mutex
	epochs  map[memoryDevicePair]uint64
}

type memoryDevicePair struct{ account, first, second uuid.UUID }

func (r *memoryDeviceResolver) GetDevice(_ context.Context, accountID, deviceID uuid.UUID) (store.DeviceRecord, error) {
	if item, ok := r.devices[accountID][deviceID]; ok {
		if item.KeyID == "" {
			item.KeyID = "pair-key"
		}
		return item, nil
	}
	return store.DeviceRecord{}, store.ErrNotFound
}

func (r *memoryDeviceResolver) AllocateDirectTransportEpoch(
	ctx context.Context,
	accountID, sourceDeviceID, targetDeviceID uuid.UUID,
) (uint64, error) {
	if _, err := r.AuthorizeDirectTransportSignal(ctx, accountID, sourceDeviceID, targetDeviceID, targetDeviceID); err != nil {
		return 0, err
	}
	first, second := sourceDeviceID, targetDeviceID
	if bytes.Compare(first[:], second[:]) > 0 {
		first, second = second, first
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.epochs == nil {
		r.epochs = map[memoryDevicePair]uint64{}
	}
	pair := memoryDevicePair{account: accountID, first: first, second: second}
	r.epochs[pair]++
	return r.epochs[pair], nil
}

func (r *memoryDeviceResolver) AuthorizeDirectTransportSignal(
	ctx context.Context,
	accountID, sourceDeviceID, targetDeviceID, responderDeviceID uuid.UUID,
) (string, error) {
	source, sourceErr := r.GetDevice(ctx, accountID, sourceDeviceID)
	target, targetErr := r.GetDevice(ctx, accountID, targetDeviceID)
	if sourceErr != nil || targetErr != nil || sourceDeviceID == targetDeviceID ||
		source.TrustState != "TRUSTED" || source.RevokedAt != nil ||
		target.TrustState != "TRUSTED" || target.RevokedAt != nil ||
		!complementaryDeviceTypes(source.Type, target.Type) {
		return "", store.ErrDenied
	}
	if responderDeviceID == sourceDeviceID {
		return source.KeyID, nil
	}
	if responderDeviceID == targetDeviceID {
		return target.KeyID, nil
	}
	return "", store.ErrDenied
}

func TestTicketIssueBindsPairAndRejectsReplayCrossAccountAndRevocation(t *testing.T) {
	account, otherAccount := uuid.New(), uuid.New()
	pc, android, outsider := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{
		account: {
			pc:      {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"},
			android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
		},
		otherAccount: {outsider: {ID: outsider, Type: "ANDROID", TrustState: "TRUSTED"}},
	}}
	service := testTicketService(t, resolver)
	principal := testPrincipal(account, pc, "PC_AGENT")
	connectionID := uuid.New()
	ticket, err := service.Issue(context.Background(), principal, android, connectionID)
	if err != nil {
		t.Fatalf("Issue: %v", err)
	}
	claims, signature := splitTicketSignature(t, ticket)
	if !ed25519.Verify(service.public, append([]byte(ticketDomain), claims...), signature) {
		t.Fatal("ticket signature did not verify")
	}
	assertTicketUsesCanonicalPackedCarrier(t, claims)
	for name, identifier := range map[string]uuid.UUID{"account": account, "initiator": pc, "responder": android, "connection": connectionID} {
		if !bytes.Contains(claims, identifier[:]) {
			t.Fatalf("signed claims do not bind %s", name)
		}
	}
	if _, err := service.Issue(context.Background(), principal, android, connectionID); !errors.Is(err, ErrTransportReplay) {
		t.Fatalf("connection replay error=%v, want ErrTransportReplay", err)
	}
	if _, err := service.Issue(context.Background(), principal, outsider, uuid.New()); !errors.Is(err, ErrTransportDenied) {
		t.Fatalf("cross-account error=%v, want denial", err)
	}
	now := service.clock().UTC()
	revoked := resolver.devices[account][android]
	revoked.TrustState, revoked.RevokedAt = "REVOKED", &now
	resolver.devices[account][android] = revoked
	if _, err := service.Issue(context.Background(), principal, android, uuid.New()); !errors.Is(err, ErrTransportDenied) {
		t.Fatalf("revoked target error=%v, want denial", err)
	}
}

func TestTicketIssueRateIsBoundedPerDevice(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	service := testTicketService(t, resolver)
	principal := testPrincipal(account, pc, "PC_AGENT")
	for count := 0; count < 12; count++ {
		if _, err := service.Issue(context.Background(), principal, android, uuid.New()); err != nil {
			t.Fatalf("ticket %d: %v", count, err)
		}
	}
	if _, err := service.Issue(context.Background(), principal, android, uuid.New()); !errors.Is(err, ErrTransportRateLimited) {
		t.Fatalf("ticket rate error=%v, want rate limit", err)
	}
}

func TestTicketIssueRateIsAlsoBoundedPerAccount(t *testing.T) {
	account, android := uuid.New(), uuid.New()
	devices := map[uuid.UUID]store.DeviceRecord{android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"}}
	pcs := make([]uuid.UUID, 7)
	for index := range pcs {
		pcs[index] = uuid.New()
		devices[pcs[index]] = store.DeviceRecord{ID: pcs[index], Type: "PC_AGENT", TrustState: "TRUSTED"}
	}
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: devices}}
	service := testTicketService(t, resolver)
	for count := 0; count < accountTicketsPerMinute; count++ {
		pc := pcs[count/10]
		if _, err := service.Issue(context.Background(), testPrincipal(account, pc, "PC_AGENT"), android, uuid.New()); err != nil {
			t.Fatalf("account ticket %d: %v", count, err)
		}
	}
	if _, err := service.Issue(context.Background(), testPrincipal(account, pcs[6], "PC_AGENT"), android, uuid.New()); !errors.Is(err, ErrTransportRateLimited) {
		t.Fatalf("account ticket rate error=%v, want rate limit", err)
	}
}

func TestConcurrentTicketIssueAllowsOneConnectionTicket(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	service := testTicketService(t, resolver)
	connection := uuid.New()
	principal := testPrincipal(account, pc, "PC_AGENT")
	const workers = 8
	start := make(chan struct{})
	errorsSeen := make(chan error, workers)
	var wait sync.WaitGroup
	for range workers {
		wait.Add(1)
		go func() {
			defer wait.Done()
			<-start
			_, err := service.Issue(context.Background(), principal, android, connection)
			errorsSeen <- err
		}()
	}
	close(start)
	wait.Wait()
	close(errorsSeen)
	succeeded, replayed := 0, 0
	for err := range errorsSeen {
		switch {
		case err == nil:
			succeeded++
		case errors.Is(err, ErrTransportReplay):
			replayed++
		default:
			t.Fatalf("unexpected concurrent issue error: %v", err)
		}
	}
	if succeeded != 1 || replayed != workers-1 {
		t.Fatalf("concurrent issue succeeded/replayed=%d/%d", succeeded, replayed)
	}
}

func TestTicketEpochRemainsMonotonicAcrossServiceRestart(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	first := testTicketService(t, resolver)
	principal := testPrincipal(account, pc, "PC_AGENT")
	firstConnection := uuid.New()
	if _, err := first.Issue(context.Background(), principal, android, firstConnection); err != nil {
		t.Fatal(err)
	}
	firstEpoch := first.connections[connectionKey{accountID: account, connection: firstConnection}].pathEpoch

	second := testTicketService(t, resolver)
	secondConnection := uuid.New()
	if _, err := second.Issue(context.Background(), principal, android, secondConnection); err != nil {
		t.Fatal(err)
	}
	secondEpoch := second.connections[connectionKey{accountID: account, connection: secondConnection}].pathEpoch
	if firstEpoch != 1 || secondEpoch != 2 {
		t.Fatalf("epochs across restart=(%d,%d), want (1,2)", firstEpoch, secondEpoch)
	}
}

func TestSignalAuthorizationEnforcesExpirySequenceAndCandidateEventBudget(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	service := testTicketService(t, resolver)
	now := time.Now().UTC()
	service.clock = func() time.Time { return now }
	connection := uuid.New()
	principal := testPrincipal(account, pc, "PC_AGENT")
	if _, err := service.Issue(context.Background(), principal, android, connection); err != nil {
		t.Fatalf("Issue: %v", err)
	}
	epoch := service.connections[connectionKey{accountID: account, connection: connection}].pathEpoch
	skippedFirst, err := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, 2, SignalPathOffer, 32))
	if err != nil {
		t.Fatal(err)
	}
	if err := service.AuthorizeSignal(context.Background(), principal, skippedFirst); !errors.Is(err, ErrTransportReplay) {
		t.Fatalf("first signal sequence 2 error=%v, want replay rejection", err)
	}
	envelope, err := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, 1, SignalPathOffer, 32))
	if err != nil {
		t.Fatal(err)
	}
	if err := service.AuthorizeSignal(context.Background(), principal, envelope); err != nil {
		t.Fatalf("first signal: %v", err)
	}
	if err := service.AuthorizeSignal(context.Background(), principal, envelope); !errors.Is(err, ErrTransportReplay) {
		t.Fatalf("signal replay error=%v", err)
	}
	gap, _ := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, 3, SignalCandidateUpdate, 32))
	if err := service.AuthorizeSignal(context.Background(), principal, gap); !errors.Is(err, ErrTransportReplay) {
		t.Fatalf("signal gap error=%v, want replay rejection", err)
	}
	nonceReplay, _ := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, 2, SignalCandidateUpdate, 32))
	nonceReplay.Nonce = append([]byte(nil), envelope.Nonce...)
	if err := service.AuthorizeSignal(context.Background(), principal, nonceReplay); !errors.Is(err, ErrTransportReplay) {
		t.Fatalf("nonce replay error=%v, want replay", err)
	}
	for sequence := uint64(2); sequence <= 32; sequence++ {
		candidate, _ := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, sequence, SignalCandidateUpdate, 32))
		if err := service.AuthorizeSignal(context.Background(), principal, candidate); err != nil {
			t.Fatalf("candidate event %d: %v", sequence, err)
		}
	}
	tooMany, _ := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, 33, SignalCandidateUpdate, 32))
	if err := service.AuthorizeSignal(context.Background(), principal, tooMany); !errors.Is(err, ErrTransportRateLimited) {
		t.Fatalf("candidate event budget error=%v", err)
	}

	now = now.Add(ticketTTL + time.Millisecond)
	expired, _ := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, 34, SignalPathClose, 32))
	if err := service.AuthorizeSignal(context.Background(), principal, expired); !errors.Is(err, ErrTransportDenied) {
		t.Fatalf("expired route error=%v, want denial", err)
	}
}

func TestSignalAuthorizationBindsBothDirectionsToCurrentResponderKeyID(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	const pcKey = "pc-current-key"
	const androidKey = "android-current-key"
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc:      {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED", KeyID: pcKey},
		android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED", KeyID: androidKey},
	}}}
	service := testTicketService(t, resolver)
	connection := uuid.New()
	pcPrincipal := testPrincipal(account, pc, "PC_AGENT")
	androidPrincipal := testPrincipal(account, android, "ANDROID")
	if _, err := service.Issue(context.Background(), pcPrincipal, android, connection); err != nil {
		t.Fatal(err)
	}
	epoch := service.connections[connectionKey{accountID: account, connection: connection}].pathEpoch

	pcToAndroid, err := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, 1, SignalPathOffer, 32))
	if err != nil {
		t.Fatal(err)
	}
	pcToAndroid.KeyID = []byte(pcKey)
	if err := service.AuthorizeSignal(context.Background(), pcPrincipal, pcToAndroid); !errors.Is(err, ErrTransportDenied) {
		t.Fatalf("initiator key error=%v, want responder-key denial", err)
	}
	pcToAndroid.KeyID = []byte(androidKey)
	if err := service.AuthorizeSignal(context.Background(), pcPrincipal, pcToAndroid); err != nil {
		t.Fatalf("responder key after rejected same sequence: %v", err)
	}

	androidToPC, err := UnmarshalSignal(testSignalFrame(connection, android, pc, epoch, 1, SignalPathAnswer, 32))
	if err != nil {
		t.Fatal(err)
	}
	androidToPC.KeyID = []byte(pcKey)
	if err := service.AuthorizeSignal(context.Background(), androidPrincipal, androidToPC); !errors.Is(err, ErrTransportDenied) {
		t.Fatalf("reverse direction recipient key error=%v, want original responder-key denial", err)
	}
	androidToPC.KeyID = []byte(androidKey)
	if err := service.AuthorizeSignal(context.Background(), androidPrincipal, androidToPC); err != nil {
		t.Fatalf("reverse direction responder key: %v", err)
	}

	rotated := resolver.devices[account][android]
	rotated.KeyID = "android-rotated-key"
	resolver.devices[account][android] = rotated
	next, err := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, 2, SignalCandidateUpdate, 32))
	if err != nil {
		t.Fatal(err)
	}
	next.KeyID = []byte(androidKey)
	if err := service.AuthorizeSignal(context.Background(), pcPrincipal, next); !errors.Is(err, ErrTransportDenied) {
		t.Fatalf("stale responder key error=%v, want denial", err)
	}
	next.KeyID = []byte(rotated.KeyID)
	if err := service.AuthorizeSignal(context.Background(), pcPrincipal, next); err != nil {
		t.Fatalf("rotated responder key after rejected same sequence: %v", err)
	}
}

func TestSignalAuthorizationRequiresProductionGateForCutoverControl(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	principal := testPrincipal(account, pc, "PC_AGENT")

	for _, permitted := range []bool{false, true} {
		name := "disabled"
		if permitted {
			name = "enabled"
		}
		t.Run(name, func(t *testing.T) {
			service, err := NewRouteTicketServiceWithRouting(
				resolver, make([]byte, ed25519.SeedSize), permitted,
			)
			if err != nil {
				t.Fatal(err)
			}
			connection := uuid.New()
			if _, err := service.Issue(context.Background(), principal, android, connection); err != nil {
				t.Fatal(err)
			}
			epoch := service.connections[connectionKey{accountID: account, connection: connection}].pathEpoch
			for index, signalType := range []SignalType{
				SignalCutoverProposal, SignalCutoverAccept, SignalCutoverCommit, SignalFallback,
			} {
				sequence := uint64(index + 1)
				if !permitted {
					sequence = 1
				}
				envelope, parseErr := UnmarshalSignal(
					testSignalFrame(connection, pc, android, epoch, sequence, signalType, 32),
				)
				if parseErr != nil {
					t.Fatal(parseErr)
				}
				authorizeErr := service.AuthorizeSignal(context.Background(), principal, envelope)
				if permitted && authorizeErr != nil {
					t.Fatalf("signal %d rejected with gate enabled: %v", signalType, authorizeErr)
				}
				if !permitted && !errors.Is(authorizeErr, ErrTransportDenied) {
					t.Fatalf("signal %d error=%v, want gate denial", signalType, authorizeErr)
				}
			}
		})
	}
}

func TestIsNextSignalSequenceBoundaries(t *testing.T) {
	maximum := uint64(math.MaxInt64)
	for _, test := range []struct {
		name           string
		previous, next uint64
		want           bool
	}{
		{name: "first", previous: 0, next: 1, want: true},
		{name: "first gap", previous: 0, next: 2},
		{name: "contiguous", previous: 1, next: 2, want: true},
		{name: "gap", previous: 1, next: 3},
		{name: "duplicate", previous: 2, next: 2},
		{name: "regression", previous: 2, next: 1},
		{name: "maximum", previous: maximum - 1, next: maximum, want: true},
		{name: "maximum exhausted", previous: maximum, next: maximum},
		{name: "overflow", previous: maximum, next: maximum + 1},
	} {
		t.Run(test.name, func(t *testing.T) {
			if got := IsNextSignalSequence(test.previous, test.next); got != test.want {
				t.Fatalf("IsNextSignalSequence(%d, %d)=%t, want %t", test.previous, test.next, got, test.want)
			}
		})
	}
}

func TestEnabledConfigUsesOnlyPinnedConfiguredSTUNServers(t *testing.T) {
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{}}
	service, err := NewRouteTicketService(resolver, make([]byte, ed25519.SeedSize), "stun:one.test:3478", "stun:[2001:db8::1]:5349")
	if err != nil {
		t.Fatal(err)
	}
	config := service.EnabledConfig(service.stunServers)
	if config.DirectRoutingPermitted {
		t.Fatal("shadow direct config permitted production routing")
	}
	if len(config.Capabilities) != 3 ||
		config.Capabilities[1] != "direct_transport_shadow_v1" ||
		config.Capabilities[2] != "direct_transport_shadow_multipair_v2" {
		t.Fatalf("shadow capabilities=%v", config.Capabilities)
	}
	if got := config.STUNServers; len(got) != 2 || got[0] != "stun:one.test:3478" || got[1] != "stun:[2001:db8::1]:5349" {
		t.Fatalf("STUN config=%v", got)
	}
	config.STUNServers[0] = "modified"
	if service.stunServers[0] != "stun:one.test:3478" {
		t.Fatal("returned STUN config aliases service state")
	}
}

func TestEnabledConfigAdvertisesProductionDataOnlyWhenExplicitlyPermitted(t *testing.T) {
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{}}
	service, err := NewRouteTicketServiceWithRouting(
		resolver, make([]byte, ed25519.SeedSize), true, "stun:one.test:3478",
	)
	if err != nil {
		t.Fatal(err)
	}
	config := service.EnabledConfig(service.stunServers)
	if !config.DirectRoutingPermitted || !service.DirectRoutingPermitted() {
		t.Fatal("explicit production routing gate was not advertised")
	}
	if len(config.Capabilities) != 5 ||
		config.Capabilities[3] != "direct_transport_data_v1" ||
		config.Capabilities[4] != "direct_session_cutover_v1" {
		t.Fatalf("production direct capabilities=%v", config.Capabilities)
	}
}

func TestEnabledConfigUsesOneDynamicRoutingPolicySnapshot(t *testing.T) {
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{}}
	service, err := NewRouteTicketServiceWithRouting(
		resolver, make([]byte, ed25519.SeedSize), true,
	)
	if err != nil {
		t.Fatal(err)
	}
	reads := 0
	service.SetRoutingPermittedOverride(func() bool {
		reads++
		return true
	})

	config := service.EnabledConfig(nil)
	if reads != 1 || !config.DirectRoutingPermitted || len(config.Capabilities) != 5 {
		t.Fatalf("routing reads=%d permitted=%t capabilities=%v", reads, config.DirectRoutingPermitted, config.Capabilities)
	}
}

func TestConfigHandlerReturnsConfiguredSTUNAllowlist(t *testing.T) {
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{}}
	service, err := NewRouteTicketService(resolver, make([]byte, ed25519.SeedSize), "stun:one.test:3478")
	if err != nil {
		t.Fatal(err)
	}
	response := httptest.NewRecorder()
	service.ConfigHandler().ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/v1/transport/config", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("config status=%d", response.Code)
	}
	var config EnabledDeviceConfig
	if err := json.Unmarshal(response.Body.Bytes(), &config); err != nil {
		t.Fatal(err)
	}
	if len(config.STUNServers) != 1 || config.STUNServers[0] != "stun:one.test:3478" || len(config.TicketSigningKeys) != 1 {
		t.Fatalf("config=%+v", config)
	}
}

func TestSignalAuthorizationRechecksBothDeviceTrustStates(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	service := testTicketService(t, resolver)
	connection := uuid.New()
	principal := testPrincipal(account, pc, "PC_AGENT")
	if _, err := service.Issue(context.Background(), principal, android, connection); err != nil {
		t.Fatal(err)
	}
	epoch := service.connections[connectionKey{accountID: account, connection: connection}].pathEpoch
	envelope, _ := UnmarshalSignal(testSignalFrame(connection, pc, android, epoch, 1, SignalPathOffer, 32))
	revokedAt := time.Now()
	revokedPC := resolver.devices[account][pc]
	revokedPC.TrustState, revokedPC.RevokedAt = "REVOKED", &revokedAt
	resolver.devices[account][pc] = revokedPC
	if err := service.AuthorizeSignal(context.Background(), principal, envelope); !errors.Is(err, ErrTransportDenied) {
		t.Fatalf("revoked source error=%v, want denial", err)
	}
}

func testTicketService(t *testing.T, resolver TransportAuthority) *RouteTicketService {
	return testTicketServiceWithRouting(t, resolver, false)
}

func testTicketServiceWithRouting(t *testing.T, resolver TransportAuthority, directRoutingPermitted bool) *RouteTicketService {
	t.Helper()
	service, err := NewRouteTicketServiceWithRouting(
		resolver, make([]byte, ed25519.SeedSize), directRoutingPermitted,
	)
	if err != nil {
		t.Fatal(err)
	}
	return service
}

func testPrincipal(accountID, deviceID uuid.UUID, deviceType string) auth.Principal {
	return auth.Principal{UserID: accountID, Device: auth.Device{ID: deviceID, UserID: accountID, Type: deviceType, TrustState: "TRUSTED"}}
}

func splitTicketSignature(t *testing.T, ticket []byte) ([]byte, []byte) {
	t.Helper()
	rest := ticket
	consumed := 0
	for len(rest) > 0 {
		number, kind, tagBytes := protowire.ConsumeTag(rest)
		if tagBytes < 0 {
			t.Fatal("invalid ticket tag")
		}
		fieldBytes := protowire.ConsumeFieldValue(number, kind, rest[tagBytes:])
		if fieldBytes < 0 {
			t.Fatal("invalid ticket field")
		}
		if number == 13 {
			signature, n := protowire.ConsumeBytes(rest[tagBytes:])
			if n < 0 || len(signature) != ed25519.SignatureSize || tagBytes+n != len(rest) {
				t.Fatal("invalid ticket signature")
			}
			return ticket[:consumed], append([]byte(nil), signature...)
		}
		advance := tagBytes + fieldBytes
		consumed += advance
		rest = rest[advance:]
	}
	t.Fatal("ticket signature missing")
	return nil, nil
}

func assertTicketUsesCanonicalPackedCarrier(t *testing.T, claims []byte) {
	t.Helper()
	rest := claims
	for len(rest) > 0 {
		number, kind, tagBytes := protowire.ConsumeTag(rest)
		if tagBytes < 0 {
			t.Fatal("invalid ticket tag")
		}
		if number == 10 {
			if kind != protowire.BytesType {
				t.Fatalf("allowed_carriers wire type=%v, want packed bytes", kind)
			}
			packed, fieldBytes := protowire.ConsumeBytes(rest[tagBytes:])
			if fieldBytes < 0 || !bytes.Equal(packed, []byte{1}) {
				t.Fatalf("allowed_carriers packed payload=%x, want 01", packed)
			}
			return
		}
		fieldBytes := protowire.ConsumeFieldValue(number, kind, rest[tagBytes:])
		if fieldBytes < 0 {
			t.Fatal("invalid ticket field")
		}
		rest = rest[tagBytes+fieldBytes:]
	}
	t.Fatal("allowed_carriers field missing")
}
