package store

import (
	"context"
	"errors"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const agentPCPolicyLookupSQL = `
	SELECT p.name,p.remote_routing_enabled,p.control_mode,p.local_priority,p.policy_version
	FROM pcs p
	JOIN users u ON u.id=p.owner_user_id AND u.status='ACTIVE'
	JOIN devices d ON d.id=p.device_id AND d.user_id=p.owner_user_id
	WHERE p.id=$1 AND p.owner_user_id=$2 AND p.device_id=$3
	  AND d.device_type IN ('WINDOWS_PC','PC_AGENT') AND d.trust_state='TRUSTED'
	  AND d.disabled_at IS NULL`

func (s *Postgres) GetAgentPCRemoteRouting(ctx context.Context, accountID, deviceID, pcID uuid.UUID) (UserPortalPCControlPolicy, error) {
	result := UserPortalPCControlPolicy{PCID: pcID}
	err := s.Pool.QueryRow(ctx, agentPCPolicyLookupSQL, pcID, accountID, deviceID).Scan(
		&result.Name, &result.RemoteRoutingEnabled, &result.Mode, &result.LocalPriority, &result.PolicyVersion)
	if errors.Is(err, pgx.ErrNoRows) {
		err = ErrDenied
	}
	return result, err
}

// Only this PC's authenticated Agent can change its routing. The explicit
// policy version prevents a delayed local enable from undoing a newer portal
// disable. Clients must call this on a user action, never on heartbeat/upsert.
func (s *Postgres) SetAgentPCRemoteRouting(ctx context.Context, accountID, deviceID, pcID uuid.UUID,
	enabled bool, expectedVersion int64, trace string) (UserPortalPCControlPolicy, error) {
	if expectedVersion < 1 || accountID == uuid.Nil || deviceID == uuid.Nil || pcID == uuid.Nil {
		return UserPortalPCControlPolicy{}, ErrDenied
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireUserPortalActionAccount(ctx, tx, accountID); err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	result := UserPortalPCControlPolicy{PCID: pcID}
	err = tx.QueryRow(ctx, agentPCPolicyLookupSQL+` FOR UPDATE OF p,d`, pcID, accountID, deviceID).Scan(
		&result.Name, &result.RemoteRoutingEnabled, &result.Mode, &result.LocalPriority, &result.PolicyVersion)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalPCControlPolicy{}, ErrDenied
	}
	if err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	if expectedVersion != result.PolicyVersion {
		return UserPortalPCControlPolicy{}, ErrConflict
	}
	if result.RemoteRoutingEnabled != enabled {
		err = tx.QueryRow(ctx, userPortalPCPolicyUpdateSQL, pcID, accountID, enabled).Scan(
			&result.Name, &result.RemoteRoutingEnabled, &result.Mode, &result.LocalPriority, &result.PolicyVersion)
		if err != nil {
			return UserPortalPCControlPolicy{}, err
		}
		if _, err = tx.Exec(ctx, `INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
			VALUES($1,'agent.pc.remote_routing.update','pc',$2,'SUCCEEDED',$3,
			jsonb_build_object('remote_routing_enabled',$4::boolean,'device_id',$5::uuid))`,
			accountID, pcID, trace, enabled, deviceID); err != nil {
			return UserPortalPCControlPolicy{}, err
		}
		if _, err = tx.Exec(ctx, userPortalPCPolicyOutboxSQL, deviceID, pcID, enabled); err != nil {
			return UserPortalPCControlPolicy{}, err
		}
	}
	return result, tx.Commit(ctx)
}
