package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"strings"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const userPortalAccountDeletionProcessingLookupSQL = `
	SELECT request.account_id
	FROM account_deletion_requests request
	WHERE request.id=$1 AND request.processor_id=$2 AND request.processing_fence=$3
	  AND request.status='PROCESSING' AND request.processor_lease_until>now()
	FOR UPDATE`

const userPortalAccountDeletionUserLookupSQL = `
	SELECT status,role,is_admin FROM users WHERE id=$1 FOR UPDATE`

const userPortalAccountDeletionLockUserSQL = `
	UPDATE users SET status='LOCKED',updated_at=now()
	WHERE id=$1 AND status='ACTIVE'`

const userPortalAccountDeletionLockOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES('account.status.changed',$1,
	       jsonb_build_object('account_id',$1::uuid,'status','LOCKED'))`

var userPortalAccountDeletionPurgePrivateStatements = []string{
	`DELETE FROM account_identity_profiles WHERE user_id=$1`,
	`DELETE FROM invitation_redemptions WHERE user_id=$1`,
	`DELETE FROM idempotency_keys WHERE user_id=$1`,
	`DELETE FROM account_deletion_requests WHERE account_id=$1 AND id<>$2`,
	`DELETE FROM pcs WHERE owner_user_id=$1`,
	`DELETE FROM devices WHERE user_id=$1`,
}

const userPortalAccountDeletionAnonymizeUserSQL = `
	UPDATE users
	SET oidc_subject_hash=$2,status='DELETED',is_admin=false,role='USER',
	    windows_bootstrap_consumed_at=NULL,updated_at=now()
	WHERE id=$1 AND status='LOCKED' AND role='USER' AND is_admin=false`

// PrepareUserPortalAccountDeletion locks the account before any external
// erasure. All normal account and device writes require ACTIVE, so this
// closes the write race before external identity disablement.
func (s *Postgres) PrepareUserPortalAccountDeletion(ctx context.Context, processorID, requestID uuid.UUID, fence int64) error {
	if s == nil || s.Pool == nil || processorID == uuid.Nil || requestID == uuid.Nil || fence < 1 {
		return ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	accountID, err := requireUserPortalAccountDeletionProcessor(ctx, tx, processorID, requestID, fence)
	if err != nil {
		return err
	}
	var status, role string
	var isAdmin bool
	if err := tx.QueryRow(ctx, userPortalAccountDeletionUserLookupSQL, accountID).Scan(&status, &role, &isAdmin); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ErrDenied
		}
		return err
	}
	if role != "USER" || isAdmin || status == "DELETED" {
		return ErrDenied
	}
	if status == "ACTIVE" {
		command, err := tx.Exec(ctx, userPortalAccountDeletionLockUserSQL, accountID)
		if err != nil {
			return err
		}
		if command.RowsAffected() != 1 {
			return ErrConflict
		}
		if _, err := tx.Exec(ctx, userPortalAccountDeletionLockOutboxSQL, accountID); err != nil {
			return err
		}
	} else if status != "LOCKED" {
		return ErrConflict
	}
	if err := tx.Commit(ctx); err != nil {
		return err
	}
	return nil
}

// FinalizeUserPortalAccountDeletion is the only successful terminal mutation.
// A stale lease/fence aborts the transaction without marking the request completed.
func (s *Postgres) FinalizeUserPortalAccountDeletion(ctx context.Context, processorID, requestID uuid.UUID, fence int64, traceID string) error {
	if s == nil || s.Pool == nil || processorID == uuid.Nil || requestID == uuid.Nil || fence < 1 ||
		!validUserPortalProcessorTrace(traceID) {
		return ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	accountID, err := requireUserPortalAccountDeletionProcessor(ctx, tx, processorID, requestID, fence)
	if errors.Is(err, ErrConflict) {
		var replayAccountID uuid.UUID
		if replayErr := tx.QueryRow(ctx, userPortalAccountDeletionCompleteReplaySQL, requestID, fence).Scan(&replayAccountID); replayErr == nil {
			return tx.Commit(ctx)
		} else if !errors.Is(replayErr, pgx.ErrNoRows) {
			return replayErr
		}
		return ErrConflict
	}
	if err != nil {
		return err
	}
	if err := requireUserPortalDeletionReady(ctx, tx, accountID); err != nil {
		return err
	}
	var status, role string
	var isAdmin bool
	if err := tx.QueryRow(ctx, userPortalAccountDeletionUserLookupSQL, accountID).Scan(&status, &role, &isAdmin); err != nil {
		return err
	}
	if status != "LOCKED" || role != "USER" || isAdmin {
		return ErrDenied
	}
	for _, statement := range userPortalAccountDeletionPurgePrivateStatements {
		arguments := []any{accountID}
		if strings.Contains(statement, "$2") {
			arguments = append(arguments, requestID)
		}
		if _, err := tx.Exec(ctx, statement, arguments...); err != nil {
			return err
		}
	}
	tombstone := userPortalDeletedIdentityDigest(accountID, requestID)
	command, err := tx.Exec(ctx, userPortalAccountDeletionAnonymizeUserSQL, accountID, tombstone[:])
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrConflict
	}
	var completedAccountID uuid.UUID
	if err := tx.QueryRow(ctx, userPortalAccountDeletionCompleteSQL, requestID, processorID, fence).Scan(&completedAccountID); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ErrConflict
		}
		return err
	}
	if completedAccountID != accountID {
		return ErrConflict
	}
	if _, err := tx.Exec(ctx, userPortalAccountDeletionProcessorAuditSQL,
		accountID, "user_portal.account_deletion.complete", requestID, "SUCCEEDED", traceID, fence,
	); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO outbox(event_type,aggregate_id,payload)
		VALUES('account.status.changed',$1,jsonb_build_object('account_id',$1::uuid,'status','DELETED'))`, accountID); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func requireUserPortalAccountDeletionProcessor(ctx context.Context, tx pgx.Tx, processorID, requestID uuid.UUID, fence int64) (uuid.UUID, error) {
	var accountID uuid.UUID
	err := tx.QueryRow(ctx, userPortalAccountDeletionProcessingLookupSQL, requestID, processorID, fence).Scan(&accountID)
	if errors.Is(err, pgx.ErrNoRows) {
		return uuid.Nil, ErrConflict
	}
	return accountID, err
}

func userPortalDeletedIdentityDigest(accountID, requestID uuid.UUID) [sha256.Size]byte {
	return sha256.Sum256([]byte("termlink/deleted-account/v1\x00" + accountID.String() + "\x00" + requestID.String()))
}
