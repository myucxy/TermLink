package migrate

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

// Destructive migration round trips must not mutate the shared integration
// schema and leave later packages with columns/constraints from an old version.
func isolatedMigrationPool(t *testing.T, ctx context.Context) *pgxpool.Pool {
	t.Helper()
	url := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if url == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required")
	}
	admin, err := pgxpool.New(ctx, url)
	if err != nil {
		t.Fatal(err)
	}
	schema := "migration_fixture_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	if _, err = admin.Exec(ctx, `CREATE SCHEMA `+schema); err != nil {
		admin.Close()
		t.Fatal(err)
	}
	config, err := pgxpool.ParseConfig(url)
	if err != nil {
		admin.Close()
		t.Fatal(err)
	}
	config.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		admin.Close()
		t.Fatal(err)
	}
	t.Cleanup(func() {
		pool.Close()
		cleanup, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_, _ = admin.Exec(cleanup, `DROP SCHEMA `+schema+` CASCADE`)
		admin.Close()
	})
	return pool
}

// Shared by the remaining schema tests. The removed commerce/privacy tests
// used to carry these helpers, which made unrelated migration tests depend on
// files that are no longer part of the supported product.
func execMigrationVersion(t *testing.T, ctx context.Context, pool *pgxpool.Pool, version int, direction string) {
	t.Helper()
	entries, err := migrations.Files.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	prefix, suffix := fmt.Sprintf("%06d_", version), "."+direction+".sql"
	name := ""
	for _, entry := range entries {
		if strings.HasPrefix(entry.Name(), prefix) && strings.HasSuffix(entry.Name(), suffix) {
			name = entry.Name()
			break
		}
	}
	if name == "" {
		t.Fatalf("migration %d %s was not found", version, direction)
	}
	sql, err := migrations.Files.ReadFile(name)
	if err != nil {
		t.Fatal(err)
	}
	tx, err := pool.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err = tx.Exec(ctx, string(sql)); err != nil {
		t.Fatalf("execute %s: %v", name, err)
	}
	if err = tx.Commit(ctx); err != nil {
		t.Fatalf("commit %s: %v", name, err)
	}
}

func repeatedMigrationByte(value byte) []byte {
	result := make([]byte, 32)
	for index := range result {
		result[index] = value
	}
	return result
}

func assertMigration25PostgresCode(t *testing.T, err error, code string) {
	t.Helper()
	if err == nil {
		t.Fatalf("operation succeeded, want PostgreSQL error %s", code)
	}
	var postgresError *pgconn.PgError
	if !errors.As(err, &postgresError) || postgresError.Code != code {
		t.Fatalf("PostgreSQL error=%v, want SQLSTATE %s", err, code)
	}
}
