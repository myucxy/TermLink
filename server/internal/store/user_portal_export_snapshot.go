package store

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const MaxUserPortalExportCleanupBatch = 32

type UserPortalExportSnapshot struct {
	SchemaVersion int             `json:"schema_version"`
	Account       json.RawMessage `json:"account"`
	Devices       json.RawMessage `json:"devices"`
	PCs           json.RawMessage `json:"pcs"`
	Sessions      json.RawMessage `json:"sessions"`
	Security      json.RawMessage `json:"security_activity"`
}

type UserPortalExportArtifact struct {
	ID              uuid.UUID
	AccountID       uuid.UUID
	ProcessingFence int64
	ObjectKey       string
	SHA256          [sha256.Size]byte
	SizeBytes       int64
	ExpiresAt       time.Time
}

var userPortalExportSnapshotSQL = []string{
	`SELECT jsonb_build_object('id',id,'status',status,'role',role,'created_at',created_at,'updated_at',updated_at) FROM users WHERE id=$1`,
	`SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'device_type',device_type,'name',name,'trust_state',trust_state,'approved_at',approved_at,'revoked_at',revoked_at,'created_at',created_at,'last_seen_at',last_seen_at) ORDER BY created_at,id),'[]'::jsonb) FROM devices WHERE user_id=$1`,
	`SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'device_id',device_id,'name',name,'agent_version',agent_version,'remote_routing_enabled',remote_routing_enabled,'control_mode',control_mode,'local_priority',local_priority,'policy_version',policy_version,'created_at',created_at,'last_seen_at',last_seen_at) ORDER BY created_at,id),'[]'::jsonb) FROM pcs WHERE owner_user_id=$1`,
	`SELECT coalesce(jsonb_agg(jsonb_build_object('id',session.id,'pc_id',session.pc_id,'shell_type',session.shell_type,'state',session.state,'columns',session.columns,'rows',session.rows,'last_output_seq',session.last_output_seq,'created_at',session.created_at,'ended_at',session.ended_at,'deleted_at',session.deleted_at) ORDER BY session.created_at,session.id),'[]'::jsonb) FROM sessions session JOIN pcs pc ON pc.id=session.pc_id WHERE pc.owner_user_id=$1`,
	`SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'action',action,'target_type',target_type,'target_id',target_id,'result',result,'created_at',created_at) ORDER BY id),'[]'::jsonb) FROM audit_events WHERE user_id=$1`,
}

var userPortalExportSnapshotScopes = []string{
	"ACCOUNT_PROFILE", "DEVICE_METADATA", "DEVICE_METADATA", "SESSION_METADATA", "ACCOUNT_PROFILE",
}

