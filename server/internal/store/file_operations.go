package store

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const (
	FileOperationList         = "LIST"
	FileOperationDownload     = "DOWNLOAD"
	FileOperationOpenTerminal = "OPEN_TERMINAL"

	FileOperationPending    = "PENDING"
	FileOperationProcessing = "PROCESSING"
	FileOperationSucceeded  = "SUCCEEDED"
	FileOperationFailed     = "FAILED"
	FileOperationCancelled  = "CANCELLED"
	FileOperationExpired    = "EXPIRED"
	FileTransferRealtimeV2  = "REALTIME_V2"

	MaxFileOperationPathBytes   = 4096
	MaxFileOperationResultBytes = 1 << 20
	// MaxRetainedFileOperationsPerAccount bounds transient plaintext paths and
	// LIST results even if a client produces work faster than cleanup runs.
	MaxRetainedFileOperationsPerAccount = 256
	MaxFileOperationCleanupBatch        = 500
	// MaxFileTransferBytes is the protocol-wide transfer ceiling.
	MaxFileTransferBytes int64 = 5 << 30
)

type FileOperationCreateInput struct {
	Operation string
	Path      string
	Offset    *int64
	ShellType *string
	ExpiresAt time.Time
}

type FileOperationUploadInput struct {
	Filename     string
	SizeBytes    int64
	SHA256       []byte
	DirectoryZIP bool
}

type FileOperationCompleteInput struct {
	Status       string
	Result       json.RawMessage
	ErrorMessage *string
}

type FileOperationCleanupResult struct {
	Expired int
	Deleted int
}

type FileOperationRecord struct {
	ID                uuid.UUID       `json:"id"`
	AccountID         uuid.UUID       `json:"-"`
	PCID              uuid.UUID       `json:"pc_id"`
	RequesterDeviceID uuid.UUID       `json:"requester_device_id"`
	Operation         string          `json:"operation"`
	Path              string          `json:"path"`
	Offset            *int64          `json:"offset,omitempty"`
	ShellType         *string         `json:"shell_type,omitempty"`
	Status            string          `json:"status"`
	AttemptCount      int             `json:"attempt_count"`
	LeaseTokenHash    []byte          `json:"-"`
	LeaseExpiresAt    *time.Time      `json:"lease_expires_at,omitempty"`
	Result            json.RawMessage `json:"result,omitempty"`
	ErrorMessage      *string         `json:"error,omitempty"`
	Filename          *string         `json:"filename,omitempty"`
	SizeBytes         *int64          `json:"size_bytes,omitempty"`
	SHA256            []byte          `json:"sha256,omitempty"`
	DirectoryZIP      *bool           `json:"directory_zip,omitempty"`
	TransferProtocol  *string         `json:"transfer_protocol,omitempty"`
	ExpiresAt         time.Time       `json:"expires_at"`
	CompletedAt       *time.Time      `json:"completed_at,omitempty"`
	CancelledAt       *time.Time      `json:"cancelled_at,omitempty"`
	CreatedAt         time.Time       `json:"created_at"`
	UpdatedAt         time.Time       `json:"updated_at"`
}

const fileOperationColumns = `
	id,account_id,pc_id,requester_device_id,operation,path,list_offset,shell_type,status,
	attempt_count,lease_token_hash,lease_expires_at,result,error_message,filename,
	size_bytes,sha256,directory_zip,transfer_protocol,expires_at,completed_at,cancelled_at,created_at,updated_at`

const fileOperationReturningColumns = `
	operation.id,operation.account_id,operation.pc_id,operation.requester_device_id,operation.operation,
	operation.path,operation.list_offset,operation.shell_type,operation.status,operation.attempt_count,
	operation.lease_token_hash,operation.lease_expires_at,operation.result,operation.error_message,
	operation.filename,operation.size_bytes,operation.sha256,operation.directory_zip,operation.transfer_protocol,
	operation.expires_at,operation.completed_at,operation.cancelled_at,operation.created_at,operation.updated_at`

type rowScanner interface {
	Scan(...any) error
}

