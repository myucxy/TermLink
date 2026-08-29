package store

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

// Role is the database-authoritative account role used by the browser
// console. OIDC proves the identity; PostgreSQL decides what that identity may
// do after the one-time SUPER_ADMIN bootstrap.
type Role string

const (
	RoleSuperAdmin Role = "SUPER_ADMIN"
	RoleAdmin      Role = "ADMIN"
	RoleAuditor    Role = "AUDITOR"
	RoleUser       Role = "USER"
)

const portalBootstrapLockID int64 = 0x544c504f5254414c

// PortalIdentityAssertion carries the bounded authorization facts extracted
// from a cryptographically verified Web OIDC token. BootstrapManaged must only
// be set for the deployment-owned bootstrap marker role; a generic
// SUPER_ADMIN claim is deliberately insufficient to move database authority.
type PortalIdentityAssertion struct {
	Role             Role
	BootstrapManaged bool
}

type portalBootstrapCandidate struct {
	ID               uuid.UUID
	BootstrapManaged bool
	DeviceCount      int64
}

var ErrLastSuperAdmin = errors.New("the last active super administrator is protected")

func ParseRole(value string) (Role, error) {
	role := Role(value)
	if !role.Valid() {
		return "", fmt.Errorf("invalid account role %q", value)
	}
	return role, nil
}

func (r Role) Valid() bool {
	switch r {
	case RoleSuperAdmin, RoleAdmin, RoleAuditor, RoleUser:
		return true
	default:
		return false
	}
}

func (r Role) CanOpenConsole() bool {
	return r == RoleSuperAdmin || r == RoleAdmin || r == RoleAuditor
}

func (r Role) CanManageAccounts() bool { return r == RoleSuperAdmin }

func (r Role) IsAdministrator() bool { return r == RoleSuperAdmin || r == RoleAdmin }

type PortalAccount struct {
	ID        uuid.UUID
	Role      Role
	Status    string
	CreatedAt time.Time
}

type PortalAdminAccount struct {
	ID        uuid.UUID
	Reference string
	Role      Role
	Status    string
	Devices   int
	CreatedAt time.Time
}

type PortalAuditEvent struct {
	ID         int64
	Action     string
	TargetType string
	TargetID   string
	Result     string
	TraceID    string
	CreatedAt  time.Time
}

type PortalConnection struct {
	ID              uuid.UUID
	Name            string
	DeviceType      string
	TrustState      string
	RunningSessions int
	LastOutputSeq   int64
	LastSeenAt      *time.Time
}

type PortalAdminDashboard struct {
	SchemaVersion       int64
	Users               int
	ConsoleAccounts     int
	Devices             int
	TrustedDevices      int
	PCs                 int
	RunningSessions     int
	Sessions            int
	Plans               int
	ActiveSubscriptions int
	Accounts            []PortalAdminAccount
	Connections         []PortalConnection
	AuditEvents         []PortalAuditEvent
}

