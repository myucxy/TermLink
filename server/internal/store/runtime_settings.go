package store

import (
	"context"
	"errors"
	"fmt"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

// Runtime setting keys are a closed whitelist: the admin portal and the
// server wiring agree on exactly these deployment parameters. Anything else
// must keep its environment variable (image, public URL, database, ports,
// logging, and the local-HTTP relaxation are container or bootstrap level).
const (
	RuntimeSettingRegistrationRecoveryEnabled = "registration_recovery_enabled"
	RuntimeSettingAccountDeletionEnabled      = "account_deletion_enabled"
	RuntimeSettingDirectSTUNServers           = "direct_stun_servers"
)

// RuntimeSettingKeys is the whitelist enforced by SetRuntimeSetting.
var RuntimeSettingKeys = map[string]struct{}{
	RuntimeSettingRegistrationRecoveryEnabled: {},
	RuntimeSettingAccountDeletionEnabled:      {},
	RuntimeSettingDirectSTUNServers:           {},
}

// GetRuntimeSetting returns the portal-owned override for key. A missing row
// means the deployment falls back to the environment default.
func (s *Postgres) GetRuntimeSetting(ctx context.Context, key string) (string, bool, error) {
	if s == nil || s.Pool == nil {
		return "", false, ErrDenied
	}
	var value string
	err := s.Pool.QueryRow(ctx,
		`SELECT value FROM runtime_settings WHERE key = $1`, key).Scan(&value)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return "", false, nil
		}
		return "", false, err
	}
	return value, true, nil
}

// SetRuntimeSetting persists a portal override. Values are validated by the
// callers (portal handlers and startup wiring) before they reach the store;
// the store only enforces the key whitelist.
func (s *Postgres) SetRuntimeSetting(ctx context.Context, key, value string, actorID uuid.UUID) error {
	if s == nil || s.Pool == nil {
		return ErrDenied
	}
	if _, known := RuntimeSettingKeys[key]; !known {
		return fmt.Errorf("%w: unknown runtime setting %q", ErrDenied, key)
	}
	tag, err := s.Pool.Exec(ctx, `
		INSERT INTO runtime_settings(key, value, updated_by, updated_at)
		VALUES($1, $2, NULLIF($3::uuid, '00000000-0000-0000-0000-000000000000'), now())
		ON CONFLICT(key) DO UPDATE
		SET value=EXCLUDED.value, updated_by=EXCLUDED.updated_by, updated_at=now()`,
		key, value, actorID)
	if err != nil {
		return err
	}
	if tag.RowsAffected() != 1 {
		return ErrDenied
	}
	return nil
}
