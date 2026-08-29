package store

import (
	"context"
	"errors"
	"regexp"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const (
	userPortalPrivacyProcessorMaxBatch = 100
	userPortalPrivacyLeaseMinimum      = 5 * time.Second
	userPortalPrivacyLeaseMaximum      = 10 * time.Minute
	userPortalSHA256DigestSize         = 32
)

var userPortalPrivacyErrorCodePattern = regexp.MustCompile(`^[A-Z][A-Z0-9_]{0,127}$`)

type UserPortalDataExportJob struct {
	ID              uuid.UUID `json:"-"`
	AccountID       uuid.UUID `json:"-"`
	Scope           []string  `json:"-"`
	ProcessingFence int64     `json:"-"`
	AttemptCount    int       `json:"-"`
	RequestedAt     time.Time `json:"-"`
	LeaseUntil      time.Time `json:"-"`
}

type UserPortalDataExportCompletion struct {
	ArtifactObjectKey string
	ArtifactSHA256    []byte
	ArtifactSizeBytes int64
	ExpiresAt         time.Time
}

type UserPortalAccountDeletionJob struct {
	ID                       uuid.UUID `json:"-"`
	AccountID                uuid.UUID `json:"-"`
	IdentityHandle           []byte    `json:"-"`
	IdentityHandleKeyVersion int16     `json:"-"`
	ProcessingFence          int64     `json:"-"`
	AttemptCount             int       `json:"-"`
	ScheduledFor             time.Time `json:"-"`
	RetentionDeadline        time.Time `json:"-"`
	LeaseUntil               time.Time `json:"-"`
}

const userPortalAccountDeletionConfirmSQL = `
	UPDATE account_deletion_requests
	SET status='CONFIRMED',confirmed_at=now(),scheduled_for=$3,retention_deadline=$4,
	    version=version+1,updated_at=now()
	WHERE id=$1 AND account_id=$2 AND status='REQUESTED' AND confirmation_expires_at>now()
	  AND $3::timestamptz>=now()
	  AND identity_handle_cipher IS NOT NULL AND identity_handle_key_version IS NOT NULL
	RETURNING id,status,version,requested_at,confirmation_expires_at,scheduled_for,
	          retention_deadline,completed_at,canceled_at`

const userPortalAccountDeletionConfirmAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
	VALUES($1,'user_portal.account_deletion.confirm','account_deletion',$2,'SUCCEEDED',$3,
	       jsonb_build_object('scheduled_for',$4::timestamptz,'retention_deadline',$5::timestamptz))`

const userPortalDataExportClaimSQL = `
	WITH candidates AS (
		SELECT id FROM data_export_requests
		WHERE status='REQUESTED' OR (status='PROCESSING' AND processor_lease_until<=now())
		ORDER BY requested_at,id
		FOR UPDATE SKIP LOCKED
		LIMIT $3
	)
	UPDATE data_export_requests request
	SET status='PROCESSING',processor_id=$1,
	    processor_lease_until=now()+($2::bigint*interval '1 millisecond'),
	    processing_started_at=coalesce(request.processing_started_at,now()),
	    processing_fence=request.processing_fence+1,attempt_count=request.attempt_count+1,
	    version=request.version+1,updated_at=now()
	FROM candidates
	WHERE request.id=candidates.id
	RETURNING request.id,request.account_id,request.scope,request.processing_fence,
	          request.attempt_count,request.requested_at,request.processor_lease_until`

const userPortalDataExportCompleteSQL = `
	UPDATE data_export_requests
	SET status='READY',artifact_object_key=$4,artifact_sha256=$5,artifact_size_bytes=$6,
	    completed_at=now(),expires_at=$7,processor_id=NULL,processor_lease_until=NULL,
	    version=version+1,updated_at=now()
	WHERE id=$1 AND processor_id=$2 AND processing_fence=$3 AND status='PROCESSING'
	  AND processor_lease_until>now()
	RETURNING account_id`

const userPortalDataExportCompleteReplaySQL = `
	SELECT account_id FROM data_export_requests
	WHERE id=$1 AND processing_fence=$2 AND status='READY'
	  AND artifact_object_key=$3 AND artifact_sha256=$4
	  AND artifact_size_bytes=$5 AND expires_at=$6`

const userPortalDataExportFailSQL = `
	UPDATE data_export_requests
	SET status='FAILED',error_code=$4,failed_at=now(),processor_id=NULL,processor_lease_until=NULL,
	    version=version+1,updated_at=now()
	WHERE id=$1 AND processor_id=$2 AND processing_fence=$3 AND status='PROCESSING'
	  AND processor_lease_until>now()
	RETURNING account_id`

const userPortalDataExportFailReplaySQL = `
	SELECT account_id FROM data_export_requests
	WHERE id=$1 AND processing_fence=$2 AND status='FAILED' AND error_code=$3`

const userPortalDataExportProcessorAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
	VALUES($1,$2,'data_export',$3,$4,$5,jsonb_build_object('processing_fence',$6::bigint))`

const userPortalDataExportProcessorOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES($1,$2,jsonb_build_object('account_id',$3::uuid,'request_id',$2::uuid))`

const userPortalAccountDeletionClaimSQL = `
	WITH candidates AS (
		SELECT id FROM account_deletion_requests
		WHERE (
			(status='CONFIRMED' AND scheduled_for<=now())
			OR (status='PROCESSING' AND processor_lease_until<=now())
			OR status='FAILED'
		) AND identity_handle_cipher IS NOT NULL AND identity_handle_key_version IS NOT NULL
		ORDER BY scheduled_for NULLS LAST,requested_at,id
		FOR UPDATE SKIP LOCKED
		LIMIT $3
	)
	UPDATE account_deletion_requests request
	SET status='PROCESSING',processor_id=$1,
	    processor_lease_until=now()+($2::bigint*interval '1 millisecond'),
	    processing_started_at=coalesce(request.processing_started_at,now()),
	    processing_fence=request.processing_fence+1,attempt_count=request.attempt_count+1,
	    error_code=NULL,failed_at=NULL,version=request.version+1,updated_at=now()
	FROM candidates
	WHERE request.id=candidates.id
	RETURNING request.id,request.account_id,request.processing_fence,request.attempt_count,
	          request.scheduled_for,request.retention_deadline,request.processor_lease_until,
	          request.identity_handle_cipher,request.identity_handle_key_version`

const userPortalAccountDeletionCompleteSQL = `
	UPDATE account_deletion_requests
	SET status='COMPLETED',completed_at=now(),processor_id=NULL,processor_lease_until=NULL,
	    version=version+1,updated_at=now()
	WHERE id=$1 AND processor_id=$2 AND processing_fence=$3 AND status='PROCESSING'
	  AND processor_lease_until>now()
	RETURNING account_id`

const userPortalAccountDeletionCompleteReplaySQL = `
	SELECT account_id FROM account_deletion_requests
	WHERE id=$1 AND processing_fence=$2 AND status='COMPLETED'`

const userPortalAccountDeletionFailSQL = `
	UPDATE account_deletion_requests
	SET status='FAILED',error_code=$4,failed_at=now(),processor_id=NULL,processor_lease_until=NULL,
	    version=version+1,updated_at=now()
	WHERE id=$1 AND processor_id=$2 AND processing_fence=$3 AND status='PROCESSING'
	  AND processor_lease_until>now()
	RETURNING account_id`

const userPortalAccountDeletionFailReplaySQL = `
	SELECT account_id FROM account_deletion_requests
	WHERE id=$1 AND processing_fence=$2 AND status='FAILED' AND error_code=$3`

const userPortalAccountDeletionProcessorAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
	VALUES($1,$2,'account_deletion',$3,$4,$5,jsonb_build_object('processing_fence',$6::bigint))`

func (s *Postgres) ConfirmUserPortalAccountDeletion(ctx context.Context, accountID, requestID uuid.UUID, scheduledFor, retentionDeadline time.Time, idempotencyKey, traceID string) (UserPortalAccountDeletion, error) {
	if requestID == uuid.Nil || scheduledFor.IsZero() || retentionDeadline.IsZero() || retentionDeadline.Before(scheduledFor) ||
		validateUserPortalActionInput(accountID, idempotencyKey, traceID) != nil {
		return UserPortalAccountDeletion{}, ErrDenied
	}
	scheduledFor = scheduledFor.UTC()
	retentionDeadline = retentionDeadline.UTC()
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireUserPortalActionAccount(ctx, tx, accountID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	hash := userPortalPrivacyActionHash(
		"account-deletion-confirm-v1", accountID.String(), requestID.String(),
		scheduledFor.Format(time.RFC3339Nano), retentionDeadline.Format(time.RFC3339Nano),
	)
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
	if current.Status == "CONFIRMED" && sameTime(current.ScheduledFor, &scheduledFor) && sameTime(current.RetentionDeadline, &retentionDeadline) {
		if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, current); err != nil {
			return UserPortalAccountDeletion{}, err
		}
		return current, tx.Commit(ctx)
	}
	if current.Status != "REQUESTED" {
		return UserPortalAccountDeletion{}, ErrConflict
	}
	if err := requireUserPortalDeletionReady(ctx, tx, accountID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	result, err = scanUserPortalAccountDeletion(tx.QueryRow(
		ctx, userPortalAccountDeletionConfirmSQL, requestID, accountID, scheduledFor, retentionDeadline,
	))
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalAccountDeletion{}, ErrConflict
	}
	if err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if _, err := tx.Exec(ctx, userPortalAccountDeletionConfirmAuditSQL,
		accountID, requestID, traceID, scheduledFor, retentionDeadline,
	); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if _, err := tx.Exec(ctx, userPortalAccountDeletionOutboxSQL, "privacy.account_deletion.confirmed", requestID, accountID); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
		return UserPortalAccountDeletion{}, err
	}
	return result, tx.Commit(ctx)
}

