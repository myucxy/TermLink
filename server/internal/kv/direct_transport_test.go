package kv

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
	"github.com/termlink/termlink/server/internal/transport"
)

func TestNewDirectTransportStatePerformsNoNetworkIO(t *testing.T) {
	client := redis.NewClient(&redis.Options{Addr: "unused.invalid:6379", MaxRetries: -1})
	t.Cleanup(func() { _ = client.Close() })
	before := client.PoolStats()
	state, err := NewDirectTransportState(client, "test:direct:v1")
	if err != nil || state == nil {
		t.Fatalf("NewDirectTransportState() state=%v error=%v", state, err)
	}
	after := client.PoolStats()
	if before.TotalConns != 0 || after.TotalConns != 0 {
		t.Fatalf("constructor performed I/O: before=%+v after=%+v", before, after)
	}
}

func TestDirectTransportStateFailureIsClosed(t *testing.T) {
	client := redis.NewClient(&redis.Options{Addr: "unused.invalid:6379", MaxRetries: -1})
	state, err := NewDirectTransportState(client, "test:direct:closed")
	if err != nil {
		t.Fatal(err)
	}
	if err := client.Close(); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	result, err := state.ReserveTicket(ctx, transport.DirectTicketReservation{
		AccountID: uuid.New(), ConnectionID: uuid.New(), InitiatorID: uuid.New(),
		ResponderID: uuid.New(), ReservationID: uuid.New(),
	})
	if !errors.Is(err, transport.ErrDirectStateUnavailable) || result.Status != 0 {
		t.Fatalf("closed-store result=%+v error=%v, want unavailable with no accepted state", result, err)
	}
}

func TestNewDirectTransportStateRejectsUnsafeNamespace(t *testing.T) {
	client := redis.NewClient(&redis.Options{Addr: "unused.invalid:6379", MaxRetries: -1})
	t.Cleanup(func() { _ = client.Close() })
	for _, namespace := range []string{" leading", "trailing ", "bad{slot}", "bad\nname"} {
		if _, err := NewDirectTransportState(client, namespace); !errors.Is(err, transport.ErrDirectStateInvalid) {
			t.Fatalf("namespace %q error=%v, want invalid", namespace, err)
		}
	}
}
