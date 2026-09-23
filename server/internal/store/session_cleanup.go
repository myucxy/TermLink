package store

import (
	"context"
	"errors"
	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

func ValidateSessionCleanup(ids []uuid.UUID) error {
	if len(ids) == 0 || len(ids) > 64 {
		return errors.New("cleanup requires 1..64 sessions")
	}
	seen := map[uuid.UUID]bool{}
	for _, id := range ids {
		if id == uuid.Nil || seen[id] {
			return errors.New("invalid cleanup IDs")
		}
		seen[id] = true
	}
	return nil
}

// The owning PC reports only durable local tombstones. Never accepts Android claims of host deletion.
func (s *Postgres) ApplyHostSessionDeletions(ctx context.Context, user, device, pc uuid.UUID, ids []uuid.UUID, trace string) error {
	if err := ValidateSessionCleanup(ids); err != nil {
		return err
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var owner uuid.UUID
	err = tx.QueryRow(ctx, `SELECT p.device_id FROM pcs p
 JOIN users u ON u.id=p.owner_user_id AND u.status='ACTIVE'
 JOIN devices d ON d.id=p.device_id AND d.user_id=p.owner_user_id AND d.trust_state='TRUSTED' AND d.revoked_at IS NULL
 AND d.device_type IN ('WINDOWS_PC','PC_AGENT')
 WHERE p.id=$1 AND p.owner_user_id=$2 AND p.device_id=$3 FOR UPDATE OF p`, pc, user, device).Scan(&owner)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	}
	if err != nil {
		return err
	}
	// Machine-local history can predate an account/PC identity change. Apply
	// this PC's tombstones only; never mutate or expose another PC's records.
	// ON CONFLICT also fences a concurrent insert under a different owner.
	_, err = tx.Exec(ctx, `INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,deleted_at,deletion_reason,ended_at)
 SELECT id,$2,'custom','DELETED',80,24,now(),'USER_REQUESTED',now() FROM unnest($1::uuid[]) id
 ON CONFLICT(id) DO UPDATE SET state='DELETED',deleted_at=COALESCE(sessions.deleted_at,now()),deletion_reason='USER_REQUESTED',ended_at=COALESCE(sessions.ended_at,now())
 WHERE sessions.pc_id=$2`, ids, pc)
	if err != nil {
		return err
	}
	if _, err = tx.Exec(ctx, `DELETE FROM control_leases l USING sessions s
 WHERE l.session_id=s.id AND s.pc_id=$2 AND s.state='DELETED' AND s.id=ANY($1::uuid[])`, ids, pc); err != nil {
		return err
	}
	_, err = tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id)
 VALUES($1,$2,'session.bulk_delete','pc',$3,'SUCCEEDED',$4)`, user, device, pc, trace)
	if err != nil {
		return err
	}
	return tx.Commit(ctx)
}