func (s *Postgres) ClaimUserPortalDataExports(ctx context.Context, processorID uuid.UUID, lease time.Duration, limit int) ([]UserPortalDataExportJob, error) {
	if processorID == uuid.Nil || !validUserPortalPrivacyLease(lease) || limit < 1 || limit > userPortalPrivacyProcessorMaxBatch {
		return nil, ErrDenied
	}
	rows, err := s.Pool.Query(ctx, userPortalDataExportClaimSQL, processorID, lease.Milliseconds(), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]UserPortalDataExportJob, 0, limit)
	for rows.Next() {
		var item UserPortalDataExportJob
		if err := rows.Scan(&item.ID, &item.AccountID, &item.Scope, &item.ProcessingFence,
			&item.AttemptCount, &item.RequestedAt, &item.LeaseUntil); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) CompleteUserPortalDataExport(ctx context.Context, processorID, requestID uuid.UUID, fence int64, completion UserPortalDataExportCompletion, traceID string) error {
	if processorID == uuid.Nil || requestID == uuid.Nil || fence < 1 || !validUserPortalArtifact(completion) || !validUserPortalProcessorTrace(traceID) {
		return ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var accountID uuid.UUID
	err = tx.QueryRow(ctx, userPortalDataExportCompleteSQL,
		requestID, processorID, fence, completion.ArtifactObjectKey, completion.ArtifactSHA256,
		completion.ArtifactSizeBytes, completion.ExpiresAt.UTC(),
	).Scan(&accountID)
	if errors.Is(err, pgx.ErrNoRows) {
		if replayErr := tx.QueryRow(ctx, userPortalDataExportCompleteReplaySQL,
			requestID, fence, completion.ArtifactObjectKey, completion.ArtifactSHA256,
			completion.ArtifactSizeBytes, completion.ExpiresAt.UTC(),
		).Scan(&accountID); replayErr == nil {
			return tx.Commit(ctx)
		} else if !errors.Is(replayErr, pgx.ErrNoRows) {
			return replayErr
		}
		return ErrConflict
	}
	if err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, userPortalDataExportProcessorAuditSQL,
		accountID, "user_portal.data_export.ready", requestID, "SUCCEEDED", traceID, fence,
	); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, userPortalDataExportProcessorOutboxSQL,
		"privacy.data_export.ready", requestID, accountID,
	); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) FailUserPortalDataExport(ctx context.Context, processorID, requestID uuid.UUID, fence int64, errorCode, traceID string) error {
	return s.finishUserPortalDataExportFailure(ctx, processorID, requestID, fence, errorCode, traceID)
}