func scanFileOperation(row rowScanner) (FileOperationRecord, error) {
	var item FileOperationRecord
	err := row.Scan(&item.ID, &item.AccountID, &item.PCID, &item.RequesterDeviceID, &item.Operation,
		&item.Path, &item.Offset, &item.ShellType, &item.Status, &item.AttemptCount,
		&item.LeaseTokenHash, &item.LeaseExpiresAt, &item.Result, &item.ErrorMessage,
		&item.Filename, &item.SizeBytes, &item.SHA256, &item.DirectoryZIP, &item.TransferProtocol, &item.ExpiresAt,
		&item.CompletedAt, &item.CancelledAt, &item.CreatedAt, &item.UpdatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return FileOperationRecord{}, ErrNotFound
	}
	return item, err
}

func (s *Postgres) CreateFileOperation(ctx context.Context, userID, requesterID, pcID uuid.UUID, input FileOperationCreateInput) (FileOperationRecord, error) {
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return FileOperationRecord{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var lockedAccountID uuid.UUID
	if err := tx.QueryRow(ctx, `SELECT id FROM users WHERE id=$1 FOR UPDATE`, userID).Scan(&lockedAccountID); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return FileOperationRecord{}, ErrDenied
		}
		return FileOperationRecord{}, err
	}
	var retained int64
	if err := tx.QueryRow(ctx, `SELECT count(*) FROM pc_file_operations WHERE account_id=$1`, userID).Scan(&retained); err != nil {
		return FileOperationRecord{}, err
	}
	if retained >= int64(MaxRetainedFileOperationsPerAccount) {
		return FileOperationRecord{}, ErrConflict
	}
	itemID := uuid.New()
	row := tx.QueryRow(ctx, `
		INSERT INTO pc_file_operations(id,account_id,pc_id,requester_device_id,operation,path,list_offset,shell_type,expires_at)
		SELECT $1,$2,p.id,$3,$4,$5,$6,$7,$8
		FROM pcs p
		JOIN devices requester ON requester.id=$3 AND requester.user_id=$2
			AND requester.device_type='ANDROID' AND requester.trust_state='TRUSTED'
		JOIN devices agent ON agent.id=p.device_id AND agent.user_id=$2
			AND agent.device_type IN ('WINDOWS_PC','PC_AGENT') AND agent.trust_state='TRUSTED'
		WHERE p.id=$9 AND p.owner_user_id=$2
		  AND 'file_operations_v1'=ANY(p.capabilities)
		  AND ($4<>'OPEN_TERMINAL' OR ('terminal_commands_v1'=ANY(p.capabilities) AND ($7=ANY(p.supported_shells) OR EXISTS (
			SELECT 1 FROM jsonb_array_elements(p.terminal_commands) command
			WHERE command->>'id'=$7
		  ))))
		RETURNING `+fileOperationColumns,
		itemID, userID, requesterID, input.Operation, input.Path, input.Offset, input.ShellType, input.ExpiresAt, pcID)
	item, err := scanFileOperation(row)
	if errors.Is(err, ErrNotFound) {
		return FileOperationRecord{}, ErrDenied
	}
	if err != nil {
		return FileOperationRecord{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return FileOperationRecord{}, err
	}
	return item, nil
}

