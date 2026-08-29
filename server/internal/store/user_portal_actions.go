package store

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

var ErrUserPortalActionUnsupported = errors.New("user portal action unsupported by current schema")

// UserPortalActionMigrationGuidance documents the minimum persistence still
// required before the unsupported lifecycle methods can become real state
// machines. Export jobs need an account-scoped request table with immutable
// scope, status, expiry, and an opaque artifact reference. Account deletion
// needs an account-scoped request table with confirmation/step-up state,
// cancellation, retention deadlines, and processor fencing. Advanced control
// modes need versioned per-PC policy columns/table and an invalidation event.
const UserPortalActionMigrationGuidance = "add account-scoped data_export_requests and account_deletion_requests state machines; add versioned per-PC takeover/collaboration policy persistence before enabling those actions"

const (
	userPortalActionDefaultLimit = 50
	userPortalActionMaxLimit     = 100
)

type UserPortalActionResult struct {
	Changed bool `json:"changed"`
}

type UserPortalPCControlPolicy struct {
	PCID                 uuid.UUID   `json:"pc_id"`
	Name                 string      `json:"name"`
	RemoteRoutingEnabled bool        `json:"remote_routing_enabled"`
	Mode                 ControlMode `json:"mode"`
	LocalPriority        bool        `json:"local_priority"`
	PolicyVersion        int64       `json:"policy_version"`
}

// UserPortalDataExport is the minimum future contract. No artifact key, URL,
// terminal content, ciphertext, or provider credential belongs in this DTO.
type UserPortalDataExport struct {
	ID          uuid.UUID  `json:"id"`
	Status      string     `json:"status"`
	RequestedAt time.Time  `json:"requested_at"`
	ExpiresAt   *time.Time `json:"expires_at,omitempty"`
}

type UserPortalAccountDeletionPreflight struct {
	AccountID           uuid.UUID `json:"account_id"`
	Status              string    `json:"status"`
	ActiveDevices       int       `json:"active_devices"`
	LiveSessions        int       `json:"live_sessions"`
	OpenOrders          int       `json:"open_orders"`
	PendingRefunds      int       `json:"pending_refunds"`
	ActiveSubscriptions int       `json:"active_subscriptions"`
	Ready               bool      `json:"ready"`
	RequestSupported    bool      `json:"request_supported"`
}

const userPortalActionAccountSQL = `
	SELECT status FROM users WHERE id=$1 FOR UPDATE`

const userPortalActionIdempotencyInsertSQL = `
	INSERT INTO idempotency_keys(user_id,idempotency_key,request_hash,expires_at)
	VALUES($1,$2,$3,now()+interval '24 hours')
	ON CONFLICT(user_id,idempotency_key) DO UPDATE
	SET request_hash=excluded.request_hash,response_status=NULL,response_body=NULL,
	    expires_at=excluded.expires_at,created_at=now()
	WHERE idempotency_keys.expires_at<now()`

const userPortalActionIdempotencyReadSQL = `
	SELECT request_hash,response_status,response_body
	FROM idempotency_keys WHERE user_id=$1 AND idempotency_key=$2`

const userPortalActionIdempotencyCompleteSQL = `
	UPDATE idempotency_keys SET response_status=200,response_body=$4::jsonb
	WHERE user_id=$1 AND idempotency_key=$2 AND request_hash=$3`

const userPortalRevokeDeviceLookupSQL = `
	SELECT device_type,trust_state
	FROM devices WHERE id=$1 AND user_id=$2 FOR UPDATE`

const userPortalRevokeDeviceSQL = `
	UPDATE devices SET trust_state='REVOKED',revoked_at=now()
	WHERE id=$1 AND user_id=$2 AND trust_state<>'REVOKED'`

const userPortalRevokeDevicePCsSQL = `
	UPDATE pcs SET key_epoch=key_epoch+1,
	       remote_routing_enabled=CASE WHEN device_id=$1 THEN false ELSE remote_routing_enabled END
	WHERE owner_user_id=$2 AND (
		device_id=$1 OR id IN (SELECT pc_id FROM device_grants WHERE device_id=$1)
		OR ($3 AND EXISTS (
			SELECT 1 FROM devices target
			WHERE target.id=$1 AND target.user_id=$2 AND target.device_type='ANDROID'
		))
	)`