// UserPortalDataExportSnapshot uses one read-only REPEATABLE READ transaction,
// so every exported section represents one account-consistent metadata view.
// Queries intentionally omit terminal content and history keys, device keys,
// provider references/payloads, idempotency keys, tokens, secrets and audit
// request metadata.
func (s *Postgres) UserPortalDataExportSnapshot(ctx context.Context, accountID uuid.UUID, scopes []string) (UserPortalExportSnapshot, error) {
	selected, err := userPortalExportSelectedScopes(scopes)
	if err != nil {
		return UserPortalExportSnapshot{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.RepeatableRead, AccessMode: pgx.ReadOnly})
	if err != nil {
		return UserPortalExportSnapshot{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	snapshot := UserPortalExportSnapshot{SchemaVersion: 1}
	targets := []*json.RawMessage{
		&snapshot.Account, &snapshot.Devices, &snapshot.PCs, &snapshot.Sessions, &snapshot.Security,
	}
	for index, query := range userPortalExportSnapshotSQL {
		// Always execute the account query as the account existence/status fence;
		// discard its JSON when ACCOUNT_PROFILE was not requested.
		if index > 0 && !selected[userPortalExportSnapshotScopes[index]] {
			continue
		}
		var encoded []byte
		if err := tx.QueryRow(ctx, query, accountID).Scan(&encoded); err != nil {
			if errors.Is(err, pgx.ErrNoRows) {
				return UserPortalExportSnapshot{}, ErrDenied
			}
			return UserPortalExportSnapshot{}, err
		}
		if selected[userPortalExportSnapshotScopes[index]] {
			*targets[index] = append((*targets[index])[:0], encoded...)
		}
	}
	if err := tx.Commit(ctx); err != nil {
		return UserPortalExportSnapshot{}, err
	}
	return snapshot, nil
}

// userPortalExportSelectedScopes accepts BILLING_RECORDS only so export jobs
// persisted by older releases can finish after the commercial subsystem has
// been retired. The legacy scope intentionally selects no output sections.
func userPortalExportSelectedScopes(scopes []string) (map[string]bool, error) {
	selected := make(map[string]bool, len(scopes))
	recognized := false
	for _, scope := range scopes {
		switch scope {
		case "ACCOUNT_PROFILE", "DEVICE_METADATA", "SESSION_METADATA":
			selected[scope] = true
			recognized = true
		case "BILLING_RECORDS":
			recognized = true
		default:
			return nil, ErrDenied
		}
	}
	if !recognized {
		return nil, ErrDenied
	}
	return selected, nil
}

// UserPortalDataExportArtifact is account-scoped and returns internal storage
// metadata only to the privacy export service, never to the Web/API response.
func (s *Postgres) UserPortalDataExportArtifact(ctx context.Context, accountID, requestID uuid.UUID) (UserPortalExportArtifact, error) {
	var item UserPortalExportArtifact
	var digest []byte
	err := s.Pool.QueryRow(ctx, `
		SELECT id,account_id,processing_fence,artifact_object_key,artifact_sha256,artifact_size_bytes,expires_at
		FROM data_export_requests
		WHERE id=$1 AND account_id=$2 AND status='READY' AND expires_at>now()`, requestID, accountID).
		Scan(&item.ID, &item.AccountID, &item.ProcessingFence, &item.ObjectKey, &digest, &item.SizeBytes, &item.ExpiresAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalExportArtifact{}, ErrDenied
	}
	if err != nil {
		return UserPortalExportArtifact{}, err
	}
	if len(digest) != sha256.Size || item.SizeBytes < 1 || item.SizeBytes > 16<<20 ||
		item.ProcessingFence < 1 || !validUserPortalExportArtifactKey(item.ObjectKey, item.AccountID, item.ID, item.ProcessingFence) {
		return UserPortalExportArtifact{}, ErrDenied
	}
	copy(item.SHA256[:], digest)
	return item, nil
}

func (s *Postgres) ExpiredUserPortalDataExportArtifacts(ctx context.Context, limit int) ([]UserPortalExportArtifact, error) {
	if limit < 1 || limit > MaxUserPortalExportCleanupBatch {
		return nil, ErrDenied
	}
	rows, err := s.Pool.Query(ctx, `
		SELECT id,account_id,processing_fence,artifact_object_key,artifact_sha256,artifact_size_bytes,expires_at
		FROM data_export_requests
		WHERE status='READY' AND expires_at<=now()
		ORDER BY expires_at,id LIMIT $1`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	items := make([]UserPortalExportArtifact, 0, limit)
	for rows.Next() {
		var item UserPortalExportArtifact
		var digest []byte
		if err := rows.Scan(&item.ID, &item.AccountID, &item.ProcessingFence, &item.ObjectKey, &digest, &item.SizeBytes, &item.ExpiresAt); err != nil {
			return nil, err
		}
		if len(digest) != sha256.Size || item.ProcessingFence < 1 || !validUserPortalExportArtifactKey(item.ObjectKey, item.AccountID, item.ID, item.ProcessingFence) {
			return nil, ErrDenied
		}
		copy(item.SHA256[:], digest)
		items = append(items, item)
	}
	return items, rows.Err()
}

func (s *Postgres) MarkUserPortalDataExportExpired(ctx context.Context, item UserPortalExportArtifact) error {
	tag, err := s.Pool.Exec(ctx, `
		UPDATE data_export_requests
		SET status='EXPIRED',artifact_object_key=NULL,artifact_sha256=NULL,artifact_size_bytes=NULL,
		    version=version+1,updated_at=now()
		WHERE id=$1 AND account_id=$2 AND status='READY' AND expires_at<=now()
		  AND artifact_object_key=$3 AND artifact_sha256=$4`, item.ID, item.AccountID, item.ObjectKey, item.SHA256[:])
	if err != nil {
		return err
	}
	if tag.RowsAffected() != 1 {
		return ErrConflict
	}
	return nil
}

func validUserPortalExportArtifactKey(key string, accountID, requestID uuid.UUID, expectedFence int64) bool {
	parts := strings.Split(key, "/")
	if len(parts) != 5 || parts[0] != "accounts" || parts[1] != accountID.String() || parts[2] != "privacy-exports" ||
		parts[3] != requestID.String() || !strings.HasSuffix(parts[4], ".bin") {
		return false
	}
	fenceText := strings.TrimSuffix(parts[4], ".bin")
	fence, err := strconv.ParseInt(fenceText, 10, 64)
	return err == nil && fence > 0 && fence == expectedFence && strconv.FormatInt(fence, 10) == fenceText
}
