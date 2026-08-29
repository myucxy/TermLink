package store

import (
	"context"
	"crypto/hmac"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

var (
	ErrInvalidServiceEmailSettings = errors.New("service email settings are invalid")
	ErrEmailChallengeInvalid       = errors.New("email verification challenge is invalid")
	ErrEmailChallengeRateLimit     = errors.New("email verification challenge is rate limited")
)

type ServiceEmailSettings struct {
	ServiceEnabled                  bool
	RegistrationVerificationEnabled bool
	PasswordResetEnabled            bool
	SMTPHost                        string
	SMTPPort                        int
	SMTPSecurity                    string
	FromName                        string
	FromAddress                     string
	ReplyTo                         string
	SMTPUsername                    string
	SMTPPasswordCiphertext          []byte
	Status                          string
	Version                         int64
	TestedAt                        *time.Time
	UpdatedAt                       time.Time
}

type ServiceEmailSettingsInput struct {
	ServiceEnabled                  bool
	RegistrationVerificationEnabled bool
	PasswordResetEnabled            bool
	SMTPHost                        string
	SMTPPort                        int
	SMTPSecurity                    string
	FromName                        string
	FromAddress                     string
	ReplyTo                         string
	SMTPUsername                    string
	SMTPPasswordCiphertext          []byte
	ExpectedVersion                 int64
}

func scanServiceEmailSettings(row pgx.Row) (ServiceEmailSettings, error) {
	var result ServiceEmailSettings
	err := row.Scan(
		&result.ServiceEnabled, &result.RegistrationVerificationEnabled, &result.PasswordResetEnabled,
		&result.SMTPHost, &result.SMTPPort, &result.SMTPSecurity, &result.FromName, &result.FromAddress,
		&result.ReplyTo, &result.SMTPUsername, &result.SMTPPasswordCiphertext, &result.Status,
		&result.Version, &result.TestedAt, &result.UpdatedAt,
	)
	return result, err
}

const serviceEmailSettingsColumns = `service_enabled,registration_verification_enabled,password_reset_enabled,
smtp_host,smtp_port,smtp_security,from_name,from_address,reply_to,smtp_username,
smtp_password_ciphertext,status,version,tested_at,updated_at`

func (s *Postgres) GetServiceEmailSettings(ctx context.Context) (ServiceEmailSettings, error) {
	return scanServiceEmailSettings(s.Pool.QueryRow(ctx,
		`SELECT `+serviceEmailSettingsColumns+` FROM service_email_settings WHERE singleton=true`))
}

// SaveVerifiedServiceEmailSettings publishes only a configuration that has
// already passed a real delivery test. SMTP secrets are ciphertext at this
// boundary and never enter audit metadata.
func (s *Postgres) SaveVerifiedServiceEmailSettings(ctx context.Context, actorID uuid.UUID, input ServiceEmailSettingsInput) (ServiceEmailSettings, error) {
	if actorID == uuid.Nil || input.ExpectedVersion < 1 || len(input.SMTPPasswordCiphertext) == 0 {
		return ServiceEmailSettings{}, ErrInvalidServiceEmailSettings
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return ServiceEmailSettings{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var role Role
	var status string
	if err = tx.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1 FOR UPDATE`, actorID).Scan(&role, &status); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ServiceEmailSettings{}, ErrDenied
		}
		return ServiceEmailSettings{}, err
	}
	if role != RoleSuperAdmin || status != "ACTIVE" {
		return ServiceEmailSettings{}, ErrDenied
	}
	result, err := scanServiceEmailSettings(tx.QueryRow(ctx, `
		UPDATE service_email_settings SET
		 service_enabled=$1,registration_verification_enabled=$2,password_reset_enabled=$3,
		 smtp_host=$4,smtp_port=$5,smtp_security=$6,from_name=$7,from_address=$8,
		 reply_to=$9,smtp_username=$10,smtp_password_ciphertext=$11,status='VERIFIED',
		 version=version+1,tested_at=now(),updated_by=$12,updated_at=now()
		WHERE singleton=true AND version=$13
		RETURNING `+serviceEmailSettingsColumns,
		input.ServiceEnabled, input.RegistrationVerificationEnabled, input.PasswordResetEnabled,
		input.SMTPHost, input.SMTPPort, input.SMTPSecurity, input.FromName, input.FromAddress,
		input.ReplyTo, input.SMTPUsername, input.SMTPPasswordCiphertext, actorID, input.ExpectedVersion))
	if errors.Is(err, pgx.ErrNoRows) {
		return ServiceEmailSettings{}, ErrConflict
	}
	if err != nil {
		return ServiceEmailSettings{}, err
	}
	if _, err = tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,'web.service_email.update','system_settings',NULL,'SUCCEEDED','service-email',
		 jsonb_build_object('version',$2::bigint,'service_enabled',$3::boolean,
		 'registration_verification_enabled',$4::boolean,'password_reset_enabled',$5::boolean))`,
		actorID, result.Version, result.ServiceEnabled, result.RegistrationVerificationEnabled, result.PasswordResetEnabled); err != nil {
		return ServiceEmailSettings{}, err
	}
	if err = tx.Commit(ctx); err != nil {
		return ServiceEmailSettings{}, err
	}
	return result, nil
}

type RegistrationEmailChallenge struct {
	ID        uuid.UUID
	EmailHash [32]byte
	CodeHash  [32]byte
	ExpiresAt time.Time
}

type AccountEmailChallenge struct {
	ID        uuid.UUID
	AccountID uuid.UUID
	EmailHash [32]byte
	CodeHash  [32]byte
	ExpiresAt time.Time
}

func (s *Postgres) CreateAccountEmailChallenge(ctx context.Context, challenge AccountEmailChallenge, resendAfter time.Duration) error {
	if challenge.ID == uuid.Nil || challenge.AccountID == uuid.Nil || challenge.ExpiresAt.IsZero() || resendAfter < 0 {
		return ErrInvalidServiceEmailSettings
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var status string
	if err = tx.QueryRow(ctx, `SELECT status FROM users WHERE id=$1 FOR UPDATE`, challenge.AccountID).Scan(&status); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ErrDenied
		}
		return err
	}
	if status != "ACTIVE" {
		return ErrDenied
	}
	var latest, databaseNow time.Time
	err = tx.QueryRow(ctx, `SELECT created_at,now() FROM account_email_change_challenges WHERE email_hash=$1 ORDER BY created_at DESC LIMIT 1 FOR UPDATE`, challenge.EmailHash[:]).Scan(&latest, &databaseNow)
	if err == nil && databaseNow.Sub(latest) < resendAfter {
		return ErrEmailChallengeRateLimit
	}
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return err
	}
	_, err = tx.Exec(ctx, `INSERT INTO account_email_change_challenges(id,account_id,email_hash,code_hash,expires_at) VALUES($1,$2,$3,$4,$5)`,
		challenge.ID, challenge.AccountID, challenge.EmailHash[:], challenge.CodeHash[:], challenge.ExpiresAt)
	if err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) ConsumeAccountEmailChallenge(ctx context.Context, accountID, challengeID uuid.UUID, emailHash, codeHash [32]byte, now time.Time) error {
	if accountID == uuid.Nil || challengeID == uuid.Nil {
		return ErrEmailChallengeInvalid
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var storedEmail, storedCode []byte
	var storedAccount uuid.UUID
	var attempts int
	var expiresAt time.Time
	var consumedAt *time.Time
	err = tx.QueryRow(ctx, `SELECT account_id,email_hash,code_hash,attempts,expires_at,consumed_at FROM account_email_change_challenges WHERE id=$1 FOR UPDATE`, challengeID).
		Scan(&storedAccount, &storedEmail, &storedCode, &attempts, &expiresAt, &consumedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrEmailChallengeInvalid
	}
	if err != nil {
		return err
	}
	valid := storedAccount == accountID && consumedAt == nil && attempts < 5 && now.Before(expiresAt) &&
		hmac.Equal(storedEmail, emailHash[:]) && hmac.Equal(storedCode, codeHash[:])
	if !valid {
		if storedAccount == accountID && consumedAt == nil && attempts < 5 {
			if _, err = tx.Exec(ctx, `UPDATE account_email_change_challenges SET attempts=attempts+1 WHERE id=$1`, challengeID); err != nil {
				return err
			}
			if err = tx.Commit(ctx); err != nil {
				return err
			}
		}
		return ErrEmailChallengeInvalid
	}
	if _, err = tx.Exec(ctx, `UPDATE account_email_change_challenges SET consumed_at=$2 WHERE id=$1 AND consumed_at IS NULL`, challengeID, now); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) CreateRegistrationEmailChallenge(ctx context.Context, challenge RegistrationEmailChallenge, resendAfter time.Duration) error {
	if challenge.ID == uuid.Nil || challenge.ExpiresAt.IsZero() || resendAfter < 0 {
		return ErrInvalidServiceEmailSettings
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var latest, databaseNow time.Time
	err = tx.QueryRow(ctx, `SELECT created_at,now() FROM registration_email_challenges WHERE email_hash=$1 ORDER BY created_at DESC LIMIT 1 FOR UPDATE`, challenge.EmailHash[:]).Scan(&latest, &databaseNow)
	if err == nil && databaseNow.Sub(latest) < resendAfter {
		return ErrEmailChallengeRateLimit
	}
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return err
	}
	_, err = tx.Exec(ctx, `INSERT INTO registration_email_challenges(id,email_hash,code_hash,expires_at) VALUES($1,$2,$3,$4)`,
		challenge.ID, challenge.EmailHash[:], challenge.CodeHash[:], challenge.ExpiresAt)
	if err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) ConsumeRegistrationEmailChallenge(ctx context.Context, challengeID uuid.UUID, emailHash, codeHash [32]byte, now time.Time) error {
	if challengeID == uuid.Nil {
		return ErrEmailChallengeInvalid
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var storedEmail, storedCode []byte
	var attempts int
	var expiresAt time.Time
	var consumedAt *time.Time
	err = tx.QueryRow(ctx, `SELECT email_hash,code_hash,attempts,expires_at,consumed_at FROM registration_email_challenges WHERE id=$1 FOR UPDATE`, challengeID).
		Scan(&storedEmail, &storedCode, &attempts, &expiresAt, &consumedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrEmailChallengeInvalid
	}
	if err != nil {
		return err
	}
	valid := consumedAt == nil && attempts < 5 && now.Before(expiresAt) &&
		hmac.Equal(storedEmail, emailHash[:]) && hmac.Equal(storedCode, codeHash[:])
	if !valid {
		if consumedAt == nil && attempts < 5 {
			_, err = tx.Exec(ctx, `UPDATE registration_email_challenges SET attempts=attempts+1 WHERE id=$1`, challengeID)
			if err != nil {
				return err
			}
			if err = tx.Commit(ctx); err != nil {
				return err
			}
		}
		return ErrEmailChallengeInvalid
	}
	if _, err = tx.Exec(ctx, `UPDATE registration_email_challenges SET consumed_at=$2 WHERE id=$1 AND consumed_at IS NULL`, challengeID, now); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) CleanupRegistrationEmailChallenges(ctx context.Context, before time.Time, limit int) (int64, error) {
	if limit < 1 || limit > 1000 {
		limit = 200
	}
	tag, err := s.Pool.Exec(ctx, `DELETE FROM registration_email_challenges WHERE id IN (
		SELECT id FROM registration_email_challenges WHERE expires_at<$1 OR consumed_at<$1 ORDER BY created_at LIMIT $2
	)`, before, limit)
	return tag.RowsAffected(), err
}