const userPortalDeleteDeviceGrantsSQL = `
	DELETE FROM device_grants
	WHERE device_id=$1 OR pc_id IN (
		SELECT id FROM pcs WHERE device_id=$1 AND owner_user_id=$2
	)`

const userPortalDeleteDeviceLeasesSQL = `
	DELETE FROM control_leases lease
	WHERE (lease.holder_device_id=$1 AND EXISTS (
		SELECT 1 FROM sessions holder_session
		JOIN pcs holder_pc ON holder_pc.id=holder_session.pc_id
		WHERE holder_session.id=lease.session_id AND holder_pc.owner_user_id=$2
	)) OR lease.session_id IN (
		SELECT session.id FROM sessions session
		JOIN pcs pc ON pc.id=session.pc_id
		WHERE pc.device_id=$1 AND pc.owner_user_id=$2
	)
	RETURNING lease.session_id,lease.holder_device_id,lease.lease_id,lease.version`

const userPortalDeviceRevokedOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES('device.revoked',$1,jsonb_build_object('user_id',$2::uuid,'device_id',$1::uuid))`

const userPortalDeviceRevokedAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id)
	VALUES($1,'user_portal.device.revoke','device',$2,'SUCCEEDED',$3)`

const userPortalPCPoliciesSQL = `
	SELECT pc.id,pc.name,pc.remote_routing_enabled,pc.control_mode,pc.local_priority,pc.policy_version
	FROM pcs pc
	JOIN users account ON account.id=pc.owner_user_id AND account.id=$1 AND account.status='ACTIVE'
	WHERE pc.owner_user_id=$1
	ORDER BY pc.name,pc.id
	LIMIT $2`

const userPortalPCLookupSQL = `
	SELECT device_id,name,remote_routing_enabled,control_mode,local_priority,policy_version
	FROM pcs WHERE id=$1 AND owner_user_id=$2 FOR UPDATE`

const userPortalPCPolicyUpdateSQL = `
	UPDATE pcs SET remote_routing_enabled=$3,policy_version=policy_version+1,updated_at=now()
	WHERE id=$1 AND owner_user_id=$2
	RETURNING name,remote_routing_enabled,control_mode,local_priority,policy_version`

const userPortalPCPolicyAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
	VALUES($1,'user_portal.pc.remote_routing.update','pc',$2,'SUCCEEDED',$3,
	       jsonb_build_object('remote_routing_enabled',$4::boolean))`

const userPortalPCPolicyOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES('grant.updated',$1,jsonb_build_object('pc_id',$2::uuid,'remote_routing_enabled',$3::boolean))`

const userPortalHistorySessionLookupSQL = `
	SELECT session.state
	FROM sessions session
	JOIN pcs pc ON pc.id=session.pc_id
	WHERE session.id=$1 AND pc.owner_user_id=$2
	FOR UPDATE OF session,pc`

const userPortalHistoryExistsSQL = `
	SELECT EXISTS(
		SELECT 1 FROM event_chunks WHERE session_id=$1 AND deleted_at IS NULL
		UNION ALL SELECT 1 FROM snapshots WHERE session_id=$1 AND deleted_at IS NULL
		UNION ALL SELECT 1 FROM transcript_pages WHERE session_id=$1 AND deleted_at IS NULL
		UNION ALL SELECT 1 FROM history_uploads WHERE session_id=$1
	)`

const userPortalHistoryDeleteAuditSQL = `
	INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id)
	VALUES($1,'user_portal.history.delete','session',$2,'SUCCEEDED',$3)`

const userPortalAccountDeletionPreflightSQL = `
	SELECT account.id,account.status,
	       (SELECT count(*) FROM devices WHERE user_id=account.id AND trust_state<>'REVOKED'),
	       (SELECT count(*) FROM sessions session JOIN pcs pc ON pc.id=session.pc_id
	         WHERE pc.owner_user_id=account.id AND session.state IN ('RUNNING','INTERRUPTED')),
	       (SELECT count(*) FROM orders WHERE account_id=account.id
	         AND status IN ('CREATED','PENDING_PAYMENT','PAID','FULFILLING','REFUNDING')),
	       (SELECT count(*) FROM refunds refund JOIN orders orders_for_refund ON orders_for_refund.id=refund.order_id
	         WHERE orders_for_refund.account_id=account.id AND refund.status IN ('APPROVAL_PENDING','CREATED','PENDING')),
	       (SELECT count(*) FROM subscriptions WHERE account_id=account.id AND status IN ('ACTIVE','GRACE'))
	FROM users account
	WHERE account.id=$1 AND account.status='ACTIVE'`

