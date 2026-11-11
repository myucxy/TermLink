package main

import (
	"bytes"
	"context"
	"os"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/keywrap"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/store"
)

func TestRuntimeMasterSecretFreshDatabaseAndRestart(t *testing.T) {
	for _, wrapped := range []bool{false, true} {
		t.Run(map[bool]string{false: "plain", true: "wrapped"}[wrapped], func(t *testing.T) {
			passphrase := ""
			if wrapped {
				passphrase = "test-only wrapping passphrase; never a deployment secret"
			}
			t.Setenv(masterWrapPassphraseEnv, passphrase)
			ctx, repository := freshRuntimeSecretDatabase(t)
			first, err := resolveWrappedMasterSecret(ctx, repository)
			if err != nil {
				t.Fatal("fresh database initialization failed:", err)
			}
			defer clear(first)
			if len(first) != 32 {
				t.Fatal("unexpected master key length")
			}
			stored, found, err := repository.GetRuntimeSecretValue(ctx, masterSecretName)
			if err != nil || !found || keywrap.IsWrapped(string(stored)) != wrapped {
				t.Fatal("persisted master storage format does not match the configured protection")
			}
			if wrapped && bytes.Equal(first, stored) {
				t.Fatal("wrapped storage persisted the plaintext master")
			}
			clear(stored)
			second, err := resolveWrappedMasterSecret(ctx, repository)
			if err != nil || !bytes.Equal(first, second) {
				t.Fatal("restart changed or lost the database-backed master key")
			}
			clear(second)
		})
	}
}

func TestRuntimeMasterSecretWrappingMigrationAndWrongPassphraseFailClosed(t *testing.T) {
	t.Setenv(masterWrapPassphraseEnv, "")
	ctx, repository := freshRuntimeSecretDatabase(t)
	first, err := resolveWrappedMasterSecret(ctx, repository)
	if err != nil {
		t.Fatal(err)
	}
	defer clear(first)
	t.Setenv(masterWrapPassphraseEnv, "test-only migration passphrase")
	migrated, err := resolveWrappedMasterSecret(ctx, repository)
	if err != nil || !bytes.Equal(first, migrated) {
		t.Fatal("wrapping migration rotated the master key")
	}
	clear(migrated)
	stored, found, err := repository.GetRuntimeSecretValue(ctx, masterSecretName)
	if err != nil || !found || !keywrap.IsWrapped(string(stored)) {
		t.Fatal("plaintext master was not migrated to wrapped storage")
	}
	defer clear(stored)
	for _, invalid := range []string{"", "test-only wrong passphrase"} {
		t.Setenv(masterWrapPassphraseEnv, invalid)
		if key, err := resolveWrappedMasterSecret(ctx, repository); err == nil || key != nil {
			clear(key)
			t.Fatal("missing or incorrect wrapping passphrase did not fail closed")
		}
		after, found, err := repository.GetRuntimeSecretValue(ctx, masterSecretName)
		if err != nil || !found || !bytes.Equal(stored, after) {
			t.Fatal("failed authentication replaced the stored master")
		}
		clear(after)
	}
}

func TestRuntimeMasterSecretConcurrentFirstStartsConverge(t *testing.T) {
	for _, wrapped := range []bool{false, true} {
		t.Run(map[bool]string{false: "plain", true: "wrapped"}[wrapped], func(t *testing.T) {
			passphrase := ""
			if wrapped {
				passphrase = "test-only concurrent-start passphrase"
			}
			t.Setenv(masterWrapPassphraseEnv, passphrase)
			ctx, repository := freshRuntimeSecretDatabase(t)
			const starts = 4
			keys := make([][]byte, starts)
			errors := make([]error, starts)
			var work sync.WaitGroup
			begin := make(chan struct{})
			for i := range starts {
				work.Add(1)
				go func() {
					defer work.Done()
					<-begin
					keys[i], errors[i] = resolveWrappedMasterSecret(ctx, repository)
				}()
			}
			close(begin)
			work.Wait()
			defer func() {
				for _, key := range keys {
					clear(key)
				}
			}()
			for i := range starts {
				if errors[i] != nil || len(keys[i]) != 32 || !bytes.Equal(keys[0], keys[i]) {
					t.Fatal("concurrent fresh starts did not resolve one shared master key")
				}
			}
			var count int
			if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM server_runtime_secrets WHERE name=$1`, masterSecretName).Scan(&count); err != nil || count != 1 {
				t.Fatal("first-start concurrency did not retain exactly one secret row")
			}
		})
	}
}

func freshRuntimeSecretDatabase(t *testing.T) (context.Context, *store.Postgres) {
	t.Helper()
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for fresh-start regression")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		cancel()
		t.Fatal(err)
	}
	name := "termlink_secret_" + uuid.NewString()[:8]
	quoted := pgx.Identifier{name}.Sanitize()
	if _, err := admin.Exec(ctx, `CREATE DATABASE `+quoted); err != nil {
		admin.Close()
		cancel()
		t.Fatal(err)
	}
	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	config.ConnConfig.Database = name
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		pool.Close()
		cleanup, done := context.WithTimeout(context.Background(), 5*time.Second)
		defer done()
		_, _ = admin.Exec(cleanup, `DROP DATABASE `+quoted+` WITH (FORCE)`)
		admin.Close()
		cancel()
	})
	if err := migrate.UpPool(ctx, pool); err != nil {
		t.Fatal(err)
	}
	var rows int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM server_runtime_secrets`).Scan(&rows); err != nil || rows != 0 {
		t.Fatal("freshly migrated database was not empty")
	}
	return ctx, &store.Postgres{Pool: pool}
}
