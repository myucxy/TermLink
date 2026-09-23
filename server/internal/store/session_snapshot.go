package store

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

// SessionSnapshotEntry contains statistics only; titles and terminal content stay on Windows.
type SessionSnapshotEntry struct {
	ID                 uuid.UUID `json:"id"`
	ShellType          string    `json:"shell_type"`
	State              string    `json:"state"`
	Columns            int       `json:"columns"`
	Rows               int       `json:"rows"`
	LastOutputSequence uint64    `json:"last_output_seq"`
}

func ValidateSessionSnapshot(items []SessionSnapshotEntry) error {
	if items == nil || len(items) > 4096 {
		return errors.New("a complete bounded session snapshot is required")
	}
	seen := make(map[uuid.UUID]bool, len(items))
	for _, item := range items {
		if item.ID == uuid.Nil || seen[item.ID] || !utf8.ValidString(item.ShellType) ||
			len(item.ShellType) < 1 || len(item.ShellType) > 64 ||
			(item.State != "RUNNING" && item.State != "INTERRUPTED" && item.State != "EXITED") ||
			item.Columns < 1 || item.Columns > 65535 || item.Rows < 1 || item.Rows > 65535 ||
			item.LastOutputSequence > math.MaxInt64 {
			return errors.New("invalid session snapshot")
		}
		seen[item.ID] = true
	}
	return nil
}

func (s *Postgres) ReplaceSessionSnapshot(ctx context.Context, userID, deviceID, pcID uuid.UUID, items []SessionSnapshotEntry) error {
	if err := ValidateSessionSnapshot(items); err != nil {
		return err
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var owner uuid.UUID
	err = tx.QueryRow(ctx, `SELECT p.device_id FROM pcs p
		JOIN devices d ON d.id=p.device_id AND d.user_id=p.owner_user_id AND d.trust_state='TRUSTED' AND d.revoked_at IS NULL
		JOIN users u ON u.id=p.owner_user_id AND u.status='ACTIVE'
		WHERE p.id=$1 AND p.owner_user_id=$2 AND p.device_id=$3 FOR UPDATE OF p`, pcID, userID, deviceID).Scan(&owner)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	}
	if err != nil {
		return err
	}
	ids := make([]uuid.UUID, 0, len(items))
	for _, item := range items {
		ids = append(ids, item.ID)
	}
	var foreign bool
	if err = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM sessions WHERE id=ANY($1::uuid[]) AND pc_id<>$2)`, ids, pcID).Scan(&foreign); err != nil {
		return err
	}
	if foreign {
		return ErrDenied
	}
	encoded, err := json.Marshal(items)
	if err != nil {
		return err
	}
	_, err = tx.Exec(ctx, `INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,last_output_seq,last_seen_at,ended_at)
		SELECT x.id,$1,x.shell_type,x.state,x.columns,x.rows,x.last_output_seq,now(),
			CASE WHEN x.state<>'RUNNING' THEN now() END
		FROM jsonb_to_recordset($2::jsonb) AS x(id uuid,shell_type text,state text,columns int,rows int,last_output_seq bigint)
		ON CONFLICT(id) DO UPDATE SET state=CASE WHEN sessions.state='EXITED' THEN sessions.state ELSE excluded.state END,columns=excluded.columns,rows=excluded.rows,
			last_output_seq=GREATEST(sessions.last_output_seq,excluded.last_output_seq),
			last_seen_at=now(),ended_at=CASE WHEN sessions.state='EXITED' THEN sessions.ended_at WHEN excluded.state='RUNNING' THEN NULL ELSE COALESCE(sessions.ended_at,now()) END
		WHERE sessions.pc_id=$1 AND sessions.deleted_at IS NULL AND sessions.state<>'DELETED'`, pcID, encoded)
	if err != nil {
		return err
	}
	// Retire orphan RUNNING rows left by an interrupted upgrade or lost final metadata write.
	_, err = tx.Exec(ctx, `UPDATE sessions SET state='INTERRUPTED',ended_at=COALESCE(ended_at,now())
		WHERE pc_id=$1 AND state='RUNNING' AND NOT(id=ANY($2::uuid[]))`, pcID, ids)
	if err != nil {
		return err
	}
	return tx.Commit(ctx)
}

// Catalog authorization is device scoped and does not depend on a session mirror row.
func (s *Postgres) SessionCatalogDevice(ctx context.Context, userID, androidID, pcID uuid.UUID) (uuid.UUID, error) {
	return s.sessionDeviceAuthority(ctx, userID, androidID, pcID, false)
}

func (s *Postgres) SessionCleanupDevice(ctx context.Context, userID, androidID, pcID uuid.UUID) (uuid.UUID, error) {
	return s.sessionDeviceAuthority(ctx, userID, androidID, pcID, true)
}

func (s *Postgres) sessionDeviceAuthority(ctx context.Context, userID, androidID, pcID uuid.UUID, manage bool) (uuid.UUID, error) {
	var deviceID uuid.UUID
	err := s.Pool.QueryRow(ctx, `SELECT p.device_id FROM pcs p
		JOIN users u ON u.id=p.owner_user_id AND u.status='ACTIVE'
		JOIN devices a ON a.id=$2 AND a.user_id=$1 AND a.trust_state='TRUSTED' AND a.revoked_at IS NULL AND a.device_type='ANDROID'
		JOIN devices d ON d.id=p.device_id AND d.user_id=$1 AND d.trust_state='TRUSTED' AND d.revoked_at IS NULL AND d.device_type IN ('WINDOWS_PC','PC_AGENT')
		WHERE p.id=$3 AND p.owner_user_id=$1 AND p.remote_routing_enabled AND
		($4 OR EXISTS(SELECT 1 FROM device_grants g WHERE g.pc_id=p.id AND g.device_id=a.id
		AND g.permission IN ('VIEW','CONTROL','MANAGE') AND (NOT $5 OR g.permission='MANAGE')
		AND (g.expires_at IS NULL OR g.expires_at>now())))`, userID, androidID, pcID, s.AutoTrustDevices, manage).Scan(&deviceID)
	if errors.Is(err, pgx.ErrNoRows) {
		return uuid.Nil, ErrDenied
	}
	return deviceID, err
}