func normalizeUserPortalActionLimit(limit int) int {
	if limit < 1 {
		return userPortalActionDefaultLimit
	}
	if limit > userPortalActionMaxLimit {
		return userPortalActionMaxLimit
	}
	return limit
}

func validateUserPortalActionInput(accountID uuid.UUID, idempotencyKey, traceID string) error {
	if accountID == uuid.Nil || len(idempotencyKey) < 8 || len(idempotencyKey) > 128 ||
		strings.TrimSpace(traceID) == "" || len(traceID) > 256 {
		return ErrDenied
	}
	return nil
}

func userPortalActionHash(operation string, ids []uuid.UUID, values ...bool) [32]byte {
	var input strings.Builder
	input.WriteString(operation)
	for _, id := range ids {
		input.WriteByte(0)
		input.WriteString(id.String())
	}
	for _, value := range values {
		input.WriteByte(0)
		input.WriteString(strconv.FormatBool(value))
	}
	return sha256.Sum256([]byte(input.String()))
}

func requireUserPortalActionAccount(ctx context.Context, tx pgx.Tx, accountID uuid.UUID) error {
	var status string
	err := tx.QueryRow(ctx, userPortalActionAccountSQL, accountID).Scan(&status)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrNotFound
	}
	if err != nil {
		return err
	}
	if status != "ACTIVE" {
		return ErrDenied
	}
	return nil
}

func claimUserPortalAction(ctx context.Context, tx pgx.Tx, accountID uuid.UUID, key string, hash [32]byte, replay any) (bool, error) {
	command, err := tx.Exec(ctx, userPortalActionIdempotencyInsertSQL, accountID, key, hash[:])
	if err != nil {
		return false, err
	}
	var storedHash []byte
	var status *int
	var body json.RawMessage
	if err := tx.QueryRow(ctx, userPortalActionIdempotencyReadSQL, accountID, key).Scan(&storedHash, &status, &body); err != nil {
		return false, err
	}
	if len(storedHash) != len(hash) || !equalBytes(storedHash, hash[:]) {
		return false, ErrConflict
	}
	if command.RowsAffected() == 1 {
		return false, nil
	}
	if status == nil || *status != 200 || len(body) == 0 {
		return false, ErrConflict
	}
	if err := json.Unmarshal(body, replay); err != nil {
		return false, fmt.Errorf("decode user portal action replay: %w", err)
	}
	return true, nil
}

func completeUserPortalAction(ctx context.Context, tx pgx.Tx, accountID uuid.UUID, key string, hash [32]byte, result any) error {
	body, err := json.Marshal(result)
	if err != nil {
		return err
	}
	command, err := tx.Exec(ctx, userPortalActionIdempotencyCompleteSQL, accountID, key, hash[:], body)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrConflict
	}
	return nil
}

