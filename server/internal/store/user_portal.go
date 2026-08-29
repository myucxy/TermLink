package store

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const (
	userPortalDefaultLimit = 50
	userPortalMaxLimit     = 100
)

var ErrInvalidUserPortalPage = errors.New("invalid user portal page")

// UserPortalUUIDCursor is a stable keyset cursor for records ordered by
// creation time and UUID, both descending.
type UserPortalUUIDCursor struct {
	CreatedAt time.Time `json:"created_at"`
	ID        uuid.UUID `json:"id"`
}

type UserPortalPageRequest struct {
	Limit  int                   `json:"limit"`
	Before *UserPortalUUIDCursor `json:"before,omitempty"`
}

type UserPortalAuditPageRequest struct {
	Limit    int   `json:"limit"`
	BeforeID int64 `json:"before_id,omitempty"`
}

type UserPortalAccountOverview struct {
	ID             uuid.UUID `json:"id"`
	Role           Role      `json:"role"`
	Status         string    `json:"status"`
	CreatedAt      time.Time `json:"created_at"`
	AndroidDevices int       `json:"android_devices"`
	PCDevices      int       `json:"pc_devices"`
	PCs            int       `json:"pcs"`
	OnlinePCs      int       `json:"online_pcs"`
	Sessions       int       `json:"sessions"`
	ActiveSessions int       `json:"active_sessions"`
}

type UserPortalGrantRecovery struct {
	PlanKey        string     `json:"plan_key"`
	CatalogVersion int64      `json:"catalog_version"`
	Source         string     `json:"source"`
	RestoresAt     time.Time  `json:"restores_at"`
	EndsAt         *time.Time `json:"ends_at,omitempty"`
	GraceUntil     *time.Time `json:"grace_until,omitempty"`
}

type UserPortalEntitlement struct {
	PlanKey         string                   `json:"plan_key"`
	CatalogVersion  int64                    `json:"catalog_version"`
	MaxRemotePCs    int                      `json:"max_remote_pcs"`
	MaxLiveSessions int                      `json:"max_live_sessions"`
	Version         int64                    `json:"version"`
	EffectiveAt     time.Time                `json:"effective_at"`
	ExpiresAt       *time.Time               `json:"expires_at,omitempty"`
	GraceUntil      *time.Time               `json:"grace_until,omitempty"`
	Source          string                   `json:"source"`
	Recovery        *UserPortalGrantRecovery `json:"recovery,omitempty"`
}

type UserPortalDevice struct {
	ID         uuid.UUID  `json:"id"`
	Type       string     `json:"type"`
	Name       string     `json:"name"`
	TrustState string     `json:"trust_state"`
	Online     bool       `json:"online"`
	LastSeenAt *time.Time `json:"last_seen_at,omitempty"`
	CreatedAt  time.Time  `json:"created_at"`
	RevokedAt  *time.Time `json:"revoked_at,omitempty"`
}

type UserPortalPC struct {
	ID                   uuid.UUID  `json:"id"`
	DeviceID             uuid.UUID  `json:"device_id"`
	Name                 string     `json:"name"`
	AgentVersion         string     `json:"agent_version"`
	Platform             string     `json:"platform"`
	Architecture         string     `json:"architecture"`
	PlatformVersion      string     `json:"platform_version"`
	RemoteRoutingEnabled bool       `json:"remote_routing_enabled"`
	Online               bool       `json:"online"`
	LastSeenAt           *time.Time `json:"last_seen_at,omitempty"`
	CreatedAt            time.Time  `json:"created_at"`
}

// UserPortalSession deliberately contains no terminal title, command,
// output sequence, dimensions, snapshot, ciphertext, or history fields.
type UserPortalSession struct {
	ID        uuid.UUID  `json:"id"`
	PCID      uuid.UUID  `json:"pc_id"`
	PCName    string     `json:"pc_name"`
	State     string     `json:"state"`
	Online    bool       `json:"online"`
	CreatedAt time.Time  `json:"created_at"`
	EndedAt   *time.Time `json:"ended_at,omitempty"`
}

// UserPortalSecurityActivity excludes free-form audit metadata, trace data,
// network hashes, and external transaction references.
type UserPortalSecurityActivity struct {
	ID         int64     `json:"id"`
	Action     string    `json:"action"`
	TargetType string    `json:"target_type"`
	Result     string    `json:"result"`
	CreatedAt  time.Time `json:"created_at"`
}

