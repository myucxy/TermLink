package store

import (
	"context"
	"errors"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

var ErrInvalidHumanVerificationSettings = errors.New("human verification settings are invalid")

type HumanVerificationSettings struct {
	Enabled          bool
	SiteKey          string
	SecretCiphertext []byte
	Version          int64
	UpdatedAt        time.Time
}

type HumanVerificationSettingsInput struct {
	Enabled          bool
	SiteKey          string
	SecretCiphertext []byte
	ExpectedVersion  int64
}

func scanHumanVerificationSettings(row pgx.Row) (HumanVerificationSettings, error) {
	var result HumanVerificationSettings
	err := row.Scan(&result.Enabled, &result.SiteKey, &result.SecretCiphertext, &result.Version, &result.UpdatedAt)
	return result, err
}

const humanVerificationSettingsColumns = `enabled,site_key,secret_ciphertext,version,updated_at`

func (s *Postgres) GetHumanVerificationSettings(ctx context.Context) (HumanVerificationSettings, error) {
	return scanHumanVerificationSettings(s.Pool.QueryRow(ctx,
		`SELECT `+humanVerificationSettingsColumns+` FROM human_verification_settings WHERE singleton=true`))
}

// SaveHumanVerificationSettings accepts only ciphertext at the storage
// boundary. The provider secret is never written to audit metadata or returned
// by an HTTP response.
func (s *Postgres) SaveHumanVerificationSettings(ctx context.Context, actorID uuid.UUID, input HumanVerificationSettingsInput) (HumanVerificationSettings, error) {
	siteKey := strings.TrimSpace(input.SiteKey)
	ciphertextLength := len(input.SecretCiphertext)
	if actorID == uuid.Nil || input.ExpectedVersion < 1 || len(siteKey) > 256 ||
		(input.Enabled && (siteKey == "" || ciphertextLength == 0)) || ciphertextLength > 1024 ||
		(ciphertextLength > 0 && ciphertextLength < 29) {
		return HumanVerificationSettings{}, ErrInvalidHumanVerificationSettings
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return HumanVerificationSettings{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var role Role
	var status string
	if err = tx.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1 FOR UPDATE`, actorID).Scan(&role, &status); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return HumanVerificationSettings{}, ErrDenied
		}
		return HumanVerificationSettings{}, err
	}
	if role != RoleSuperAdmin || status != "ACTIVE" {
		return HumanVerificationSettings{}, ErrDenied
	}
	result, err := scanHumanVerificationSettings(tx.QueryRow(ctx, `
		UPDATE human_verification_settings SET enabled=$1,site_key=$2,secret_ciphertext=$3,
		 version=version+1,updated_by=$4,updated_at=now()
		WHERE singleton=true AND version=$5
		RETURNING `+humanVerificationSettingsColumns,
		input.Enabled, siteKey, nullableBytes(input.SecretCiphertext), actorID, input.ExpectedVersion))
	if errors.Is(err, pgx.ErrNoRows) {
		return HumanVerificationSettings{}, ErrConflict
	}
	if err != nil {
		return HumanVerificationSettings{}, err
	}
	if _, err = tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,'web.human_verification.update','system_settings',NULL,'SUCCEEDED','human-verification',
		 jsonb_build_object('version',$2::bigint,'enabled',$3::boolean,'site_key_configured',$4::boolean,'secret_configured',$5::boolean))`,
		actorID, result.Version, result.Enabled, result.SiteKey != "", len(result.SecretCiphertext) > 0); err != nil {
		return HumanVerificationSettings{}, err
	}
	if err = tx.Commit(ctx); err != nil {
		return HumanVerificationSettings{}, err
	}
	return result, nil
}