func (s *Postgres) RevokeUserPortalDevice(ctx context.Context, accountID, deviceID uuid.UUID, idempotencyKey, traceID string) (UserPortalActionResult, error) {
	if deviceID == uuid.Nil || validateUserPortalActionInput(accountID, idempotencyKey, traceID) != nil {
		return UserPortalActionResult{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return UserPortalActionResult{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireUserPortalActionAccount(ctx, tx, accountID); err != nil {
		return UserPortalActionResult{}, err
	}
	hash := userPortalActionHash("user-portal-device-revoke-v1", []uuid.UUID{accountID, deviceID})
	var result UserPortalActionResult
	replayed, err := claimUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, &result)
	if err != nil {
		return UserPortalActionResult{}, err
	}
	if replayed {
		return result, tx.Commit(ctx)
	}
	var deviceType, trustState string
	err = tx.QueryRow(ctx, userPortalRevokeDeviceLookupSQL, deviceID, accountID).Scan(&deviceType, &trustState)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalActionResult{}, ErrDenied
	}
	if err != nil {
		return UserPortalActionResult{}, err
	}
	result.Changed = trustState != "REVOKED"
	if result.Changed {
		command, err := tx.Exec(ctx, userPortalRevokeDeviceSQL, deviceID, accountID)
		if err != nil {
			return UserPortalActionResult{}, err
		}
		if command.RowsAffected() != 1 {
			return UserPortalActionResult{}, ErrConflict
		}
		if _, err := tx.Exec(ctx, userPortalRevokeDevicePCsSQL, deviceID, accountID, s.AutoTrustDevices && deviceType == "ANDROID"); err != nil {
			return UserPortalActionResult{}, err
		}
		if _, err := tx.Exec(ctx, userPortalDeleteDeviceGrantsSQL, deviceID, accountID); err != nil {
			return UserPortalActionResult{}, err
		}
		rows, err := tx.Query(ctx, userPortalDeleteDeviceLeasesSQL, deviceID, accountID)
		if err != nil {
			return UserPortalActionResult{}, err
		}
		leases, err := scanRevokedLeases(rows)
		if err != nil {
			return UserPortalActionResult{}, err
		}
		if err := enqueueRevokedLeases(ctx, tx, leases); err != nil {
			return UserPortalActionResult{}, err
		}
		if _, err := tx.Exec(ctx, userPortalDeviceRevokedOutboxSQL, deviceID, accountID); err != nil {
			return UserPortalActionResult{}, err
		}
		if _, err := tx.Exec(ctx, userPortalDeviceRevokedAuditSQL, accountID, deviceID, traceID); err != nil {
			return UserPortalActionResult{}, err
		}
	}
	if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
		return UserPortalActionResult{}, err
	}
	return result, tx.Commit(ctx)
}

func (s *Postgres) ListUserPortalPCControlPolicies(ctx context.Context, accountID uuid.UUID, limit int) ([]UserPortalPCControlPolicy, error) {
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return nil, err
	}
	limit = normalizeUserPortalActionLimit(limit)
	rows, err := s.Pool.Query(ctx, userPortalPCPoliciesSQL, accountID, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]UserPortalPCControlPolicy, 0, limit)
	for rows.Next() {
		var item UserPortalPCControlPolicy
		if err := rows.Scan(&item.PCID, &item.Name, &item.RemoteRoutingEnabled, &item.Mode, &item.LocalPriority, &item.PolicyVersion); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) SetUserPortalPCRemoteRouting(ctx context.Context, accountID, pcID uuid.UUID, enabled bool, idempotencyKey, traceID string) (UserPortalPCControlPolicy, error) {
	if pcID == uuid.Nil || validateUserPortalActionInput(accountID, idempotencyKey, traceID) != nil {
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
	hash := userPortalActionHash("user-portal-pc-remote-routing-v1", []uuid.UUID{accountID, pcID}, enabled)
	var result UserPortalPCControlPolicy
	replayed, err := claimUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, &result)
	if err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	if replayed {
		return result, tx.Commit(ctx)
	}
	var deviceID uuid.UUID
	var current bool
	err = tx.QueryRow(ctx, userPortalPCLookupSQL, pcID, accountID).Scan(
		&deviceID, &result.Name, &current, &result.Mode, &result.LocalPriority, &result.PolicyVersion,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalPCControlPolicy{}, ErrDenied
	}
	if err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	result.PCID = pcID
	result.RemoteRoutingEnabled = current
	if current != enabled {
		err := tx.QueryRow(ctx, userPortalPCPolicyUpdateSQL, pcID, accountID, enabled).Scan(
			&result.Name, &result.RemoteRoutingEnabled, &result.Mode, &result.LocalPriority, &result.PolicyVersion,
		)
		if errors.Is(err, pgx.ErrNoRows) {
			return UserPortalPCControlPolicy{}, ErrDenied
		}
		if err != nil {
			return UserPortalPCControlPolicy{}, err
		}
		if _, err := tx.Exec(ctx, userPortalPCPolicyAuditSQL, accountID, pcID, traceID, enabled); err != nil {
			return UserPortalPCControlPolicy{}, err
		}
		if _, err := tx.Exec(ctx, userPortalPCPolicyOutboxSQL, deviceID, pcID, enabled); err != nil {
			return UserPortalPCControlPolicy{}, err
		}
	}
	if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
		return UserPortalPCControlPolicy{}, err
	}
	return result, tx.Commit(ctx)
}