// PortalAdminSection loads only the bounded data required by one console
// section. The legacy PortalAdminDashboard method remains for compatibility
// and integration diagnostics, while production page requests avoid its
// accounts + connections + audit mega-query fan-out.
func (s *Postgres) PortalAdminSection(ctx context.Context, actorID uuid.UUID, section string) (PortalAdminDashboard, error) {
	var actorRole Role
	var actorStatus string
	if err := s.Pool.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1`, actorID).Scan(&actorRole, &actorStatus); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return PortalAdminDashboard{}, ErrDenied
		}
		return PortalAdminDashboard{}, err
	}
	if actorStatus != "ACTIVE" || !actorRole.CanOpenConsole() {
		return PortalAdminDashboard{}, ErrDenied
	}

	var result PortalAdminDashboard
	switch section {
	case "overview":
		if err := s.Pool.QueryRow(ctx, `
			SELECT
				(SELECT max(version) FROM schema_migrations),
				(SELECT count(*) FROM users WHERE status <> 'DELETED'),
				(SELECT count(*) FROM users WHERE status <> 'DELETED' AND role IN ('SUPER_ADMIN','ADMIN','AUDITOR')),
				(SELECT count(*) FROM devices),
				(SELECT count(*) FROM devices WHERE trust_state='TRUSTED'),
				(SELECT count(*) FROM pcs),
				(SELECT count(*) FROM sessions WHERE state <> 'DELETED'),
				(SELECT count(*) FROM sessions WHERE state='RUNNING'),
				(SELECT count(*) FROM plans),
				(SELECT count(*) FROM subscriptions WHERE status IN ('ACTIVE','GRACE'))`).
			Scan(&result.SchemaVersion, &result.Users, &result.ConsoleAccounts, &result.Devices,
				&result.TrustedDevices, &result.PCs, &result.Sessions, &result.RunningSessions,
				&result.Plans, &result.ActiveSubscriptions); err != nil {
			return PortalAdminDashboard{}, err
		}
		rows, err := s.Pool.Query(ctx, `
			SELECT id,action,target_type,coalesce(target_id::text,''),result,trace_id,created_at
			FROM audit_events ORDER BY id DESC LIMIT 7`)
		if err != nil {
			return PortalAdminDashboard{}, err
		}
		defer rows.Close()
		for rows.Next() {
			var item PortalAuditEvent
			if err := rows.Scan(&item.ID, &item.Action, &item.TargetType, &item.TargetID, &item.Result, &item.TraceID, &item.CreatedAt); err != nil {
				return PortalAdminDashboard{}, err
			}
			result.AuditEvents = append(result.AuditEvents, item)
		}
		return result, rows.Err()
	case "connections":
		rows, err := s.Pool.Query(ctx, `
			SELECT d.id,d.name,d.device_type,d.trust_state,d.last_seen_at,
				count(s.id) FILTER (WHERE s.state='RUNNING'),coalesce(max(s.last_output_seq),0)
			FROM devices d
			LEFT JOIN pcs p ON p.device_id=d.id
			LEFT JOIN sessions s ON s.pc_id=p.id AND s.state <> 'DELETED'
			GROUP BY d.id,d.name,d.device_type,d.trust_state,d.last_seen_at
			ORDER BY d.last_seen_at DESC NULLS LAST,d.created_at DESC LIMIT 100`)
		if err != nil {
			return PortalAdminDashboard{}, err
		}
		defer rows.Close()
		for rows.Next() {
			var item PortalConnection
			if err := rows.Scan(&item.ID, &item.Name, &item.DeviceType, &item.TrustState, &item.LastSeenAt, &item.RunningSessions, &item.LastOutputSeq); err != nil {
				return PortalAdminDashboard{}, err
			}
			result.Connections = append(result.Connections, item)
		}
		return result, rows.Err()
	case "accounts":
		rows, err := s.Pool.Query(ctx, `
			SELECT u.id,u.role,u.status,u.created_at,count(d.id)
			FROM users u LEFT JOIN devices d ON d.user_id=u.id
			WHERE u.status <> 'DELETED'
			GROUP BY u.id,u.role,u.status,u.created_at
			ORDER BY CASE u.role WHEN 'SUPER_ADMIN' THEN 0 WHEN 'ADMIN' THEN 1 WHEN 'AUDITOR' THEN 2 ELSE 3 END,u.created_at,u.id
			LIMIT 200`)
		if err != nil {
			return PortalAdminDashboard{}, err
		}
		defer rows.Close()
		for rows.Next() {
			var item PortalAdminAccount
			if err := rows.Scan(&item.ID, &item.Role, &item.Status, &item.CreatedAt, &item.Devices); err != nil {
				return PortalAdminDashboard{}, err
			}
			item.Reference = "acct_" + item.ID.String()[:8]
			result.Accounts = append(result.Accounts, item)
		}
		return result, rows.Err()
	case "audit":
		rows, err := s.Pool.Query(ctx, `
			SELECT id,action,target_type,coalesce(target_id::text,''),result,trace_id,created_at
			FROM audit_events ORDER BY id DESC LIMIT 100`)
		if err != nil {
			return PortalAdminDashboard{}, err
		}
		defer rows.Close()
		for rows.Next() {
			var item PortalAuditEvent
			if err := rows.Scan(&item.ID, &item.Action, &item.TargetType, &item.TargetID, &item.Result, &item.TraceID, &item.CreatedAt); err != nil {
				return PortalAdminDashboard{}, err
			}
			result.AuditEvents = append(result.AuditEvents, item)
		}
		return result, rows.Err()
	case "billing":
		if err := s.Pool.QueryRow(ctx, `SELECT (SELECT count(*) FROM plans),(SELECT count(*) FROM subscriptions WHERE status IN ('ACTIVE','GRACE'))`).
			Scan(&result.Plans, &result.ActiveSubscriptions); err != nil {
			return PortalAdminDashboard{}, err
		}
		return result, nil
	case "deployment":
		if err := s.Pool.QueryRow(ctx, `SELECT max(version) FROM schema_migrations`).Scan(&result.SchemaVersion); err != nil {
			return PortalAdminDashboard{}, err
		}
		return result, nil
	default:
		return result, nil
	}
}

// SyncPortalIdentity creates the account record used by the browser portal.
// A signed SUPER_ADMIN assertion can grant the role only while the database
// has no active SUPER_ADMIN. A deployment-managed assertion may additionally
// recover authority from one device-free bootstrap identity that no longer
// matches the current IdP subject. Every grant or recovery is transactional
// and audited; ordinary IdP role assertions never replace database authority.
func (s *Postgres) SyncPortalIdentity(ctx context.Context, subjectHash [32]byte, assertion PortalIdentityAssertion, traceID string) (PortalAccount, error) {
	if !assertion.Role.Valid() {
		assertion.Role = RoleUser
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return PortalAccount{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock($1)`, portalBootstrapLockID); err != nil {
		return PortalAccount{}, err
	}
	userID, err := ensureUser(ctx, tx, subjectHash)
	if err != nil {
		return PortalAccount{}, err
	}
	var result PortalAccount
	result.ID = userID
	if err := tx.QueryRow(ctx, `SELECT role,status,created_at FROM users WHERE id=$1 FOR UPDATE`, userID).
		Scan(&result.Role, &result.Status, &result.CreatedAt); err != nil {
		return PortalAccount{}, err
	}
	if result.Status != "ACTIVE" {
		if _, err := tx.Exec(ctx, `
			INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
			VALUES($1,'identity.web.login','account',$1,'DENIED',$2,jsonb_build_object('status',$3::text))`,
			userID, traceID, result.Status); err != nil {
			return PortalAccount{}, err
		}
		if err := tx.Commit(ctx); err != nil {
			return PortalAccount{}, err
		}
		return PortalAccount{}, ErrDenied
	}
	if assertion.Role == RoleSuperAdmin && result.Role != RoleSuperAdmin {
		candidates, err := portalBootstrapCandidates(ctx, tx, userID)
		if err != nil {
			return PortalAccount{}, err
		}
		if len(candidates) == 0 {
			result.Role = RoleSuperAdmin
			if _, err := tx.Exec(ctx, `UPDATE users SET role='SUPER_ADMIN',is_admin=true,updated_at=now() WHERE id=$1`, userID); err != nil {
				return PortalAccount{}, err
			}
			if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,'identity.bootstrap.super_admin','account',$1,'SUCCEEDED','identity-bootstrap',jsonb_build_object('role','SUPER_ADMIN'))`, userID); err != nil {
				return PortalAccount{}, err
			}
		} else if previousID, ok := managedBootstrapRebindCandidate(assertion, candidates); ok {
			if _, err := tx.Exec(ctx, `UPDATE users SET role='USER',is_admin=false,updated_at=now() WHERE id=$1`, previousID); err != nil {
				return PortalAccount{}, err
			}
			if _, err := tx.Exec(ctx, `UPDATE users SET role='SUPER_ADMIN',is_admin=true,updated_at=now() WHERE id=$1`, userID); err != nil {
				return PortalAccount{}, err
			}
			result.Role = RoleSuperAdmin
			if _, err := tx.Exec(ctx, `
				INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
				VALUES($1,'identity.bootstrap.super_admin.rebind','account',$2,'SUCCEEDED',$3,
					jsonb_build_object('previous_role','SUPER_ADMIN','new_role','SUPER_ADMIN','previous_account_role','USER'))`,
				userID, previousID, traceID); err != nil {
				return PortalAccount{}, err
			}
		}
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,'identity.web.login','account',$1,'SUCCEEDED',$2,jsonb_build_object('role',$3::text))`,
		userID, traceID, result.Role); err != nil {
		return PortalAccount{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return PortalAccount{}, err
	}
	return result, nil
}

