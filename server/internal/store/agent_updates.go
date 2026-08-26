package store

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
)

const (
	AgentUpdatePending        = "PENDING"
	AgentUpdateProcessing     = "PROCESSING"
	AgentUpdateSucceeded      = "SUCCEEDED"
	AgentUpdateFailed         = "FAILED"
	AgentUpdateCancelled      = "CANCELLED"
	AgentUpdateExpired        = "EXPIRED"
	AgentUpdateRolledBack     = "ROLLED_BACK"
	AgentUpdateRollbackFailed = "ROLLBACK_FAILED"

	AgentUpdateRequested           = "REQUESTED"
	AgentUpdatePrechecking         = "PRECHECKING"
	AgentUpdateDownloading         = "DOWNLOADING"
	AgentUpdateVerified            = "VERIFIED"
	AgentUpdateDrainingSessions    = "DRAINING_SESSIONS"
	AgentUpdateInstalling          = "INSTALLING"
	AgentUpdateStartingNewAgent    = "STARTING_NEW_AGENT"
	AgentUpdateVerifyingConnection = "VERIFYING_CONNECTION"
	AgentUpdateRollingBack         = "ROLLING_BACK"
)

type AgentUpdateCreateInput struct {
	CurrentVersion            string
	CurrentBuild              int64
	TargetVersion             string
	TargetBuild               int64
	ArtifactURL               string
	ArtifactFilename          string
	ArtifactSizeBytes         int64
	ArtifactSHA256            [32]byte
	ArtifactSignerFingerprint *string
	ExpiresAt                 time.Time
}

type AgentUpdateProgressInput struct {
	Phase              string
	ProgressPercent    int
	ActiveSessionCount *int
}

type AgentUpdateCompleteInput struct {
	Status               string
	ErrorCode            *string
	ErrorDetail          *string
	ObservedAgentVersion *string
	ObservedAgentBuild   *int64
}

type AgentUpdateRecord struct {
	ID                        uuid.UUID  `json:"id"`
	AccountID                 uuid.UUID  `json:"-"`
	PCID                      uuid.UUID  `json:"pc_id"`
	RequesterDeviceID         uuid.UUID  `json:"requester_device_id"`
	CurrentVersion            string     `json:"current_version"`
	CurrentBuild              int64      `json:"current_build"`
	TargetVersion             string     `json:"target_version"`
	TargetBuild               int64      `json:"target_build"`
	ArtifactURL               string     `json:"artifact_url"`
	ArtifactFilename          string     `json:"artifact_filename"`
	ArtifactSizeBytes         int64      `json:"artifact_size_bytes"`
	ArtifactSHA256            []byte     `json:"-"`
	ArtifactSignerFingerprint *string    `json:"artifact_signer_fingerprint,omitempty"`
	Status                    string     `json:"status"`
	Phase                     string     `json:"phase"`
	ProgressPercent           int        `json:"progress_percent"`
	ActiveSessionCount        *int       `json:"active_session_count,omitempty"`
	AttemptCount              int        `json:"attempt_count"`
	LeaseTokenHash            []byte     `json:"-"`
	LeaseExpiresAt            *time.Time `json:"lease_expires_at,omitempty"`
	ErrorCode                 *string    `json:"error_code,omitempty"`
	ErrorDetail               *string    `json:"error_detail,omitempty"`
	ObservedAgentVersion      *string    `json:"observed_agent_version,omitempty"`
	ObservedAgentBuild        *int64     `json:"observed_agent_build,omitempty"`
	ExpiresAt                 time.Time  `json:"expires_at"`
	StartedAt                 *time.Time `json:"started_at,omitempty"`
	CompletedAt               *time.Time `json:"completed_at,omitempty"`
	CancelledAt               *time.Time `json:"cancelled_at,omitempty"`
	CreatedAt                 time.Time  `json:"created_at"`
	UpdatedAt                 time.Time  `json:"updated_at"`
}

