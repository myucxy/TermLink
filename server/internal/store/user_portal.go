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

const userPortalDevicesSQL = `
	SELECT device.id,device.device_type,device.name,device.trust_state,
	       coalesce(device.last_seen_at>now()-($4 * interval '1 second'),false),
	       device.last_seen_at,device.created_at,device.revoked_at,device.disabled_at
	FROM devices device
	JOIN users account ON account.id=device.user_id AND account.id=$1 AND account.status='ACTIVE'
	WHERE device.user_id=$1 AND device.device_type IN ('ANDROID','WINDOWS_PC','PC_AGENT')
	  AND ($2::timestamptz IS NULL OR (device.created_at,device.id)<($2::timestamptz,$3::uuid))
	ORDER BY device.created_at DESC,device.id DESC
	LIMIT $5`

const userPortalPCsSQL = `
	SELECT coalesce(pc.id,device.id),device.id,coalesce(pc.name,device.name),
	       coalesce(pc.agent_version,'未上报'),coalesce(pc.platform,'unknown'),
	       coalesce(pc.architecture,'unknown'),coalesce(pc.platform_version,'未上报'),
	       coalesce(pc.remote_routing_enabled,true),
	       coalesce(coalesce(pc.last_seen_at,device.last_seen_at)>now()-($4 * interval '1 second'),false),
	       coalesce(pc.last_seen_at,device.last_seen_at),coalesce(pc.created_at,device.created_at)
	FROM devices device
	JOIN users account ON account.id=device.user_id AND account.id=$1 AND account.status='ACTIVE'
	LEFT JOIN pcs pc ON pc.device_id=device.id AND pc.owner_user_id=$1
	WHERE device.user_id=$1 AND device.device_type IN ('WINDOWS_PC','PC_AGENT')
	  AND ($2::timestamptz IS NULL OR (coalesce(pc.created_at,device.created_at),coalesce(pc.id,device.id))<($2::timestamptz,$3::uuid))
	ORDER BY coalesce(pc.created_at,device.created_at) DESC,coalesce(pc.id,device.id) DESC
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
		var disabledAt *time.Time
		if err := rows.Scan(&item.ID, &item.Type, &item.Name, &item.TrustState, &item.Online,
			&item.LastSeenAt, &item.CreatedAt, &item.RevokedAt, &disabledAt); err != nil {
			return nil, err
		}
		if disabledAt != nil {
			item.TrustState = "DISABLED"
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
