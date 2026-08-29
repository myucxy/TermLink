package store

import (
	"context"
	"errors"

	"github.com/google/uuid"
)

// MaxSessionRouteBatchSize is a defense-in-depth limit for realtime authority
// refreshes. Callers must split larger subscription sets instead of sending an
// unbounded UUID array to PostgreSQL.
const MaxSessionRouteBatchSize = 256

var ErrSessionRouteBatchTooLarge = errors.New("session route batch exceeds limit")

// SessionRoutes resolves the same authority tuple as SessionRoute for several
// sessions owned by one account and requested by one Android device. Missing
// map entries are authoritative denials (deleted, revoked, expired, disabled,
// untrusted, or no longer remotely routable sessions).
func (s *Postgres) SessionRoutes(ctx context.Context, userID, deviceID uuid.UUID, sessionIDs []uuid.UUID) (map[uuid.UUID]SessionRouteAuthorization, error) {
	result := make(map[uuid.UUID]SessionRouteAuthorization, len(sessionIDs))
	if len(sessionIDs) == 0 {
		return result, nil
	}
	if len(sessionIDs) > MaxSessionRouteBatchSize {
		return nil, ErrSessionRouteBatchTooLarge
	}
	rows, err := s.Pool.Query(ctx, `
		-- Keep this authority predicate identical to SessionRoute: same-account
		-- automatic trust grants MANAGE, while legacy explicit grants retain
		-- VIEW/CONTROL/MANAGE and their expiry when automatic trust is disabled.
		SELECT session.id,
		       CASE WHEN pc.device_id=actor.id THEN 'MANAGE' WHEN $4 THEN 'MANAGE' ELSE device_grant.permission END,
		       pc.device_id,
		       CASE WHEN $4 THEN NULL ELSE device_grant.expires_at END
		FROM sessions session
		JOIN pcs pc ON pc.id=session.pc_id
		JOIN users account ON account.id=pc.owner_user_id AND account.status='ACTIVE'
		JOIN devices owner_device ON owner_device.id=pc.device_id AND owner_device.trust_state='TRUSTED'
		JOIN devices actor ON actor.id=$2 AND actor.user_id=$3 AND actor.trust_state='TRUSTED'
		LEFT JOIN device_grants device_grant ON device_grant.pc_id=pc.id AND device_grant.device_id=actor.id
		  AND (device_grant.expires_at IS NULL OR device_grant.expires_at>now())
		WHERE session.id=ANY($1::uuid[]) AND pc.owner_user_id=$3
		  AND pc.remote_routing_enabled AND session.deleted_at IS NULL
		  AND ($4 OR pc.device_id=actor.id OR device_grant.device_id IS NOT NULL)`,
		sessionIDs, deviceID, userID, s.AutoTrustDevices)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	for rows.Next() {
		var sessionID uuid.UUID
		var route SessionRouteAuthorization
		if err := rows.Scan(&sessionID, &route.Permission, &route.PCDeviceID, &route.GrantExpiresAt); err != nil {
			return nil, err
		}
		result[sessionID] = route
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return result, nil
}
