package store

import (
	"context"
	"sort"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const (
	expiredGrantCandidatesSQL = `
		SELECT id,account_id FROM entitlement_grants
		WHERE status='ACTIVE' AND ends_at IS NOT NULL AND ends_at<=$1
		ORDER BY ends_at,id LIMIT $2`
	expireSelectedGrantsSQL = `
		UPDATE entitlement_grants SET status='EXPIRED',updated_at=$2
		WHERE id=ANY($1) AND status='ACTIVE' AND ends_at IS NOT NULL AND ends_at<=$2
		RETURNING account_id`
	purgeExpiredIdempotencySQL = `WITH due AS (
		SELECT user_id,idempotency_key FROM idempotency_keys WHERE expires_at<=$1
		ORDER BY expires_at,user_id,idempotency_key LIMIT $2 FOR UPDATE SKIP LOCKED
	) DELETE FROM idempotency_keys target USING due
	WHERE target.user_id=due.user_id AND target.idempotency_key=due.idempotency_key`
	purgeProcessedOutboxSQL = `WITH due AS (
		SELECT id FROM outbox WHERE processed_at IS NOT NULL AND processed_at<$1
		ORDER BY processed_at,id LIMIT $2 FOR UPDATE SKIP LOCKED
	) DELETE FROM outbox target USING due WHERE target.id=due.id`
	redactTerminalPaymentNotificationsSQL = `WITH due AS (
		SELECT id FROM payment_notifications
		WHERE processing_state IN('PROCESSED','REJECTED') AND processed_at<$1
		  AND payload_ciphertext IS NOT NULL
		ORDER BY processed_at,id LIMIT $2 FOR UPDATE SKIP LOCKED
	) UPDATE payment_notifications target SET payload_ciphertext=NULL
	FROM due WHERE target.id=due.id`
	countAmbiguousInvitationReservationsSQL = `SELECT count(*) FROM (
		SELECT 1 FROM invitation_redemptions
		WHERE state='RESERVED' AND reserved_until<=$1 LIMIT $2
	) candidates`
)

func boundedCleanupLimit(limit int) int {
	if limit < 1 || limit > 500 {
		return 100
	}
	return limit
}

// ExpireEntitlementGrants performs the source transition and materialized
// entitlement fallback in one serializable transaction. Account locks are
// acquired in UUID order, matching grant-creation paths that lock the user
// before mutating sources and preventing cross-account lock inversions.
func (s *Postgres) ExpireEntitlementGrants(ctx context.Context, now time.Time, limit int) (int64, int, error) {
	limit = boundedCleanupLimit(limit)
	now = now.UTC()
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return 0, 0, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, expiredGrantCandidatesSQL, now, limit)
	if err != nil {
		return 0, 0, err
	}
	var grantIDs, accountIDs []uuid.UUID
	accounts := make(map[uuid.UUID]struct{})
	for rows.Next() {
		var grantID, accountID uuid.UUID
		if err := rows.Scan(&grantID, &accountID); err != nil {
			rows.Close()
			return 0, 0, err
		}
		grantIDs = append(grantIDs, grantID)
		if _, exists := accounts[accountID]; !exists {
			accounts[accountID] = struct{}{}
			accountIDs = append(accountIDs, accountID)
		}
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return 0, 0, err
	}
	rows.Close()
	if len(grantIDs) == 0 {
		return 0, 0, tx.Commit(ctx)
	}
	sort.Slice(accountIDs, func(i, j int) bool { return accountIDs[i].String() < accountIDs[j].String() })
	lockRows, err := tx.Query(ctx, `SELECT id FROM users WHERE id=ANY($1) ORDER BY id FOR UPDATE`, accountIDs)
	if err != nil {
		return 0, 0, err
	}
	for lockRows.Next() {
		var ignored uuid.UUID
		if err := lockRows.Scan(&ignored); err != nil {
			lockRows.Close()
			return 0, 0, err
		}
	}
	if err := lockRows.Err(); err != nil {
		lockRows.Close()
		return 0, 0, err
	}
	lockRows.Close()
	expiredRows, err := tx.Query(ctx, expireSelectedGrantsSQL, grantIDs, now)
	if err != nil {
		return 0, 0, err
	}
	affected := make(map[uuid.UUID]struct{})
	var expired int64
	for expiredRows.Next() {
		var accountID uuid.UUID
		if err := expiredRows.Scan(&accountID); err != nil {
			expiredRows.Close()
			return 0, 0, err
		}
		affected[accountID] = struct{}{}
		expired++
	}
	if err := expiredRows.Err(); err != nil {
		expiredRows.Close()
		return 0, 0, err
	}
	expiredRows.Close()
	recomputed := 0
	for _, accountID := range accountIDs {
		if _, ok := affected[accountID]; !ok {
			continue
		}
		if _, err := recomputeEntitlementFromMaterializedGrants(ctx, tx, accountID); err != nil {
			return 0, 0, err
		}
		recomputed++
	}
	if err := tx.Commit(ctx); err != nil {
		return 0, 0, err
	}
	return expired, recomputed, nil
}

func (s *Postgres) PurgeExpiredIdempotencyKeys(ctx context.Context, now time.Time, limit int) (int64, error) {
	command, err := s.Pool.Exec(ctx, purgeExpiredIdempotencySQL, now.UTC(), boundedCleanupLimit(limit))
	if err != nil {
		return 0, err
	}
	return command.RowsAffected(), nil
}

func (s *Postgres) PurgeProcessedOutbox(ctx context.Context, before time.Time, limit int) (int64, error) {
	command, err := s.Pool.Exec(ctx, purgeProcessedOutboxSQL, before.UTC(), boundedCleanupLimit(limit))
	if err != nil {
		return 0, err
	}
	return command.RowsAffected(), nil
}

func (s *Postgres) RedactPaymentNotificationPayloads(ctx context.Context, before time.Time, limit int) (int64, error) {
	command, err := s.Pool.Exec(ctx, redactTerminalPaymentNotificationsSQL, before.UTC(), boundedCleanupLimit(limit))
	if err != nil {
		return 0, err
	}
	return command.RowsAffected(), nil
}

func (s *Postgres) CountExpiredAmbiguousInvitationReservations(ctx context.Context, now time.Time, limit int) (int, bool, error) {
	limit = boundedCleanupLimit(limit)
	var count int
	err := s.Pool.QueryRow(ctx, countAmbiguousInvitationReservationsSQL, now.UTC(), limit+1).Scan(&count)
	if err != nil {
		return 0, false, err
	}
	more := count > limit
	if more {
		count = limit
	}
	return count, more, nil
}