const agentUpdateColumns = `
	id,account_id,pc_id,requester_device_id,current_version,current_build,target_version,target_build,
	artifact_url,artifact_filename,artifact_size_bytes,artifact_sha256,artifact_signer_fingerprint,
	status,phase,progress_percent,active_session_count,attempt_count,lease_token_hash,lease_expires_at,
	error_code,error_detail,observed_agent_version,observed_agent_build,expires_at,started_at,completed_at,cancelled_at,created_at,updated_at`

type agentUpdateRow interface {
	Scan(...any) error
}

func scanAgentUpdate(row agentUpdateRow) (AgentUpdateRecord, error) {
	var item AgentUpdateRecord
	err := row.Scan(
		&item.ID, &item.AccountID, &item.PCID, &item.RequesterDeviceID,
		&item.CurrentVersion, &item.CurrentBuild, &item.TargetVersion, &item.TargetBuild,
		&item.ArtifactURL, &item.ArtifactFilename, &item.ArtifactSizeBytes, &item.ArtifactSHA256,
		&item.ArtifactSignerFingerprint, &item.Status, &item.Phase, &item.ProgressPercent,
		&item.ActiveSessionCount, &item.AttemptCount, &item.LeaseTokenHash, &item.LeaseExpiresAt,
		&item.ErrorCode, &item.ErrorDetail, &item.ObservedAgentVersion, &item.ObservedAgentBuild, &item.ExpiresAt,
		&item.StartedAt, &item.CompletedAt, &item.CancelledAt, &item.CreatedAt, &item.UpdatedAt,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return AgentUpdateRecord{}, ErrNotFound
	}
	return item, err
}

func (s *Postgres) CreateAgentUpdate(
	ctx context.Context,
	userID, requesterID, pcID uuid.UUID,
	input AgentUpdateCreateInput,
) (AgentUpdateRecord, error) {
	row := s.Pool.QueryRow(ctx, `
		INSERT INTO agent_update_jobs(
			id,account_id,pc_id,requester_device_id,current_version,current_build,target_version,target_build,
			artifact_url,artifact_filename,artifact_size_bytes,artifact_sha256,artifact_signer_fingerprint,expires_at)
		SELECT $1,$2,p.id,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13
		FROM pcs p
		JOIN users account ON account.id=$2 AND account.status='ACTIVE'
		JOIN devices requester ON requester.id=$3 AND requester.user_id=$2
			AND requester.device_type='ANDROID' AND requester.trust_state='TRUSTED'
		JOIN devices agent ON agent.id=p.device_id AND agent.user_id=$2
			AND agent.device_type IN ('WINDOWS_PC','PC_AGENT') AND agent.trust_state='TRUSTED'
		WHERE p.id=$14 AND p.owner_user_id=$2 AND p.agent_version=$4
		  AND p.platform='windows' AND 'remote_agent_update_v1'=ANY(p.capabilities)
		  AND p.remote_routing_enabled
		  AND p.last_seen_at > now()-($15 * interval '1 second')
		RETURNING `+agentUpdateColumns,
		uuid.New(), userID, requesterID, input.CurrentVersion, input.CurrentBuild,
		input.TargetVersion, input.TargetBuild, input.ArtifactURL, input.ArtifactFilename,
		input.ArtifactSizeBytes, input.ArtifactSHA256[:], input.ArtifactSignerFingerprint,
		input.ExpiresAt, pcID, int(sessionOnlineWindow/time.Second),
	)
	item, err := scanAgentUpdate(row)
	if errors.Is(err, ErrNotFound) {
		return AgentUpdateRecord{}, ErrDenied
	}
	var postgresError *pgconn.PgError
	if errors.As(err, &postgresError) && postgresError.Code == "23505" {
		return AgentUpdateRecord{}, ErrConflict
	}
	return item, err
}

