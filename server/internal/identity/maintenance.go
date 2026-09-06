package identity

import (
	"context"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

const identityMaintenanceInterval = 6 * time.Hour

func cleanupExpiredIdentityState(ctx context.Context, pool *pgxpool.Pool, now time.Time, includeSigningKeys bool) error {
	if ctx == nil || pool == nil {
		return ErrIdentityUnavailable
	}
	tx, err := pool.BeginTx(ctx, pgx.TxOptions{})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	statements := []string{
		`DELETE FROM identity_oauth_sessions WHERE expires_at <= $1`,
		`DELETE FROM identity_client_assertion_jtis WHERE expires_at <= $1`,
		`DELETE FROM identity_password_reset_tokens WHERE expires_at <= $1 OR consumed_at IS NOT NULL`,
	}
	if includeSigningKeys {
		statements = append(statements, `DELETE FROM identity_signing_keys WHERE verify_until <= $1`)
	}
	for _, statement := range statements {
		if _, err = tx.Exec(ctx, statement, now.UTC()); err != nil {
			return err
		}
	}
	return tx.Commit(ctx)
}
