package store

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestDirectRoutingPolicyDefaultsAutomaticAndPersistsRelayOnly(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "direct_routing_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err = admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		t.Fatal(err)
	}
	defer admin.Exec(context.Background(), "DROP SCHEMA "+quotedSchema+" CASCADE")

	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	config.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	if err = migrate.UpPool(ctx, pool); err != nil {
		t.Fatal(err)
	}
	repository := &Postgres{Pool: pool}
	permitted, err := repository.GetDirectRoutingPolicy(ctx)
	if err != nil || !permitted {
		t.Fatalf("missing policy permitted=%t err=%v, want automatic Direct", permitted, err)
	}
	if err = repository.SetDirectRoutingPolicy(ctx, false, uuid.Nil); err != nil {
		t.Fatal(err)
	}
	permitted, err = repository.GetDirectRoutingPolicy(ctx)
	if err != nil || permitted {
		t.Fatalf("explicit Relay-only permitted=%t err=%v", permitted, err)
	}
}