const userPortalAccountOverviewSQL = `
	SELECT account.id,account.role,account.status,account.created_at,
	       (SELECT count(*) FROM devices d WHERE d.user_id=account.id AND d.device_type='ANDROID'),
	       (SELECT count(*) FROM devices d WHERE d.user_id=account.id AND d.device_type IN ('WINDOWS_PC','PC_AGENT')),
	       (SELECT count(*) FROM pcs p WHERE p.owner_user_id=account.id),
	       (SELECT count(*) FROM pcs p WHERE p.owner_user_id=account.id
	          AND p.last_seen_at>now()-($2 * interval '1 second')),
	       (SELECT count(*) FROM sessions session
	          JOIN pcs p ON p.id=session.pc_id
	         WHERE p.owner_user_id=account.id AND session.state<>'DELETED'),
	       (SELECT count(*) FROM sessions session
	          JOIN pcs p ON p.id=session.pc_id
	         WHERE p.owner_user_id=account.id AND session.state IN ('RUNNING','INTERRUPTED'))
	FROM users account
	WHERE account.id=$1 AND account.status='ACTIVE'`

const userPortalEntitlementSQL = `
	SELECT coalesce(entitlement.plan_key,free_plan.plan_key),
	       coalesce(entitlement.catalog_version,free_plan.catalog_version),
	       coalesce(entitlement.version,0),
	       coalesce(entitlement.effective_at,account.created_at),
	       entitlement.expires_at,entitlement.grace_until,
	       coalesce(entitlement.source,'DEFAULT')
	FROM users account
	LEFT JOIN entitlements entitlement ON entitlement.account_id=account.id
	LEFT JOIN LATERAL (
		SELECT plan.plan_key,catalog.version AS catalog_version
		FROM plans plan
		JOIN catalog_versions catalog ON catalog.id=plan.catalog_version_id
		WHERE catalog.status='PUBLISHED' AND plan.plan_key='free'
		ORDER BY catalog.version DESC
		LIMIT 1
	) free_plan ON true
	WHERE account.id=$1 AND account.status='ACTIVE'`

const userPortalGrantRecoverySQL = `
	SELECT entitlement_grant.plan_key,entitlement_grant.catalog_version,entitlement_grant.source_type,entitlement_grant.ends_at,entitlement_grant.grace_until
	FROM entitlement_grants entitlement_grant
	JOIN users account ON account.id=entitlement_grant.account_id AND account.id=$1 AND account.status='ACTIVE'
	JOIN catalog_versions catalog ON catalog.version=entitlement_grant.catalog_version
	JOIN plans plan ON plan.catalog_version_id=catalog.id AND plan.plan_key=entitlement_grant.plan_key
	WHERE entitlement_grant.account_id=$1 AND entitlement_grant.status='ACTIVE'
	  AND entitlement_grant.starts_at<=$2
	  AND (entitlement_grant.ends_at IS NULL OR entitlement_grant.ends_at>$2)
	ORDER BY plan.price_minor DESC,plan.sort_order DESC,
	         (entitlement_grant.source_type='DEFAULT') ASC,entitlement_grant.ends_at DESC NULLS FIRST,
	         entitlement_grant.created_at DESC,entitlement_grant.id DESC
	LIMIT 1`

const userPortalDevicesSQL = `
	SELECT device.id,device.device_type,device.name,device.trust_state,
	       coalesce(device.last_seen_at>now()-($4 * interval '1 second'),false),
	       device.last_seen_at,device.created_at,device.revoked_at
	FROM devices device
	JOIN users account ON account.id=device.user_id AND account.id=$1 AND account.status='ACTIVE'
	WHERE device.user_id=$1 AND device.device_type IN ('ANDROID','WINDOWS_PC','PC_AGENT')
	  AND ($2::timestamptz IS NULL OR (device.created_at,device.id)<($2::timestamptz,$3::uuid))
	ORDER BY device.created_at DESC,device.id DESC
	LIMIT $5`

