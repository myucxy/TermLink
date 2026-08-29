package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"sort"
	"strings"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

type UserPortalAccountDeletionObjectKind string

const (
	UserPortalAccountDeletionStoredObject  UserPortalAccountDeletionObjectKind = "STORED_OBJECT"
	UserPortalAccountDeletionPrivacyExport UserPortalAccountDeletionObjectKind = "PRIVACY_EXPORT"
)

// UserPortalAccountDeletionObject is an internal, short-lived capability.
// Callers must keep ObjectKey in process memory only; it must never be logged,
// audited, serialized into API responses, or copied into an error message.
type UserPortalAccountDeletionObject struct {
	Kind      UserPortalAccountDeletionObjectKind `json:"-"`
	ObjectKey string                              `json:"-"`
}

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

const userPortalAccountDeletionObjectsSQL = `
	SELECT object_kind,object_key,source_id,processing_fence
	FROM (
		SELECT 'STORED_OBJECT'::text object_kind,chunk.object_key,
		       chunk.id source_id,0::bigint processing_fence
		FROM event_chunks chunk
		JOIN sessions session ON session.id=chunk.session_id
		JOIN pcs pc ON pc.id=session.pc_id
		WHERE pc.owner_user_id=$1
		UNION ALL
		SELECT 'STORED_OBJECT',snapshot.object_key,snapshot.id,0::bigint
		FROM snapshots snapshot
		JOIN sessions session ON session.id=snapshot.session_id
		JOIN pcs pc ON pc.id=session.pc_id
		WHERE pc.owner_user_id=$1
		UNION ALL
		SELECT 'STORED_OBJECT',page.object_key,page.id,0::bigint
		FROM transcript_pages page
		JOIN sessions session ON session.id=page.session_id
		JOIN pcs pc ON pc.id=session.pc_id
		WHERE pc.owner_user_id=$1
		UNION ALL
		SELECT 'STORED_OBJECT',upload.object_key,upload.id,0::bigint
		FROM history_uploads upload
		WHERE upload.user_id=$1
		UNION ALL
		SELECT 'STORED_OBJECT',operation.object_key,operation.id,0::bigint
		FROM pc_file_operations operation
		WHERE operation.account_id=$1 AND operation.object_key IS NOT NULL
		UNION ALL
		SELECT 'PRIVACY_EXPORT',request.artifact_object_key,request.id,request.processing_fence
		FROM data_export_requests request
		WHERE request.account_id=$1 AND request.artifact_object_key IS NOT NULL
	) objects
	ORDER BY object_kind,object_key,source_id`

var userPortalAccountDeletionRedactCommerceStatements = []string{
	`UPDATE orders SET idempotency_key='deleted-' || id::text,updated_at=now() WHERE account_id=$1`,
	`UPDATE payment_transactions payment
	 SET payment_payload='{}'::jsonb,updated_at=now()
	 FROM orders account_order
	 WHERE account_order.id=payment.order_id AND account_order.account_id=$1`,
	`UPDATE payment_notifications notification
	 SET payload_ciphertext=NULL
	 WHERE notification.order_no IN (SELECT order_no FROM orders WHERE account_id=$1)`,
	`UPDATE refunds refund
	 SET reason='ACCOUNT_DELETED',updated_at=now()
	 FROM orders account_order
	 WHERE account_order.id=refund.order_id AND account_order.account_id=$1`,
}

var userPortalAccountDeletionPurgePrivateStatements = []string{
	`DELETE FROM redemption_claims WHERE account_id=$1`,
	`DELETE FROM entitlement_grants WHERE account_id=$1`,
	`DELETE FROM subscriptions WHERE account_id=$1`,
	`DELETE FROM entitlements WHERE account_id=$1`,
	`DELETE FROM quota_selections WHERE account_id=$1`,
	`DELETE FROM invitation_redemptions WHERE user_id=$1`,
	`DELETE FROM idempotency_keys WHERE user_id=$1`,
	`DELETE FROM data_export_requests WHERE account_id=$1`,
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
// erasure. All normal account/device/history writes require ACTIVE, so this
// closes the write race before the returned object capabilities are used.
func (s *Postgres) PrepareUserPortalAccountDeletion(ctx context.Context, processorID, requestID uuid.UUID, fence int64) ([]UserPortalAccountDeletionObject, error) {
	if s == nil || s.Pool == nil || processorID == uuid.Nil || requestID == uuid.Nil || fence < 1 {
		return nil, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	accountID, err := requireUserPortalAccountDeletionProcessor(ctx, tx, processorID, requestID, fence)
	if err != nil {
		return nil, err
	}
	var status, role string
	var isAdmin bool
	if err := tx.QueryRow(ctx, userPortalAccountDeletionUserLookupSQL, accountID).Scan(&status, &role, &isAdmin); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, ErrDenied
		}
		return nil, err
	}
	if role != "USER" || isAdmin || status == "DELETED" {
		return nil, ErrDenied
	}
	if status == "ACTIVE" {
		command, err := tx.Exec(ctx, userPortalAccountDeletionLockUserSQL, accountID)
		if err != nil {
			return nil, err
		}
		if command.RowsAffected() != 1 {
			return nil, ErrConflict
		}
		if _, err := tx.Exec(ctx, userPortalAccountDeletionLockOutboxSQL, accountID); err != nil {
			return nil, err
		}
	} else if status != "LOCKED" {
		return nil, ErrConflict
	}
	objects, err := loadUserPortalAccountDeletionObjects(ctx, tx, accountID)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return objects, nil
}

