package transport

import (
	"context"
	"crypto/ed25519"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

func newRenewTestService(t *testing.T, account, pc, android uuid.UUID) *RouteTicketService {
	t.Helper()
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc:      {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"},
		android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	service, err := NewRouteTicketServiceWithRouting(resolver, make([]byte, ed25519.SeedSize), true)
	if err != nil {
		t.Fatal(err)
	}
	return service
}

func TestRenewExtendsSameConnectionAndEpoch(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	service := newRenewTestService(t, account, pc, android)
	pcPrincipal := testPrincipal(account, pc, "PC_AGENT")
	connection := uuid.New()

	original, err := service.Issue(context.Background(), pcPrincipal, android, connection)
	if err != nil {
		t.Fatalf("issue: %v", err)
	}
	key := connectionKey{accountID: account, connection: connection}
	beforeExpiry := service.connections[key].expiresAt
	beforeEpoch := service.connections[key].pathEpoch
	service.clock = func() time.Time { return beforeExpiry.Add(-ticketTTL + time.Second) }

	androidPrincipal := testPrincipal(account, android, "ANDROID")
	if _, perr := parseSignedTicket(original, service.public, service.keyID); perr != nil {
		t.Fatalf("direct parse failed: %v", perr)
	}
	renewed, err := service.Renew(context.Background(), androidPrincipal, pc, connection, original)
	if err != nil {
		t.Fatalf("renew: %v", err)
	}
	after := service.connections[key]
	t.Logf("renew ok, state.expiresAt now %v", after.expiresAt)
	if !after.expiresAt.After(beforeExpiry) {
		t.Fatalf("renewal did not extend expiry: %v -> %v", beforeExpiry, after.expiresAt)
	}
	if after.pathEpoch != beforeEpoch {
		t.Fatalf("renewal advanced epoch: %d -> %d", beforeEpoch, after.pathEpoch)
	}
	claims, err := parseSignedTicket(renewed, service.public, service.keyID)
	if err != nil {
		t.Fatalf("parse renewed: %v", err)
	}
	if claims.connectionID != connection || claims.initiator != pc || claims.responder != android {
		t.Fatalf("renewed claims changed identity: %+v", claims)
	}
}

func TestRenewRejectsExpiredTicketAndForeignPrincipal(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	service := newRenewTestService(t, account, pc, android)
	pcPrincipal := testPrincipal(account, pc, "PC_AGENT")
	connection := uuid.New()

	original, err := service.Issue(context.Background(), pcPrincipal, android, connection)
	if err != nil {
		t.Fatalf("issue: %v", err)
	}
	key := connectionKey{accountID: account, connection: connection}
	service.connections[key].expiresAt = service.clock().Add(-time.Minute)

	foreign := testPrincipal(uuid.New(), uuid.New(), "ANDROID")
	if _, err := service.Renew(context.Background(), foreign, android, connection, original); err != ErrTransportDenied {
		t.Fatalf("foreign principal err=%v", err)
	}
	if _, err := service.Renew(context.Background(), pcPrincipal, android, connection, original); err != ErrTransportDenied {
		t.Fatalf("expired ticket err=%v, want ErrTransportDenied", err)
	}

	tampered := append([]byte(nil), original...)
	tampered[len(tampered)-1] ^= 0xFF
	androidPrincipal := testPrincipal(account, android, "ANDROID")
	if _, err := service.Renew(context.Background(), androidPrincipal, pc, connection, tampered); err != ErrTransportInvalid {
		t.Fatalf("tampered err=%v, want ErrTransportInvalid", err)
	}
}

func TestRenewRateLimitsPerConnection(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	service := newRenewTestService(t, account, pc, android)
	pcPrincipal := testPrincipal(account, pc, "PC_AGENT")
	connection := uuid.New()
	original, err := service.Issue(context.Background(), pcPrincipal, android, connection)
	if err != nil {
		t.Fatalf("issue: %v", err)
	}
	androidPrincipal := testPrincipal(account, android, "ANDROID")
	for attempt := 0; attempt < maxRenewalsPerMinute; attempt++ {
		if _, err := service.Renew(context.Background(), androidPrincipal, pc, connection, original); err != nil {
			t.Fatalf("renew %d: %v", attempt, err)
		}
	}
	if _, err := service.Renew(context.Background(), androidPrincipal, pc, connection, original); err != ErrTransportRateLimited {
		t.Fatalf("third renewal err=%v, want rate limited", err)
	}
}

func TestRenewRejectsAfterRoutingPolicyIsDisabled(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	service := newRenewTestService(t, account, pc, android)
	pcPrincipal := testPrincipal(account, pc, "PC_AGENT")
	connection := uuid.New()
	original, err := service.Issue(context.Background(), pcPrincipal, android, connection)
	if err != nil {
		t.Fatalf("issue: %v", err)
	}
	permitted := false
	service.SetRoutingPermittedOverride(func() bool { return permitted })

	androidPrincipal := testPrincipal(account, android, "ANDROID")
	if _, err := service.Renew(context.Background(), androidPrincipal, pc, connection, original); err != ErrTransportDenied {
		t.Fatalf("renew after routing disabled err=%v, want ErrTransportDenied", err)
	}
}