func (s *Postgres) GetAgentUpdate(
	ctx context.Context,
	userID, requesterID, pcID, jobID uuid.UUID,
) (AgentUpdateRecord, error) {
	if err := s.expireAgentUpdate(ctx, userID, pcID, jobID); err != nil {
		return AgentUpdateRecord{}, err
	}
	return scanAgentUpdate(s.Pool.QueryRow(ctx, `
		SELECT `+agentUpdateColumns+`
		FROM agent_update_jobs job
		WHERE job.id=$1 AND job.account_id=$2 AND job.pc_id=$3 AND job.requester_device_id=$4
		  AND EXISTS(SELECT 1 FROM devices d WHERE d.id=$4 AND d.user_id=$2
		      AND d.device_type='ANDROID' AND d.trust_state='TRUSTED')`,
		jobID, userID, pcID, requesterID,
	))
}

func (s *Postgres) GetActiveAgentUpdate(
	ctx context.Context,
	userID, requesterID, pcID uuid.UUID,
) (*AgentUpdateRecord, error) {
	if _, err := s.Pool.Exec(ctx, `
		UPDATE agent_update_jobs SET status='EXPIRED',updated_at=now()
		WHERE account_id=$1 AND pc_id=$2 AND requester_device_id=$3
		  AND status IN ('PENDING','PROCESSING') AND expires_at<=now()`,
		userID, pcID, requesterID,
	); err != nil {
		return nil, err
	}
	item, err := scanAgentUpdate(s.Pool.QueryRow(ctx, `
		SELECT `+agentUpdateColumns+`
		FROM agent_update_jobs job
		WHERE job.account_id=$1 AND job.pc_id=$2 AND job.requester_device_id=$3
		  AND job.status IN ('PENDING','PROCESSING')
		  AND EXISTS(SELECT 1 FROM devices d WHERE d.id=$3 AND d.user_id=$1
		      AND d.device_type='ANDROID' AND d.trust_state='TRUSTED')
		ORDER BY job.created_at DESC,job.id DESC LIMIT 1`,
		userID, pcID, requesterID,
	))
	if errors.Is(err, ErrNotFound) {
		return nil, nil
	}
	return &item, err
}