// FinalizeUserPortalAccountDeletion is the only successful terminal mutation.
// deletedObjects must be the exact preparation list whose idempotent external
// deletion already succeeded. A stale lease/fence or a changed object set
// aborts the transaction without marking the request completed.
func (s *Postgres) FinalizeUserPortalAccountDeletion(ctx context.Context, processorID, requestID uuid.UUID, fence int64, deletedObjects []UserPortalAccountDeletionObject, traceID string) error {
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
	currentObjects, err := loadUserPortalAccountDeletionObjects(ctx, tx, accountID)
	if err != nil {
		return err
	}
	if !sameUserPortalAccountDeletionObjects(accountID, currentObjects, deletedObjects) {
		return ErrConflict
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
	for _, statement := range userPortalAccountDeletionRedactCommerceStatements {
		if _, err := tx.Exec(ctx, statement, accountID); err != nil {
			return err
		}
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

func loadUserPortalAccountDeletionObjects(ctx context.Context, tx pgx.Tx, accountID uuid.UUID) ([]UserPortalAccountDeletionObject, error) {
	rows, err := tx.Query(ctx, userPortalAccountDeletionObjectsSQL, accountID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	objects := make([]UserPortalAccountDeletionObject, 0)
	seen := make(map[string]struct{})
	for rows.Next() {
		var kind string
		var key string
		var sourceID uuid.UUID
		var processingFence int64
		if err := rows.Scan(&kind, &key, &sourceID, &processingFence); err != nil {
			return nil, err
		}
		object := UserPortalAccountDeletionObject{Kind: UserPortalAccountDeletionObjectKind(kind), ObjectKey: key}
		if !validUserPortalAccountDeletionObject(accountID, sourceID, processingFence, object) {
			return nil, ErrDenied
		}
		canonical := string(object.Kind) + "\x00" + object.ObjectKey
		if _, exists := seen[canonical]; exists {
			continue
		}
		seen[canonical] = struct{}{}
		objects = append(objects, object)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	sort.Slice(objects, func(left, right int) bool {
		if objects[left].Kind != objects[right].Kind {
			return objects[left].Kind < objects[right].Kind
		}
		return objects[left].ObjectKey < objects[right].ObjectKey
	})
	return objects, nil
}

func validUserPortalAccountDeletionObject(accountID, sourceID uuid.UUID, processingFence int64, object UserPortalAccountDeletionObject) bool {
	if accountID == uuid.Nil || sourceID == uuid.Nil || len(object.ObjectKey) < 1 || len(object.ObjectKey) > 1024 ||
		!strings.HasPrefix(object.ObjectKey, "accounts/"+accountID.String()+"/") || strings.Contains(object.ObjectKey, "\\") {
		return false
	}
	for _, segment := range strings.Split(object.ObjectKey, "/") {
		if segment == "" || segment == ".." {
			return false
		}
	}
	switch object.Kind {
	case UserPortalAccountDeletionStoredObject:
		return processingFence == 0
	case UserPortalAccountDeletionPrivacyExport:
		return processingFence > 0 && validUserPortalExportArtifactKey(object.ObjectKey, accountID, sourceID, processingFence)
	default:
		return false
	}
}

func sameUserPortalAccountDeletionObjects(accountID uuid.UUID, current, deleted []UserPortalAccountDeletionObject) bool {
	if len(current) != len(deleted) {
		return false
	}
	canonical := func(objects []UserPortalAccountDeletionObject) ([]string, bool) {
		result := make([]string, 0, len(objects))
		seen := make(map[string]struct{}, len(objects))
		for _, object := range objects {
			if len(object.ObjectKey) < 1 || len(object.ObjectKey) > 1024 ||
				!strings.HasPrefix(object.ObjectKey, "accounts/"+accountID.String()+"/") ||
				(object.Kind != UserPortalAccountDeletionStoredObject && object.Kind != UserPortalAccountDeletionPrivacyExport) {
				return nil, false
			}
			value := string(object.Kind) + "\x00" + object.ObjectKey
			if _, exists := seen[value]; exists {
				return nil, false
			}
			seen[value] = struct{}{}
			result = append(result, value)
		}
		sort.Strings(result)
		return result, true
	}
	left, leftOK := canonical(current)
	right, rightOK := canonical(deleted)
	if !leftOK || !rightOK {
		return false
	}
	for index := range left {
		if left[index] != right[index] {
			return false
		}
	}
	return true
}

func userPortalDeletedIdentityDigest(accountID, requestID uuid.UUID) [sha256.Size]byte {
	return sha256.Sum256([]byte("termlink/deleted-account/v1\x00" + accountID.String() + "\x00" + requestID.String()))
}
