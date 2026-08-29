package store

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

type ControlMode string

const (
	ControlModeSingle        ControlMode = "SINGLE"
	ControlModeTakeover      ControlMode = "TAKEOVER"
	ControlModeCollaborative ControlMode = "COLLABORATIVE"
)

type SessionControlPolicy struct {
	Mode           ControlMode
	LocalPriority  bool
	PolicyVersion  int64
	PCID           uuid.UUID
	PCDeviceID     uuid.UUID
	GrantExpiresAt *time.Time
}

func (policy SessionControlPolicy) Valid() bool {
	return (policy.Mode == ControlModeSingle || policy.Mode == ControlModeTakeover || policy.Mode == ControlModeCollaborative) &&
		policy.PolicyVersion > 0 && policy.PCID != uuid.Nil && policy.PCDeviceID != uuid.Nil
}

// SessionControlPolicy resolves routing authority and the PC's control policy
// in one PostgreSQL snapshot. It deliberately returns no terminal payload or
// content. The authority predicate mirrors SessionRoute exactly.
func (s *Postgres) SessionControlPolicy(ctx context.Context, userID, deviceID, sessionID uuid.UUID) (SessionControlPolicy, error) {
	var policy SessionControlPolicy
	err := s.Pool.QueryRow(ctx, `
		SELECT pc.control_mode,pc.local_priority,pc.policy_version,pc.id,pc.device_id,
		       CASE WHEN $4 THEN NULL ELSE device_grant.expires_at END
		FROM sessions session
		JOIN pcs pc ON pc.id=session.pc_id
		JOIN users account ON account.id=pc.owner_user_id AND account.status='ACTIVE'
		JOIN devices owner_device ON owner_device.id=pc.device_id AND owner_device.trust_state='TRUSTED'
		JOIN devices actor ON actor.id=$2 AND actor.user_id=$3 AND actor.trust_state='TRUSTED'
		LEFT JOIN device_grants device_grant ON device_grant.pc_id=pc.id AND device_grant.device_id=actor.id
		  AND (device_grant.expires_at IS NULL OR device_grant.expires_at>now())
		WHERE session.id=$1 AND pc.owner_user_id=$3 AND pc.remote_routing_enabled
		  AND session.deleted_at IS NULL
		  AND ($4 OR pc.device_id=actor.id OR device_grant.device_id IS NOT NULL)`,
		sessionID, deviceID, userID, s.AutoTrustDevices).Scan(
		&policy.Mode, &policy.LocalPriority, &policy.PolicyVersion, &policy.PCID,
		&policy.PCDeviceID, &policy.GrantExpiresAt,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return SessionControlPolicy{}, ErrDenied
	}
	if err != nil {
		return SessionControlPolicy{}, err
	}
	if !policy.Valid() || policy.GrantExpiresAt != nil && !time.Now().Before(*policy.GrantExpiresAt) {
		return SessionControlPolicy{}, ErrDenied
	}
	return policy, nil
}