func (s *Postgres) finishUserPortalDataExportFailure(ctx context.Context, processorID, requestID uuid.UUID, fence int64, errorCode, traceID string) error {
	if processorID == uuid.Nil || requestID == uuid.Nil || fence < 1 || !validUserPortalErrorCode(errorCode) || !validUserPortalProcessorTrace(traceID) {
		return ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var accountID uuid.UUID
	if err := tx.QueryRow(ctx, userPortalDataExportFailSQL, requestID, processorID, fence, errorCode).Scan(&accountID); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			if replayErr := tx.QueryRow(ctx, userPortalDataExportFailReplaySQL, requestID, fence, errorCode).Scan(&accountID); replayErr == nil {
				return tx.Commit(ctx)
			} else if !errors.Is(replayErr, pgx.ErrNoRows) {
				return replayErr
			}
			return ErrConflict
		}
		return err
	}
	if _, err := tx.Exec(ctx, userPortalDataExportProcessorAuditSQL,
		accountID, "user_portal.data_export.fail", requestID, "FAILED", traceID, fence,
	); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, userPortalDataExportProcessorOutboxSQL,
		"privacy.data_export.failed", requestID, accountID,
	); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) ClaimUserPortalAccountDeletions(ctx context.Context, processorID uuid.UUID, lease time.Duration, limit int) ([]UserPortalAccountDeletionJob, error) {
	if processorID == uuid.Nil || !validUserPortalPrivacyLease(lease) || limit < 1 || limit > userPortalPrivacyProcessorMaxBatch {
		return nil, ErrDenied
	}
	rows, err := s.Pool.Query(ctx, userPortalAccountDeletionClaimSQL, processorID, lease.Milliseconds(), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]UserPortalAccountDeletionJob, 0, limit)
	for rows.Next() {
		var item UserPortalAccountDeletionJob
		if err := rows.Scan(&item.ID, &item.AccountID, &item.ProcessingFence, &item.AttemptCount,
			&item.ScheduledFor, &item.RetentionDeadline, &item.LeaseUntil,
			&item.IdentityHandle, &item.IdentityHandleKeyVersion); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

// CompleteUserPortalAccountDeletion is retained for processors whose account
// has no object-store artifacts. General processors must call Prepare, delete
// every returned capability, then call Finalize with that exact list.
func (s *Postgres) CompleteUserPortalAccountDeletion(ctx context.Context, processorID, requestID uuid.UUID, fence int64, traceID string) error {
	objects, err := s.PrepareUserPortalAccountDeletion(ctx, processorID, requestID, fence)
	if err != nil {
		// Finalize performs the completed-state replay check. This keeps a lost
		// successful response idempotent without weakening processor fencing.
		if errors.Is(err, ErrConflict) {
			return s.FinalizeUserPortalAccountDeletion(ctx, processorID, requestID, fence, nil, traceID)
		}
		return err
	}
	if len(objects) != 0 {
		return ErrConflict
	}
	return s.FinalizeUserPortalAccountDeletion(ctx, processorID, requestID, fence, nil, traceID)
}

func (s *Postgres) FailUserPortalAccountDeletion(ctx context.Context, processorID, requestID uuid.UUID, fence int64, errorCode, traceID string) error {
	if processorID == uuid.Nil || requestID == uuid.Nil || fence < 1 || !validUserPortalErrorCode(errorCode) || !validUserPortalProcessorTrace(traceID) {
		return ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var accountID uuid.UUID
	if err := tx.QueryRow(ctx, userPortalAccountDeletionFailSQL, requestID, processorID, fence, errorCode).Scan(&accountID); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			if replayErr := tx.QueryRow(ctx, userPortalAccountDeletionFailReplaySQL, requestID, fence, errorCode).Scan(&accountID); replayErr == nil {
				return tx.Commit(ctx)
			} else if !errors.Is(replayErr, pgx.ErrNoRows) {
				return replayErr
			}
			return ErrConflict
		}
		return err
	}
	if _, err := tx.Exec(ctx, userPortalAccountDeletionProcessorAuditSQL,
		accountID, "user_portal.account_deletion.fail", requestID, "FAILED", traceID, fence,
	); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, userPortalAccountDeletionOutboxSQL,
		"privacy.account_deletion.failed", requestID, accountID,
	); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func validUserPortalPrivacyLease(lease time.Duration) bool {
	return lease >= userPortalPrivacyLeaseMinimum && lease <= userPortalPrivacyLeaseMaximum
}

func validUserPortalArtifact(completion UserPortalDataExportCompletion) bool {
	key := completion.ArtifactObjectKey
	if len(key) < 1 || len(key) > 512 || len(completion.ArtifactSHA256) != userPortalSHA256DigestSize ||
		completion.ArtifactSizeBytes < 0 || completion.ExpiresAt.IsZero() || !completion.ExpiresAt.After(time.Now()) {
		return false
	}
	if !isASCIIAlphaNumeric(key[0]) || strings.Contains(key, "\\") {
		return false
	}
	for _, segment := range strings.Split(key, "/") {
		if segment == "" || segment == ".." {
			return false
		}
	}
	for _, character := range key {
		if character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
			character >= '0' && character <= '9' || strings.ContainsRune("._/-", character) {
			continue
		}
		return false
	}
	return true
}

func isASCIIAlphaNumeric(character byte) bool {
	return character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
		character >= '0' && character <= '9'
}

func validUserPortalErrorCode(code string) bool {
	return userPortalPrivacyErrorCodePattern.MatchString(code)
}

func validUserPortalProcessorTrace(traceID string) bool {
	return strings.TrimSpace(traceID) != "" && len(traceID) <= 256
}
