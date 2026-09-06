package migrate

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestEmbeddedIdentityMigrationRoundTrip(t *testing.T) {
	databaseURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL"))
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for embedded identity migration integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "embedded_identity_migration_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quoted := pgx.Identifier{schema}.Sanitize()
	if _, err = admin.Exec(ctx, "CREATE SCHEMA "+quoted); err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cleanupCancel()
		if _, cleanupErr := admin.Exec(cleanupContext, "DROP SCHEMA "+quoted+" CASCADE"); cleanupErr != nil {
			t.Error(cleanupErr)
		}
	}()
	configuration, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	configuration.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, configuration)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	up, err := migrations.Files.ReadFile("000043_embedded_identity_provider.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000043_embedded_identity_provider.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	for cycle := 1; cycle <= 2; cycle++ {
		if _, err = pool.Exec(ctx, string(up)); err != nil {
			t.Fatalf("migration up cycle %d: %v", cycle, err)
		}
		var accountTable, signingKeyTable *string
		if err = pool.QueryRow(ctx, `SELECT to_regclass('identity_accounts')::text,to_regclass('identity_signing_keys')::text`).Scan(&accountTable, &signingKeyTable); err != nil || accountTable == nil || signingKeyTable == nil {
			t.Fatalf("migration up cycle %d tables = %v/%v, %v", cycle, accountTable, signingKeyTable, err)
		}
		if _, err = pool.Exec(ctx, string(down)); err != nil {
			t.Fatalf("migration down cycle %d: %v", cycle, err)
		}
		if err = pool.QueryRow(ctx, `SELECT to_regclass('identity_accounts')::text,to_regclass('identity_signing_keys')::text`).Scan(&accountTable, &signingKeyTable); err != nil || accountTable != nil || signingKeyTable != nil {
			t.Fatalf("migration down cycle %d tables = %v/%v, %v", cycle, accountTable, signingKeyTable, err)
		}
	}
}
