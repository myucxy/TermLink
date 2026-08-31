package store

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const (
	registrationRecoveryDefaultLimit = 50
	registrationRecoveryMaxLimit     = 200
)

type RegistrationRecoveryCandidate struct {
	AttemptID     uuid.UUID
	State         string
	ReservedUntil time.Time
	ReleasedAt    *time.Time
	UpdatedAt     time.Time
}

type RegistrationRecoveryResolution string

const (
	RegistrationRecoveryConfirmed        RegistrationRecoveryResolution = "CONFIRMED"
	RegistrationRecoveryAlreadyConfirmed RegistrationRecoveryResolution = "ALREADY_CONFIRMED"
	RegistrationRecoveryOrphan           RegistrationRecoveryResolution = "ORPHAN"
	RegistrationRecoveryReleased         RegistrationRecoveryResolution = "RELEASED"
	RegistrationRecoveryAlreadyReleased  RegistrationRecoveryResolution = "ALREADY_RELEASED"
)

const listRegistrationRecoveryCandidatesSQL = `
	SELECT r.registration_attempt_id,r.state,r.reserved_until,r.released_at,r.updated_at
	FROM invitation_redemptions r
	WHERE r.user_id IS NULL
	  AND r.state IN ('RESERVED','RELEASED')
	  AND CASE WHEN r.state='RELEASED' THEN r.released_at ELSE r.reserved_until END <= $1
	  AND NOT EXISTS (
	      SELECT 1 FROM audit_events a
	      WHERE a.target_type='registration_attempt'
	        AND a.target_id=r.registration_attempt_id
	        AND a.result='SUCCEEDED'
	        AND a.action IN (
	            'registration.recovery.uncreated_released',
	            'registration.recovery.released_absent',
	            'registration.recovery.orphan_deleted'
	        )
	  )
	ORDER BY CASE WHEN r.state='RELEASED' THEN r.released_at ELSE r.reserved_until END,
	         r.registration_attempt_id
	LIMIT $2`

func normalizeRegistrationRecoveryLimit(limit int) int {
	if limit < 1 {
		return registrationRecoveryDefaultLimit
	}
	if limit > registrationRecoveryMaxLimit {
		return registrationRecoveryMaxLimit
	}
	return limit
}

