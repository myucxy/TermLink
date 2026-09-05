package store

import (
	"context"
	"testing"
	"time"
)

func TestCleanupSessionsIsStableNoOp(t *testing.T) {
	cleaned, err := (&Postgres{}).CleanupSessions(
		context.Background(),
		time.Now().Add(-time.Hour),
		time.Now().Add(-time.Hour),
		100,
	)
	if err != nil || cleaned != 0 {
		t.Fatalf("CleanupSessions() = %d, %v; want 0, nil", cleaned, err)
	}
}