func (s *Postgres) CancelAgentUpdate(
	ctx context.Context,
	userID, requesterID, pcID, jobID uuid.UUID,
) (AgentUpdateRecord, error) {
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return AgentUpdateRecord{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	item, err := scanAgentUpdate(tx.QueryRow(ctx, `
		SELECT `+agentUpdateColumns+` FROM agent_update_jobs job
		WHERE id=$1 AND account_id=$2 AND pc_id=$3 AND requester_device_id=$4
		  AND EXISTS(SELECT 1 FROM devices d WHERE d.id=$4 AND d.user_id=$2
		      AND d.device_type='ANDROID' AND d.trust_state='TRUSTED') FOR UPDATE`,
		jobID, userID, pcID, requesterID,
	))
	if err != nil {
		return AgentUpdateRecord{}, err
	}
	if item.Status == AgentUpdateCancelled {
		if err := tx.Commit(ctx); err != nil {
			return AgentUpdateRecord{}, err
		}
		return item, nil
	}
	if (item.Status != AgentUpdatePending && item.Status != AgentUpdateProcessing) ||
		!agentUpdateCancelablePhase(item.Phase) {
		return AgentUpdateRecord{}, ErrConflict
	}
	item, err = scanAgentUpdate(tx.QueryRow(ctx, `
		UPDATE agent_update_jobs
		SET status='CANCELLED',cancelled_at=now(),updated_at=now()
		WHERE id=$1 RETURNING `+agentUpdateColumns, jobID))
	if err != nil {
		return AgentUpdateRecord{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return AgentUpdateRecord{}, err
	}
	return item, nil
}

func (s *Postgres) ClaimAgentUpdate(
	ctx context.Context,
	userID, agentDeviceID, pcID uuid.UUID,
	leaseDuration time.Duration,
) (*AgentUpdateRecord, string, error) {
	token := make([]byte, 32)
	if _, err := rand.Read(token); err != nil {
		return nil, "", err
	}
	tokenHash := sha256.Sum256(token)
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return nil, "", err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var authorized bool
	if err := tx.QueryRow(ctx, `
		SELECT EXISTS(SELECT 1 FROM pcs p JOIN devices d ON d.id=p.device_id
		WHERE p.id=$1 AND p.owner_user_id=$2 AND p.device_id=$3
		  AND p.platform='windows' AND 'remote_agent_update_v1'=ANY(p.capabilities)
		  AND d.user_id=$2 AND d.device_type IN ('WINDOWS_PC','PC_AGENT') AND d.trust_state='TRUSTED')`,
		pcID, userID, agentDeviceID,
	).Scan(&authorized); err != nil {
		return nil, "", err
	}
	if !authorized {
		return nil, "", ErrDenied
	}
	if _, err := tx.Exec(ctx, `
		UPDATE agent_update_jobs SET status='EXPIRED',updated_at=now()
		WHERE account_id=$1 AND pc_id=$2 AND status IN ('PENDING','PROCESSING') AND expires_at<=now()`,
		userID, pcID,
	); err != nil {
		return nil, "", err
	}
	item, err := scanAgentUpdate(tx.QueryRow(ctx, `
		WITH candidate AS (
			SELECT job.id FROM agent_update_jobs job
			WHERE job.account_id=$1 AND job.pc_id=$2 AND job.expires_at>now()
			  AND (job.status='PENDING' OR (job.status='PROCESSING' AND (
			      job.lease_expires_at<=now() OR job.phase IN ('INSTALLING','STARTING_NEW_AGENT',
			      'VERIFYING_CONNECTION','ROLLING_BACK'))))
			ORDER BY job.created_at,job.id FOR UPDATE SKIP LOCKED LIMIT 1
		)
		UPDATE agent_update_jobs job
		SET status='PROCESSING',attempt_count=attempt_count+1,lease_token_hash=$3,
		    lease_expires_at=LEAST(job.expires_at,now()+($4 * interval '1 millisecond')),
		    started_at=COALESCE(started_at,now()),updated_at=now()
		FROM candidate WHERE job.id=candidate.id RETURNING `+agentUpdateColumns,
		userID, pcID, tokenHash[:], leaseDuration.Milliseconds(),
	))
	if errors.Is(err, ErrNotFound) {
		if err := tx.Commit(ctx); err != nil {
			return nil, "", err
		}
		return nil, "", nil
	}
	if err != nil {
		return nil, "", err
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, "", err
	}
	return &item, base64.RawURLEncoding.EncodeToString(token), nil
}

func (s *Postgres) RenewAgentUpdate(
	ctx context.Context,
	userID, agentDeviceID, pcID, jobID uuid.UUID,
	leaseHash [32]byte,
	leaseDuration time.Duration,
) (AgentUpdateRecord, error) {
	return s.mutateAgentUpdateLease(ctx, userID, agentDeviceID, pcID, jobID, leaseHash,
		func(ctx context.Context, tx pgx.Tx, item AgentUpdateRecord) (AgentUpdateRecord, error) {
			return scanAgentUpdate(tx.QueryRow(ctx, `
				UPDATE agent_update_jobs job
				SET lease_expires_at=LEAST(job.expires_at,now()+($2 * interval '1 millisecond')),updated_at=now()
				WHERE id=$1 RETURNING `+agentUpdateColumns, jobID, leaseDuration.Milliseconds()))
		})
}

func (s *Postgres) ProgressAgentUpdate(
	ctx context.Context,
	userID, agentDeviceID, pcID, jobID uuid.UUID,
	leaseHash [32]byte,
	input AgentUpdateProgressInput,
) (AgentUpdateRecord, error) {
	return s.mutateAgentUpdateLease(ctx, userID, agentDeviceID, pcID, jobID, leaseHash,
		func(ctx context.Context, tx pgx.Tx, item AgentUpdateRecord) (AgentUpdateRecord, error) {
			if input.ProgressPercent < item.ProgressPercent ||
				(input.Phase != item.Phase && !validAgentUpdatePhaseTransition(item.Phase, input.Phase)) {
				return AgentUpdateRecord{}, ErrConflict
			}
			return scanAgentUpdate(tx.QueryRow(ctx, `
				UPDATE agent_update_jobs SET phase=$2,progress_percent=$3,
				    active_session_count=COALESCE($4,active_session_count),updated_at=now()
				WHERE id=$1 RETURNING `+agentUpdateColumns,
				jobID, input.Phase, input.ProgressPercent, input.ActiveSessionCount,
			))
		})
}

func (s *Postgres) CompleteAgentUpdate(
	ctx context.Context,
	userID, agentDeviceID, pcID, jobID uuid.UUID,
	leaseHash [32]byte,
	input AgentUpdateCompleteInput,
) (AgentUpdateRecord, error) {
	return s.mutateAgentUpdateLease(ctx, userID, agentDeviceID, pcID, jobID, leaseHash,
		func(ctx context.Context, tx pgx.Tx, item AgentUpdateRecord) (AgentUpdateRecord, error) {
			phase, progress, valid := terminalAgentUpdatePhase(item.Phase, input.Status)
			advertisedVersion := ""
			if input.Status == AgentUpdateSucceeded || input.Status == AgentUpdateRolledBack {
				if err := tx.QueryRow(ctx, `
					SELECT agent_version FROM pcs
					WHERE id=$1 AND owner_user_id=$2 AND device_id=$3
					FOR SHARE`, pcID, userID, agentDeviceID).Scan(&advertisedVersion); err != nil {
					if errors.Is(err, pgx.ErrNoRows) {
						return AgentUpdateRecord{}, ErrDenied
					}
					return AgentUpdateRecord{}, err
				}
			}
			if !valid || !validAgentUpdateCompletion(item, input, advertisedVersion) {
				return AgentUpdateRecord{}, ErrConflict
			}
			return scanAgentUpdate(tx.QueryRow(ctx, `
				UPDATE agent_update_jobs
				SET status=$2,phase=$3,progress_percent=$4,error_code=$5,error_detail=$6,
				    observed_agent_version=$7,observed_agent_build=$8,completed_at=now(),updated_at=now()
				WHERE id=$1 RETURNING `+agentUpdateColumns,
				jobID, input.Status, phase, progress, input.ErrorCode, input.ErrorDetail,
				input.ObservedAgentVersion, input.ObservedAgentBuild,
			))
		})
}

func validAgentUpdateCompletion(
	item AgentUpdateRecord,
	input AgentUpdateCompleteInput,
	advertisedVersion string,
) bool {
	switch input.Status {
	case AgentUpdateSucceeded:
		return input.ErrorCode == nil && input.ErrorDetail == nil &&
			input.ObservedAgentVersion != nil && input.ObservedAgentBuild != nil &&
			*input.ObservedAgentVersion == advertisedVersion &&
			*input.ObservedAgentBuild == item.TargetBuild
	case AgentUpdateRolledBack:
		return input.ErrorCode != nil && input.ObservedAgentVersion != nil &&
			input.ObservedAgentBuild != nil && *input.ObservedAgentVersion == advertisedVersion &&
			*input.ObservedAgentBuild == item.CurrentBuild
	default:
		return input.ErrorCode != nil
	}
}

func (s *Postgres) mutateAgentUpdateLease(
	ctx context.Context,
	userID, agentDeviceID, pcID, jobID uuid.UUID,
	leaseHash [32]byte,
	mutation func(context.Context, pgx.Tx, AgentUpdateRecord) (AgentUpdateRecord, error),
) (AgentUpdateRecord, error) {
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return AgentUpdateRecord{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	item, err := scanAgentUpdate(tx.QueryRow(ctx, `
		SELECT `+agentUpdateColumns+` FROM agent_update_jobs job
		WHERE job.id=$1 AND job.account_id=$2 AND job.pc_id=$3
		  AND EXISTS(SELECT 1 FROM pcs p JOIN devices d ON d.id=p.device_id
		      WHERE p.id=$3 AND p.owner_user_id=$2 AND p.device_id=$4
		        AND p.platform='windows' AND 'remote_agent_update_v1'=ANY(p.capabilities)
		        AND d.user_id=$2 AND d.device_type IN ('WINDOWS_PC','PC_AGENT') AND d.trust_state='TRUSTED')
		FOR UPDATE`, jobID, userID, pcID, agentDeviceID,
	))
	if err != nil {
		return AgentUpdateRecord{}, err
	}
	if item.Status != AgentUpdateProcessing || item.LeaseExpiresAt == nil ||
		!item.LeaseExpiresAt.After(time.Now().UTC()) || !item.ExpiresAt.After(time.Now().UTC()) ||
		subtle.ConstantTimeCompare(item.LeaseTokenHash, leaseHash[:]) != 1 {
		return AgentUpdateRecord{}, ErrConflict
	}
	item, err = mutation(ctx, tx, item)
	if err != nil {
		return AgentUpdateRecord{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return AgentUpdateRecord{}, err
	}
	return item, nil
}

func (s *Postgres) expireAgentUpdate(ctx context.Context, userID, pcID, jobID uuid.UUID) error {
	_, err := s.Pool.Exec(ctx, `
		UPDATE agent_update_jobs SET status='EXPIRED',updated_at=now()
		WHERE id=$1 AND account_id=$2 AND pc_id=$3
		  AND status IN ('PENDING','PROCESSING') AND expires_at<=now()`, jobID, userID, pcID)
	return err
}

func agentUpdateCancelablePhase(phase string) bool {
	switch phase {
	case AgentUpdateRequested, AgentUpdatePrechecking, AgentUpdateDownloading, AgentUpdateVerified:
		return true
	default:
		return false
	}
}

func validAgentUpdatePhaseTransition(from, to string) bool {
	switch from {
	case AgentUpdateRequested:
		return to == AgentUpdatePrechecking
	case AgentUpdatePrechecking:
		return to == AgentUpdateDownloading
	case AgentUpdateDownloading:
		return to == AgentUpdateVerified
	case AgentUpdateVerified:
		return to == AgentUpdateDrainingSessions
	case AgentUpdateDrainingSessions:
		return to == AgentUpdateInstalling
	case AgentUpdateInstalling:
		return to == AgentUpdateStartingNewAgent || to == AgentUpdateRollingBack
	case AgentUpdateStartingNewAgent:
		return to == AgentUpdateVerifyingConnection || to == AgentUpdateRollingBack
	case AgentUpdateVerifyingConnection:
		return to == AgentUpdateRollingBack
	default:
		return false
	}
}

func terminalAgentUpdatePhase(current, status string) (string, int, bool) {
	switch status {
	case AgentUpdateSucceeded:
		return AgentUpdateSucceeded, 100, current == AgentUpdateVerifyingConnection
	case AgentUpdateFailed:
		return AgentUpdateFailed, 100,
			agentUpdateCancelablePhase(current) || current == AgentUpdateDrainingSessions
	case AgentUpdateRolledBack:
		return AgentUpdateRolledBack, 100, current == AgentUpdateRollingBack
	case AgentUpdateRollbackFailed:
		return AgentUpdateRollbackFailed, 100, current == AgentUpdateRollingBack
	default:
		return "", 0, false
	}
}
