// Package migrate applies the embedded PostgreSQL schema migrations.
package migrate

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io/fs"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

const advisoryLockID int64 = 0x5445524d4c494e4b

type migration struct {
	version  int64
	name     string
	sql      string
	checksum string
}

// Up opens PostgreSQL, serializes migration execution, and applies every new
// migration transactionally. A changed checksum is rejected.
func Up(ctx context.Context, databaseURL string) error {
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		return fmt.Errorf("open PostgreSQL: %w", err)
	}
	defer pool.Close()
	if err := pool.Ping(ctx); err != nil {
		return fmt.Errorf("ping PostgreSQL: %w", err)
	}
	return UpPool(ctx, pool)
}

// UpPool applies migrations using an existing connection pool.
func UpPool(ctx context.Context, pool *pgxpool.Pool) error {
	items, err := load(migrations.Files)
	if err != nil {
		return err
	}
	conn, err := pool.Acquire(ctx)
	if err != nil {
		return fmt.Errorf("acquire migration connection: %w", err)
	}
	defer conn.Release()
	if _, err := conn.Exec(ctx, `SELECT pg_advisory_lock($1)`, advisoryLockID); err != nil {
		return fmt.Errorf("lock migrations: %w", err)
	}
	defer func() { _, _ = conn.Exec(context.Background(), `SELECT pg_advisory_unlock($1)`, advisoryLockID) }()

	if _, err := conn.Exec(ctx, `
		CREATE TABLE IF NOT EXISTS schema_migrations (
			version bigint PRIMARY KEY,
			name text NOT NULL,
			checksum text NOT NULL,
			applied_at timestamptz NOT NULL DEFAULT now()
		)`); err != nil {
		return fmt.Errorf("create migration ledger: %w", err)
	}
	for _, item := range items {
		var existing string
		err := conn.QueryRow(ctx, `SELECT checksum FROM schema_migrations WHERE version=$1`, item.version).Scan(&existing)
		switch {
		case err == nil:
			if existing != item.checksum {
				return fmt.Errorf("migration %d checksum changed", item.version)
			}
			continue
		case err != pgx.ErrNoRows:
			return fmt.Errorf("read migration %d: %w", item.version, err)
		}
		tx, err := conn.Begin(ctx)
		if err != nil {
			return fmt.Errorf("begin migration %d: %w", item.version, err)
		}
		if _, err = tx.Exec(ctx, item.sql); err == nil {
			_, err = tx.Exec(ctx, `INSERT INTO schema_migrations(version,name,checksum) VALUES($1,$2,$3)`, item.version, item.name, item.checksum)
		}
		if err != nil {
			_ = tx.Rollback(ctx)
			return fmt.Errorf("apply migration %d: %w", item.version, err)
		}
		if err := tx.Commit(ctx); err != nil {
			return fmt.Errorf("commit migration %d: %w", item.version, err)
		}
	}
	return nil
}

func load(source fs.FS) ([]migration, error) {
	entries, err := fs.ReadDir(source, ".")
	if err != nil {
		return nil, fmt.Errorf("read migrations: %w", err)
	}
	var result []migration
	seen := make(map[int64]bool)
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".up.sql") {
			continue
		}
		prefix, _, ok := strings.Cut(entry.Name(), "_")
		if !ok {
			return nil, fmt.Errorf("invalid migration name %q", entry.Name())
		}
		version, err := strconv.ParseInt(prefix, 10, 64)
		if err != nil || version < 1 || seen[version] {
			return nil, fmt.Errorf("invalid or duplicate migration version %q", entry.Name())
		}
		data, err := fs.ReadFile(source, filepath.ToSlash(entry.Name()))
		if err != nil {
			return nil, fmt.Errorf("read migration %q: %w", entry.Name(), err)
		}
		if strings.TrimSpace(string(data)) == "" {
			return nil, fmt.Errorf("empty migration %q", entry.Name())
		}
		digest := sha256.Sum256(data)
		result = append(result, migration{
			version: version, name: entry.Name(), sql: string(data),
			checksum: hex.EncodeToString(digest[:]),
		})
		seen[version] = true
	}
	sort.Slice(result, func(i, j int) bool { return result[i].version < result[j].version })
	if len(result) == 0 {
		return nil, fmt.Errorf("no up migrations embedded")
	}
	return result, nil
}
