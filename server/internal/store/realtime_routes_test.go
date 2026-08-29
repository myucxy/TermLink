package store

import (
	"context"
	"errors"
	"testing"

	"github.com/google/uuid"
)

func TestSessionRoutesSkipsEmptyBatchWithoutDatabase(t *testing.T) {
	routes, err := (&Postgres{}).SessionRoutes(context.Background(), uuid.New(), uuid.New(), nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(routes) != 0 {
		t.Fatalf("empty route batch returned %d entries", len(routes))
	}
}

func TestSessionRoutesRejectsOversizedBatchBeforeDatabase(t *testing.T) {
	sessions := make([]uuid.UUID, MaxSessionRouteBatchSize+1)
	for index := range sessions {
		sessions[index] = uuid.New()
	}
	_, err := (&Postgres{}).SessionRoutes(context.Background(), uuid.New(), uuid.New(), sessions)
	if !errors.Is(err, ErrSessionRouteBatchTooLarge) {
		t.Fatalf("oversized route batch error = %v, want ErrSessionRouteBatchTooLarge", err)
	}
}