func (s *Postgres) ListRegistrationRecoveryCandidates(ctx context.Context, before time.Time, limit int) ([]RegistrationRecoveryCandidate, error) {
	limit = normalizeRegistrationRecoveryLimit(limit)
	rows, err := s.Pool.Query(ctx, listRegistrationRecoveryCandidatesSQL, before.UTC(), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]RegistrationRecoveryCandidate, 0, limit)
	for rows.Next() {
		var item RegistrationRecoveryCandidate
		if err := rows.Scan(&item.AttemptID, &item.State, &item.ReservedUntil, &item.ReleasedAt, &item.UpdatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

// ConfirmRecoveredRegistration is the durable half of the positive recovery
// path. It deliberately accepts an expired RESERVED row: an exact, HMAC-proven
// Keycloak identity is stronger evidence than the local reservation timeout.
func (s *Postgres) ConfirmRecoveredRegistration(ctx context.Context, attemptID uuid.UUID) (RegistrationRecoveryResolution, error) {
	if attemptID == uuid.Nil {
		return "", ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return "", err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err = lockRegistrationRecoveryAttempt(ctx, tx, attemptID); err != nil {
		return "", err
	}
	var state string
	var userID *uuid.UUID
	err = tx.QueryRow(ctx, `SELECT state,user_id FROM invitation_redemptions WHERE registration_attempt_id=$1 FOR UPDATE`, attemptID).Scan(&state, &userID)
	if errors.Is(err, pgx.ErrNoRows) {
		return RegistrationRecoveryOrphan, tx.Commit(ctx)
	}
	if err != nil {
		return "", err
	}
	switch state {
	case "RESERVED":
		if _, err = tx.Exec(ctx, `UPDATE invitation_redemptions SET state='PROVIDER_ACCEPTED',provider_accepted_at=coalesce(provider_accepted_at,now()),updated_at=now() WHERE registration_attempt_id=$1 AND state='RESERVED'`, attemptID); err != nil {
			return "", err
		}
		if err = insertRegistrationRecoveryAudit(ctx, tx, attemptID, "registration.recovery.provider_confirmed"); err != nil {
			return "", err
		}
		return RegistrationRecoveryConfirmed, tx.Commit(ctx)
	case "PROVIDER_ACCEPTED", "REDEEMED":
		return RegistrationRecoveryAlreadyConfirmed, tx.Commit(ctx)
	case "RELEASED":
		return RegistrationRecoveryOrphan, tx.Commit(ctx)
	default:
		return "", ErrConflict
	}
}

// ReleaseAbsentRegistration releases inventory only after the caller has an
// authoritative ABSENT result from Keycloak. It is idempotent and never
// releases a provider-accepted or redeemed registration.
func (s *Postgres) ReleaseAbsentRegistration(ctx context.Context, attemptID uuid.UUID) (RegistrationRecoveryResolution, error) {
	if attemptID == uuid.Nil {
		return "", ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return "", err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err = lockRegistrationRecoveryAttempt(ctx, tx, attemptID); err != nil {
		return "", err
	}
	var codeID uuid.UUID
	var state string
	err = tx.QueryRow(ctx, `SELECT code_id,state FROM invitation_redemptions WHERE registration_attempt_id=$1 FOR UPDATE`, attemptID).Scan(&codeID, &state)
	if errors.Is(err, pgx.ErrNoRows) {
		return RegistrationRecoveryAlreadyReleased, tx.Commit(ctx)
	}
	if err != nil {
		return "", err
	}
	switch state {
	case "PROVIDER_ACCEPTED", "REDEEMED":
		return RegistrationRecoveryAlreadyConfirmed, tx.Commit(ctx)
	case "RESERVED":
		if _, err = tx.Exec(ctx, `UPDATE invitation_redemptions SET state='RELEASED',released_at=coalesce(released_at,now()),updated_at=now() WHERE registration_attempt_id=$1 AND state='RESERVED'`, attemptID); err != nil {
			return "", err
		}
		if _, err = tx.Exec(ctx, `DELETE FROM invitation_registration_profiles WHERE redemption_id IN (SELECT id FROM invitation_redemptions WHERE registration_attempt_id=$1)`, attemptID); err != nil {
			return "", err
		}
		if _, err = tx.Exec(ctx, `UPDATE invitation_codes SET used_count=greatest(used_count-1,0),updated_at=now() WHERE id=$1`, codeID); err != nil {
			return "", err
		}
		if err = insertRegistrationRecoveryAudit(ctx, tx, attemptID, "registration.recovery.uncreated_released"); err != nil {
			return "", err
		}
		return RegistrationRecoveryReleased, tx.Commit(ctx)
	case "RELEASED":
		if err = insertRegistrationRecoveryAudit(ctx, tx, attemptID, "registration.recovery.released_absent"); err != nil {
			return "", err
		}
		return RegistrationRecoveryAlreadyReleased, tx.Commit(ctx)
	default:
		return "", ErrConflict
	}
}

func (s *Postgres) RecordRegistrationOrphanDeleted(ctx context.Context, attemptID uuid.UUID) error {
	if attemptID == uuid.Nil {
		return ErrDenied
	}
	// The advisory lock serializes both present and missing attempt rows. Use
	// READ COMMITTED here so a waiter observes the first replica's terminal
	// audit marker after the lock is acquired instead of retaining an older
	// serializable snapshot for a row that does not exist.
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.ReadCommitted})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err = lockRegistrationRecoveryAttempt(ctx, tx, attemptID); err != nil {
		return err
	}
	var state string
	err = tx.QueryRow(ctx, `SELECT state FROM invitation_redemptions WHERE registration_attempt_id=$1 FOR UPDATE`, attemptID).Scan(&state)
	if err == nil && state != "RELEASED" {
		return ErrConflict
	}
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return err
	}
	if err = insertRegistrationRecoveryAudit(ctx, tx, attemptID, "registration.recovery.orphan_deleted"); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) RegistrationSubjectExists(ctx context.Context, subjectHash [32]byte) (bool, error) {
	var exists bool
	err := s.Pool.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM users WHERE oidc_subject_hash=$1)`, subjectHash[:]).Scan(&exists)
	return exists, err
}

func insertRegistrationRecoveryAudit(ctx context.Context, tx pgx.Tx, attemptID uuid.UUID, action string) error {
	_, err := tx.Exec(ctx, `
		INSERT INTO audit_events(action,target_type,target_id,result,trace_id,metadata)
		SELECT $2,'registration_attempt',$1,'SUCCEEDED','registration-recovery','{}'::jsonb
		WHERE NOT EXISTS (
			SELECT 1 FROM audit_events
			WHERE action=$2 AND target_type='registration_attempt' AND target_id=$1 AND result='SUCCEEDED'
		)`, attemptID, action)
	return err
}

// Missing invitation rows have no tuple to lock. A transaction-scoped
// advisory lock keeps concurrent recovery replicas from inserting duplicate
// terminal audit markers for the same attempt while retaining row locks as
// the authoritative state-transition guard.
func lockRegistrationRecoveryAttempt(ctx context.Context, tx pgx.Tx, attemptID uuid.UUID) error {
	_, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtextextended('registration-recovery:' || $1::uuid::text,0))`, attemptID)
	return err
}