func (s *Postgres) RequestUserPortalHistoryDeletion(ctx context.Context, accountID, sessionID uuid.UUID, idempotencyKey, traceID string) (UserPortalActionResult, error) {
	if sessionID == uuid.Nil || validateUserPortalActionInput(accountID, idempotencyKey, traceID) != nil {
		return UserPortalActionResult{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return UserPortalActionResult{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireUserPortalActionAccount(ctx, tx, accountID); err != nil {
		return UserPortalActionResult{}, err
	}
	hash := userPortalActionHash("user-portal-history-delete-v1", []uuid.UUID{accountID, sessionID})
	var result UserPortalActionResult
	replayed, err := claimUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, &result)
	if err != nil {
		return UserPortalActionResult{}, err
	}
	if replayed {
		return result, tx.Commit(ctx)
	}
	var state string
	err = tx.QueryRow(ctx, userPortalHistorySessionLookupSQL, sessionID, accountID).Scan(&state)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalActionResult{}, ErrDenied
	}
	if err != nil {
		return UserPortalActionResult{}, err
	}
	if state != "EXITED" && state != "DELETED" {
		return UserPortalActionResult{}, ErrSessionActive
	}
	if err := tx.QueryRow(ctx, userPortalHistoryExistsSQL, sessionID).Scan(&result.Changed); err != nil {
		return UserPortalActionResult{}, err
	}
	if result.Changed {
		if err := tombstoneSessionHistory(ctx, tx, sessionID); err != nil {
			return UserPortalActionResult{}, err
		}
		if _, err := tx.Exec(ctx, userPortalHistoryDeleteAuditSQL, accountID, sessionID, traceID); err != nil {
			return UserPortalActionResult{}, err
		}
	}
	if err := completeUserPortalAction(ctx, tx, accountID, idempotencyKey, hash, result); err != nil {
		return UserPortalActionResult{}, err
	}
	return result, tx.Commit(ctx)
}

func (s *Postgres) GetUserPortalAccountDeletionPreflight(ctx context.Context, accountID uuid.UUID) (UserPortalAccountDeletionPreflight, error) {
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return UserPortalAccountDeletionPreflight{}, err
	}
	var result UserPortalAccountDeletionPreflight
	err := s.Pool.QueryRow(ctx, userPortalAccountDeletionPreflightSQL, accountID).Scan(
		&result.AccountID, &result.Status, &result.ActiveDevices, &result.LiveSessions,
		&result.OpenOrders, &result.PendingRefunds, &result.ActiveSubscriptions,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalAccountDeletionPreflight{}, ErrDenied
	}
	if err != nil {
		return UserPortalAccountDeletionPreflight{}, err
	}
	result.Ready = result.LiveSessions == 0 && result.OpenOrders == 0 && result.PendingRefunds == 0 && result.ActiveSubscriptions == 0
	result.RequestSupported = true
	return result, nil
}

func (s *Postgres) RequestUserPortalDataExport(ctx context.Context, accountID uuid.UUID, idempotencyKey, traceID string) (UserPortalDataExport, error) {
	return s.requestUserPortalDataExport(ctx, accountID, idempotencyKey, traceID)
}

func (s *Postgres) GetUserPortalDataExport(ctx context.Context, accountID, exportID uuid.UUID) (UserPortalDataExport, error) {
	return s.getUserPortalDataExport(ctx, accountID, exportID)
}

func (s *Postgres) RequestUserPortalAccountDeletion(ctx context.Context, accountID uuid.UUID, idempotencyKey, traceID string) error {
	_, err := s.requestUserPortalAccountDeletion(ctx, accountID, idempotencyKey, traceID)
	return err
}

func equalBytes(left, right []byte) bool {
	if len(left) != len(right) {
		return false
	}
	var different byte
	for index := range left {
		different |= left[index] ^ right[index]
	}
	return different == 0
}
