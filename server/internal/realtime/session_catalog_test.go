package realtime

import (
	"context"
	"errors"
	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"github.com/termlink/termlink/server/internal/store"
	"testing"
)

type catalogAuthority struct {
	*countingRouteAuthority
	user, android, pc, device uuid.UUID
	denied                    bool
}

func (s *catalogAuthority) SessionCatalogDevice(_ context.Context, user, android, pc uuid.UUID) (uuid.UUID, error) {
	if s.denied || user != s.user || android != s.android || pc != s.pc {
		return uuid.Nil, store.ErrDenied
	}
	return s.device, nil
}

func TestCatalogRouteUsesDeviceAuthorityWithoutTerminalSubscription(t *testing.T) {
	for _, mode := range []string{"request", "response", "wrong-pc", "wrong-account", "revoked-grant", "stale-sender", "capability-missing", "terminal-ack"} {
		t.Run(mode, func(t *testing.T) {
			s := &catalogAuthority{user: uuid.New(), android: uuid.New(), pc: uuid.New(), device: uuid.New()}
			h := &Hub{store: s, peers: map[uuid.UUID]*peer{}, observer: noopObserver{}}
			a, p := testPeer(h, s.android), testPeer(h, s.device)
			a.role, p.role = "android", "pc"
			a.principal.UserID, p.principal.UserID = s.user, s.user
			a.capabilities = map[string]bool{"host_session_catalog_v1": true}
			p.capabilities = map[string]bool{"host_session_catalog_v1": true}
			h.peers[s.android], h.peers[s.device] = a, p
			sender, target, pc := a, p, s.pc
			typ := realtimewire.SessionCatalogRequest
			if mode == "response" {
				sender, target = p, a
				typ = realtimewire.SessionCatalogResponse
			}
			envelope := realtimewire.Envelope{MessageType: typ, RecipientDeviceID: target.principal.Device.ID[:], Sequence: 1}
			switch mode {
			case "wrong-pc":
				pc = uuid.New()
			case "wrong-account":
				target.principal.UserID = uuid.New()
			case "revoked-grant":
				s.denied = true
			case "stale-sender":
				h.peers[s.android] = testPeer(h, s.android)
			case "capability-missing":
				target.capabilities = nil
			case "terminal-ack":
				envelope.ACK = 1
			}
			err := sender.routeSessionCatalog(envelope, pc, []byte("encrypted-catalog"))
			allowed := mode == "request" || mode == "response"
			if allowed && err != nil || !allowed && !errors.Is(err, store.ErrDenied) {
				t.Fatalf("allowed=%v err=%v", allowed, err)
			}
			if allowed && len(target.send) != 1 || !allowed && len(target.send) != 0 {
				t.Fatal("unexpected forwarding")
			}
			if len(a.subscriptions) != 0 || len(p.subscriptions) != 0 || len(a.subscriptionACKs) != 0 {
				t.Fatal("catalog modified terminal state")
			}
		})
	}
}