const userPortalPCsSQL = `
	SELECT pc.id,pc.device_id,pc.name,pc.agent_version,pc.platform,pc.architecture,
	       pc.platform_version,pc.remote_routing_enabled,
	       coalesce(pc.last_seen_at>now()-($4 * interval '1 second'),false),
	       pc.last_seen_at,pc.created_at
	FROM pcs pc
	JOIN users account ON account.id=pc.owner_user_id AND account.id=$1 AND account.status='ACTIVE'
	JOIN devices device ON device.id=pc.device_id AND device.user_id=$1
	WHERE pc.owner_user_id=$1
	  AND ($2::timestamptz IS NULL OR (pc.created_at,pc.id)<($2::timestamptz,$3::uuid))
	ORDER BY pc.created_at DESC,pc.id DESC
	LIMIT $5`

const userPortalSessionsSQL = `
	SELECT session.id,session.pc_id,pc.name,session.state,
	       coalesce(session.state='RUNNING' AND CASE
	           WHEN pc.session_heartbeat_capable
	             THEN session.last_seen_at>now()-($4 * interval '1 second')
	           ELSE pc.last_seen_at>now()-($4 * interval '1 second')
	       END,false),session.created_at,session.ended_at
	FROM sessions session
	JOIN pcs pc ON pc.id=session.pc_id AND pc.owner_user_id=$1
	JOIN users account ON account.id=pc.owner_user_id AND account.id=$1 AND account.status='ACTIVE'
	WHERE pc.owner_user_id=$1 AND session.state<>'DELETED' AND session.deleted_at IS NULL
	  AND ($2::timestamptz IS NULL OR (session.created_at,session.id)<($2::timestamptz,$3::uuid))
	ORDER BY session.created_at DESC,session.id DESC
	LIMIT $5`

const userPortalSecurityActivitySQL = `
	SELECT audit.id,audit.action,audit.target_type,audit.result,audit.created_at
	FROM audit_events audit
	JOIN users account ON account.id=audit.user_id AND account.id=$1 AND account.status='ACTIVE'
	WHERE audit.user_id=$1 AND ($2::bigint=0 OR audit.id<$2)
	ORDER BY audit.id DESC
	LIMIT $3`

func normalizeUserPortalLimit(limit int) int {
	if limit < 1 {
		return userPortalDefaultLimit
	}
	if limit > userPortalMaxLimit {
		return userPortalMaxLimit
	}
	return limit
}

func normalizeUserPortalPage(request UserPortalPageRequest) (int, *time.Time, uuid.UUID, error) {
	limit := normalizeUserPortalLimit(request.Limit)
	if request.Before == nil {
		return limit, nil, uuid.Nil, nil
	}
	if request.Before.CreatedAt.IsZero() || request.Before.ID == uuid.Nil {
		return 0, nil, uuid.Nil, ErrInvalidUserPortalPage
	}
	createdAt := request.Before.CreatedAt.UTC()
	return limit, &createdAt, request.Before.ID, nil
}

func userPortalEntitlementForAPI(result UserPortalEntitlement) UserPortalEntitlement {
	result.MaxRemotePCs = UnlimitedQuota
	result.MaxLiveSessions = UnlimitedQuota
	return result
}

func (s *Postgres) requireUserPortalAccount(ctx context.Context, accountID uuid.UUID) error {
	var status string
	err := s.Pool.QueryRow(ctx, `SELECT status FROM users WHERE id=$1`, accountID).Scan(&status)
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

func (s *Postgres) GetUserPortalAccountOverview(ctx context.Context, accountID uuid.UUID) (UserPortalAccountOverview, error) {
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return UserPortalAccountOverview{}, err
	}
	var result UserPortalAccountOverview
	err := s.Pool.QueryRow(ctx, userPortalAccountOverviewSQL, accountID, int(sessionOnlineWindow/time.Second)).Scan(
		&result.ID, &result.Role, &result.Status, &result.CreatedAt,
		&result.AndroidDevices, &result.PCDevices, &result.PCs, &result.OnlinePCs,
		&result.Sessions, &result.ActiveSessions,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalAccountOverview{}, ErrDenied
	}
	return result, err
}