// CleanupFileOperations performs one globally bounded, concurrent-safe pass.
// It first turns abandoned live work into EXPIRED terminal work. It only
// deletes terminal rows older than terminalBefore.
func (s *Postgres) CleanupFileOperations(
	ctx context.Context,
	now, terminalBefore time.Time,
	limit int,
) (FileOperationCleanupResult, error) {
	if limit < 1 || limit > MaxFileOperationCleanupBatch || terminalBefore.After(now) {
		return FileOperationCleanupResult{}, errors.New("invalid file-operation cleanup request")
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return FileOperationCleanupResult{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	rows, err := tx.Query(ctx, `
		SELECT operation.id,operation.status
		FROM pc_file_operations operation
		WHERE (
			operation.status IN ('PENDING','PROCESSING') AND operation.expires_at<=$1
		) OR (
			operation.status IN ('SUCCEEDED','FAILED','CANCELLED','EXPIRED')
			AND COALESCE(operation.completed_at,operation.cancelled_at,operation.updated_at)<=$2
		)
		ORDER BY COALESCE(operation.completed_at,operation.cancelled_at,operation.expires_at,operation.updated_at),operation.id
		FOR UPDATE OF operation SKIP LOCKED
		LIMIT $3`, now.UTC(), terminalBefore.UTC(), limit)
	if err != nil {
		return FileOperationCleanupResult{}, err
	}
	var expireIDs, deleteIDs []uuid.UUID
	for rows.Next() {
		var id uuid.UUID
		var status string
		if err := rows.Scan(&id, &status); err != nil {
			rows.Close()
			return FileOperationCleanupResult{}, err
		}
		if status == FileOperationPending || status == FileOperationProcessing {
			expireIDs = append(expireIDs, id)
		} else {
			deleteIDs = append(deleteIDs, id)
		}
	}
	rowsErr := rows.Err()
	rows.Close()
	if rowsErr != nil {
		return FileOperationCleanupResult{}, rowsErr
	}

	result := FileOperationCleanupResult{}
	if len(expireIDs) > 0 {
		command, err := tx.Exec(ctx, `
			UPDATE pc_file_operations
			SET status='EXPIRED',lease_token_hash=NULL,lease_expires_at=NULL,updated_at=$2
			WHERE id=ANY($1::uuid[]) AND status IN ('PENDING','PROCESSING')`, expireIDs, now.UTC())
		if err != nil {
			return FileOperationCleanupResult{}, err
		}
		result.Expired = int(command.RowsAffected())
	}
	if len(deleteIDs) > 0 {
		command, err := tx.Exec(ctx, `
			DELETE FROM pc_file_operations
			WHERE id=ANY($1::uuid[]) AND status IN ('SUCCEEDED','FAILED','CANCELLED','EXPIRED')`, deleteIDs)
		if err != nil {
			return FileOperationCleanupResult{}, err
		}
		result.Deleted = int(command.RowsAffected())
	}
	if err := tx.Commit(ctx); err != nil {
		return FileOperationCleanupResult{}, err
	}
	return result, nil
}

// ClaimFileOperation atomically leases at most one oldest job. SKIP LOCKED
// permits multiple Agent pollers without handing a job to two callers.
func (s *Postgres) ClaimFileOperation(ctx context.Context, userID, agentDeviceID, pcID uuid.UUID, leaseDuration time.Duration) (*FileOperationRecord, string, error) {
	token := make([]byte, 32)
	if _, err := rand.Read(token); err != nil {
		return nil, "", fmt.Errorf("generate file-operation lease: %w", err)
	}
	tokenHash := sha256.Sum256(token)
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return nil, "", err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var authorized bool
	if err := tx.QueryRow(ctx, `
		SELECT EXISTS (
			SELECT 1 FROM pcs p JOIN devices agent ON agent.id=p.device_id
			WHERE p.id=$1 AND p.owner_user_id=$2 AND p.device_id=$3
			  AND 'file_operations_v1'=ANY(p.capabilities)
			  AND agent.user_id=$2 AND agent.device_type IN ('WINDOWS_PC','PC_AGENT') AND agent.trust_state='TRUSTED'
		)`, pcID, userID, agentDeviceID).Scan(&authorized); err != nil {
		return nil, "", err
	}
	if !authorized {
		return nil, "", ErrDenied
	}

	if _, err := tx.Exec(ctx, `
		UPDATE pc_file_operations operation SET status='EXPIRED',updated_at=now()
		WHERE operation.pc_id=$1 AND operation.account_id=$2
		  AND operation.status IN ('PENDING','PROCESSING') AND operation.expires_at<=now()
		  AND EXISTS (
			SELECT 1 FROM pcs p JOIN devices agent ON agent.id=p.device_id
			WHERE p.id=$1 AND p.owner_user_id=$2 AND p.device_id=$3
			  AND 'file_operations_v1'=ANY(p.capabilities)
			  AND agent.user_id=$2 AND agent.device_type IN ('WINDOWS_PC','PC_AGENT') AND agent.trust_state='TRUSTED'
		  )`, pcID, userID, agentDeviceID); err != nil {
		return nil, "", err
	}

	row := tx.QueryRow(ctx, `
		WITH authority AS (
			SELECT 1 FROM pcs p JOIN devices agent ON agent.id=p.device_id
			WHERE p.id=$1 AND p.owner_user_id=$2 AND p.device_id=$3
			  AND 'file_operations_v1'=ANY(p.capabilities)
			  AND agent.user_id=$2 AND agent.device_type IN ('WINDOWS_PC','PC_AGENT') AND agent.trust_state='TRUSTED'
		), candidate AS (
			SELECT operation.id
			FROM pc_file_operations operation, authority
			WHERE operation.pc_id=$1 AND operation.account_id=$2 AND operation.expires_at>now()
			  AND (operation.status='PENDING'
			       OR (operation.status='PROCESSING' AND operation.lease_expires_at<=now()))
			ORDER BY operation.created_at,operation.id
			FOR UPDATE OF operation SKIP LOCKED
			LIMIT 1
		)
		UPDATE pc_file_operations operation
		SET status='PROCESSING',attempt_count=attempt_count+1,lease_token_hash=$4,
			lease_expires_at=LEAST(operation.expires_at,now()+($5 * interval '1 millisecond')),updated_at=now()
		FROM candidate
		WHERE operation.id=candidate.id
		RETURNING `+fileOperationReturningColumns,
		pcID, userID, agentDeviceID, tokenHash[:], leaseDuration.Milliseconds())
	item, err := scanFileOperation(row)
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

func (s *Postgres) GetFileOperation(ctx context.Context, userID, requesterID, pcID, operationID uuid.UUID) (FileOperationRecord, error) {
	_, err := s.Pool.Exec(ctx, `
		UPDATE pc_file_operations operation SET status='EXPIRED',updated_at=now()
		WHERE id=$1 AND account_id=$2 AND requester_device_id=$3 AND pc_id=$4
		  AND status IN ('PENDING','PROCESSING') AND expires_at<=now()
		  AND EXISTS (SELECT 1 FROM devices WHERE id=$3 AND user_id=$2 AND device_type='ANDROID' AND trust_state='TRUSTED')`,
		operationID, userID, requesterID, pcID)
	if err != nil {
		return FileOperationRecord{}, err
	}
	item, err := scanFileOperation(s.Pool.QueryRow(ctx, `
		SELECT `+fileOperationColumns+`
		FROM pc_file_operations operation
		WHERE id=$1 AND account_id=$2 AND requester_device_id=$3 AND pc_id=$4
		  AND EXISTS (SELECT 1 FROM devices WHERE id=$3 AND user_id=$2 AND device_type='ANDROID' AND trust_state='TRUSTED')`,
		operationID, userID, requesterID, pcID))
	return item, err
}

func (s *Postgres) CancelFileOperation(ctx context.Context, userID, requesterID, pcID, operationID uuid.UUID) (FileOperationRecord, error) {
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return FileOperationRecord{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	item, err := scanFileOperation(tx.QueryRow(ctx, `
		SELECT `+fileOperationColumns+`
		FROM pc_file_operations operation
		WHERE id=$1 AND account_id=$2 AND requester_device_id=$3 AND pc_id=$4
		  AND EXISTS (SELECT 1 FROM devices WHERE id=$3 AND user_id=$2 AND device_type='ANDROID' AND trust_state='TRUSTED')
		FOR UPDATE`, operationID, userID, requesterID, pcID))
	if err != nil {
		return FileOperationRecord{}, err
	}
	switch item.Status {
	case FileOperationCancelled:
		if err := tx.Commit(ctx); err != nil {
			return FileOperationRecord{}, err
		}
		return item, nil
	case FileOperationPending, FileOperationProcessing:
	default:
		return FileOperationRecord{}, ErrConflict
	}
	status := FileOperationCancelled
	if !item.ExpiresAt.After(time.Now().UTC()) {
		status = FileOperationExpired
	}
	item, err = scanFileOperation(tx.QueryRow(ctx, `
		UPDATE pc_file_operations SET status=$2,cancelled_at=CASE WHEN $2='CANCELLED' THEN now() END,updated_at=now()
		WHERE id=$1 RETURNING `+fileOperationColumns, operationID, status))
	if err != nil {
		return FileOperationRecord{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return FileOperationRecord{}, err
	}
	return item, nil
}

// RecordFileOperationTransfer publishes only authenticated metadata for the
// storage-free realtime transfer. File bytes never enter PostgreSQL or object
// storage. Replaying the same metadata is idempotent across worker reconnects.
func (s *Postgres) RecordFileOperationTransfer(ctx context.Context, userID, agentDeviceID, pcID, operationID uuid.UUID, leaseHash [32]byte, input FileOperationUploadInput) (FileOperationRecord, error) {
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return FileOperationRecord{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	item, err := s.getAgentFileOperationForUpdate(ctx, tx, userID, agentDeviceID, pcID, operationID)
	if err != nil {
		return FileOperationRecord{}, err
	}
	now := time.Now().UTC()
	if item.Status != FileOperationProcessing || item.Operation != FileOperationDownload ||
		!equalFileOperationHash(item.LeaseTokenHash, leaseHash[:]) || item.LeaseExpiresAt == nil ||
		!item.LeaseExpiresAt.After(now) || !item.ExpiresAt.After(now) {
		return FileOperationRecord{}, ErrConflict
	}
	if item.TransferProtocol != nil {
		if *item.TransferProtocol != FileTransferRealtimeV2 || item.Filename == nil ||
			*item.Filename != input.Filename || item.SizeBytes == nil || *item.SizeBytes != input.SizeBytes ||
			!equalFileOperationHash(item.SHA256, input.SHA256) || item.DirectoryZIP == nil ||
			*item.DirectoryZIP != input.DirectoryZIP {
			return FileOperationRecord{}, ErrConflict
		}
		if err := tx.Commit(ctx); err != nil {
			return FileOperationRecord{}, err
		}
		return item, nil
	}
	item, err = scanFileOperation(tx.QueryRow(ctx, `
		UPDATE pc_file_operations
		SET filename=$2,size_bytes=$3,sha256=$4,directory_zip=$5,
		    transfer_protocol='REALTIME_V2',updated_at=now()
		WHERE id=$1 RETURNING `+fileOperationColumns,
		operationID, input.Filename, input.SizeBytes, input.SHA256, input.DirectoryZIP))
	if err != nil {
		return FileOperationRecord{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return FileOperationRecord{}, err
	}
	return item, nil
}

func (s *Postgres) AgentFileOperation(ctx context.Context, userID, agentDeviceID, pcID, operationID uuid.UUID, leaseHash [32]byte) (FileOperationRecord, error) {
	item, err := scanFileOperation(s.Pool.QueryRow(ctx, `
		SELECT `+fileOperationColumns+`
		FROM pc_file_operations operation
		WHERE id=$1 AND account_id=$2 AND pc_id=$3 AND lease_token_hash=$4
		  AND EXISTS (SELECT 1 FROM pcs p JOIN devices d ON d.id=p.device_id
			WHERE p.id=$3 AND p.owner_user_id=$2 AND p.device_id=$5
			  AND 'file_operations_v1'=ANY(p.capabilities)
			  AND d.user_id=$2 AND d.device_type IN ('WINDOWS_PC','PC_AGENT') AND d.trust_state='TRUSTED')`,
		operationID, userID, pcID, leaseHash[:], agentDeviceID))
	return item, err
}

func (s *Postgres) RenewFileOperation(ctx context.Context, userID, agentDeviceID, pcID, operationID uuid.UUID, leaseHash [32]byte, leaseDuration time.Duration) (FileOperationRecord, error) {
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return FileOperationRecord{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	item, err := s.getAgentFileOperationForUpdate(ctx, tx, userID, agentDeviceID, pcID, operationID)
	if err != nil {
		return FileOperationRecord{}, err
	}
	now := time.Now().UTC()
	if item.Status != FileOperationProcessing || !equalFileOperationHash(item.LeaseTokenHash, leaseHash[:]) ||
		item.LeaseExpiresAt == nil || !item.LeaseExpiresAt.After(now) || !item.ExpiresAt.After(now) {
		return FileOperationRecord{}, ErrConflict
	}
	item, err = scanFileOperation(tx.QueryRow(ctx, `
		UPDATE pc_file_operations operation
		SET lease_expires_at=LEAST(operation.expires_at,now()+($2 * interval '1 millisecond')),updated_at=now()
		WHERE id=$1 RETURNING `+fileOperationReturningColumns, operationID, leaseDuration.Milliseconds()))
	if err != nil {
		return FileOperationRecord{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return FileOperationRecord{}, err
	}
	return item, nil
}

func (s *Postgres) CompleteFileOperation(ctx context.Context, userID, agentDeviceID, pcID, operationID uuid.UUID, leaseHash [32]byte, input FileOperationCompleteInput) (FileOperationRecord, error) {
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return FileOperationRecord{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	item, err := s.getAgentFileOperationForUpdate(ctx, tx, userID, agentDeviceID, pcID, operationID)
	if err != nil {
		return FileOperationRecord{}, err
	}
	if !equalFileOperationHash(item.LeaseTokenHash, leaseHash[:]) {
		return FileOperationRecord{}, ErrConflict
	}
	if item.Status == input.Status {
		if !jsonEqual(item.Result, input.Result) || !equalOptionalString(item.ErrorMessage, input.ErrorMessage) {
			return FileOperationRecord{}, ErrConflict
		}
		if err := tx.Commit(ctx); err != nil {
			return FileOperationRecord{}, err
		}
		return item, nil
	}
	if item.Status != FileOperationProcessing || item.LeaseExpiresAt == nil ||
		!item.LeaseExpiresAt.After(time.Now().UTC()) || !item.ExpiresAt.After(time.Now().UTC()) {
		return FileOperationRecord{}, ErrConflict
	}
	if input.Status == FileOperationSucceeded && item.Operation == FileOperationDownload {
		if item.TransferProtocol == nil || *item.TransferProtocol != FileTransferRealtimeV2 {
			return FileOperationRecord{}, ErrConflict
		}
	}
	item, err = scanFileOperation(tx.QueryRow(ctx, `
		UPDATE pc_file_operations
		SET status=$2,result=$3,error_message=$4,completed_at=now(),updated_at=now()
		WHERE id=$1 RETURNING `+fileOperationColumns,
		operationID, input.Status, nullableJSON(input.Result), input.ErrorMessage))
	if err != nil {
		return FileOperationRecord{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return FileOperationRecord{}, err
	}
	return item, nil
}

func (s *Postgres) getAgentFileOperationForUpdate(ctx context.Context, tx pgx.Tx, userID, agentDeviceID, pcID, operationID uuid.UUID) (FileOperationRecord, error) {
	item, err := scanFileOperation(tx.QueryRow(ctx, `
		SELECT `+fileOperationColumns+`
		FROM pc_file_operations operation
		WHERE id=$1 AND account_id=$2 AND pc_id=$3
		  AND EXISTS (SELECT 1 FROM pcs p JOIN devices d ON d.id=p.device_id
			WHERE p.id=$3 AND p.owner_user_id=$2 AND p.device_id=$4
			  AND 'file_operations_v1'=ANY(p.capabilities)
			  AND d.user_id=$2 AND d.device_type IN ('WINDOWS_PC','PC_AGENT') AND d.trust_state='TRUSTED')
		FOR UPDATE`, operationID, userID, pcID, agentDeviceID))
	return item, err
}

func nullableJSON(value json.RawMessage) any {
	if len(value) == 0 {
		return nil
	}
	return []byte(value)
}

func equalFileOperationHash(left, right []byte) bool {
	if len(left) != len(right) {
		return false
	}
	var difference byte
	for index := range left {
		difference |= left[index] ^ right[index]
	}
	return difference == 0
}

func equalOptionalString(left, right *string) bool {
	return (left == nil && right == nil) || (left != nil && right != nil && *left == *right)
}

func jsonEqual(left, right json.RawMessage) bool {
	if len(left) == 0 || string(left) == "null" {
		return len(right) == 0 || string(right) == "null"
	}
	if len(right) == 0 || string(right) == "null" {
		return false
	}
	var leftValue, rightValue any
	return json.Unmarshal(left, &leftValue) == nil && json.Unmarshal(right, &rightValue) == nil &&
		fmt.Sprintf("%#v", leftValue) == fmt.Sprintf("%#v", rightValue)
}
