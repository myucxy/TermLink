package store

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"

	"github.com/jackc/pgx/v5"
)

// EnsureRuntimeSecret returns a stable, randomly generated secret owned by the
// database. The advisory lock makes the first-start race converge across
// simultaneous Server processes.
func (s *Postgres) EnsureRuntimeSecret(ctx context.Context, name string, size int) ([]byte, error) {
	if s == nil || s.Pool == nil || len(name) < 3 || len(name) > 128 || size < 32 || size > 128 {
		return nil, errors.New("invalid runtime secret request")
	}
	value := make([]byte, size)
	if _, err := rand.Read(value); err != nil {
		return nil, fmt.Errorf("generate runtime secret: %w", err)
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err = tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtext($1))`, name); err != nil {
		return nil, err
	}
	var stored []byte
	err = tx.QueryRow(ctx, `
		INSERT INTO server_runtime_secrets(name,value)
		VALUES($1,$2)
		ON CONFLICT(name) DO UPDATE SET updated_at=server_runtime_secrets.updated_at
		RETURNING value`, name, value).Scan(&stored)
	if err != nil {
		return nil, err
	}
	if err = tx.Commit(ctx); err != nil {
		return nil, err
	}
	if len(stored) < 32 {
		return nil, fmt.Errorf("runtime secret %q has invalid length", name)
	}
	return append([]byte(nil), stored...), nil
}

// GetRuntimeSecretValue returns the raw stored value of a runtime secret and
// whether it exists. Callers decide the interpretation (plain vs wrapped).
func (s *Postgres) GetRuntimeSecretValue(ctx context.Context, name string) ([]byte, bool, error) {
	if s == nil || s.Pool == nil || len(name) < 3 || len(name) > 128 {
		return nil, false, errors.New("invalid runtime secret request")
	}
	var stored []byte
	err := s.Pool.QueryRow(ctx,
		`SELECT value FROM server_runtime_secrets WHERE name=$1`, name).Scan(&stored)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, false, nil
		}
		return nil, false, err
	}
	return stored, true, nil
}

// UpdateRuntimeSecretValue replaces a runtime secret's value in place. The
// caller owns generation and wrapping semantics.
func (s *Postgres) UpdateRuntimeSecretValue(ctx context.Context, name string, value []byte) error {
	if s == nil || s.Pool == nil || len(name) < 3 || len(name) > 128 || len(value) < 32 {
		return errors.New("invalid runtime secret update")
	}
	tag, err := s.Pool.Exec(ctx,
		`UPDATE server_runtime_secrets SET value=$2, updated_at=now() WHERE name=$1`,
		name, value)
	if err != nil {
		return err
	}
	if tag.RowsAffected() != 1 {
		return errors.New("runtime secret to update does not exist")
	}
	return nil
}
