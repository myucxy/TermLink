package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const userPortalDeletionConfirmationTTL = 15 * time.Minute

const (
	userPortalIdentityHandleCipherSize    = 64
	userPortalIdentityHandleMaxKeyVersion = int16(32767)
)

type UserPortalPCControlPolicyInput struct {
	RemoteRoutingEnabled bool
	Mode                 ControlMode
	LocalPriority        bool
	ExpectedVersion      int64
}

type UserPortalAccountDeletion struct {
	ID                    uuid.UUID  `json:"id"`
	Status                string     `json:"status"`
	Version               int64      `json:"version"`
	RequestedAt           time.Time  `json:"requested_at"`
	ConfirmationExpiresAt time.Time  `json:"confirmation_expires_at"`
	ScheduledFor          *time.Time `json:"scheduled_for,omitempty"`
	RetentionDeadline     *time.Time `json:"retention_deadline,omitempty"`
	CompletedAt           *time.Time `json:"completed_at,omitempty"`
	CanceledAt            *time.Time `json:"canceled_at,omitempty"`
}

const userPortalDataExportInsertSQL = `
	INSERT INTO data_export_requests(id,account_id,scope)
	VALUES($1,$2,ARRAY['ACCOUNT_PROFILE','DEVICE_METADATA','SESSION_METADATA']::text[])
	ON CONFLICT (account_id) WHERE status IN ('REQUESTED','PROCESSING','READY') DO NOTHING
	RETURNING id,status,requested_at,expires_at`

const userPortalDataExportActiveSQL = `
	SELECT id,status,requested_at,expires_at
	FROM data_export_requests
	WHERE account_id=$1 AND status IN ('REQUESTED','PROCESSING','READY')
	ORDER BY created_at DESC,id DESC
	LIMIT 1`

const userPortalDataExportByAccountSQL = `
	SELECT id,status,requested_at,expires_at
	FROM data_export_requests
	WHERE id=$1 AND account_id=$2`

const userPortalDataExportAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id)
	VALUES($1,'user_portal.data_export.request','data_export',$2,'SUCCEEDED',$3)`

const userPortalDataExportOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES('privacy.data_export.requested',$1,
	       jsonb_build_object('account_id',$2::uuid,'request_id',$1::uuid))`

const userPortalAccountDeletionInsertSQL = `
	INSERT INTO account_deletion_requests(id,account_id,confirmation_expires_at)
	VALUES($1,$2,now()+($3::bigint*interval '1 millisecond'))
	ON CONFLICT (account_id) WHERE status IN ('REQUESTED','CONFIRMED','PROCESSING','FAILED') DO NOTHING
	RETURNING id,status,version,requested_at,confirmation_expires_at,scheduled_for,
	          retention_deadline,completed_at,canceled_at`

const userPortalAccountDeletionActiveSQL = `
	SELECT id,status,version,requested_at,confirmation_expires_at,scheduled_for,
	       retention_deadline,completed_at,canceled_at
	FROM account_deletion_requests
	WHERE account_id=$1 AND status IN ('REQUESTED','CONFIRMED','PROCESSING','FAILED')
	ORDER BY created_at DESC,id DESC
	LIMIT 1`

const userPortalAccountDeletionExpireSQL = `
	UPDATE account_deletion_requests
	SET status='CANCELED',canceled_at=now(),version=version+1,updated_at=now()
	WHERE account_id=$1 AND status='REQUESTED' AND confirmation_expires_at<=now()
	RETURNING id`

const userPortalAccountDeletionByAccountSQL = `
	SELECT id,status,version,requested_at,confirmation_expires_at,scheduled_for,
	       retention_deadline,completed_at,canceled_at
	FROM account_deletion_requests
	WHERE id=$1 AND account_id=$2`

const userPortalAccountDeletionByAccountForUpdateSQL = userPortalAccountDeletionByAccountSQL + ` FOR UPDATE`

const userPortalAccountDeletionIdentityLookupSQL = `
	SELECT id,status,version,requested_at,confirmation_expires_at,scheduled_for,
	       retention_deadline,completed_at,canceled_at,
	       identity_handle_cipher,identity_handle_key_version
	FROM account_deletion_requests
	WHERE id=$1 AND account_id=$2
	FOR UPDATE`

const userPortalAccountDeletionIdentityBindSQL = `
	UPDATE account_deletion_requests
	SET identity_handle_cipher=$3,identity_handle_key_version=$4,
	    version=version+1,updated_at=now()
	WHERE id=$1 AND account_id=$2 AND status='REQUESTED'
	  AND confirmation_expires_at>now()
	  AND identity_handle_cipher IS NULL AND identity_handle_key_version IS NULL
	RETURNING id,status,version,requested_at,confirmation_expires_at,scheduled_for,
	          retention_deadline,completed_at,canceled_at`

const userPortalAccountDeletionIdentityAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
	VALUES($1,'user_portal.account_deletion.identity.bind','account_deletion',$2,'SUCCEEDED',$3,
	       jsonb_build_object('identity_handle_key_version',$4::smallint))`

const userPortalAccountDeletionCancelSQL = `
	UPDATE account_deletion_requests
	SET status='CANCELED',canceled_at=now(),processor_id=NULL,processor_lease_until=NULL,
	    version=version+1,updated_at=now()
	WHERE id=$1 AND account_id=$2 AND status IN ('REQUESTED','CONFIRMED','FAILED')
	RETURNING id,status,version,requested_at,confirmation_expires_at,scheduled_for,
	          retention_deadline,completed_at,canceled_at`

const userPortalAccountDeletionRequestAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id)
	VALUES($1,'user_portal.account_deletion.request','account_deletion',$2,'SUCCEEDED',$3)`

const userPortalAccountDeletionCancelAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id)
	VALUES($1,'user_portal.account_deletion.cancel','account_deletion',$2,'SUCCEEDED',$3)`

const userPortalAccountDeletionExpireAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
	VALUES($1,'user_portal.account_deletion.expire','account_deletion',$2,'SUCCEEDED',$3,
	       jsonb_build_object('reason','CONFIRMATION_EXPIRED'))`

const userPortalAccountDeletionOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES($1,$2,jsonb_build_object('account_id',$3::uuid,'request_id',$2::uuid))`

const userPortalFullPCPolicyUpdateSQL = `
	UPDATE pcs
	SET remote_routing_enabled=$3,control_mode=$4,local_priority=$5,
	    policy_version=policy_version+1,updated_at=now()
	WHERE id=$1 AND owner_user_id=$2 AND policy_version=$6
	RETURNING name,remote_routing_enabled,control_mode,local_priority,policy_version`

const userPortalFullPCPolicyAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
	VALUES($1,'user_portal.pc.control_policy.update','pc',$2,'SUCCEEDED',$3,
	       jsonb_build_object(
	           'old_remote_routing_enabled',$4::boolean,'new_remote_routing_enabled',$5::boolean,
	           'old_mode',$6::text,'new_mode',$7::text,
	           'old_local_priority',$8::boolean,'new_local_priority',$9::boolean,
	           'old_policy_version',$10::bigint,'new_policy_version',$11::bigint))`

const userPortalFullPCPolicyOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES('grant.updated',$1,jsonb_build_object(
	    'pc_id',$2::uuid,'remote_routing_enabled',$3::boolean,
	    'control_mode',$4::text,'local_priority',$5::boolean,'policy_version',$6::bigint))`

func scanUserPortalDataExport(row pgx.Row) (UserPortalDataExport, error) {
	var result UserPortalDataExport
	err := row.Scan(&result.ID, &result.Status, &result.RequestedAt, &result.ExpiresAt)
	return result, err
}

func scanUserPortalAccountDeletion(row pgx.Row) (UserPortalAccountDeletion, error) {
	var result UserPortalAccountDeletion
	err := row.Scan(
		&result.ID, &result.Status, &result.Version, &result.RequestedAt,
		&result.ConfirmationExpiresAt, &result.ScheduledFor, &result.RetentionDeadline,
		&result.CompletedAt, &result.CanceledAt,
	)
	return result, err
}

func (s *Postgres) requestUserPortalDataExport(ctx context.Context, accountID uuid.UUID, idempotencyKey, traceID string) (UserPortalDataExport, error) {
	if err := validateUserPortalActionInput(accountID, idempotencyKey, traceID); err != nil {
		return UserPortalDataExport{}, err
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return UserPortalDataExport{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireUserPortalActionAccount(ctx, tx, accountID); err != nil {
		return UserPortalDataExport{}, err
	}
	hash := userPortalPrivacyActionHash("data-export-request-v1", accountID.String())
	var result UserPortalDataExport
	replayed, err := claimUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, &result)
	if err != nil {
		return UserPortalDataExport{}, err
	}
	if replayed {
		return result, tx.Commit(ctx)
	}
	requestID := uuid.New()
	result, err = scanUserPortalDataExport(tx.QueryRow(ctx, userPortalDataExportInsertSQL, requestID, accountID))
	created := err == nil
	if errors.Is(err, pgx.ErrNoRows) {
		result, err = scanUserPortalDataExport(tx.QueryRow(ctx, userPortalDataExportActiveSQL, accountID))
	}
	if err != nil {
		return UserPortalDataExport{}, err
	}
	if created {
		if _, err := tx.Exec(ctx, userPortalDataExportAuditSQL, accountID, result.ID, traceID); err != nil {
			return UserPortalDataExport{}, err
		}
		if _, err := tx.Exec(ctx, userPortalDataExportOutboxSQL, result.ID, accountID); err != nil {
			return UserPortalDataExport{}, err
		}
	}
	if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
		return UserPortalDataExport{}, err
	}
	return result, tx.Commit(ctx)
}

func (s *Postgres) getUserPortalDataExport(ctx context.Context, accountID, exportID uuid.UUID) (UserPortalDataExport, error) {
	if accountID == uuid.Nil || exportID == uuid.Nil {
		return UserPortalDataExport{}, ErrDenied
	}
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return UserPortalDataExport{}, err
	}
	result, err := scanUserPortalDataExport(s.Pool.QueryRow(ctx, userPortalDataExportByAccountSQL, exportID, accountID))
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalDataExport{}, ErrDenied
	}
	return result, err
}

func (s *Postgres) requestUserPortalAccountDeletion(ctx context.Context, accountID uuid.UUID, idempotencyKey, traceID string) (UserPortalAccountDeletion, error) {
	if err := validateUserPortalActionInput(accountID, idempotencyKey, traceID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireUserPortalActionAccount(ctx, tx, accountID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	hash := userPortalPrivacyActionHash("account-deletion-request-v1", accountID.String())
	var result UserPortalAccountDeletion
	replayed, err := claimUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, &result)
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if replayed {
		return result, tx.Commit(ctx)
	}
	var expiredRequestID uuid.UUID
	expireErr := tx.QueryRow(ctx, userPortalAccountDeletionExpireSQL, accountID).Scan(&expiredRequestID)
	if expireErr == nil {
		if _, err := tx.Exec(ctx, userPortalAccountDeletionExpireAuditSQL, accountID, expiredRequestID, traceID); err != nil {
			return UserPortalAccountDeletion{}, err
		}
		if _, err := tx.Exec(ctx, userPortalAccountDeletionOutboxSQL, "privacy.account_deletion.canceled", expiredRequestID, accountID); err != nil {
			return UserPortalAccountDeletion{}, err
		}
	} else if !errors.Is(expireErr, pgx.ErrNoRows) {
		return UserPortalAccountDeletion{}, expireErr
	}
	result, err = scanUserPortalAccountDeletion(tx.QueryRow(ctx, userPortalAccountDeletionActiveSQL, accountID))
	if err == nil {
		if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
			return UserPortalAccountDeletion{}, err
		}
		return result, tx.Commit(ctx)
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return UserPortalAccountDeletion{}, err
	}
	if err := requireUserPortalDeletionReady(ctx, tx, accountID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	requestID := uuid.New()
	result, err = scanUserPortalAccountDeletion(tx.QueryRow(
		ctx, userPortalAccountDeletionInsertSQL, requestID, accountID, userPortalDeletionConfirmationTTL.Milliseconds(),
	))
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if _, err := tx.Exec(ctx, userPortalAccountDeletionRequestAuditSQL, accountID, result.ID, traceID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if _, err := tx.Exec(ctx, userPortalAccountDeletionOutboxSQL, "privacy.account_deletion.requested", result.ID, accountID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	return result, tx.Commit(ctx)
}

// RequestUserPortalAccountDeletionState is the state-returning form for Web
// and API adapters. RequestUserPortalAccountDeletion remains source compatible
// with callers that only need success/failure.
func (s *Postgres) RequestUserPortalAccountDeletionState(ctx context.Context, accountID uuid.UUID, idempotencyKey, traceID string) (UserPortalAccountDeletion, error) {
	return s.requestUserPortalAccountDeletion(ctx, accountID, idempotencyKey, traceID)
}

// BindUserPortalAccountDeletionIdentityHandle atomically attaches the opaque
// AEAD ciphertext while the request is still REQUESTED. Binding the exact
// same operation key/account/request/key-version replays even if a caller had
// to regenerate randomized AES-GCM ciphertext after losing the first response.
// With a new operation key, only the exact stored ciphertext/version is
// idempotent; replacement is denied so identity cannot drift after binding.
func (s *Postgres) BindUserPortalAccountDeletionIdentityHandle(ctx context.Context, accountID, requestID uuid.UUID, identityHandleCipher []byte, keyVersion int16, idempotencyKey, traceID string) (UserPortalAccountDeletion, error) {
	if requestID == uuid.Nil || len(identityHandleCipher) != userPortalIdentityHandleCipherSize ||
		keyVersion < 1 || keyVersion > userPortalIdentityHandleMaxKeyVersion ||
		validateUserPortalActionInput(accountID, idempotencyKey, traceID) != nil {
		return UserPortalAccountDeletion{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireUserPortalActionAccount(ctx, tx, accountID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	hash := userPortalPrivacyActionHash(
		"account-deletion-identity-bind-v1", accountID.String(), requestID.String(),
		strconv.FormatInt(int64(keyVersion), 10),
	)
	var result UserPortalAccountDeletion
	replayed, err := claimUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, &result)
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if replayed {
		return result, tx.Commit(ctx)
	}
	var currentCipher []byte
	var currentKeyVersion *int16
	err = tx.QueryRow(ctx, userPortalAccountDeletionIdentityLookupSQL, requestID, accountID).Scan(
		&result.ID, &result.Status, &result.Version, &result.RequestedAt,
		&result.ConfirmationExpiresAt, &result.ScheduledFor, &result.RetentionDeadline,
		&result.CompletedAt, &result.CanceledAt, &currentCipher, &currentKeyVersion,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalAccountDeletion{}, ErrDenied
	}
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if len(currentCipher) != 0 || currentKeyVersion != nil {
		if currentKeyVersion == nil || *currentKeyVersion != keyVersion || !equalBytes(currentCipher, identityHandleCipher) {
			return UserPortalAccountDeletion{}, ErrConflict
		}
		if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
			return UserPortalAccountDeletion{}, err
		}
		return result, tx.Commit(ctx)
	}
	if result.Status != "REQUESTED" || !result.ConfirmationExpiresAt.After(time.Now()) {
		return UserPortalAccountDeletion{}, ErrConflict
	}
	result, err = scanUserPortalAccountDeletion(tx.QueryRow(
		ctx, userPortalAccountDeletionIdentityBindSQL, requestID, accountID, identityHandleCipher, keyVersion,
	))
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalAccountDeletion{}, ErrConflict
	}
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if _, err := tx.Exec(ctx, userPortalAccountDeletionIdentityAuditSQL, accountID, requestID, traceID, keyVersion); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	return result, tx.Commit(ctx)
}

func (s *Postgres) GetUserPortalAccountDeletion(ctx context.Context, accountID, requestID uuid.UUID) (UserPortalAccountDeletion, error) {
	if accountID == uuid.Nil || requestID == uuid.Nil {
		return UserPortalAccountDeletion{}, ErrDenied
	}
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	result, err := scanUserPortalAccountDeletion(s.Pool.QueryRow(ctx, userPortalAccountDeletionByAccountSQL, requestID, accountID))
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalAccountDeletion{}, ErrDenied
	}
	return result, err
}

func (s *Postgres) CancelUserPortalAccountDeletion(ctx context.Context, accountID, requestID uuid.UUID, idempotencyKey, traceID string) (UserPortalAccountDeletion, error) {
	if requestID == uuid.Nil || validateUserPortalActionInput(accountID, idempotencyKey, traceID) != nil {
		return UserPortalAccountDeletion{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireUserPortalActionAccount(ctx, tx, accountID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	hash := userPortalPrivacyActionHash("account-deletion-cancel-v1", accountID.String(), requestID.String())
	var result UserPortalAccountDeletion
	replayed, err := claimUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, &result)
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if replayed {
		return result, tx.Commit(ctx)
	}
	current, err := scanUserPortalAccountDeletion(tx.QueryRow(ctx, userPortalAccountDeletionByAccountForUpdateSQL, requestID, accountID))
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalAccountDeletion{}, ErrDenied
	}
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	changed := current.Status != "CANCELED"
	if changed {
		result, err = scanUserPortalAccountDeletion(tx.QueryRow(ctx, userPortalAccountDeletionCancelSQL, requestID, accountID))
		if errors.Is(err, pgx.ErrNoRows) {
			return UserPortalAccountDeletion{}, ErrConflict
		}
		if err != nil {
			return UserPortalAccountDeletion{}, err
		}
		if _, err := tx.Exec(ctx, userPortalAccountDeletionCancelAuditSQL, accountID, requestID, traceID); err != nil {
			return UserPortalAccountDeletion{}, err
		}
		if _, err := tx.Exec(ctx, userPortalAccountDeletionOutboxSQL, "privacy.account_deletion.canceled", requestID, accountID); err != nil {
			return UserPortalAccountDeletion{}, err
		}
	} else {
		result = current
	}
	if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	return result, tx.Commit(ctx)
}

func requireUserPortalDeletionReady(ctx context.Context, tx pgx.Tx, accountID uuid.UUID) error {
	var liveSessions int
	err := tx.QueryRow(ctx, `
		SELECT count(*) FROM sessions session JOIN pcs pc ON pc.id=session.pc_id
		WHERE pc.owner_user_id=$1 AND session.state IN ('RUNNING','INTERRUPTED')`, accountID).Scan(&liveSessions)
	if err != nil {
		return err
	}
	if liveSessions != 0 {
		return ErrConflict
	}
	return nil
}

func (s *Postgres) SetUserPortalPCControlPolicy(ctx context.Context, accountID, pcID uuid.UUID, input UserPortalPCControlPolicyInput, idempotencyKey, traceID string) (UserPortalPCControlPolicy, error) {
	if pcID == uuid.Nil || !validControlMode(input.Mode) || input.ExpectedVersion < 1 ||
		validateUserPortalActionInput(accountID, idempotencyKey, traceID) != nil {
		return UserPortalPCControlPolicy{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireUserPortalActionAccount(ctx, tx, accountID); err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	hash := userPortalPrivacyActionHash(
		"pc-control-policy-v1", accountID.String(), pcID.String(),
		strconv.FormatBool(input.RemoteRoutingEnabled), string(input.Mode),
		strconv.FormatBool(input.LocalPriority), strconv.FormatInt(input.ExpectedVersion, 10),
	)
	var result UserPortalPCControlPolicy
	replayed, err := claimUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, &result)
	if err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	if replayed {
		return result, tx.Commit(ctx)
	}
	var deviceID uuid.UUID
	err = tx.QueryRow(ctx, userPortalPCLookupSQL, pcID, accountID).Scan(
		&deviceID, &result.Name, &result.RemoteRoutingEnabled, &result.Mode,
		&result.LocalPriority, &result.PolicyVersion,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalPCControlPolicy{}, ErrDenied
	}
	if err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	result.PCID = pcID
	if result.PolicyVersion != input.ExpectedVersion {
		return UserPortalPCControlPolicy{}, ErrConflict
	}
	old := result
	changed := result.RemoteRoutingEnabled != input.RemoteRoutingEnabled || result.Mode != input.Mode || result.LocalPriority != input.LocalPriority
	if changed {
		result, err = updateUserPortalPCControlPolicy(ctx, tx, accountID, pcID, input, result.Name)
		if err != nil {
			return UserPortalPCControlPolicy{}, err
		}
		if _, err := tx.Exec(ctx, userPortalFullPCPolicyAuditSQL,
			accountID, pcID, traceID, old.RemoteRoutingEnabled, result.RemoteRoutingEnabled,
			old.Mode, result.Mode, old.LocalPriority, result.LocalPriority,
			old.PolicyVersion, result.PolicyVersion,
		); err != nil {
			return UserPortalPCControlPolicy{}, err
		}
		if _, err := tx.Exec(ctx, userPortalFullPCPolicyOutboxSQL,
			deviceID, pcID, result.RemoteRoutingEnabled, result.Mode, result.LocalPriority, result.PolicyVersion,
		); err != nil {
			return UserPortalPCControlPolicy{}, err
		}
	}
	if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	return result, tx.Commit(ctx)
}

func updateUserPortalPCControlPolicy(ctx context.Context, tx pgx.Tx, accountID, pcID uuid.UUID, input UserPortalPCControlPolicyInput, name string) (UserPortalPCControlPolicy, error) {
	result := UserPortalPCControlPolicy{PCID: pcID, Name: name}
	err := tx.QueryRow(ctx, userPortalFullPCPolicyUpdateSQL,
		pcID, accountID, input.RemoteRoutingEnabled, input.Mode, input.LocalPriority, input.ExpectedVersion,
	).Scan(&result.Name, &result.RemoteRoutingEnabled, &result.Mode, &result.LocalPriority, &result.PolicyVersion)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalPCControlPolicy{}, ErrConflict
	}
	return result, err
}

func validControlMode(mode ControlMode) bool {
	return mode == ControlModeSingle || mode == ControlModeTakeover || mode == ControlModeCollaborative
}

func userPortalPrivacyActionHash(operation string, values ...string) [32]byte {
	var canonical strings.Builder
	canonical.WriteString(operation)
	for _, value := range values {
		canonical.WriteByte(0)
		canonical.WriteString(value)
	}
	return sha256.Sum256([]byte(canonical.String()))
}