func portalBootstrapCandidates(ctx context.Context, tx pgx.Tx, currentID uuid.UUID) ([]portalBootstrapCandidate, error) {
	rows, err := tx.Query(ctx, `
		SELECT u.id,
			EXISTS (
				SELECT 1 FROM audit_events bootstrap
				WHERE bootstrap.user_id=u.id
				  AND bootstrap.action IN ('identity.bootstrap.super_admin','identity.bootstrap.super_admin.rebind')
				  AND bootstrap.result='SUCCEEDED'
			) AND NOT EXISTS (
				SELECT 1 FROM audit_events changed
				WHERE changed.target_id=u.id AND changed.action='admin.account.role.update'
			) AS bootstrap_managed,
			(SELECT count(*) FROM devices d WHERE d.user_id=u.id) AS device_count
		FROM users u
		WHERE u.role='SUPER_ADMIN' AND u.status='ACTIVE' AND u.id<>$1
		ORDER BY u.id
		FOR UPDATE OF u`, currentID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	candidates := make([]portalBootstrapCandidate, 0, 2)
	for rows.Next() {
		var candidate portalBootstrapCandidate
		if err := rows.Scan(&candidate.ID, &candidate.BootstrapManaged, &candidate.DeviceCount); err != nil {
			return nil, err
		}
		candidates = append(candidates, candidate)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return candidates, nil
}

func managedBootstrapRebindCandidate(assertion PortalIdentityAssertion, candidates []portalBootstrapCandidate) (uuid.UUID, bool) {
	if assertion.Role != RoleSuperAdmin || !assertion.BootstrapManaged || len(candidates) != 1 {
		return uuid.Nil, false
	}
	candidate := candidates[0]
	if candidate.ID == uuid.Nil || !candidate.BootstrapManaged || candidate.DeviceCount != 0 {
		return uuid.Nil, false
	}
	return candidate.ID, true
}

// RecordPortalLoginFailure captures only a stable failure reason and trace ID.
// No authorization code, token, raw subject, IP address, or browser content is
// accepted by this method.
func (s *Postgres) RecordPortalLoginFailure(ctx context.Context, traceID, reason string) error {
	_, err := s.Pool.Exec(ctx, `
		INSERT INTO audit_events(action,target_type,result,trace_id,metadata)
		VALUES('identity.web.login','account','FAILED',$1,jsonb_build_object('reason',$2::text))`, traceID, reason)
	return err
}

func (s *Postgres) GetPortalAccount(ctx context.Context, subjectHash [32]byte) (PortalAccount, error) {
	var result PortalAccount
	err := s.Pool.QueryRow(ctx, `SELECT id,role,status,created_at FROM users WHERE oidc_subject_hash=$1`, subjectHash[:]).
		Scan(&result.ID, &result.Role, &result.Status, &result.CreatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return PortalAccount{}, ErrNotFound
	}
	if err != nil {
		return PortalAccount{}, err
	}
	if result.Status != "ACTIVE" {
		return PortalAccount{}, ErrDenied
	}
	return result, nil
}

func (s *Postgres) PortalAdminDashboard(ctx context.Context, actorID uuid.UUID) (PortalAdminDashboard, error) {
	var actorRole Role
	var actorStatus string
	if err := s.Pool.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1`, actorID).Scan(&actorRole, &actorStatus); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return PortalAdminDashboard{}, ErrDenied
		}
		return PortalAdminDashboard{}, err
	}
	if actorStatus != "ACTIVE" || !actorRole.CanOpenConsole() {
		return PortalAdminDashboard{}, ErrDenied
	}

	var result PortalAdminDashboard
	if err := s.Pool.QueryRow(ctx, `SELECT max(version) FROM schema_migrations`).Scan(&result.SchemaVersion); err != nil {
		return PortalAdminDashboard{}, err
	}
	if err := s.Pool.QueryRow(ctx, `
		SELECT
			count(*),
			count(*) FILTER (WHERE role IN ('SUPER_ADMIN','ADMIN','AUDITOR'))
		FROM users WHERE status <> 'DELETED'`).Scan(&result.Users, &result.ConsoleAccounts); err != nil {
		return PortalAdminDashboard{}, err
	}
	if err := s.Pool.QueryRow(ctx, `
		SELECT count(*),count(*) FILTER (WHERE trust_state='TRUSTED') FROM devices`).
		Scan(&result.Devices, &result.TrustedDevices); err != nil {
		return PortalAdminDashboard{}, err
	}
	if err := s.Pool.QueryRow(ctx, `SELECT count(*) FROM pcs`).Scan(&result.PCs); err != nil {
		return PortalAdminDashboard{}, err
	}
	if err := s.Pool.QueryRow(ctx, `SELECT count(*),count(*) FILTER (WHERE state='RUNNING') FROM sessions WHERE state <> 'DELETED'`).
		Scan(&result.Sessions, &result.RunningSessions); err != nil {
		return PortalAdminDashboard{}, err
	}
	if err := s.Pool.QueryRow(ctx, `SELECT count(*) FROM plans`).Scan(&result.Plans); err != nil {
		return PortalAdminDashboard{}, err
	}
	if err := s.Pool.QueryRow(ctx, `SELECT count(*) FROM subscriptions WHERE status IN ('ACTIVE','GRACE')`).Scan(&result.ActiveSubscriptions); err != nil {
		return PortalAdminDashboard{}, err
	}

	accountRows, err := s.Pool.Query(ctx, `
		SELECT u.id,u.role,u.status,u.created_at,count(d.id)
		FROM users u LEFT JOIN devices d ON d.user_id=u.id
		WHERE u.status <> 'DELETED'
		GROUP BY u.id,u.role,u.status,u.created_at
		ORDER BY CASE u.role WHEN 'SUPER_ADMIN' THEN 0 WHEN 'ADMIN' THEN 1 WHEN 'AUDITOR' THEN 2 ELSE 3 END,u.created_at,u.id
		LIMIT 200`)
	if err != nil {
		return PortalAdminDashboard{}, err
	}
	for accountRows.Next() {
		var item PortalAdminAccount
		if err := accountRows.Scan(&item.ID, &item.Role, &item.Status, &item.CreatedAt, &item.Devices); err != nil {
			accountRows.Close()
			return PortalAdminDashboard{}, err
		}
		item.Reference = "acct_" + item.ID.String()[:8]
		result.Accounts = append(result.Accounts, item)
	}
	if err := accountRows.Err(); err != nil {
		accountRows.Close()
		return PortalAdminDashboard{}, err
	}
	accountRows.Close()

	connectionRows, err := s.Pool.Query(ctx, `
		SELECT d.id,d.name,d.device_type,d.trust_state,d.last_seen_at,
			count(s.id) FILTER (WHERE s.state='RUNNING'),coalesce(max(s.last_output_seq),0)
		FROM devices d
		LEFT JOIN pcs p ON p.device_id=d.id
		LEFT JOIN sessions s ON s.pc_id=p.id AND s.state <> 'DELETED'
		GROUP BY d.id,d.name,d.device_type,d.trust_state,d.last_seen_at
		ORDER BY d.last_seen_at DESC NULLS LAST,d.created_at DESC
		LIMIT 100`)
	if err != nil {
		return PortalAdminDashboard{}, err
	}
	for connectionRows.Next() {
		var item PortalConnection
		if err := connectionRows.Scan(&item.ID, &item.Name, &item.DeviceType, &item.TrustState, &item.LastSeenAt, &item.RunningSessions, &item.LastOutputSeq); err != nil {
			connectionRows.Close()
			return PortalAdminDashboard{}, err
		}
		result.Connections = append(result.Connections, item)
	}
	if err := connectionRows.Err(); err != nil {
		connectionRows.Close()
		return PortalAdminDashboard{}, err
	}
	connectionRows.Close()

	auditRows, err := s.Pool.Query(ctx, `
		SELECT id,action,target_type,coalesce(target_id::text,''),result,trace_id,created_at
		FROM audit_events ORDER BY id DESC LIMIT 40`)
	if err != nil {
		return PortalAdminDashboard{}, err
	}
	for auditRows.Next() {
		var item PortalAuditEvent
		if err := auditRows.Scan(&item.ID, &item.Action, &item.TargetType, &item.TargetID, &item.Result, &item.TraceID, &item.CreatedAt); err != nil {
			auditRows.Close()
			return PortalAdminDashboard{}, err
		}
		result.AuditEvents = append(result.AuditEvents, item)
	}
	if err := auditRows.Err(); err != nil {
		auditRows.Close()
		return PortalAdminDashboard{}, err
	}
	auditRows.Close()
	return result, nil
}

func (s *Postgres) UpdatePortalAccountRole(ctx context.Context, actorID, targetID uuid.UUID, newRole Role, traceID string) error {
	if !newRole.Valid() {
		return ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock($1)`, portalBootstrapLockID); err != nil {
		return err
	}
	if err := requireSuperAdmin(ctx, tx, actorID); err != nil {
		return err
	}
	var oldRole Role
	var status string
	if err := tx.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1 FOR UPDATE`, targetID).Scan(&oldRole, &status); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ErrNotFound
		}
		return err
	}
	if status == "DELETED" {
		return ErrDenied
	}
	if oldRole == RoleSuperAdmin && newRole != RoleSuperAdmin && status == "ACTIVE" {
		if err := protectLastSuperAdmin(ctx, tx); err != nil {
			return err
		}
	}
	if _, err := tx.Exec(ctx, `UPDATE users SET role=$2,is_admin=$3,updated_at=now() WHERE id=$1`, targetID, newRole, newRole.IsAdministrator()); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,'admin.account.role.update','account',$2,'SUCCEEDED',$3,jsonb_build_object('old_role',$4::text,'new_role',$5::text))`,
		actorID, targetID, traceID, oldRole, newRole); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) UpdatePortalAccountStatus(ctx context.Context, actorID, targetID uuid.UUID, newStatus, traceID string) error {
	if newStatus != "ACTIVE" && newStatus != "LOCKED" {
		return ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock($1)`, portalBootstrapLockID); err != nil {
		return err
	}
	if err := requireSuperAdmin(ctx, tx, actorID); err != nil {
		return err
	}
	var role Role
	var oldStatus string
	if err := tx.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1 FOR UPDATE`, targetID).Scan(&role, &oldStatus); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ErrNotFound
		}
		return err
	}
	if oldStatus == "DELETED" {
		return ErrDenied
	}
	if role == RoleSuperAdmin && oldStatus == "ACTIVE" && newStatus != "ACTIVE" {
		if err := protectLastSuperAdmin(ctx, tx); err != nil {
			return err
		}
	}
	if _, err := tx.Exec(ctx, `UPDATE users SET status=$2,updated_at=now() WHERE id=$1`, targetID, newStatus); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,'admin.account.status.update','account',$2,'SUCCEEDED',$3,jsonb_build_object('old_status',$4::text,'new_status',$5::text))`,
		actorID, targetID, traceID, oldStatus, newStatus); err != nil {
		return err
	}
	if oldStatus != newStatus {
		if _, err := tx.Exec(ctx, `
			INSERT INTO outbox(event_type,aggregate_id,payload)
			VALUES('account.status.changed',$1,jsonb_build_object('account_id',$1::uuid,'status',$2::text))`,
			targetID, newStatus); err != nil {
			return err
		}
	}
	return tx.Commit(ctx)
}

func requireSuperAdmin(ctx context.Context, tx pgx.Tx, actorID uuid.UUID) error {
	var role Role
	var status string
	if err := tx.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1 FOR UPDATE`, actorID).Scan(&role, &status); err != nil {
		return ErrDenied
	}
	if role != RoleSuperAdmin || status != "ACTIVE" {
		return ErrDenied
	}
	return nil
}

func protectLastSuperAdmin(ctx context.Context, tx pgx.Tx) error {
	var active int
	if err := tx.QueryRow(ctx, `SELECT count(*) FROM users WHERE role='SUPER_ADMIN' AND status='ACTIVE'`).Scan(&active); err != nil {
		return err
	}
	if active <= 1 {
		return ErrLastSuperAdmin
	}
	return nil
}