// GetUserPortalEntitlement returns the materialized current entitlement and,
// for a temporary current grant, the grant that is expected to take over at
// its expiry. Quantity fields retain the public unlimited sentinel regardless
// of legacy catalog values.
func (s *Postgres) GetUserPortalEntitlement(ctx context.Context, accountID uuid.UUID) (UserPortalEntitlement, error) {
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return UserPortalEntitlement{}, err
	}
	var result UserPortalEntitlement
	err := s.Pool.QueryRow(ctx, userPortalEntitlementSQL, accountID).Scan(
		&result.PlanKey, &result.CatalogVersion, &result.Version, &result.EffectiveAt,
		&result.ExpiresAt, &result.GraceUntil, &result.Source,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return UserPortalEntitlement{}, ErrDenied
	}
	if err != nil {
		return UserPortalEntitlement{}, err
	}
	result = userPortalEntitlementForAPI(result)
	if result.ExpiresAt == nil || !result.ExpiresAt.After(time.Now().UTC()) {
		return result, nil
	}
	var recovery UserPortalGrantRecovery
	err = s.Pool.QueryRow(ctx, userPortalGrantRecoverySQL, accountID, *result.ExpiresAt).Scan(
		&recovery.PlanKey, &recovery.CatalogVersion, &recovery.Source,
		&recovery.EndsAt, &recovery.GraceUntil,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return result, nil
	}
	if err != nil {
		return UserPortalEntitlement{}, err
	}
	recovery.RestoresAt = result.ExpiresAt.UTC()
	result.Recovery = &recovery
	return result, nil
}

func (s *Postgres) ListUserPortalDevices(ctx context.Context, accountID uuid.UUID, request UserPortalPageRequest) ([]UserPortalDevice, error) {
	limit, beforeAt, beforeID, err := normalizeUserPortalPage(request)
	if err != nil {
		return nil, err
	}
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return nil, err
	}
	rows, err := s.Pool.Query(ctx, userPortalDevicesSQL, accountID, beforeAt, beforeID, int(sessionOnlineWindow/time.Second), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]UserPortalDevice, 0, limit)
	for rows.Next() {
		var item UserPortalDevice
		if err := rows.Scan(&item.ID, &item.Type, &item.Name, &item.TrustState, &item.Online,
			&item.LastSeenAt, &item.CreatedAt, &item.RevokedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) ListUserPortalPCs(ctx context.Context, accountID uuid.UUID, request UserPortalPageRequest) ([]UserPortalPC, error) {
	limit, beforeAt, beforeID, err := normalizeUserPortalPage(request)
	if err != nil {
		return nil, err
	}
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return nil, err
	}
	rows, err := s.Pool.Query(ctx, userPortalPCsSQL, accountID, beforeAt, beforeID, int(sessionOnlineWindow/time.Second), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]UserPortalPC, 0, limit)
	for rows.Next() {
		var item UserPortalPC
		if err := rows.Scan(&item.ID, &item.DeviceID, &item.Name, &item.AgentVersion,
			&item.Platform, &item.Architecture, &item.PlatformVersion,
			&item.RemoteRoutingEnabled, &item.Online, &item.LastSeenAt, &item.CreatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) ListUserPortalSessions(ctx context.Context, accountID uuid.UUID, request UserPortalPageRequest) ([]UserPortalSession, error) {
	limit, beforeAt, beforeID, err := normalizeUserPortalPage(request)
	if err != nil {
		return nil, err
	}
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return nil, err
	}
	rows, err := s.Pool.Query(ctx, userPortalSessionsSQL, accountID, beforeAt, beforeID, int(sessionOnlineWindow/time.Second), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]UserPortalSession, 0, limit)
	for rows.Next() {
		var item UserPortalSession
		if err := rows.Scan(&item.ID, &item.PCID, &item.PCName, &item.State, &item.Online,
			&item.CreatedAt, &item.EndedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) ListUserPortalSecurityActivity(ctx context.Context, accountID uuid.UUID, request UserPortalAuditPageRequest) ([]UserPortalSecurityActivity, error) {
	if request.BeforeID < 0 {
		return nil, ErrInvalidUserPortalPage
	}
	if err := s.requireUserPortalAccount(ctx, accountID); err != nil {
		return nil, err
	}
	limit := normalizeUserPortalLimit(request.Limit)
	rows, err := s.Pool.Query(ctx, userPortalSecurityActivitySQL, accountID, request.BeforeID, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]UserPortalSecurityActivity, 0, limit)
	for rows.Next() {
		var item UserPortalSecurityActivity
		if err := rows.Scan(&item.ID, &item.Action, &item.TargetType, &item.Result, &item.CreatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}
