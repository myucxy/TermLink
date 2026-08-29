// Package store implements the PostgreSQL authority for TermLink metadata.
// Terminal plaintext is deliberately absent from every model and query.
package store

import (
	"context"
	"crypto/ed25519"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/billing"
	"github.com/termlink/termlink/server/migrations"
)

var (
	ErrNotFound                 = errors.New("record not found")
	ErrConflict                 = errors.New("record conflict")
	ErrDenied                   = errors.New("operation denied")
	ErrSessionActive            = errors.New("session is active")
	ErrInvalidHistoryPage       = errors.New("invalid history page")
	ErrHistoryAnchorAhead       = errors.New("history anchor is ahead of the session")
	ErrInvalidHistorySnapshot   = errors.New("invalid history snapshot")
	ErrInvalidHistoryTranscript = errors.New("invalid history transcript")
	ErrInvalidHistoryRecipient  = errors.New("invalid history recipient")
	// ErrEntitlement is retained for wire compatibility with older clients;
	// current PC/session paths no longer produce it because account quantities
	// are unlimited.
	ErrEntitlement = errors.New("entitlement limit reached")
)

// UnlimitedQuota is the wire-level value used for legacy entitlement fields
// whose enforcement has been removed. The columns remain for catalog and
// billing compatibility, while API consumers can render -1 as “unlimited”.
const UnlimitedQuota = -1

const (
	HistoryFormatBoundedEventReplay       = "BOUNDED_EVENT_REPLAY"
	HistoryFormatTerminalSnapshot         = "TERMLINK_TERMINAL_SNAPSHOT"
	HistoryFormatTerminalTranscript       = "TERMLINK_TERMINAL_TRANSCRIPT"
	MaxSnapshotCiphertextBytes      int64 = 2 << 20
	MaxTranscriptCiphertextBytes    int64 = 512 << 10
	MaxTranscriptPublicationPages         = 100_000
)

// sessionOnlineWindow is the server-authoritative reachability window. A
// heartbeat-capable Agent must refresh the exact session; legacy Agents fall
// back to PC presence until they advertise that capability.
const sessionOnlineWindow = 45 * time.Second

const latestSchemaVersion int64 = migrations.LatestVersion

type Postgres struct {
	Pool *pgxpool.Pool
	// AutoTrustDevices is enabled by the first-version production server. The
	// legacy pending/grant implementation remains available for migrations and
	// focused compatibility tests without exposing it as a product step.
	AutoTrustDevices bool
}

type OpenOptions struct {
	MaxConns          int32
	MinConns          int32
	MaxConnLifetime   time.Duration
	MaxConnIdleTime   time.Duration
	HealthCheckPeriod time.Duration
	ConnectTimeout    time.Duration
}

func Open(ctx context.Context, databaseURL string) (*Postgres, error) {
	return OpenWithOptions(ctx, databaseURL, OpenOptions{MinConns: 2})
}

func OpenWithOptions(ctx context.Context, databaseURL string, options OpenOptions) (*Postgres, error) {
	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		return nil, fmt.Errorf("parse PostgreSQL URL: %w", err)
	}
	if options.MaxConns <= 0 {
		options.MaxConns = 20
	}
	if options.MinConns < 0 {
		options.MinConns = 0
	}
	if options.MaxConnLifetime <= 0 {
		options.MaxConnLifetime = 30 * time.Minute
	}
	if options.MaxConnIdleTime <= 0 {
		options.MaxConnIdleTime = 5 * time.Minute
	}
	if options.HealthCheckPeriod <= 0 {
		options.HealthCheckPeriod = time.Minute
	}
	if options.ConnectTimeout <= 0 {
		options.ConnectTimeout = 5 * time.Second
	}
	config.MaxConns = options.MaxConns
	config.MinConns = options.MinConns
	config.MaxConnLifetime = options.MaxConnLifetime
	config.MaxConnIdleTime = options.MaxConnIdleTime
	config.HealthCheckPeriod = options.HealthCheckPeriod
	config.ConnConfig.ConnectTimeout = options.ConnectTimeout
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		return nil, fmt.Errorf("open PostgreSQL: %w", err)
	}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		return nil, fmt.Errorf("ping PostgreSQL: %w", err)
	}
	return &Postgres{Pool: pool}, nil
}

func (s *Postgres) Close() {
	if s != nil && s.Pool != nil {
		s.Pool.Close()
	}
}
func (s *Postgres) Ping(ctx context.Context) error { return s.Pool.Ping(ctx) }

func (s *Postgres) VerifySchema(ctx context.Context) error {
	var version int64
	if err := s.Pool.QueryRow(ctx, `SELECT max(version) FROM schema_migrations`).Scan(&version); err != nil {
		return fmt.Errorf("production schema unavailable; run migrate: %w", err)
	}
	if version != latestSchemaVersion {
		return fmt.Errorf("unsupported production schema version %d", version)
	}
	return nil
}

type RegisterDeviceInput struct {
	ID                uuid.UUID
	Type              string
	Name              string
	SigningPublicKey  []byte
	ExchangePublicKey []byte
	KeyID             string
}

type DeviceRecord struct {
	ID                uuid.UUID  `json:"id"`
	Type              string     `json:"type"`
	Name              string     `json:"name"`
	KeyID             string     `json:"key_id"`
	SigningPublicKey  []byte     `json:"signing_public_key,omitempty"`
	ExchangePublicKey []byte     `json:"exchange_public_key,omitempty"`
	TrustState        string     `json:"trust_state"`
	ApprovedBy        *uuid.UUID `json:"approved_by,omitempty"`
	ApprovedAt        *time.Time `json:"approved_at,omitempty"`
	RevokedAt         *time.Time `json:"revoked_at,omitempty"`
	CreatedAt         time.Time  `json:"created_at"`
}

// RegisterDevice creates the user on first OIDC login. Production enables
// AutoTrustDevices, making non-revoked devices immediately usable inside the
// same account. The bootstrap/PENDING relationships remain for compatibility
// tests and deployments that explicitly leave automatic account access off.
func (s *Postgres) RegisterDevice(ctx context.Context, subjectHash [32]byte, input RegisterDeviceInput, traceID string) (uuid.UUID, DeviceRecord, bool, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return uuid.Nil, DeviceRecord{}, false, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	userID, err := ensureUser(ctx, tx, subjectHash)
	if err != nil {
		return uuid.Nil, DeviceRecord{}, false, err
	}
	var bootstrapConsumedAt *time.Time
	if err := tx.QueryRow(ctx, `
		SELECT windows_bootstrap_consumed_at
		FROM users WHERE id=$1 FOR UPDATE`, userID).Scan(&bootstrapConsumedAt); err != nil {
		return uuid.Nil, DeviceRecord{}, false, err
	}
	bootstrap := IsPCAgentDeviceType(input.Type) && bootstrapConsumedAt == nil
	trustState := "PENDING"
	var approvedAt *time.Time
	var approvedBy *uuid.UUID
	if bootstrap || s.AutoTrustDevices {
		trustState = "TRUSTED"
		now := time.Now().UTC()
		approvedAt = &now
	}
	// The partial unique index reserves a single trusted Windows PC with a
	// nil approver as the account bootstrap device.  Auto-trusting a later PC
	// must retain the bootstrap relationship instead of attempting to create a
	// second nil-approved row (which would make the Agent an unknown device).
	// The user row is locked above, so this lookup is serialized with all other
	// registrations for the account.
	if IsPCAgentDeviceType(input.Type) && !bootstrap && s.AutoTrustDevices {
		var bootstrapID uuid.UUID
		err := tx.QueryRow(ctx, `
			SELECT id
			FROM devices
			WHERE user_id=$1 AND device_type IN ('WINDOWS_PC','PC_AGENT') AND approved_by IS NULL
			ORDER BY created_at,id
			LIMIT 1`, userID).Scan(&bootstrapID)
		if err == nil {
			approvedBy = &bootstrapID
		} else if !errors.Is(err, pgx.ErrNoRows) {
			return uuid.Nil, DeviceRecord{}, false, err
		}
	}
	var record DeviceRecord
	err = tx.QueryRow(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_by,approved_at)
		VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
		ON CONFLICT (id) DO NOTHING
		RETURNING id,device_type,name,key_id,signing_public_key,exchange_public_key,trust_state,approved_by,approved_at,revoked_at,created_at`,
		input.ID, userID, input.Type, input.Name, input.SigningPublicKey, input.ExchangePublicKey, input.KeyID, trustState, approvedBy, approvedAt,
	).Scan(&record.ID, &record.Type, &record.Name, &record.KeyID, &record.SigningPublicKey, &record.ExchangePublicKey, &record.TrustState, &record.ApprovedBy, &record.ApprovedAt, &record.RevokedAt, &record.CreatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return uuid.Nil, DeviceRecord{}, false, ErrConflict
	}
	if err != nil {
		return uuid.Nil, DeviceRecord{}, false, err
	}
	if bootstrap {
		command, err := tx.Exec(ctx, `
			UPDATE users SET windows_bootstrap_consumed_at=now()
			WHERE id=$1 AND windows_bootstrap_consumed_at IS NULL`, userID)
		if err != nil {
			return uuid.Nil, DeviceRecord{}, false, err
		}
		if command.RowsAffected() != 1 {
			return uuid.Nil, DeviceRecord{}, false, ErrConflict
		}
	}
	if err := ensureFreeEntitlement(ctx, tx, userID); err != nil {
		return uuid.Nil, DeviceRecord{}, false, err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,$2,'device.register','device',$2,'SUCCEEDED',$3,jsonb_build_object('trust_state',$4::text,'bootstrap',$5::boolean))`, userID, record.ID, traceID, trustState, bootstrap); err != nil {
		return uuid.Nil, DeviceRecord{}, false, err
	}
	if err := tx.Commit(ctx); err != nil {
		return uuid.Nil, DeviceRecord{}, false, err
	}
	return userID, record, bootstrap, nil
}

func ensureUser(ctx context.Context, tx pgx.Tx, subjectHash [32]byte) (uuid.UUID, error) {
	var userID uuid.UUID
	err := tx.QueryRow(ctx, `SELECT id FROM users WHERE oidc_subject_hash=$1 FOR UPDATE`, subjectHash[:]).Scan(&userID)
	if err == nil {
		return userID, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return uuid.Nil, err
	}
	userID = uuid.New()
	_, err = tx.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subjectHash[:])
	return userID, err
}

func ensureFreeEntitlement(ctx context.Context, tx pgx.Tx, userID uuid.UUID) error {
	_, err := tx.Exec(ctx, `
		INSERT INTO entitlements(account_id,plan_key,catalog_version,max_remote_pcs,max_live_sessions,feature_flags,version,effective_at)
		SELECT $1,p.plan_key,c.version,p.max_remote_pcs,p.max_live_sessions,p.feature_flags,1,now()
		FROM catalog_versions c JOIN plans p ON p.catalog_version_id=c.id
		WHERE c.status='PUBLISHED' AND p.plan_key='free'
		ON CONFLICT (account_id) DO NOTHING`, userID)
	return err
}

func (s *Postgres) LoadDeviceForSubject(ctx context.Context, subjectHash [32]byte, deviceID uuid.UUID) (auth.Device, error) {
	var result auth.Device
	var key []byte
	err := s.Pool.QueryRow(ctx, `
		SELECT d.id,d.user_id,d.device_type,d.signing_public_key,d.trust_state
		FROM devices d JOIN users u ON u.id=d.user_id
		WHERE d.id=$1 AND u.oidc_subject_hash=$2 AND u.status='ACTIVE'`, deviceID, subjectHash[:],
	).Scan(&result.ID, &result.UserID, &result.Type, &key, &result.TrustState)
	if errors.Is(err, pgx.ErrNoRows) {
		return auth.Device{}, ErrNotFound
	}
	if err != nil {
		return auth.Device{}, err
	}
	if len(key) != ed25519.PublicKeySize {
		return auth.Device{}, fmt.Errorf("invalid stored signing key")
	}
	result.SigningPublicKey = ed25519.PublicKey(key)
	return result, nil
}

func (s *Postgres) ListDevices(ctx context.Context, userID uuid.UUID) ([]DeviceRecord, error) {
	rows, err := s.Pool.Query(ctx, `SELECT id,device_type,name,key_id,signing_public_key,exchange_public_key,trust_state,approved_by,approved_at,revoked_at,created_at FROM devices WHERE user_id=$1 ORDER BY created_at,id`, userID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]DeviceRecord, 0)
	for rows.Next() {
		var item DeviceRecord
		if err := rows.Scan(&item.ID, &item.Type, &item.Name, &item.KeyID, &item.SigningPublicKey, &item.ExchangePublicKey, &item.TrustState, &item.ApprovedBy, &item.ApprovedAt, &item.RevokedAt, &item.CreatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) GetDevice(ctx context.Context, userID, deviceID uuid.UUID) (DeviceRecord, error) {
	var item DeviceRecord
	err := s.Pool.QueryRow(ctx, `SELECT id,device_type,name,key_id,signing_public_key,exchange_public_key,trust_state,approved_by,approved_at,revoked_at,created_at FROM devices WHERE id=$1 AND user_id=$2`, deviceID, userID).
		Scan(&item.ID, &item.Type, &item.Name, &item.KeyID, &item.SigningPublicKey, &item.ExchangePublicKey, &item.TrustState, &item.ApprovedBy, &item.ApprovedAt, &item.RevokedAt, &item.CreatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return DeviceRecord{}, ErrNotFound
	}
	return item, err
}

// SelfTrustAndroidDevice atomically consumes one OIDC token identifier and
// promotes only the calling Android device. It deliberately creates no PC
// grant: device trust and per-PC VIEW/CONTROL/MANAGE authority are separate.
// A repeated call after a successful transition is idempotent and does not
// create another audit event.
func (s *Postgres) SelfTrustAndroidDevice(ctx context.Context, userID, deviceID uuid.UUID, tokenIDHash [32]byte, traceID string) (DeviceRecord, bool, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return DeviceRecord{}, false, err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	var record DeviceRecord
	err = tx.QueryRow(ctx, `
		SELECT id,device_type,name,key_id,signing_public_key,exchange_public_key,
		       trust_state,approved_by,approved_at,revoked_at,created_at
		FROM devices
		WHERE id=$1 AND user_id=$2
		FOR UPDATE`, deviceID, userID,
	).Scan(&record.ID, &record.Type, &record.Name, &record.KeyID, &record.SigningPublicKey,
		&record.ExchangePublicKey, &record.TrustState, &record.ApprovedBy,
		&record.ApprovedAt, &record.RevokedAt, &record.CreatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return DeviceRecord{}, false, ErrDenied
	}
	if err != nil {
		return DeviceRecord{}, false, err
	}
	if record.Type != "ANDROID" || record.TrustState == "REVOKED" {
		return DeviceRecord{}, false, ErrDenied
	}
	if record.TrustState == "TRUSTED" {
		if err := tx.Commit(ctx); err != nil {
			return DeviceRecord{}, false, err
		}
		return record, false, nil
	}

	command, err := tx.Exec(ctx, `
		INSERT INTO device_self_trust_tokens(jti_hash,user_id,device_id)
		VALUES($1,$2,$3)
		ON CONFLICT (jti_hash) DO NOTHING`, tokenIDHash[:], userID, deviceID)
	if err != nil {
		return DeviceRecord{}, false, err
	}
	if command.RowsAffected() != 1 {
		return DeviceRecord{}, false, ErrDenied
	}

	err = tx.QueryRow(ctx, `
		UPDATE devices
		SET trust_state='TRUSTED',approved_by=id,approved_at=now()
		WHERE id=$1 AND user_id=$2 AND device_type='ANDROID' AND trust_state='PENDING'
		RETURNING id,device_type,name,key_id,signing_public_key,exchange_public_key,
		          trust_state,approved_by,approved_at,revoked_at,created_at`, deviceID, userID,
	).Scan(&record.ID, &record.Type, &record.Name, &record.KeyID, &record.SigningPublicKey,
		&record.ExchangePublicKey, &record.TrustState, &record.ApprovedBy,
		&record.ApprovedAt, &record.RevokedAt, &record.CreatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return DeviceRecord{}, false, ErrDenied
	}
	if err != nil {
		return DeviceRecord{}, false, err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,$2,'device.self_trust','device',$2,'SUCCEEDED',$3,
		       jsonb_build_object('device_type','ANDROID','grants_created',false))`,
		userID, deviceID, traceID); err != nil {
		return DeviceRecord{}, false, err
	}
	if err := tx.Commit(ctx); err != nil {
		return DeviceRecord{}, false, err
	}
	return record, true, nil
}

type PairingChallengeInput struct {
	ID             uuid.UUID
	TargetDeviceID uuid.UUID
	PCID           uuid.UUID
	Permission     string
	CodeHash       [32]byte
	ChallengeNonce [32]byte
	ExpiresAt      time.Time
}

type PairingChallengeRecord struct {
	ID                      uuid.UUID
	TargetDeviceID          uuid.UUID
	TargetName              string
	TargetSigningPublicKey  []byte
	TargetExchangePublicKey []byte
	PCID                    uuid.UUID
	Permission              string
	ExpiresAt               time.Time
}

// CreatePairingChallenge records a five-minute, target-specific approval
// intent. Only the PC that owns the selected route, or a trusted device with a
// MANAGE grant for that PC, can create one.
func (s *Postgres) CreatePairingChallenge(ctx context.Context, userID, actorID uuid.UUID, input PairingChallengeInput) (PairingChallengeRecord, error) {
	if input.ID == uuid.Nil || input.TargetDeviceID == uuid.Nil || input.PCID == uuid.Nil ||
		(input.Permission != "VIEW" && input.Permission != "CONTROL" && input.Permission != "MANAGE") ||
		!input.ExpiresAt.After(time.Now().UTC()) {
		return PairingChallengeRecord{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return PairingChallengeRecord{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	var record PairingChallengeRecord
	err = tx.QueryRow(ctx, `
		SELECT $3::uuid,t.id,t.name,t.signing_public_key,t.exchange_public_key,p.id,$5::text,$6::timestamptz
		FROM devices actor
		JOIN devices t ON t.user_id=actor.user_id
		JOIN pcs p ON p.id=$4 AND p.owner_user_id=actor.user_id AND p.remote_routing_enabled
		LEFT JOIN device_grants g ON g.pc_id=p.id AND g.device_id=actor.id AND (g.expires_at IS NULL OR g.expires_at>now())
		WHERE actor.id=$2 AND actor.user_id=$1 AND actor.trust_state='TRUSTED'
		  AND t.id=$7 AND t.trust_state='PENDING'
		  AND (p.device_id=actor.id OR g.permission='MANAGE')`,
		userID, actorID, input.ID, input.PCID, input.Permission, input.ExpiresAt, input.TargetDeviceID,
	).Scan(&record.ID, &record.TargetDeviceID, &record.TargetName, &record.TargetSigningPublicKey,
		&record.TargetExchangePublicKey, &record.PCID, &record.Permission, &record.ExpiresAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return PairingChallengeRecord{}, ErrDenied
	}
	if err != nil {
		return PairingChallengeRecord{}, err
	}
	if _, err := tx.Exec(ctx, `UPDATE pairing_challenges SET consumed_at=now() WHERE user_id=$1 AND target_device_id=$2 AND consumed_at IS NULL`, userID, input.TargetDeviceID); err != nil {
		return PairingChallengeRecord{}, err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO pairing_challenges(id,user_id,approving_device_id,target_device_id,code_hash,challenge_nonce,expires_at,pc_id,permission)
		VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)`,
		input.ID, userID, actorID, input.TargetDeviceID, input.CodeHash[:], input.ChallengeNonce[:], input.ExpiresAt, input.PCID, input.Permission,
	); err != nil {
		return PairingChallengeRecord{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return PairingChallengeRecord{}, err
	}
	return record, nil
}

// CompletePairingChallenge consumes the challenge even when the supplied
// proof is wrong. This gives a pending device one attempt per short-lived
// challenge and prevents online short-code guessing.
func (s *Postgres) CompletePairingChallenge(ctx context.Context, userID, targetID, challengeID uuid.UUID, codeHash, challengeNonce []byte, traceID string) error {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var storedHash, storedNonce []byte
	var actorID, pcID uuid.UUID
	var permission string
	var active bool
	err = tx.QueryRow(ctx, `
		SELECT code_hash,challenge_nonce,approving_device_id,pc_id,permission,
		       consumed_at IS NULL AND expires_at>now()
		FROM pairing_challenges
		WHERE id=$1 AND user_id=$2 AND target_device_id=$3
		FOR UPDATE`, challengeID, userID, targetID,
	).Scan(&storedHash, &storedNonce, &actorID, &pcID, &permission, &active)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	}
	if err != nil {
		return err
	}
	if !active {
		return ErrConflict
	}
	if _, err := tx.Exec(ctx, `UPDATE pairing_challenges SET consumed_at=now() WHERE id=$1`, challengeID); err != nil {
		return err
	}
	proofMatches := len(codeHash) == 32 && len(challengeNonce) == 32 &&
		hmac.Equal(storedHash, codeHash) && hmac.Equal(storedNonce, challengeNonce)
	if !proofMatches {
		if err := tx.Commit(ctx); err != nil {
			return err
		}
		return ErrDenied
	}
	command, err := tx.Exec(ctx, `
		WITH actor AS (
			SELECT actor.id
			FROM devices actor
			JOIN pcs p ON p.id=$4 AND p.owner_user_id=actor.user_id AND p.remote_routing_enabled
			LEFT JOIN device_grants g ON g.pc_id=p.id AND g.device_id=actor.id AND (g.expires_at IS NULL OR g.expires_at>now())
			WHERE actor.id=$3 AND actor.user_id=$1 AND actor.trust_state='TRUSTED'
			  AND (p.device_id=actor.id OR g.permission='MANAGE')
		)
		UPDATE devices SET trust_state='TRUSTED',approved_by=$3,approved_at=now()
		WHERE id=$2 AND user_id=$1 AND trust_state='PENDING' AND EXISTS(SELECT 1 FROM actor)`,
		userID, targetID, actorID, pcID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		if err := tx.Commit(ctx); err != nil {
			return err
		}
		return ErrDenied
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO device_grants(device_id,pc_id,permission,approved_by)
		VALUES($1,$2,$3,$4)
		ON CONFLICT(device_id,pc_id) DO UPDATE SET permission=excluded.permission,approved_by=excluded.approved_by,expires_at=NULL,updated_at=now()`,
		targetID, pcID, permission, actorID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,$2,'device.pair','device',$3,'SUCCEEDED',$4,jsonb_build_object('pc_id',$5::uuid,'permission',$6::text,'challenge_id',$7::uuid))`,
		userID, actorID, targetID, traceID, pcID, permission, challengeID); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

// RevokeDevice applies account-scoped MANAGE semantics in auto-trust mode. A
// revoked Android is removed from every PC recipient snapshot on the account,
// so each affected key epoch advances even when no device_grants row exists.
// Legacy mode retains its bootstrap/explicit-MANAGE authorization rules.
func (s *Postgres) RevokeDevice(ctx context.Context, userID, actorID, targetID uuid.UUID, traceID string) error {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	command, err := tx.Exec(ctx, `
		WITH actor AS (
			SELECT id,device_type,approved_by
			FROM devices
			WHERE id=$1 AND user_id=$2 AND trust_state='TRUSTED'
		), target AS (
			SELECT id,device_type
			FROM devices
			WHERE id=$3 AND user_id=$2 AND trust_state<>'REVOKED'
		), affected_pcs AS (
			SELECT pc_id AS id
			FROM device_grants
			WHERE device_id=$3 AND (expires_at IS NULL OR expires_at>now())
			UNION
			SELECT id FROM pcs WHERE device_id=$3 AND owner_user_id=$2
			UNION
			SELECT p.id
			FROM pcs p
			WHERE $4 AND p.owner_user_id=$2
			  AND EXISTS(SELECT 1 FROM target WHERE target.device_type='ANDROID')
		), authority AS (
			SELECT 1
			FROM actor
			WHERE actor.id=$3
			   OR $4
			   OR (actor.device_type IN ('WINDOWS_PC','PC_AGENT') AND actor.approved_by IS NULL)
			   OR (
				EXISTS(SELECT 1 FROM affected_pcs)
				AND NOT EXISTS (
					SELECT 1
					FROM affected_pcs affected
					JOIN pcs p ON p.id=affected.id AND p.owner_user_id=$2
					LEFT JOIN device_grants manager
					  ON manager.pc_id=p.id AND manager.device_id=$1
					 AND (manager.expires_at IS NULL OR manager.expires_at>now())
					WHERE p.device_id<>$1 AND COALESCE(manager.permission,'')<>'MANAGE'
				)
			   )
		)
		UPDATE devices SET trust_state='REVOKED',revoked_at=now()
		WHERE id=$3 AND user_id=$2 AND trust_state<>'REVOKED'
		  AND EXISTS(SELECT 1 FROM target) AND EXISTS(SELECT 1 FROM authority)`, actorID, userID, targetID, s.AutoTrustDevices)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	// Advancing every affected PC epoch before removing grants ensures that a
	// revoked device is never included in newly encrypted live/history data.
	// A revoked PC route is disabled as part of the same transaction.
	if _, err := tx.Exec(ctx, `
		UPDATE pcs SET key_epoch=key_epoch+1,
			remote_routing_enabled=CASE WHEN device_id=$1 THEN false ELSE remote_routing_enabled END
		WHERE owner_user_id=$2 AND (
			device_id=$1 OR id IN (SELECT pc_id FROM device_grants WHERE device_id=$1)
			OR ($3 AND EXISTS (
				SELECT 1 FROM devices target
				WHERE target.id=$1 AND target.user_id=$2 AND target.device_type='ANDROID'
			))
		)`, targetID, userID, s.AutoTrustDevices); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `
		DELETE FROM device_grants
		WHERE device_id=$1 OR pc_id IN (SELECT id FROM pcs WHERE device_id=$1 AND owner_user_id=$2)`, targetID, userID); err != nil {
		return err
	}
	rows, err := tx.Query(ctx, `
		DELETE FROM control_leases
		WHERE session_id IN (
			SELECT s.id FROM sessions s JOIN pcs p ON p.id=s.pc_id
			WHERE p.device_id=$1 AND p.owner_user_id=$2
		)
		RETURNING session_id,holder_device_id,lease_id,version`, targetID, userID)
	if err != nil {
		return err
	}
	revokedLeases, err := scanRevokedLeases(rows)
	if err != nil {
		return err
	}
	if err := enqueueRevokedLeases(ctx, tx, revokedLeases); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO outbox(event_type,aggregate_id,payload) VALUES('device.revoked',$1,jsonb_build_object('user_id',$2::uuid,'device_id',$1::uuid))`, targetID, userID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id) VALUES($1,$2,'device.revoke','device',$3,'SUCCEEDED',$4)`, userID, actorID, targetID, traceID); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

type PCRecord struct {
	ID                        uuid.UUID                   `json:"id"`
	DeviceID                  uuid.UUID                   `json:"device_id"`
	Name                      string                      `json:"name"`
	AgentVersion              string                      `json:"agent_version"`
	Platform                  string                      `json:"platform"`
	Architecture              string                      `json:"architecture"`
	PlatformVersion           string                      `json:"platform_version"`
	Capabilities              []string                    `json:"capabilities"`
	KeyEpoch                  int64                       `json:"key_epoch"`
	RemoteRoutingEnabled      bool                        `json:"remote_routing_enabled"`
	SessionHeartbeatCapable   bool                        `json:"session_heartbeat_capable"`
	SessionHeartbeatEnabledAt *time.Time                  `json:"session_heartbeat_enabled_at,omitempty"`
	RemoteAgentUpdateCapable  bool                        `json:"remote_agent_update_capable"`
	SupportedShells           []string                    `json:"supported_shells"`
	TerminalCommands          []TerminalCommandCapability `json:"terminal_commands"`
	Online                    bool                        `json:"online"`
	LastSeenAt                *time.Time                  `json:"last_seen_at,omitempty"`
}

type TerminalCommandCapability struct {
	ID          string `json:"id"`
	DisplayName string `json:"display_name"`
	ShellType   string `json:"shell_type"`
	Source      string `json:"source"`
}

func (s *Postgres) UpsertPC(ctx context.Context, userID, deviceID uuid.UUID, item PCRecord) error {
	applyLegacyPCDefaults(&item)
	if item.TerminalCommands == nil {
		item.TerminalCommands = []TerminalCommandCapability{}
	}
	terminalCommands, err := json.Marshal(item.TerminalCommands)
	if err != nil {
		return fmt.Errorf("encode terminal command capabilities: %w", err)
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	// PC routes are account-owned resources.  Entitlement rows still exist for
	// billing/catalog compatibility, but they no longer gate registration: one
	// account may register any number of trusted Windows PCs.
	var trustedDevice int
	if err := tx.QueryRow(ctx, `
		SELECT 1
		FROM devices d
		WHERE d.id=$1 AND d.user_id=$2 AND d.device_type IN ('WINDOWS_PC','PC_AGENT') AND d.trust_state='TRUSTED'
		FOR UPDATE`, deviceID, userID).Scan(&trustedDevice); errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	} else if err != nil {
		return err
	}
	var existingOwner, existingDevice uuid.UUID
	err = tx.QueryRow(ctx, `SELECT owner_user_id,device_id FROM pcs WHERE id=$1 FOR UPDATE`, item.ID).Scan(&existingOwner, &existingDevice)
	hasExisting := err == nil
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return err
	}
	if hasExisting && (existingOwner != userID || existingDevice != deviceID) {
		return ErrDenied
	}
	_, err = tx.Exec(ctx, `
		INSERT INTO pcs(
			id,owner_user_id,device_id,name,agent_version,key_epoch,last_seen_at,
			session_heartbeat_capable,session_heartbeat_enabled_at,supported_shells,
			remote_agent_update_capable,terminal_commands,platform,architecture,platform_version,capabilities)
		VALUES($1,$2,$3,$4,$5,$6,now(),$7,CASE WHEN $7 THEN now() END,
		       COALESCE($8::text[],ARRAY[]::text[]),$9,$10::jsonb,$11,$12,$13,COALESCE($14::text[],ARRAY[]::text[]))
		ON CONFLICT(id) DO UPDATE SET name=excluded.name,agent_version=excluded.agent_version,
		key_epoch=GREATEST(pcs.key_epoch,excluded.key_epoch),last_seen_at=now(),
		session_heartbeat_capable=pcs.session_heartbeat_capable OR excluded.session_heartbeat_capable,
		session_heartbeat_enabled_at=COALESCE(pcs.session_heartbeat_enabled_at,excluded.session_heartbeat_enabled_at),
		supported_shells=excluded.supported_shells,
		remote_agent_update_capable=excluded.remote_agent_update_capable,
		terminal_commands=excluded.terminal_commands,
		platform=excluded.platform,architecture=excluded.architecture,
		platform_version=excluded.platform_version,capabilities=excluded.capabilities
		WHERE pcs.owner_user_id=$2 AND pcs.device_id=$3`, item.ID, userID, deviceID, item.Name, item.AgentVersion, item.KeyEpoch, item.SessionHeartbeatCapable, item.SupportedShells, item.RemoteAgentUpdateCapable, terminalCommands, item.Platform, item.Architecture, item.PlatformVersion, item.Capabilities)
	if err != nil {
		return err
	}
	return tx.Commit(ctx)
}

// TouchPCPresence is called by the authenticated production Realtime
// heartbeat. It refreshes both the Windows device and its PC route without
// accepting a client-supplied PC identifier.
func (s *Postgres) TouchPCPresence(ctx context.Context, userID, deviceID uuid.UUID) error {
	command, err := s.Pool.Exec(ctx, `
		WITH trusted AS (
			UPDATE devices SET last_seen_at=now()
			WHERE id=$1 AND user_id=$2 AND device_type IN ('WINDOWS_PC','PC_AGENT') AND trust_state='TRUSTED'
			  AND EXISTS(SELECT 1 FROM users WHERE id=$2 AND status='ACTIVE')
			RETURNING id
		)
		UPDATE pcs SET last_seen_at=now()
		WHERE owner_user_id=$2 AND device_id=$1 AND EXISTS(SELECT 1 FROM trusted)`, deviceID, userID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	return nil
}

// TouchDevicePresence keeps the account security page's last-active value
// current for an authenticated non-PC Realtime connection.
func (s *Postgres) TouchDevicePresence(ctx context.Context, userID, deviceID uuid.UUID) error {
	command, err := s.Pool.Exec(ctx, `
		UPDATE devices SET last_seen_at=now()
		WHERE id=$1 AND user_id=$2 AND trust_state='TRUSTED'
		  AND EXISTS(SELECT 1 FROM users WHERE id=$2 AND status='ACTIVE')`, deviceID, userID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	return nil
}

func (s *Postgres) ListPCs(ctx context.Context, userID, deviceID uuid.UUID) ([]PCRecord, error) {
	rows, err := s.Pool.Query(ctx, `
		SELECT p.id,p.device_id,p.name,p.agent_version,p.platform,p.architecture,p.platform_version,p.capabilities,
		       p.key_epoch,p.remote_routing_enabled,
		       p.session_heartbeat_capable,p.session_heartbeat_enabled_at,
		       p.remote_agent_update_capable,p.supported_shells,p.terminal_commands,
		       COALESCE(p.last_seen_at > now()-($4 * interval '1 second'),false),p.last_seen_at
		FROM pcs p
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices actor ON actor.id=$2 AND actor.user_id=$1 AND actor.trust_state='TRUSTED'
		WHERE p.owner_user_id=$1 AND (
			$3 OR p.device_id=actor.id OR EXISTS(
				SELECT 1 FROM device_grants g
				WHERE g.pc_id=p.id AND g.device_id=actor.id
				  AND (g.expires_at IS NULL OR g.expires_at>now())
			)
		)
		ORDER BY p.name,p.id`, userID, deviceID, s.AutoTrustDevices, int(sessionOnlineWindow/time.Second))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]PCRecord, 0)
	for rows.Next() {
		var item PCRecord
		var terminalCommands []byte
		if err := rows.Scan(&item.ID, &item.DeviceID, &item.Name, &item.AgentVersion,
			&item.Platform, &item.Architecture, &item.PlatformVersion, &item.Capabilities, &item.KeyEpoch,
			&item.RemoteRoutingEnabled, &item.SessionHeartbeatCapable, &item.SessionHeartbeatEnabledAt,
			&item.RemoteAgentUpdateCapable, &item.SupportedShells, &terminalCommands, &item.Online, &item.LastSeenAt); err != nil {
			return nil, err
		}
		if err := json.Unmarshal(terminalCommands, &item.TerminalCommands); err != nil {
			return nil, fmt.Errorf("decode terminal command capabilities: %w", err)
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

type SessionRecord struct {
	ID                      uuid.UUID  `json:"id"`
	PCID                    uuid.UUID  `json:"pc_id"`
	ShellType               string     `json:"shell_type"`
	TitleCipher             []byte     `json:"title_cipher,omitempty"`
	State                   string     `json:"state"`
	Columns                 int        `json:"columns"`
	Rows                    int        `json:"rows"`
	LastOutputSequence      uint64     `json:"last_output_seq"`
	SnapshotStatus          *string    `json:"snapshot_status,omitempty"`
	SnapshotReasonCode      *string    `json:"snapshot_reason_code,omitempty"`
	SessionHeartbeatCapable bool       `json:"session_heartbeat_capable,omitempty"`
	Online                  bool       `json:"online"`
	CreatedAt               time.Time  `json:"created_at"`
	LastSeenAt              *time.Time `json:"last_seen_at,omitempty"`
	EndedAt                 *time.Time `json:"ended_at,omitempty"`
}

func (s *Postgres) UpsertSession(ctx context.Context, userID, deviceID uuid.UUID, item SessionRecord) error {
	var err error
	for attempt := 0; attempt < 5; attempt++ {
		err = s.upsertSessionOnce(ctx, userID, deviceID, item)
		if !isPostgresRetryableTransactionError(err) {
			return err
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
	}
	return err
}

func (s *Postgres) upsertSessionOnce(ctx context.Context, userID, deviceID uuid.UUID, item SessionRecord) error {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	// Use the same PC-then-session lock order as automatic cleanup. A committed
	// PC capability change or fresh session heartbeat therefore wins the stale
	// recheck, while an AUTO_STALE tombstone can be safely resurrected only by
	// this authenticated owning Windows device path.
	var pcHeartbeatCapable bool
	err = tx.QueryRow(ctx, `
		SELECT session_heartbeat_capable
		FROM pcs
		WHERE id=$1 AND owner_user_id=$2 AND device_id=$3
		FOR UPDATE`, item.PCID, userID, deviceID).Scan(&pcHeartbeatCapable)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	}
	if err != nil {
		return err
	}
	var existingPCID uuid.UUID
	err = tx.QueryRow(ctx, `SELECT pc_id FROM sessions WHERE id=$1 FOR UPDATE`, item.ID).Scan(&existingPCID)
	if err == nil && existingPCID != item.PCID {
		return ErrDenied
	}
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return err
	}

	heartbeat := item.SessionHeartbeatCapable && pcHeartbeatCapable
	command, err := tx.Exec(ctx, `
		INSERT INTO sessions(
			id,pc_id,shell_type,title_cipher,state,columns,rows,last_output_seq,ended_at,
			snapshot_status,snapshot_reason_code,last_seen_at)
		VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,CASE WHEN $12 THEN now() END)
		ON CONFLICT(id) DO UPDATE SET
			title_cipher=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason IS DISTINCT FROM 'AUTO_STALE' THEN sessions.title_cipher
				WHEN NOT $13 THEN sessions.title_cipher
				ELSE excluded.title_cipher
			END,
			snapshot_status=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason IS DISTINCT FROM 'AUTO_STALE' THEN sessions.snapshot_status
				WHEN NOT $14 THEN sessions.snapshot_status
				ELSE excluded.snapshot_status
			END,
			snapshot_reason_code=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason IS DISTINCT FROM 'AUTO_STALE' THEN sessions.snapshot_reason_code
				WHEN NOT $14 THEN sessions.snapshot_reason_code
				ELSE excluded.snapshot_reason_code
			END,
			state=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason='AUTO_STALE' THEN excluded.state
				WHEN sessions.state IN ('EXITED','DELETED') THEN sessions.state
				ELSE excluded.state
			END,
			columns=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason IS DISTINCT FROM 'AUTO_STALE' THEN sessions.columns
				ELSE excluded.columns
			END,
			rows=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason IS DISTINCT FROM 'AUTO_STALE' THEN sessions.rows
				ELSE excluded.rows
			END,
			last_output_seq=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason IS DISTINCT FROM 'AUTO_STALE' THEN sessions.last_output_seq
				WHEN sessions.state='DELETED' AND sessions.deletion_reason='AUTO_STALE' THEN excluded.last_output_seq
				ELSE GREATEST(sessions.last_output_seq,excluded.last_output_seq)
			END,
			ended_at=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason='AUTO_STALE' THEN excluded.ended_at
				WHEN sessions.state='DELETED' THEN sessions.ended_at
				WHEN sessions.state='EXITED' THEN CASE WHEN excluded.state='EXITED' THEN COALESCE(sessions.ended_at,excluded.ended_at) ELSE sessions.ended_at END
				WHEN excluded.state='EXITED' THEN COALESCE(excluded.ended_at,sessions.ended_at)
				ELSE excluded.ended_at
			END,
			last_seen_at=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason IS DISTINCT FROM 'AUTO_STALE' THEN sessions.last_seen_at
				WHEN $12 OR (sessions.state='DELETED' AND sessions.deletion_reason='AUTO_STALE') THEN now()
				ELSE sessions.last_seen_at
			END,
			deleted_at=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason='AUTO_STALE' THEN NULL
				ELSE sessions.deleted_at
			END,
			deletion_reason=CASE
				WHEN sessions.state='DELETED' AND sessions.deletion_reason='AUTO_STALE' THEN NULL
				ELSE sessions.deletion_reason
			END
		WHERE sessions.pc_id=$2`, item.ID, item.PCID, item.ShellType, item.TitleCipher, item.State,
		item.Columns, item.Rows, int64(item.LastOutputSequence), item.EndedAt,
		item.SnapshotStatus, item.SnapshotReasonCode, heartbeat, item.TitleCipher != nil,
		item.SnapshotStatus != nil)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	return tx.Commit(ctx)
}

func (s *Postgres) ListSessions(ctx context.Context, userID, deviceID, pcID uuid.UUID) ([]SessionRecord, error) {
	return s.listSessions(ctx, userID, deviceID, pcID, false)
}

// ListSessionsIncludingEnded is the explicit compatibility/history view. The
// default ListSessions contract is the live session directory used by product
// clients, so an exited/interrupted session or a session whose PC stopped
// heartbeating cannot remain visible as an actionable terminal forever.
func (s *Postgres) ListSessionsIncludingEnded(ctx context.Context, userID, deviceID, pcID uuid.UUID) ([]SessionRecord, error) {
	return s.listSessions(ctx, userID, deviceID, pcID, true)
}

func (s *Postgres) listSessions(ctx context.Context, userID, deviceID, pcID uuid.UUID, includeEnded bool) ([]SessionRecord, error) {
	rows, err := s.Pool.Query(ctx, `
		SELECT s.id,s.pc_id,s.shell_type,s.title_cipher,s.state,s.columns,s.rows,s.last_output_seq,
		       s.snapshot_status,s.snapshot_reason_code,s.created_at,s.last_seen_at,s.ended_at,presence.online
		FROM sessions s
		JOIN pcs p ON p.id=s.pc_id
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices actor ON actor.id=$3 AND actor.user_id=$2 AND actor.trust_state='TRUSTED'
		CROSS JOIN LATERAL (
			SELECT COALESCE(
				s.state='RUNNING' AND CASE
					WHEN p.session_heartbeat_capable
						THEN s.last_seen_at > now()-($6 * interval '1 second')
					ELSE p.last_seen_at > now()-($6 * interval '1 second')
				END,
				false
			) AS online
		) presence
		WHERE p.id=$1 AND p.owner_user_id=$2 AND s.deleted_at IS NULL
		  AND ($5 OR presence.online) AND (
			$4 OR p.device_id=actor.id OR EXISTS(
				SELECT 1 FROM device_grants g
				WHERE g.pc_id=p.id AND g.device_id=actor.id
				  AND (g.expires_at IS NULL OR g.expires_at>now())
			)
		)
		ORDER BY s.created_at DESC,s.id`,
		pcID, userID, deviceID, s.AutoTrustDevices, includeEnded, int(sessionOnlineWindow/time.Second))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]SessionRecord, 0)
	for rows.Next() {
		var i SessionRecord
		var seq int64
		if err := rows.Scan(&i.ID, &i.PCID, &i.ShellType, &i.TitleCipher, &i.State, &i.Columns,
			&i.Rows, &seq, &i.SnapshotStatus, &i.SnapshotReasonCode,
			&i.CreatedAt, &i.LastSeenAt, &i.EndedAt, &i.Online); err != nil {
			return nil, err
		}
		i.LastOutputSequence = uint64(seq)
		result = append(result, i)
	}
	return result, rows.Err()
}

func (s *Postgres) PutGrant(ctx context.Context, userID, actorID, deviceID, pcID uuid.UUID, permission string, expiresAt *time.Time, traceID string) error {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	command, err := tx.Exec(ctx, `
		INSERT INTO device_grants(device_id,pc_id,permission,approved_by,expires_at)
		SELECT target.id,p.id,$3,actor.id,$5
		FROM pcs p
		JOIN devices actor ON actor.id=$4 AND actor.user_id=$6 AND actor.trust_state='TRUSTED'
		JOIN devices target ON target.id=$1 AND target.user_id=$6 AND target.trust_state='TRUSTED'
		LEFT JOIN device_grants manager
		  ON manager.pc_id=p.id AND manager.device_id=actor.id
		 AND (manager.expires_at IS NULL OR manager.expires_at>now())
		WHERE p.id=$2 AND p.owner_user_id=$6 AND p.remote_routing_enabled
		  AND (p.device_id=actor.id OR manager.permission='MANAGE')
		ON CONFLICT(device_id,pc_id) DO UPDATE
		SET permission=excluded.permission,approved_by=excluded.approved_by,
			expires_at=excluded.expires_at,updated_at=now()`, deviceID, pcID, permission, actorID, expiresAt, userID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO outbox(event_type,aggregate_id,payload)
		VALUES('grant.updated',$1,jsonb_build_object('user_id',$2::uuid,'device_id',$1::uuid,'pc_id',$3::uuid,'permission',$4::text))`,
		deviceID, userID, pcID, permission); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,$2,'grant.update','device',$3,'SUCCEEDED',$4,
			jsonb_build_object('pc_id',$5::uuid,'permission',$6::text,'expires_at',$7::timestamptz))`,
		userID, actorID, deviceID, traceID, pcID, permission, expiresAt); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

type DeleteGrantResult struct {
	KeyEpoch        int64
	LeaseSessionIDs []uuid.UUID
}

type revokedLeaseRecord struct {
	SessionID      uuid.UUID
	HolderDeviceID uuid.UUID
	LeaseID        uuid.UUID
	Version        int64
}

func scanRevokedLeases(rows pgx.Rows) ([]revokedLeaseRecord, error) {
	defer rows.Close()
	result := make([]revokedLeaseRecord, 0)
	for rows.Next() {
		var lease revokedLeaseRecord
		if err := rows.Scan(&lease.SessionID, &lease.HolderDeviceID, &lease.LeaseID, &lease.Version); err != nil {
			return nil, err
		}
		result = append(result, lease)
	}
	return result, rows.Err()
}

func enqueueRevokedLeases(ctx context.Context, tx pgx.Tx, leases []revokedLeaseRecord) error {
	for _, lease := range leases {
		if _, err := tx.Exec(ctx, `
			INSERT INTO outbox(event_type,aggregate_id,payload)
			VALUES('lease.revoked',$1,jsonb_build_object(
				'session_id',$1::uuid,'holder_device_id',$2::uuid,
				'lease_id',$3::uuid,'version',$4::bigint))`,
			lease.SessionID, lease.HolderDeviceID, lease.LeaseID, lease.Version); err != nil {
			return err
		}
	}
	return nil
}

// DeleteGrant removes one device's authority on one PC. Shared durable leases
// are retained because holder_device_id is only the protocol-compatible first
// participant, not exclusive ownership. LeaseSessionIDs lets the API remove
// the revoked Android from Valkey without disturbing other participants.
func (s *Postgres) DeleteGrant(ctx context.Context, userID, actorID, deviceID, pcID uuid.UUID, traceID string) (DeleteGrantResult, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return DeleteGrantResult{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var keyEpoch int64
	err = tx.QueryRow(ctx, `
		SELECT p.key_epoch
		FROM pcs p
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices actor ON actor.id=$2 AND actor.user_id=$1 AND actor.trust_state='TRUSTED'
		LEFT JOIN device_grants manager
		  ON manager.pc_id=p.id AND manager.device_id=actor.id
		 AND (manager.expires_at IS NULL OR manager.expires_at>now())
		WHERE p.id=$3 AND p.owner_user_id=$1 AND p.remote_routing_enabled
		  AND (p.device_id=actor.id OR manager.permission='MANAGE')
		FOR UPDATE OF p`, userID, actorID, pcID).Scan(&keyEpoch)
	if errors.Is(err, pgx.ErrNoRows) {
		return DeleteGrantResult{}, ErrDenied
	}
	if err != nil {
		return DeleteGrantResult{}, err
	}
	command, err := tx.Exec(ctx, `DELETE FROM device_grants WHERE device_id=$1 AND pc_id=$2`, deviceID, pcID)
	if err != nil {
		return DeleteGrantResult{}, err
	}
	if command.RowsAffected() != 1 {
		return DeleteGrantResult{}, ErrNotFound
	}
	rows, err := tx.Query(ctx, `SELECT id FROM sessions WHERE pc_id=$1 AND deleted_at IS NULL`, pcID)
	if err != nil {
		return DeleteGrantResult{}, err
	}
	leaseSessionIDs := make([]uuid.UUID, 0)
	for rows.Next() {
		var sessionID uuid.UUID
		if err := rows.Scan(&sessionID); err != nil {
			rows.Close()
			return DeleteGrantResult{}, err
		}
		leaseSessionIDs = append(leaseSessionIDs, sessionID)
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return DeleteGrantResult{}, err
	}
	rows.Close()
	keyEpoch++
	if _, err := tx.Exec(ctx, `UPDATE pcs SET key_epoch=$2 WHERE id=$1`, pcID, keyEpoch); err != nil {
		return DeleteGrantResult{}, err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO outbox(event_type,aggregate_id,payload)
		VALUES('grant.revoked',$1,jsonb_build_object('user_id',$2::uuid,'device_id',$1::uuid,'pc_id',$3::uuid,'key_epoch',$4::bigint))`,
		deviceID, userID, pcID, keyEpoch); err != nil {
		return DeleteGrantResult{}, err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,$2,'grant.revoke','device',$3,'SUCCEEDED',$4,jsonb_build_object('pc_id',$5::uuid,'key_epoch',$6::bigint))`,
		userID, actorID, deviceID, traceID, pcID, keyEpoch); err != nil {
		return DeleteGrantResult{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return DeleteGrantResult{}, err
	}
	return DeleteGrantResult{KeyEpoch: keyEpoch, LeaseSessionIDs: leaseSessionIDs}, nil
}

type PCGrantRecord struct {
	DeviceID          uuid.UUID  `json:"device_id"`
	DeviceType        string     `json:"device_type"`
	DeviceName        string     `json:"device_name"`
	ExchangePublicKey []byte     `json:"exchange_public_key"`
	KeyID             string     `json:"key_id"`
	Permission        string     `json:"permission"`
	ExpiresAt         *time.Time `json:"expires_at,omitempty"`
}

// ListPCGrants returns the PC's active encryption recipients. In production
// auto-trust mode every trusted Android device on the account is an implicit
// MANAGE recipient; legacy deployments continue to use explicit active grants.
// The authoritative key epoch is returned with the same repeatable-read
// snapshot so an Agent never encrypts a refreshed recipient set under a stale
// epoch.
func (s *Postgres) ListPCGrants(ctx context.Context, userID, actorID, pcID uuid.UUID) (int64, []PCGrantRecord, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.RepeatableRead, AccessMode: pgx.ReadOnly})
	if err != nil {
		return 0, nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var keyEpoch int64
	err = tx.QueryRow(ctx, `
		SELECT p.key_epoch
		FROM pcs p
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices actor ON actor.id=$2 AND actor.user_id=$1 AND actor.trust_state='TRUSTED'
		LEFT JOIN device_grants manager
		  ON manager.pc_id=p.id AND manager.device_id=actor.id
		 AND (manager.expires_at IS NULL OR manager.expires_at>now())
		WHERE p.id=$3 AND p.owner_user_id=$1 AND p.remote_routing_enabled
		  AND ($4 OR p.device_id=actor.id OR manager.permission='MANAGE')`, userID, actorID, pcID, s.AutoTrustDevices).Scan(&keyEpoch)
	if errors.Is(err, pgx.ErrNoRows) {
		return 0, nil, ErrDenied
	}
	if err != nil {
		return 0, nil, err
	}
	rows, err := tx.Query(ctx, `
		SELECT d.id,d.device_type,d.name,d.exchange_public_key,d.key_id,
		       CASE WHEN $3 AND d.device_type='ANDROID' THEN 'MANAGE' ELSE g.permission END,
		       CASE WHEN $3 AND d.device_type='ANDROID' THEN NULL ELSE g.expires_at END
		FROM devices d
		LEFT JOIN device_grants g
		  ON g.device_id=d.id AND g.pc_id=$2
		 AND (g.expires_at IS NULL OR g.expires_at>now())
		WHERE d.user_id=$1 AND d.trust_state='TRUSTED'
		  AND (($3 AND d.device_type='ANDROID') OR g.device_id IS NOT NULL)
		ORDER BY d.created_at,d.id`, userID, pcID, s.AutoTrustDevices)
	if err != nil {
		return 0, nil, err
	}
	defer rows.Close()
	items := make([]PCGrantRecord, 0)
	for rows.Next() {
		var item PCGrantRecord
		if err := rows.Scan(&item.DeviceID, &item.DeviceType, &item.DeviceName, &item.ExchangePublicKey, &item.KeyID, &item.Permission, &item.ExpiresAt); err != nil {
			return 0, nil, err
		}
		items = append(items, item)
	}
	if err := rows.Err(); err != nil {
		return 0, nil, err
	}
	if err := tx.Commit(ctx); err != nil {
		return 0, nil, err
	}
	return keyEpoch, items, nil
}

type SessionRouteAuthorization struct {
	Permission     string
	PCDeviceID     uuid.UUID
	GrantExpiresAt *time.Time
}

// SessionRoute resolves both the device permission and the owning Windows
// device from one authoritative snapshot. Relay subscription and low-rate
// input paths use this once; high-volume PC output routes use the validated
// subscription cache and are invalidated by authority events/heartbeats.
func (s *Postgres) SessionRoute(ctx context.Context, userID, deviceID, sessionID uuid.UUID) (SessionRouteAuthorization, error) {
	var result SessionRouteAuthorization
	err := s.Pool.QueryRow(ctx, `
		-- The first production release uses account-scoped automatic trust.
		-- Treat same-account devices as full MANAGE participants so clients do
		-- not need a separate grant/pairing step. Explicit legacy grants still
		-- retain their VIEW/CONTROL/MANAGE semantics when AutoTrustDevices is off.
		SELECT CASE WHEN p.device_id=actor.id THEN 'MANAGE' WHEN $4 THEN 'MANAGE' ELSE g.permission END,
		       p.device_id,
		       CASE WHEN $4 THEN NULL ELSE g.expires_at END
		FROM sessions s
		JOIN pcs p ON p.id=s.pc_id
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices owner_device ON owner_device.id=p.device_id AND owner_device.trust_state='TRUSTED'
		JOIN devices actor ON actor.id=$2 AND actor.user_id=$3 AND actor.trust_state='TRUSTED'
		LEFT JOIN device_grants g ON g.pc_id=p.id AND g.device_id=actor.id AND (g.expires_at IS NULL OR g.expires_at>now())
		WHERE s.id=$1 AND p.owner_user_id=$3 AND p.remote_routing_enabled AND s.deleted_at IS NULL
		  AND ($4 OR p.device_id=actor.id OR g.device_id IS NOT NULL)`, sessionID, deviceID, userID, s.AutoTrustDevices).
		Scan(&result.Permission, &result.PCDeviceID, &result.GrantExpiresAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return SessionRouteAuthorization{}, ErrDenied
	}
	return result, err
}

func (s *Postgres) SessionPermission(ctx context.Context, userID, deviceID, sessionID uuid.UUID) (string, error) {
	route, err := s.SessionRoute(ctx, userID, deviceID, sessionID)
	return route.Permission, err
}

func (s *Postgres) SessionPCDevice(ctx context.Context, userID, sessionID uuid.UUID) (uuid.UUID, error) {
	var id uuid.UUID
	err := s.Pool.QueryRow(ctx, `
		SELECT p.device_id
		FROM sessions s
		JOIN pcs p ON p.id=s.pc_id
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices owner_device ON owner_device.id=p.device_id AND owner_device.trust_state='TRUSTED'
		WHERE s.id=$1 AND p.owner_user_id=$2 AND p.remote_routing_enabled AND s.deleted_at IS NULL`, sessionID, userID).Scan(&id)
	if errors.Is(err, pgx.ErrNoRows) {
		return uuid.Nil, ErrNotFound
	}
	return id, err
}

type HistoryItem struct {
	Kind                 string    `json:"kind"`
	ID                   uuid.UUID `json:"id"`
	StartSequence        uint64    `json:"start_seq,omitempty"`
	EndSequence          uint64    `json:"end_seq,omitempty"`
	SnapshotSequence     uint64    `json:"snapshot_seq,omitempty"`
	AsOfSnapshotSequence uint64    `json:"as_of_snapshot_seq,omitempty"`
	FirstLineID          uint64    `json:"first_line_id,omitempty"`
	LastLineID           uint64    `json:"last_line_id,omitempty"`
	Rows                 int       `json:"rows,omitempty"`
	Columns              int       `json:"columns,omitempty"`
	ObjectKey            string    `json:"object_key"`
	KeyID                string    `json:"key_id"`
	CiphertextSHA256     []byte    `json:"ciphertext_sha256"`
	SizeBytes            int64     `json:"size_bytes"`
	FormatName           string    `json:"format_name,omitempty"`
	FormatVersion        int       `json:"format_version,omitempty"`
	CreatedAt            time.Time `json:"created_at"`
	HistoryRevision      int64     `json:"-"`
}

type HistoryKind string

const (
	HistoryKindAll        HistoryKind = "all"
	HistoryKindChunk      HistoryKind = "chunk"
	HistoryKindSnapshot   HistoryKind = "snapshot"
	HistoryKindTranscript HistoryKind = "transcript"
)

type HistoryPosition struct {
	Sequence int64
	Kind     HistoryKind
	ID       uuid.UUID
}

type HistoryPageRequest struct {
	Kind         HistoryKind
	FormatName   string
	Limit        int
	AsOfRevision *int64
	Before       *HistoryPosition
}

type HistoryPage struct {
	Items        []HistoryItem
	AsOfRevision int64
	HasMore      bool
}

type TranscriptPageRequest struct {
	AsOfSnapshotSequence int64
	BeforeLineID         *int64
	AsOfRevision         *int64
	Limit                int
}

type TranscriptPage struct {
	Items        []HistoryItem
	AsOfRevision int64
	HasMore      bool
	Ready        bool
}

type HistoryUploadRecord struct {
	ID                   uuid.UUID
	SessionID            uuid.UUID
	Kind                 string
	ObjectKey            string
	KeyID                string
	CiphertextSHA256     []byte
	SizeBytes            int64
	StartSequence        *int64
	EndSequence          *int64
	SnapshotSequence     *int64
	AsOfSnapshotSequence *int64
	FirstLineID          *int64
	LastLineID           *int64
	// Transcript publication metadata is present only on terminal Snapshot
	// uploads. Legacy snapshots leave both fields nil and retain legacy paging.
	TranscriptInitialBeforeLineID *int64
	TranscriptPageCount           *int
	Rows                          *int
	Columns                       *int
	FormatName                    *string
	FormatVersion                 *int
	RecipientDeviceID             uuid.UUID
	RecipientKeyEpoch             int64
	ExpiresAt                     time.Time
}

func ParseHistoryRecipientKeyID(kind, formatName, keyID string) (uuid.UUID, int64, error) {
	prefix := ""
	switch {
	case kind == "CHUNK" && formatName == "":
		prefix = "history-v1"
	case kind == "SNAPSHOT" && formatName == HistoryFormatBoundedEventReplay:
		prefix = "history-v1"
	case kind == "SNAPSHOT" && formatName == HistoryFormatTerminalSnapshot:
		prefix = "snapshot-v1"
	case kind == "TRANSCRIPT" && formatName == HistoryFormatTerminalTranscript:
		prefix = "transcript-v1"
	default:
		return uuid.Nil, 0, ErrInvalidHistoryRecipient
	}
	parts := strings.Split(keyID, ":")
	if len(parts) != 3 || parts[0] != prefix || len(parts[1]) != 32 || parts[1] != strings.ToLower(parts[1]) ||
		len(parts[2]) < 8 || len(parts[2]) > 19 {
		return uuid.Nil, 0, ErrInvalidHistoryRecipient
	}
	for _, value := range parts[1] {
		if value < '0' || value > '9' && value < 'a' || value > 'f' {
			return uuid.Nil, 0, ErrInvalidHistoryRecipient
		}
	}
	for _, value := range parts[2] {
		if value < '0' || value > '9' {
			return uuid.Nil, 0, ErrInvalidHistoryRecipient
		}
	}
	recipient, err := uuid.Parse(parts[1])
	if err != nil || strings.ReplaceAll(recipient.String(), "-", "") != parts[1] {
		return uuid.Nil, 0, ErrInvalidHistoryRecipient
	}
	epoch, err := strconv.ParseInt(parts[2], 10, 64)
	if err != nil || epoch < 1 || fmt.Sprintf("%08d", epoch) != parts[2] ||
		((prefix == "snapshot-v1" || prefix == "transcript-v1") && len(parts[2]) != 8) {
		return uuid.Nil, 0, ErrInvalidHistoryRecipient
	}
	return recipient, epoch, nil
}

func validateHistoryUploadRecipient(item HistoryUploadRecord) error {
	formatName := ""
	if item.FormatName != nil {
		formatName = *item.FormatName
	}
	recipient, epoch, err := ParseHistoryRecipientKeyID(item.Kind, formatName, item.KeyID)
	if err != nil || recipient != item.RecipientDeviceID || epoch != item.RecipientKeyEpoch {
		return ErrInvalidHistoryRecipient
	}
	return nil
}

func ValidateHistorySnapshotMetadata(sequence, sizeBytes int64, rows, columns int, formatName string, formatVersion int) error {
	if sequence < 0 || sizeBytes < 1 || sizeBytes > MaxSnapshotCiphertextBytes || rows < 1 || rows > 256 ||
		columns < 1 || columns > 512 || formatVersion != 1 {
		return ErrInvalidHistorySnapshot
	}
	switch formatName {
	case HistoryFormatBoundedEventReplay:
		// Legacy bounded replay may describe the initial state at sequence zero.
		return nil
	case HistoryFormatTerminalSnapshot:
		if sequence == 0 {
			return ErrInvalidHistorySnapshot
		}
		return nil
	default:
		return ErrInvalidHistorySnapshot
	}
}

func validateHistorySnapshotUpload(item HistoryUploadRecord) error {
	if item.SnapshotSequence == nil || item.Rows == nil || item.Columns == nil || item.FormatName == nil || item.FormatVersion == nil ||
		item.StartSequence != nil || item.EndSequence != nil || item.AsOfSnapshotSequence != nil ||
		item.FirstLineID != nil || item.LastLineID != nil {
		return ErrInvalidHistorySnapshot
	}
	publicationMetadataPresent := item.TranscriptInitialBeforeLineID != nil || item.TranscriptPageCount != nil
	if publicationMetadataPresent && (item.TranscriptInitialBeforeLineID == nil || item.TranscriptPageCount == nil ||
		*item.TranscriptInitialBeforeLineID < 1 || *item.TranscriptPageCount < 0 ||
		*item.TranscriptPageCount > MaxTranscriptPublicationPages ||
		*item.FormatName != HistoryFormatTerminalSnapshot) {
		return ErrInvalidHistorySnapshot
	}
	return ValidateHistorySnapshotMetadata(*item.SnapshotSequence, item.SizeBytes, *item.Rows, *item.Columns, *item.FormatName, *item.FormatVersion)
}

func ValidateHistoryTranscriptMetadata(asOfSnapshotSequence, firstLineID, lastLineID, sizeBytes int64, formatName string, formatVersion int) error {
	if asOfSnapshotSequence < 1 || firstLineID < 1 || lastLineID < firstLineID ||
		sizeBytes < 1 || sizeBytes > MaxTranscriptCiphertextBytes ||
		formatName != HistoryFormatTerminalTranscript || formatVersion != 1 {
		return ErrInvalidHistoryTranscript
	}
	return nil
}

func validateHistoryTranscriptUpload(item HistoryUploadRecord) error {
	if item.AsOfSnapshotSequence == nil || item.FirstLineID == nil || item.LastLineID == nil ||
		item.FormatName == nil || item.FormatVersion == nil || item.StartSequence != nil ||
		item.EndSequence != nil || item.SnapshotSequence != nil || item.Rows != nil || item.Columns != nil ||
		item.TranscriptInitialBeforeLineID != nil || item.TranscriptPageCount != nil {
		return ErrInvalidHistoryTranscript
	}
	return ValidateHistoryTranscriptMetadata(*item.AsOfSnapshotSequence, *item.FirstLineID, *item.LastLineID,
		item.SizeBytes, *item.FormatName, *item.FormatVersion)
}

func validateHistoryUploadShape(item HistoryUploadRecord) error {
	switch item.Kind {
	case "CHUNK":
		if item.StartSequence == nil || item.EndSequence == nil || *item.StartSequence < 1 ||
			*item.EndSequence < *item.StartSequence || item.SnapshotSequence != nil || item.Rows != nil ||
			item.Columns != nil || item.FormatName != nil || item.FormatVersion != nil ||
			item.AsOfSnapshotSequence != nil || item.FirstLineID != nil || item.LastLineID != nil ||
			item.TranscriptInitialBeforeLineID != nil || item.TranscriptPageCount != nil {
			return ErrInvalidHistoryPage
		}
		return nil
	case "SNAPSHOT":
		return validateHistorySnapshotUpload(item)
	case "TRANSCRIPT":
		return validateHistoryTranscriptUpload(item)
	default:
		return ErrInvalidHistoryPage
	}
}

func (s *Postgres) CreateHistoryUpload(ctx context.Context, userID, actorID uuid.UUID, item HistoryUploadRecord) error {
	if err := validateHistoryUploadShape(item); err != nil {
		return err
	}
	if err := validateHistoryUploadRecipient(item); err != nil {
		return err
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.ReadCommitted})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var allowed bool
	err = tx.QueryRow(ctx, `
		SELECT true
		FROM sessions session
		JOIN pcs pc
		  ON pc.id=session.pc_id AND pc.owner_user_id=$1 AND pc.device_id=$2
		 AND pc.remote_routing_enabled AND pc.key_epoch=$5
		JOIN devices actor
		  ON actor.id=$2 AND actor.user_id=$1 AND actor.device_type IN ('WINDOWS_PC','PC_AGENT') AND actor.trust_state='TRUSTED'
		JOIN devices recipient
		  ON recipient.id=$4 AND recipient.user_id=$1 AND recipient.device_type='ANDROID' AND recipient.trust_state='TRUSTED'
		LEFT JOIN device_grants recipient_grant
		  ON recipient_grant.pc_id=pc.id AND recipient_grant.device_id=recipient.id
		 AND (recipient_grant.expires_at IS NULL OR recipient_grant.expires_at>now())
		WHERE session.id=$3 AND session.deleted_at IS NULL
		  AND ($6 OR recipient_grant.device_id IS NOT NULL)
		FOR UPDATE OF session,pc,actor,recipient`, userID, actorID, item.SessionID,
		item.RecipientDeviceID, item.RecipientKeyEpoch, s.AutoTrustDevices).Scan(&allowed)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	}
	if err != nil {
		return err
	}
	// Create a new session tuple version while holding the deletion fence. A
	// serializable delete that began while this upload was uncommitted must
	// restart before it can collect history object keys.
	if _, err := tx.Exec(ctx, `UPDATE sessions SET history_revision=history_revision WHERE id=$1`, item.SessionID); err != nil {
		return err
	}
	command, err := tx.Exec(ctx, `
		INSERT INTO history_uploads(id,user_id,actor_device_id,session_id,kind,object_key,key_id,ciphertext_sha256,size_bytes,start_seq,end_seq,snapshot_seq,as_of_snapshot_seq,first_line_id,last_line_id,transcript_initial_before_line_id,transcript_page_count,rows,columns,format_name,format_version,recipient_device_id,recipient_key_epoch,expires_at)
		SELECT $1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24
		FROM sessions s
		JOIN pcs p ON p.id=s.pc_id AND p.owner_user_id=$2 AND p.device_id=$3 AND p.remote_routing_enabled AND p.key_epoch=$23
		JOIN devices actor ON actor.id=$3 AND actor.user_id=$2 AND actor.device_type IN ('WINDOWS_PC','PC_AGENT') AND actor.trust_state='TRUSTED'
		JOIN devices recipient ON recipient.id=$22 AND recipient.user_id=$2 AND recipient.device_type='ANDROID' AND recipient.trust_state='TRUSTED'
		LEFT JOIN device_grants recipient_grant
		  ON recipient_grant.pc_id=p.id AND recipient_grant.device_id=recipient.id
		 AND (recipient_grant.expires_at IS NULL OR recipient_grant.expires_at>now())
		WHERE s.id=$4 AND s.deleted_at IS NULL AND ($25 OR recipient_grant.device_id IS NOT NULL)`,
		item.ID, userID, actorID, item.SessionID, item.Kind, item.ObjectKey, item.KeyID, item.CiphertextSHA256,
		item.SizeBytes, item.StartSequence, item.EndSequence, item.SnapshotSequence, item.AsOfSnapshotSequence,
		item.FirstLineID, item.LastLineID, item.TranscriptInitialBeforeLineID, item.TranscriptPageCount,
		item.Rows, item.Columns, item.FormatName, item.FormatVersion,
		item.RecipientDeviceID, item.RecipientKeyEpoch, item.ExpiresAt,
		s.AutoTrustDevices)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	return tx.Commit(ctx)
}

func requireHistoryTranscriptAnchor(
	ctx context.Context,
	query historyRecipientAuthorityQuery,
	sessionID, recipientID uuid.UUID,
	asOfSnapshotSequence int64,
	maxRevision *int64,
) error {
	var found bool
	err := query.QueryRow(ctx, `
		SELECT true
		FROM snapshots
		WHERE session_id=$1 AND recipient_device_id=$2 AND snapshot_seq=$3
		  AND format_name='TERMLINK_TERMINAL_SNAPSHOT' AND format_version=1
		  AND deleted_at IS NULL AND ($4::bigint IS NULL OR history_revision<=$4)`,
		sessionID, recipientID, asOfSnapshotSequence, maxRevision).Scan(&found)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrNotFound
	}
	return err
}

type transcriptPublicationRecord struct {
	InitialBeforeLineID int64
	ExpectedPageCount   int
	CompletedRevision   *int64
}

func findTranscriptPublication(
	ctx context.Context,
	query historyRecipientAuthorityQuery,
	sessionID, recipientID uuid.UUID,
	asOfSnapshotSequence int64,
) (transcriptPublicationRecord, bool, error) {
	var publication transcriptPublicationRecord
	err := query.QueryRow(ctx, `
		SELECT initial_before_line_id,expected_page_count,completed_revision
		FROM transcript_publications
		WHERE session_id=$1 AND recipient_device_id=$2 AND as_of_snapshot_seq=$3`,
		sessionID, recipientID, asOfSnapshotSequence,
	).Scan(&publication.InitialBeforeLineID, &publication.ExpectedPageCount, &publication.CompletedRevision)
	if errors.Is(err, pgx.ErrNoRows) {
		return transcriptPublicationRecord{}, false, nil
	}
	if err != nil {
		return transcriptPublicationRecord{}, false, err
	}
	return publication, true, nil
}

type historyRecipientAuthorityQuery interface {
	QueryRow(context.Context, string, ...any) pgx.Row
}

func (s *Postgres) requireHistoryRecipientAuthority(
	ctx context.Context,
	query historyRecipientAuthorityQuery,
	userID, actorID, sessionID, recipientID uuid.UUID,
	keyEpoch int64,
) error {
	var allowed bool
	err := query.QueryRow(ctx, `
		SELECT true
		FROM sessions s
		JOIN pcs p
		  ON p.id=s.pc_id AND p.owner_user_id=$1 AND p.device_id=$2
		 AND p.remote_routing_enabled AND p.key_epoch=$5
		JOIN devices actor
		  ON actor.id=$2 AND actor.user_id=$1 AND actor.device_type IN ('WINDOWS_PC','PC_AGENT') AND actor.trust_state='TRUSTED'
		JOIN devices recipient
		  ON recipient.id=$4 AND recipient.user_id=$1 AND recipient.device_type='ANDROID' AND recipient.trust_state='TRUSTED'
		LEFT JOIN device_grants recipient_grant
		  ON recipient_grant.pc_id=p.id AND recipient_grant.device_id=recipient.id
		 AND (recipient_grant.expires_at IS NULL OR recipient_grant.expires_at>now())
		WHERE s.id=$3 AND s.deleted_at IS NULL AND ($6 OR recipient_grant.device_id IS NOT NULL)
		FOR SHARE OF s,p,actor,recipient`, userID, actorID, sessionID, recipientID, keyEpoch, s.AutoTrustDevices,
	).Scan(&allowed)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	}
	return err
}

func (s *Postgres) PendingHistoryUpload(ctx context.Context, userID, actorID, sessionID, uploadID uuid.UUID) (HistoryUploadRecord, error) {
	var item HistoryUploadRecord
	err := s.Pool.QueryRow(ctx, `
		SELECT id,session_id,kind,object_key,key_id,ciphertext_sha256,size_bytes,start_seq,end_seq,snapshot_seq,as_of_snapshot_seq,first_line_id,last_line_id,transcript_initial_before_line_id,transcript_page_count,rows,columns,format_name,format_version,recipient_device_id,recipient_key_epoch,expires_at
		FROM history_uploads
		WHERE id=$1 AND user_id=$2 AND actor_device_id=$3 AND session_id=$4 AND committed_at IS NULL AND expires_at>now()`,
		uploadID, userID, actorID, sessionID,
	).Scan(&item.ID, &item.SessionID, &item.Kind, &item.ObjectKey, &item.KeyID, &item.CiphertextSHA256,
		&item.SizeBytes, &item.StartSequence, &item.EndSequence, &item.SnapshotSequence, &item.AsOfSnapshotSequence,
		&item.FirstLineID, &item.LastLineID, &item.TranscriptInitialBeforeLineID, &item.TranscriptPageCount,
		&item.Rows, &item.Columns,
		&item.FormatName, &item.FormatVersion, &item.RecipientDeviceID, &item.RecipientKeyEpoch, &item.ExpiresAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return HistoryUploadRecord{}, ErrNotFound
	}
	if err != nil {
		return HistoryUploadRecord{}, err
	}
	if err := validateHistoryUploadShape(item); err != nil {
		return HistoryUploadRecord{}, err
	}
	if err := validateHistoryUploadRecipient(item); err != nil {
		return HistoryUploadRecord{}, err
	}
	if err := s.requireHistoryRecipientAuthority(ctx, s.Pool, userID, actorID, sessionID, item.RecipientDeviceID, item.RecipientKeyEpoch); err != nil {
		return HistoryUploadRecord{}, err
	}
	return item, nil
}

func (s *Postgres) CommitHistoryUpload(ctx context.Context, userID, actorID, sessionID, uploadID uuid.UUID) (HistoryItem, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return HistoryItem{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var lastOutputSequence int64
	err = tx.QueryRow(ctx, `
		SELECT session.last_output_seq
		FROM sessions session
		WHERE session.id=$1 AND session.deleted_at IS NULL
		  AND EXISTS (
		    SELECT 1 FROM history_uploads upload
		    WHERE upload.id=$2 AND upload.user_id=$3 AND upload.actor_device_id=$4
		      AND upload.session_id=session.id AND upload.committed_at IS NULL AND upload.expires_at>now()
		  )
		FOR UPDATE OF session`, sessionID, uploadID, userID, actorID).Scan(&lastOutputSequence)
	if errors.Is(err, pgx.ErrNoRows) {
		return HistoryItem{}, ErrConflict
	}
	if err != nil {
		return HistoryItem{}, err
	}
	var upload HistoryUploadRecord
	err = tx.QueryRow(ctx, `
		SELECT id,session_id,kind,object_key,key_id,ciphertext_sha256,size_bytes,start_seq,end_seq,snapshot_seq,as_of_snapshot_seq,first_line_id,last_line_id,transcript_initial_before_line_id,transcript_page_count,rows,columns,format_name,format_version,recipient_device_id,recipient_key_epoch,expires_at
		FROM history_uploads
		WHERE id=$1 AND user_id=$2 AND actor_device_id=$3 AND session_id=$4 AND committed_at IS NULL AND expires_at>now()
		FOR UPDATE`, uploadID, userID, actorID, sessionID,
	).Scan(&upload.ID, &upload.SessionID, &upload.Kind, &upload.ObjectKey, &upload.KeyID, &upload.CiphertextSHA256,
		&upload.SizeBytes, &upload.StartSequence, &upload.EndSequence, &upload.SnapshotSequence,
		&upload.AsOfSnapshotSequence, &upload.FirstLineID, &upload.LastLineID,
		&upload.TranscriptInitialBeforeLineID, &upload.TranscriptPageCount, &upload.Rows,
		&upload.Columns, &upload.FormatName, &upload.FormatVersion, &upload.RecipientDeviceID,
		&upload.RecipientKeyEpoch, &upload.ExpiresAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return HistoryItem{}, ErrConflict
	}
	if err != nil {
		return HistoryItem{}, err
	}
	if err := validateHistoryUploadShape(upload); err != nil {
		return HistoryItem{}, err
	}
	if err := validateHistoryUploadRecipient(upload); err != nil {
		return HistoryItem{}, err
	}
	if err := s.requireHistoryRecipientAuthority(ctx, tx, userID, actorID, sessionID, upload.RecipientDeviceID, upload.RecipientKeyEpoch); err != nil {
		return HistoryItem{}, err
	}
	if upload.Kind == "SNAPSHOT" && upload.FormatName != nil && *upload.FormatName == HistoryFormatTerminalSnapshot &&
		upload.SnapshotSequence != nil && *upload.SnapshotSequence > lastOutputSequence {
		return HistoryItem{}, ErrInvalidHistorySnapshot
	}
	itemID := uuid.New()
	var createdAt time.Time
	var historyRevision int64
	if upload.Kind == "CHUNK" {
		// The object key identifies this upload attempt, while the encrypted
		// content identity is key, digest, and size. Retain the first attempt's
		// canonical row when an identical sequence range is uploaded again.
		err = tx.QueryRow(ctx, `
			INSERT INTO event_chunks(id,session_id,start_seq,end_seq,object_key,key_id,ciphertext_sha256,size_bytes,recipient_device_id)
			VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
			ON CONFLICT ON CONSTRAINT event_chunks_session_recipient_range_key DO UPDATE
			SET id=event_chunks.id
			WHERE event_chunks.key_id=EXCLUDED.key_id
			  AND event_chunks.ciphertext_sha256=EXCLUDED.ciphertext_sha256
			  AND event_chunks.size_bytes=EXCLUDED.size_bytes
			RETURNING id,object_key,key_id,ciphertext_sha256,size_bytes,created_at,history_revision`, itemID, sessionID,
			upload.StartSequence, upload.EndSequence, upload.ObjectKey, upload.KeyID, upload.CiphertextSHA256,
			upload.SizeBytes, upload.RecipientDeviceID).Scan(&itemID, &upload.ObjectKey, &upload.KeyID, &upload.CiphertextSHA256,
			&upload.SizeBytes, &createdAt, &historyRevision)
		if errors.Is(err, pgx.ErrNoRows) {
			return HistoryItem{}, ErrConflict
		}
	} else if upload.Kind == "SNAPSHOT" {
		err = tx.QueryRow(ctx, `
			INSERT INTO snapshots(id,session_id,snapshot_seq,rows,columns,object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version,recipient_device_id)
			VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
			ON CONFLICT ON CONSTRAINT snapshots_session_recipient_sequence_format_key DO UPDATE
			SET id=snapshots.id
			WHERE snapshots.key_id=EXCLUDED.key_id
			  AND snapshots.ciphertext_sha256=EXCLUDED.ciphertext_sha256
			  AND snapshots.size_bytes=EXCLUDED.size_bytes
			  AND snapshots.rows=EXCLUDED.rows
			  AND snapshots.columns=EXCLUDED.columns
			RETURNING id,object_key,key_id,ciphertext_sha256,size_bytes,rows,columns,created_at,history_revision`, itemID, sessionID, upload.SnapshotSequence,
			upload.Rows, upload.Columns, upload.ObjectKey, upload.KeyID, upload.CiphertextSHA256, upload.SizeBytes,
			upload.FormatName, upload.FormatVersion, upload.RecipientDeviceID).Scan(&itemID, &upload.ObjectKey,
			&upload.KeyID, &upload.CiphertextSHA256, &upload.SizeBytes, upload.Rows, upload.Columns, &createdAt,
			&historyRevision)
		if errors.Is(err, pgx.ErrNoRows) {
			return HistoryItem{}, ErrConflict
		}
	} else {
		err = commitTranscriptPage(ctx, tx, sessionID, &upload, &itemID, &createdAt, &historyRevision)
	}
	if err != nil {
		return HistoryItem{}, err
	}
	if upload.Kind == "SNAPSHOT" && upload.FormatName != nil && *upload.FormatName == HistoryFormatTerminalSnapshot {
		if err := ensureTranscriptPublication(ctx, tx, sessionID, upload); err != nil {
			return HistoryItem{}, err
		}
	}
	if _, err := tx.Exec(ctx, `UPDATE history_uploads SET committed_at=now() WHERE id=$1`, uploadID); err != nil {
		return HistoryItem{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return HistoryItem{}, err
	}
	item := HistoryItem{Kind: strings.ToLower(upload.Kind), ID: itemID, ObjectKey: upload.ObjectKey, KeyID: upload.KeyID,
		CiphertextSHA256: upload.CiphertextSHA256, SizeBytes: upload.SizeBytes, CreatedAt: createdAt,
		HistoryRevision: historyRevision}
	if upload.StartSequence != nil {
		item.StartSequence = uint64(*upload.StartSequence)
	}
	if upload.EndSequence != nil {
		item.EndSequence = uint64(*upload.EndSequence)
	}
	if upload.SnapshotSequence != nil {
		item.SnapshotSequence = uint64(*upload.SnapshotSequence)
	}
	if upload.AsOfSnapshotSequence != nil {
		item.AsOfSnapshotSequence = uint64(*upload.AsOfSnapshotSequence)
	}
	if upload.FirstLineID != nil {
		item.FirstLineID = uint64(*upload.FirstLineID)
	}
	if upload.LastLineID != nil {
		item.LastLineID = uint64(*upload.LastLineID)
	}
	if upload.Rows != nil {
		item.Rows = *upload.Rows
	}
	if upload.Columns != nil {
		item.Columns = *upload.Columns
	}
	if upload.FormatName != nil {
		item.FormatName = *upload.FormatName
	}
	if upload.FormatVersion != nil {
		item.FormatVersion = *upload.FormatVersion
	}
	return item, nil
}

func ensureTranscriptPublication(
	ctx context.Context,
	tx pgx.Tx,
	sessionID uuid.UUID,
	upload HistoryUploadRecord,
) error {
	if upload.TranscriptInitialBeforeLineID == nil || upload.TranscriptPageCount == nil || upload.SnapshotSequence == nil {
		return nil
	}
	var ignored int64
	err := tx.QueryRow(ctx, `
		INSERT INTO transcript_publications(
		  session_id,recipient_device_id,as_of_snapshot_seq,initial_before_line_id,expected_page_count)
		VALUES($1,$2,$3,$4,$5)
		ON CONFLICT (session_id,recipient_device_id,as_of_snapshot_seq) DO UPDATE
		SET initial_before_line_id=transcript_publications.initial_before_line_id
		WHERE transcript_publications.initial_before_line_id=EXCLUDED.initial_before_line_id
		  AND transcript_publications.expected_page_count=EXCLUDED.expected_page_count
		RETURNING initial_before_line_id`,
		sessionID, upload.RecipientDeviceID, upload.SnapshotSequence,
		upload.TranscriptInitialBeforeLineID, upload.TranscriptPageCount,
	).Scan(&ignored)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrConflict
	}
	if err != nil {
		return err
	}
	return finalizeTranscriptPublication(ctx, tx, sessionID, upload.RecipientDeviceID, *upload.SnapshotSequence)
}

func lockTranscriptPublication(
	ctx context.Context,
	tx pgx.Tx,
	sessionID, recipientID uuid.UUID,
	asOfSnapshotSequence int64,
) (transcriptPublicationRecord, bool, error) {
	var publication transcriptPublicationRecord
	err := tx.QueryRow(ctx, `
		SELECT initial_before_line_id,expected_page_count,completed_revision
		FROM transcript_publications
		WHERE session_id=$1 AND recipient_device_id=$2 AND as_of_snapshot_seq=$3
		FOR UPDATE`, sessionID, recipientID, asOfSnapshotSequence,
	).Scan(&publication.InitialBeforeLineID, &publication.ExpectedPageCount, &publication.CompletedRevision)
	if errors.Is(err, pgx.ErrNoRows) {
		return transcriptPublicationRecord{}, false, nil
	}
	if err != nil {
		return transcriptPublicationRecord{}, false, err
	}
	return publication, true, nil
}

func finalizeTranscriptPublication(
	ctx context.Context,
	tx pgx.Tx,
	sessionID, recipientID uuid.UUID,
	asOfSnapshotSequence int64,
) error {
	publication, found, err := lockTranscriptPublication(ctx, tx, sessionID, recipientID, asOfSnapshotSequence)
	if err != nil || !found || publication.CompletedRevision != nil {
		return err
	}

	var snapshotRevision int64
	err = tx.QueryRow(ctx, `
		SELECT history_revision
		FROM snapshots
		WHERE session_id=$1 AND recipient_device_id=$2 AND snapshot_seq=$3
		  AND format_name='TERMLINK_TERMINAL_SNAPSHOT' AND format_version=1 AND deleted_at IS NULL`,
		sessionID, recipientID, asOfSnapshotSequence,
	).Scan(&snapshotRevision)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrConflict
	}
	if err != nil {
		return err
	}

	var pageCount int
	var maxPageRevision *int64
	var pagesBelowInitial bool
	err = tx.QueryRow(ctx, `
		SELECT count(*),max(history_revision),coalesce(bool_and(last_line_id<$4),true)
		FROM transcript_pages
		WHERE session_id=$1 AND recipient_device_id=$2 AND as_of_snapshot_seq=$3 AND deleted_at IS NULL`,
		sessionID, recipientID, asOfSnapshotSequence, publication.InitialBeforeLineID,
	).Scan(&pageCount, &maxPageRevision, &pagesBelowInitial)
	if err != nil {
		return err
	}
	if !pagesBelowInitial || pageCount > publication.ExpectedPageCount {
		return ErrConflict
	}
	if pageCount < publication.ExpectedPageCount {
		return nil
	}
	completedRevision := snapshotRevision
	if maxPageRevision != nil && *maxPageRevision > completedRevision {
		completedRevision = *maxPageRevision
	}
	command, err := tx.Exec(ctx, `
		UPDATE transcript_publications
		SET completed_revision=$4,completed_at=now()
		WHERE session_id=$1 AND recipient_device_id=$2 AND as_of_snapshot_seq=$3
		  AND completed_revision IS NULL`, sessionID, recipientID, asOfSnapshotSequence, completedRevision)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrConflict
	}
	return nil
}

func commitTranscriptPage(
	ctx context.Context,
	tx pgx.Tx,
	sessionID uuid.UUID,
	upload *HistoryUploadRecord,
	itemID *uuid.UUID,
	createdAt *time.Time,
	historyRevision *int64,
) error {
	err := tx.QueryRow(ctx, `
		SELECT id,object_key,key_id,ciphertext_sha256,size_bytes,created_at,history_revision
		FROM transcript_pages
		WHERE session_id=$1 AND recipient_device_id=$2 AND as_of_snapshot_seq=$3
		  AND first_line_id=$4 AND last_line_id=$5
		  AND format_name=$6 AND format_version=$7 AND deleted_at IS NULL
		  AND key_id=$8 AND ciphertext_sha256=$9 AND size_bytes=$10`,
		sessionID, upload.RecipientDeviceID, upload.AsOfSnapshotSequence, upload.FirstLineID,
		upload.LastLineID, upload.FormatName, upload.FormatVersion, upload.KeyID,
		upload.CiphertextSHA256, upload.SizeBytes,
	).Scan(itemID, &upload.ObjectKey, &upload.KeyID, &upload.CiphertextSHA256, &upload.SizeBytes, createdAt, historyRevision)
	if err == nil {
		return nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return err
	}
	publication, publicationFound, err := lockTranscriptPublication(
		ctx, tx, sessionID, upload.RecipientDeviceID, *upload.AsOfSnapshotSequence,
	)
	if err != nil {
		return err
	}
	if publicationFound && (publication.CompletedRevision != nil || *upload.LastLineID >= publication.InitialBeforeLineID) {
		return ErrConflict
	}

	var overlaps bool
	err = tx.QueryRow(ctx, `
		SELECT EXISTS (
		  SELECT 1 FROM transcript_pages
		  WHERE session_id=$1 AND recipient_device_id=$2 AND as_of_snapshot_seq=$3
		    AND deleted_at IS NULL AND first_line_id<=$5 AND last_line_id>=$4
		)`, sessionID, upload.RecipientDeviceID, upload.AsOfSnapshotSequence,
		upload.FirstLineID, upload.LastLineID).Scan(&overlaps)
	if err != nil {
		return err
	}
	if overlaps {
		return ErrConflict
	}

	err = tx.QueryRow(ctx, `
		INSERT INTO transcript_pages(
		  id,session_id,recipient_device_id,as_of_snapshot_seq,first_line_id,last_line_id,
		  object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version)
		VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
		RETURNING id,object_key,key_id,ciphertext_sha256,size_bytes,created_at,history_revision`,
		*itemID, sessionID, upload.RecipientDeviceID, upload.AsOfSnapshotSequence,
		upload.FirstLineID, upload.LastLineID, upload.ObjectKey, upload.KeyID,
		upload.CiphertextSHA256, upload.SizeBytes, upload.FormatName, upload.FormatVersion,
	).Scan(itemID, &upload.ObjectKey, &upload.KeyID, &upload.CiphertextSHA256, &upload.SizeBytes, createdAt, historyRevision)
	var postgresError *pgconn.PgError
	if errors.As(err, &postgresError) && postgresError.Code == "23505" {
		return ErrConflict
	}
	if err != nil {
		return err
	}
	if publicationFound {
		return finalizeTranscriptPublication(ctx, tx, sessionID, upload.RecipientDeviceID, *upload.AsOfSnapshotSequence)
	}
	return nil
}

// SessionHistoryObjectKeys checks delete-specific MANAGE authority and
// activity before returning the currently indexed ciphertext keys.
func (s *Postgres) SessionHistoryObjectKeys(ctx context.Context, userID, actorID, sessionID uuid.UUID) ([]string, error) {
	var permission string
	var deleted, active bool
	err := s.Pool.QueryRow(ctx, `
		SELECT CASE
		         WHEN p.device_id=actor.id OR $4 THEN 'MANAGE'
		         ELSE COALESCE(manager.permission,'')
		       END,
		       s.deleted_at IS NOT NULL,
		       s.state='RUNNING'
		         AND CASE
		           WHEN p.session_heartbeat_capable
		             THEN s.last_seen_at > now()-($5 * interval '1 second')
		           ELSE p.last_seen_at > now()-($5 * interval '1 second')
		         END
		FROM sessions s
		JOIN pcs p ON p.id=s.pc_id
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices actor
		  ON actor.id=$2 AND actor.user_id=$3 AND actor.trust_state='TRUSTED'
		LEFT JOIN device_grants manager
		  ON manager.pc_id=p.id AND manager.device_id=actor.id
		 AND (manager.expires_at IS NULL OR manager.expires_at>now())
		WHERE s.id=$1 AND p.owner_user_id=$3`, sessionID, actorID, userID, s.AutoTrustDevices,
		float64(sessionOnlineWindow/time.Second)).Scan(&permission, &deleted, &active)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, ErrDenied
	}
	if err != nil {
		return nil, err
	}
	if permission != "MANAGE" {
		return nil, ErrDenied
	}
	if deleted {
		return nil, nil
	}
	if active {
		return nil, ErrSessionActive
	}
	rows, err := s.Pool.Query(ctx, `
		SELECT object_key FROM event_chunks WHERE session_id=$1 AND deleted_at IS NULL
		UNION SELECT object_key FROM snapshots WHERE session_id=$1 AND deleted_at IS NULL
		UNION SELECT object_key FROM transcript_pages WHERE session_id=$1 AND deleted_at IS NULL
		UNION SELECT object_key FROM history_uploads WHERE session_id=$1`, sessionID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []string
	for rows.Next() {
		var key string
		if err := rows.Scan(&key); err != nil {
			return nil, err
		}
		result = append(result, key)
	}
	return result, rows.Err()
}

func (s *Postgres) MarkSessionDeleted(ctx context.Context, userID, actorID, sessionID uuid.UUID, traceID string) error {
	var err error
	for attempt := 0; attempt < 5; attempt++ {
		err = s.markSessionDeletedOnce(ctx, userID, actorID, sessionID, traceID)
		if !isPostgresRetryableTransactionError(err) {
			return err
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
	}
	return err
}

func isPostgresRetryableTransactionError(err error) bool {
	var postgresError *pgconn.PgError
	return errors.As(err, &postgresError) && (postgresError.Code == "40001" || postgresError.Code == "40P01")
}

func (s *Postgres) markSessionDeletedOnce(ctx context.Context, userID, actorID, sessionID uuid.UUID, traceID string) error {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var permission string
	var deleted, active bool
	err = tx.QueryRow(ctx, `
		SELECT CASE
		         WHEN p.device_id=actor.id OR $4 THEN 'MANAGE'
		         ELSE COALESCE(manager.permission,'')
		       END,
		       s.deleted_at IS NOT NULL,
		       s.state='RUNNING'
		         AND CASE
		           WHEN p.session_heartbeat_capable
		             THEN s.last_seen_at > now()-($5 * interval '1 second')
		           ELSE p.last_seen_at > now()-($5 * interval '1 second')
		         END
		FROM sessions s
		JOIN pcs p ON p.id=s.pc_id
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices actor ON actor.id=$2 AND actor.user_id=$3 AND actor.trust_state='TRUSTED'
		LEFT JOIN device_grants manager
		  ON manager.pc_id=p.id AND manager.device_id=actor.id
		 AND (manager.expires_at IS NULL OR manager.expires_at>now())
		WHERE s.id=$1 AND p.owner_user_id=$3
		FOR UPDATE OF s,p`, sessionID, actorID, userID, s.AutoTrustDevices,
		float64(sessionOnlineWindow/time.Second)).Scan(&permission, &deleted, &active)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	}
	if err != nil {
		return err
	}
	if permission != "MANAGE" {
		return ErrDenied
	}
	if deleted {
		return tx.Commit(ctx)
	}
	if active {
		return ErrSessionActive
	}
	if err := tombstoneSessionHistory(ctx, tx, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `
		UPDATE sessions
		SET state='DELETED',deleted_at=now(),deletion_reason='USER_REQUESTED'
		WHERE id=$1`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id) VALUES($1,$2,'session.delete','session',$3,'SUCCEEDED',$4)`, userID, actorID, sessionID, traceID); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func tombstoneSessionHistory(ctx context.Context, tx pgx.Tx, sessionID uuid.UUID) error {
	if _, err := tx.Exec(ctx, `
		WITH object_keys AS (
			SELECT object_key FROM event_chunks WHERE session_id=$1 AND deleted_at IS NULL
			UNION SELECT object_key FROM snapshots WHERE session_id=$1 AND deleted_at IS NULL
			UNION SELECT object_key FROM transcript_pages WHERE session_id=$1 AND deleted_at IS NULL
			UNION SELECT object_key FROM history_uploads WHERE session_id=$1
		)
		INSERT INTO outbox(event_type,aggregate_id,payload)
		SELECT 'history.object.delete',$1,jsonb_build_object('object_key',object_key)
		FROM object_keys
		ON CONFLICT DO NOTHING`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `DELETE FROM control_leases WHERE session_id=$1`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `UPDATE event_chunks SET deleted_at=now() WHERE session_id=$1 AND deleted_at IS NULL`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `UPDATE snapshots SET deleted_at=now() WHERE session_id=$1 AND deleted_at IS NULL`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `UPDATE transcript_pages SET deleted_at=now() WHERE session_id=$1 AND deleted_at IS NULL`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `DELETE FROM history_uploads WHERE session_id=$1`, sessionID); err != nil {
		return err
	}
	return nil
}

const maxAutomaticSessionCleanupBatch = 100

// CleanupSessions tombstones a bounded batch of server-side session metadata.
// It does not publish a realtime command or ask a Windows Agent to terminate a
// process. EXITED rows are safe after endedBefore. RUNNING and INTERRUPTED rows
// are eligible only after their PC opted in to explicit session heartbeats and
// that session metadata is older than staleBefore.
func (s *Postgres) CleanupSessions(ctx context.Context, endedBefore, staleBefore time.Time, batchSize int) (int, error) {
	if batchSize <= 0 {
		return 0, nil
	}
	if batchSize > maxAutomaticSessionCleanupBatch {
		batchSize = maxAutomaticSessionCleanupBatch
	}
	rows, err := s.Pool.Query(ctx, `
		SELECT session.id
		FROM sessions session
		JOIN pcs pc ON pc.id=session.pc_id
		WHERE session.deleted_at IS NULL AND (
			(session.state='EXITED'
			 AND COALESCE(session.ended_at,session.last_seen_at)<=$1)
			OR
			(session.state IN ('RUNNING','INTERRUPTED')
			 AND pc.session_heartbeat_capable
			 AND GREATEST(session.last_seen_at,pc.session_heartbeat_enabled_at)<=$2)
		)
		ORDER BY CASE WHEN session.state='EXITED' THEN 0 ELSE 1 END,
		         COALESCE(session.ended_at,session.last_seen_at,pc.session_heartbeat_enabled_at),
		         session.id
		LIMIT $3`, endedBefore, staleBefore, batchSize)
	if err != nil {
		return 0, err
	}
	var candidates []uuid.UUID
	for rows.Next() {
		var sessionID uuid.UUID
		if err := rows.Scan(&sessionID); err != nil {
			rows.Close()
			return 0, err
		}
		candidates = append(candidates, sessionID)
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return 0, err
	}
	rows.Close()

	cleaned := 0
	for _, sessionID := range candidates {
		changed, err := s.cleanupSession(ctx, sessionID, endedBefore, staleBefore)
		if err != nil {
			return cleaned, err
		}
		if changed {
			cleaned++
		}
	}
	return cleaned, nil
}

func (s *Postgres) cleanupSession(ctx context.Context, sessionID uuid.UUID, endedBefore, staleBefore time.Time) (bool, error) {
	var changed bool
	var err error
	for attempt := 0; attempt < 5; attempt++ {
		changed, err = s.cleanupSessionOnce(ctx, sessionID, endedBefore, staleBefore)
		if !isPostgresRetryableTransactionError(err) {
			return changed, err
		}
		if ctx.Err() != nil {
			return false, ctx.Err()
		}
	}
	return changed, err
}

func (s *Postgres) cleanupSessionOnce(ctx context.Context, sessionID uuid.UUID, endedBefore, staleBefore time.Time) (bool, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return false, err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	// Lock the owning PC before the session, matching UpsertSession. The second
	// query is the authoritative eligibility check; the unlocked candidate scan
	// is only a bounded work queue.
	var userID uuid.UUID
	var heartbeatCapable bool
	var heartbeatEnabledAt *time.Time
	err = tx.QueryRow(ctx, `
		SELECT pc.owner_user_id,pc.session_heartbeat_capable,pc.session_heartbeat_enabled_at
		FROM pcs pc
		JOIN sessions session ON session.pc_id=pc.id
		WHERE session.id=$1
		FOR UPDATE OF pc`, sessionID).Scan(&userID, &heartbeatCapable, &heartbeatEnabledAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return false, tx.Commit(ctx)
	}
	if err != nil {
		return false, err
	}

	var state string
	var deletedAt, endedAt, lastSeenAt *time.Time
	err = tx.QueryRow(ctx, `
		SELECT state,deleted_at,ended_at,last_seen_at
		FROM sessions
		WHERE id=$1
		FOR UPDATE`, sessionID).Scan(&state, &deletedAt, &endedAt, &lastSeenAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return false, tx.Commit(ctx)
	}
	if err != nil {
		return false, err
	}
	if deletedAt != nil {
		return false, tx.Commit(ctx)
	}

	reason := ""
	switch state {
	case "EXITED":
		reference := endedAt
		if reference == nil {
			reference = lastSeenAt
		}
		if reference != nil && !reference.After(endedBefore) {
			reason = "AUTO_ENDED"
		}
	case "RUNNING", "INTERRUPTED":
		if !heartbeatCapable {
			break
		}
		reference := lastSeenAt
		if reference == nil || (heartbeatEnabledAt != nil && heartbeatEnabledAt.After(*reference)) {
			reference = heartbeatEnabledAt
		}
		if reference != nil && !reference.After(staleBefore) {
			reason = "AUTO_STALE"
		}
	}
	if reason == "" {
		return false, tx.Commit(ctx)
	}

	if err := tombstoneSessionHistory(ctx, tx, sessionID); err != nil {
		return false, err
	}
	if _, err := tx.Exec(ctx, `
		UPDATE sessions
		SET state='DELETED',deleted_at=now(),deletion_reason=$2
		WHERE id=$1`, sessionID, reason); err != nil {
		return false, err
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO audit_events(
			user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,NULL,'session.auto_delete','session',$2,'SUCCEEDED',$3,
			jsonb_build_object('deletion_reason',$4::text,'previous_state',$5::text))`,
		userID, sessionID, "session-cleanup:"+sessionID.String(), reason, state); err != nil {
		return false, err
	}
	if err := tx.Commit(ctx); err != nil {
		return false, err
	}
	return true, nil
}

func (s *Postgres) ListHistory(ctx context.Context, userID, deviceID, sessionID uuid.UUID) ([]HistoryItem, error) {
	if _, err := s.SessionPermission(ctx, userID, deviceID, sessionID); err != nil {
		return nil, err
	}
	var ownerPC bool
	if err := s.Pool.QueryRow(ctx, `
		SELECT p.device_id=$2
		FROM sessions s JOIN pcs p ON p.id=s.pc_id
		WHERE s.id=$1 AND p.owner_user_id=$3 AND s.deleted_at IS NULL`, sessionID, deviceID, userID,
	).Scan(&ownerPC); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, ErrDenied
		}
		return nil, err
	}
	rows, err := s.Pool.Query(ctx, `
		SELECT 'chunk',id,start_seq,end_seq,NULL::bigint,NULL::integer,NULL::integer,
		       object_key,key_id,ciphertext_sha256,size_bytes,''::text,0,created_at,history_revision
		FROM event_chunks
		WHERE session_id=$1 AND deleted_at IS NULL AND ($2 OR recipient_device_id=$3)
		UNION ALL
		SELECT 'snapshot',id,NULL::bigint,NULL::bigint,snapshot_seq,rows,columns,
		       object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version,created_at,history_revision
		FROM snapshots
		WHERE session_id=$1 AND deleted_at IS NULL AND ($2 OR recipient_device_id=$3)
		ORDER BY 14,2`, sessionID, ownerPC, deviceID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]HistoryItem, 0)
	for rows.Next() {
		item, err := scanHistoryItem(rows)
		if err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

// ListHistoryPage returns an immutable view of the history catalog. The
// session row share lock is the read-side of the revision trigger's write
// fence: an in-flight upload is either fully before or fully after the anchor.
func (s *Postgres) ListHistoryPage(ctx context.Context, userID, deviceID, sessionID uuid.UUID, request HistoryPageRequest) (HistoryPage, error) {
	if request.Limit < 1 || request.Limit > 100 ||
		(request.Kind != HistoryKindAll && request.Kind != HistoryKindChunk && request.Kind != HistoryKindSnapshot) ||
		(request.FormatName != "" && (request.Kind != HistoryKindSnapshot ||
			(request.FormatName != HistoryFormatBoundedEventReplay && request.FormatName != HistoryFormatTerminalSnapshot))) ||
		(request.AsOfRevision != nil && *request.AsOfRevision < 0) {
		return HistoryPage{}, ErrInvalidHistoryPage
	}
	if request.Before != nil {
		if request.Before.Sequence < 0 || request.Before.ID == uuid.Nil ||
			(request.Before.Kind != HistoryKindChunk && request.Before.Kind != HistoryKindSnapshot) ||
			(request.Kind != HistoryKindAll && request.Before.Kind != request.Kind) {
			return HistoryPage{}, ErrInvalidHistoryPage
		}
	}

	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.ReadCommitted})
	if err != nil {
		return HistoryPage{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	var currentRevision, lastOutputSequence int64
	var ownerPC bool
	err = tx.QueryRow(ctx, `
		SELECT s.history_revision,s.last_output_seq,p.device_id=$2
		FROM sessions s
		JOIN pcs p ON p.id=s.pc_id
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices owner_device ON owner_device.id=p.device_id AND owner_device.trust_state='TRUSTED'
		JOIN devices actor ON actor.id=$2 AND actor.user_id=$3 AND actor.trust_state='TRUSTED'
		LEFT JOIN device_grants g
		  ON g.pc_id=p.id AND g.device_id=actor.id
		 AND (g.expires_at IS NULL OR g.expires_at>now())
		WHERE s.id=$1 AND p.owner_user_id=$3 AND p.remote_routing_enabled AND s.deleted_at IS NULL
		  AND ($4 OR p.device_id=actor.id OR g.device_id IS NOT NULL)
		FOR SHARE OF s`, sessionID, deviceID, userID, s.AutoTrustDevices).Scan(&currentRevision, &lastOutputSequence, &ownerPC)
	if errors.Is(err, pgx.ErrNoRows) {
		return HistoryPage{}, ErrDenied
	}
	if err != nil {
		return HistoryPage{}, err
	}
	anchor := currentRevision
	if request.AsOfRevision != nil {
		anchor = *request.AsOfRevision
		if anchor > currentRevision {
			return HistoryPage{}, ErrHistoryAnchorAhead
		}
	}

	var beforeSequence any
	beforeRank := 0
	beforeID := uuid.Nil
	if request.Before != nil {
		beforeSequence = request.Before.Sequence
		beforeRank = historyKindRank(request.Before.Kind)
		beforeID = request.Before.ID
	}
	rows, err := tx.Query(ctx, `
		WITH candidates AS (
			SELECT 'chunk'::text AS kind,id,start_seq,end_seq,NULL::bigint AS snapshot_seq,
			       NULL::integer AS rows,NULL::integer AS columns,object_key,key_id,ciphertext_sha256,
			       size_bytes,''::text AS format_name,0 AS format_version,created_at,history_revision,
			       end_seq AS logical_sequence,1 AS kind_rank
			FROM event_chunks
			WHERE session_id=$1 AND deleted_at IS NULL AND history_revision<=$2
			  AND ($6='all' OR $6='chunk')
			  AND ($7 OR recipient_device_id=$8)
			  AND ($3::bigint IS NULL OR end_seq<$3 OR
			       (end_seq=$3 AND (1<$4 OR (1=$4 AND id<$5))))
			UNION ALL
			SELECT 'snapshot'::text AS kind,id,NULL::bigint,NULL::bigint,snapshot_seq,
			       rows,columns,object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version,
			       created_at,history_revision,snapshot_seq AS logical_sequence,2 AS kind_rank
			FROM snapshots
			WHERE session_id=$1 AND deleted_at IS NULL AND history_revision<=$2
			  AND ($6='all' OR $6='snapshot')
			  AND ($7 OR recipient_device_id=$8)
			  AND ($9='' OR format_name=$9)
			  AND (format_name<>'TERMLINK_TERMINAL_SNAPSHOT' OR snapshot_seq<=$11)
			  AND ($3::bigint IS NULL OR snapshot_seq<$3 OR
			       (snapshot_seq=$3 AND (2<$4 OR (2=$4 AND id<$5))))
		)
		SELECT kind,id,start_seq,end_seq,snapshot_seq,rows,columns,object_key,key_id,ciphertext_sha256,
		       size_bytes,format_name,format_version,created_at,history_revision
		FROM candidates
		ORDER BY logical_sequence DESC,kind_rank DESC,id DESC
		LIMIT $10`, sessionID, anchor, beforeSequence, beforeRank, beforeID, string(request.Kind), ownerPC,
		deviceID, request.FormatName, request.Limit+1, lastOutputSequence)
	if err != nil {
		return HistoryPage{}, err
	}
	items := make([]HistoryItem, 0, request.Limit+1)
	for rows.Next() {
		item, scanErr := scanHistoryItem(rows)
		if scanErr != nil {
			rows.Close()
			return HistoryPage{}, scanErr
		}
		items = append(items, item)
	}
	rowsErr := rows.Err()
	rows.Close()
	if rowsErr != nil {
		return HistoryPage{}, rowsErr
	}
	hasMore := len(items) > request.Limit
	if hasMore {
		items = items[:request.Limit]
	}
	if err := tx.Commit(ctx); err != nil {
		return HistoryPage{}, err
	}
	return HistoryPage{Items: items, AsOfRevision: anchor, HasMore: hasMore}, nil
}

func historyKindRank(kind HistoryKind) int {
	if kind == HistoryKindSnapshot {
		return 2
	}
	return 1
}

type historyItemScanner interface {
	Scan(dest ...any) error
}

func scanHistoryItem(row historyItemScanner) (HistoryItem, error) {
	var item HistoryItem
	var start, end, snapshot *int64
	var rows, columns *int
	if err := row.Scan(&item.Kind, &item.ID, &start, &end, &snapshot, &rows, &columns,
		&item.ObjectKey, &item.KeyID, &item.CiphertextSHA256, &item.SizeBytes, &item.FormatName,
		&item.FormatVersion, &item.CreatedAt, &item.HistoryRevision); err != nil {
		return HistoryItem{}, err
	}
	if start != nil {
		item.StartSequence = uint64(*start)
	}
	if end != nil {
		item.EndSequence = uint64(*end)
	}
	if snapshot != nil {
		item.SnapshotSequence = uint64(*snapshot)
	}
	if rows != nil {
		item.Rows = *rows
	}
	if columns != nil {
		item.Columns = *columns
	}
	return item, nil
}

// ListTranscriptPage reads one encrypted transcript page for the requesting
// Android recipient. Transcript rows are deliberately separate from the
// legacy event/snapshot history catalog and its "all" selector.
func (s *Postgres) ListTranscriptPage(
	ctx context.Context,
	userID, deviceID, sessionID uuid.UUID,
	request TranscriptPageRequest,
) (TranscriptPage, error) {
	if request.Limit != 1 || request.AsOfSnapshotSequence < 1 ||
		(request.BeforeLineID != nil && *request.BeforeLineID < 1) ||
		(request.AsOfRevision != nil && *request.AsOfRevision < 0) {
		return TranscriptPage{}, ErrInvalidHistoryPage
	}

	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.ReadCommitted})
	if err != nil {
		return TranscriptPage{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	var currentRevision int64
	err = tx.QueryRow(ctx, `
		SELECT session.history_revision
		FROM sessions session
		JOIN pcs pc ON pc.id=session.pc_id AND pc.owner_user_id=$2 AND pc.remote_routing_enabled
		JOIN users account ON account.id=$2 AND account.status='ACTIVE'
		JOIN devices actor
		  ON actor.id=$3 AND actor.user_id=$2 AND actor.device_type='ANDROID' AND actor.trust_state='TRUSTED'
		LEFT JOIN device_grants actor_grant
		  ON actor_grant.pc_id=pc.id AND actor_grant.device_id=actor.id
		 AND (actor_grant.expires_at IS NULL OR actor_grant.expires_at>now())
		WHERE session.id=$1 AND session.deleted_at IS NULL
		  AND ($4 OR actor_grant.device_id IS NOT NULL)
		FOR SHARE OF session`, sessionID, userID, deviceID, s.AutoTrustDevices).Scan(&currentRevision)
	if errors.Is(err, pgx.ErrNoRows) {
		return TranscriptPage{}, ErrDenied
	}
	if err != nil {
		return TranscriptPage{}, err
	}

	publication, publicationFound, err := findTranscriptPublication(
		ctx, tx, sessionID, deviceID, request.AsOfSnapshotSequence,
	)
	if err != nil {
		return TranscriptPage{}, err
	}
	anchorRevision := currentRevision
	ready := true
	if publicationFound {
		if publication.CompletedRevision == nil {
			if request.AsOfRevision != nil {
				return TranscriptPage{}, ErrHistoryAnchorAhead
			}
			if err := tx.Commit(ctx); err != nil {
				return TranscriptPage{}, err
			}
			return TranscriptPage{Items: []HistoryItem{}, AsOfRevision: currentRevision, Ready: false}, nil
		}
		anchorRevision = *publication.CompletedRevision
		if request.AsOfRevision != nil && *request.AsOfRevision != anchorRevision {
			return TranscriptPage{}, ErrHistoryAnchorAhead
		}
	} else if request.AsOfRevision != nil {
		anchorRevision = *request.AsOfRevision
		if anchorRevision > currentRevision {
			return TranscriptPage{}, ErrHistoryAnchorAhead
		}
	}
	if err := requireHistoryTranscriptAnchor(ctx, tx, sessionID, deviceID, request.AsOfSnapshotSequence, &anchorRevision); err != nil {
		if errors.Is(err, ErrNotFound) {
			if err := tx.Commit(ctx); err != nil {
				return TranscriptPage{}, err
			}
			return TranscriptPage{Items: []HistoryItem{}, AsOfRevision: anchorRevision, Ready: false}, nil
		}
		return TranscriptPage{}, err
	}

	rows, err := tx.Query(ctx, `
		SELECT id,as_of_snapshot_seq,first_line_id,last_line_id,object_key,key_id,
		       ciphertext_sha256,size_bytes,format_name,format_version,created_at,history_revision
		FROM transcript_pages
		WHERE session_id=$1 AND recipient_device_id=$2 AND as_of_snapshot_seq=$3
		  AND deleted_at IS NULL AND history_revision<=$4
		  AND ($5::bigint IS NULL OR last_line_id<$5)
		ORDER BY last_line_id DESC,first_line_id DESC,id DESC
		LIMIT 2`, sessionID, deviceID, request.AsOfSnapshotSequence, anchorRevision, request.BeforeLineID)
	if err != nil {
		return TranscriptPage{}, err
	}
	items := make([]HistoryItem, 0, 2)
	for rows.Next() {
		var item HistoryItem
		var asOfSnapshotSequence, firstLineID, lastLineID int64
		if err := rows.Scan(&item.ID, &asOfSnapshotSequence, &firstLineID, &lastLineID,
			&item.ObjectKey, &item.KeyID, &item.CiphertextSHA256, &item.SizeBytes,
			&item.FormatName, &item.FormatVersion, &item.CreatedAt, &item.HistoryRevision); err != nil {
			rows.Close()
			return TranscriptPage{}, err
		}
		item.Kind = string(HistoryKindTranscript)
		item.AsOfSnapshotSequence = uint64(asOfSnapshotSequence)
		item.FirstLineID = uint64(firstLineID)
		item.LastLineID = uint64(lastLineID)
		items = append(items, item)
	}
	rowsErr := rows.Err()
	rows.Close()
	if rowsErr != nil {
		return TranscriptPage{}, rowsErr
	}
	hasMore := len(items) > request.Limit
	if hasMore {
		items = items[:request.Limit]
	}
	if err := tx.Commit(ctx); err != nil {
		return TranscriptPage{}, err
	}
	return TranscriptPage{Items: items, AsOfRevision: anchorRevision, HasMore: hasMore, Ready: ready}, nil
}

type Plan struct {
	Key           string `json:"key"`
	DisplayName   string `json:"display_name"`
	Description   string `json:"description"`
	BillingPeriod string `json:"billing_period"`
	// Quantity fields are exposed as -1 (UnlimitedQuota) by PublishedPlans;
	// database/catalog values remain for backward-compatible billing storage.
	MaxRemotePCs    int             `json:"max_remote_pcs"`
	MaxLiveSessions int             `json:"max_live_sessions"`
	FeatureFlags    json.RawMessage `json:"feature_flags"`
	ChannelProducts json.RawMessage `json:"channel_products"`
	SortOrder       int             `json:"sort_order"`
	PriceMinor      int64           `json:"price_minor"`
	Currency        string          `json:"currency"`
}

// PlanDraftInput is the server-side catalog definition. JSON values are
// validated by the API and validated again by store transactions before they
// can enter a draft catalog.
type PlanDraftInput struct {
	PlanKey          string
	DisplayName      string
	Description      string
	BillingPeriod    string
	MaxRemotePCs     int
	MaxLiveSessions  int
	FeatureFlags     json.RawMessage
	ChannelProducts  json.RawMessage
	SortOrder        int
	VisiblePlatforms []string
	AvailableFrom    *time.Time
	AvailableUntil   *time.Time
	FallbackPlanKey  string
	PriceMinor       int64
	Currency         string
}

type PlanDraft struct {
	ID               uuid.UUID       `json:"id"`
	CatalogVersion   int64           `json:"catalog_version"`
	Key              string          `json:"key"`
	DisplayName      string          `json:"display_name"`
	Description      string          `json:"description"`
	BillingPeriod    string          `json:"billing_period"`
	MaxRemotePCs     int             `json:"max_remote_pcs"`
	MaxLiveSessions  int             `json:"max_live_sessions"`
	FeatureFlags     json.RawMessage `json:"feature_flags"`
	ChannelProducts  json.RawMessage `json:"channel_products"`
	SortOrder        int             `json:"sort_order"`
	VisiblePlatforms []string        `json:"visible_platforms"`
	AvailableFrom    *time.Time      `json:"available_from,omitempty"`
	AvailableUntil   *time.Time      `json:"available_until,omitempty"`
	FallbackPlanKey  string          `json:"fallback_plan_key,omitempty"`
	PriceMinor       int64           `json:"price_minor"`
	Currency         string          `json:"currency"`
}

// IsAdmin is intentionally a database lookup. No client claim or device
// metadata is treated as an administrator grant.
func (s *Postgres) IsAdmin(ctx context.Context, userID uuid.UUID) (bool, error) {
	var admin bool
	err := s.Pool.QueryRow(ctx, `SELECT is_admin FROM users WHERE id=$1 AND status='ACTIVE'`, userID).Scan(&admin)
	if errors.Is(err, pgx.ErrNoRows) {
		return false, ErrDenied
	}
	return admin, err
}

func validatePlanDraftInput(input PlanDraftInput) error {
	input.PlanKey = strings.TrimSpace(input.PlanKey)
	input.DisplayName = strings.TrimSpace(input.DisplayName)
	input.BillingPeriod = strings.ToUpper(strings.TrimSpace(input.BillingPeriod))
	if len(input.PlanKey) < 1 || len(input.PlanKey) > 64 || input.DisplayName == "" || len(input.DisplayName) > 128 || len(input.Description) > 4096 || len(input.FallbackPlanKey) > 64 {
		return ErrDenied
	}
	if len(input.VisiblePlatforms) == 0 || len(input.VisiblePlatforms) > 8 {
		return ErrDenied
	}
	for _, character := range input.PlanKey {
		if (character < 'a' || character > 'z') && (character < 'A' || character > 'Z') && (character < '0' || character > '9') && character != '_' && character != '-' && character != '.' {
			return ErrDenied
		}
	}
	if input.BillingPeriod != "FREE" && input.BillingPeriod != "MONTHLY" && input.BillingPeriod != "YEARLY" {
		return ErrDenied
	}
	if input.MaxRemotePCs != UnlimitedQuota || input.MaxLiveSessions != UnlimitedQuota || input.SortOrder < -100000 || input.SortOrder > 100000 || input.PriceMinor < 0 || input.PriceMinor > 100_000_000 {
		return ErrDenied
	}
	if input.Currency != "CNY" || (input.BillingPeriod == "FREE" && input.PriceMinor != 0) {
		return ErrDenied
	}
	if input.AvailableFrom != nil && input.AvailableUntil != nil && !input.AvailableUntil.After(*input.AvailableFrom) {
		return ErrDenied
	}
	if !isJSONObject(input.FeatureFlags) || !isJSONObject(input.ChannelProducts) || len(input.FeatureFlags) > 64<<10 || len(input.ChannelProducts) > 64<<10 {
		return ErrDenied
	}
	for _, platform := range input.VisiblePlatforms {
		switch strings.ToUpper(strings.TrimSpace(platform)) {
		case "ANDROID", "WEB", "WINDOWS":
		default:
			return ErrDenied
		}
	}
	return nil
}

func normalizePlanDraftInput(input PlanDraftInput) PlanDraftInput {
	// Quantity limits are retained only as compatibility fields. Every new
	// catalog definition is persisted with the unlimited sentinel.
	input.MaxRemotePCs = UnlimitedQuota
	input.MaxLiveSessions = UnlimitedQuota
	if len(input.FeatureFlags) == 0 {
		input.FeatureFlags = json.RawMessage(`{}`)
	}
	if len(input.ChannelProducts) == 0 {
		input.ChannelProducts = json.RawMessage(`{}`)
	}
	if len(input.VisiblePlatforms) == 0 {
		input.VisiblePlatforms = []string{"ANDROID", "WEB"}
	}
	input.Currency = strings.ToUpper(strings.TrimSpace(input.Currency))
	if input.Currency == "" {
		input.Currency = "CNY"
	}
	return input
}

func isJSONObject(raw json.RawMessage) bool {
	if !json.Valid(raw) {
		return false
	}
	trimmed := strings.TrimSpace(string(raw))
	return strings.HasPrefix(trimmed, "{") && strings.HasSuffix(trimmed, "}")
}

func (s *Postgres) adminTx(ctx context.Context, adminID uuid.UUID) (pgx.Tx, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return nil, err
	}
	var admin bool
	if err := tx.QueryRow(ctx, `SELECT is_admin FROM users WHERE id=$1 AND status='ACTIVE' FOR UPDATE`, adminID).Scan(&admin); errors.Is(err, pgx.ErrNoRows) || !admin {
		_ = tx.Rollback(ctx)
		return nil, ErrDenied
	} else if err != nil {
		_ = tx.Rollback(ctx)
		return nil, err
	}
	return tx, nil
}

func scanPlanDraft(row pgx.Row) (PlanDraft, error) {
	var item PlanDraft
	err := row.Scan(&item.ID, &item.CatalogVersion, &item.Key, &item.DisplayName, &item.Description,
		&item.BillingPeriod, &item.MaxRemotePCs, &item.MaxLiveSessions, &item.FeatureFlags,
		&item.ChannelProducts, &item.SortOrder, &item.VisiblePlatforms, &item.AvailableFrom,
		&item.AvailableUntil, &item.FallbackPlanKey, &item.PriceMinor, &item.Currency)
	return item, err
}

// CreatePlanDraft creates a plan in the current DRAFT catalog, creating a new
// draft version when needed. Published rows are never mutated.
func (s *Postgres) CreatePlanDraft(ctx context.Context, adminID, actorDeviceID uuid.UUID, input PlanDraftInput, traceID string) (PlanDraft, error) {
	input = normalizePlanDraftInput(input)
	if err := validatePlanDraftInput(input); err != nil {
		return PlanDraft{}, err
	}
	tx, err := s.adminTx(ctx, adminID)
	if err != nil {
		return PlanDraft{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var catalogID uuid.UUID
	var version int64
	err = tx.QueryRow(ctx, `SELECT id,version FROM catalog_versions WHERE status='DRAFT' ORDER BY version DESC LIMIT 1 FOR UPDATE`).Scan(&catalogID, &version)
	if errors.Is(err, pgx.ErrNoRows) {
		if err = tx.QueryRow(ctx, `SELECT COALESCE(max(version),0)+1 FROM catalog_versions`).Scan(&version); err != nil {
			return PlanDraft{}, err
		}
		catalogID = uuid.New()
		if _, err = tx.Exec(ctx, `INSERT INTO catalog_versions(id,version,status) VALUES($1,$2,'DRAFT')`, catalogID, version); err != nil {
			return PlanDraft{}, err
		}
		// Start a draft from the current published catalog. This preserves the
		// free plan and any unchanged plans when an administrator publishes one
		// new or edited definition.
		rows, queryErr := tx.Query(ctx, `SELECT p.plan_key,p.display_name,p.description,p.billing_period,p.max_remote_pcs,p.max_live_sessions,p.feature_flags,p.channel_products,p.sort_order,p.visible_platforms,p.available_from,p.available_until,p.fallback_plan_key,p.price_minor,p.currency FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.status='PUBLISHED'`)
		if queryErr != nil {
			return PlanDraft{}, queryErr
		}
		type planCopy struct {
			key, displayName, description, period string
			maxPCs, maxSessions, sortOrder        int
			priceMinor                            int64
			currency                              string
			flags, products                       json.RawMessage
			platforms                             []string
			from, until                           *time.Time
			fallback                              *string
		}
		var copies []planCopy
		for rows.Next() {
			var key, displayName, description, period string
			var maxPCs, maxSessions, sortOrder int
			var flags, products json.RawMessage
			var platforms []string
			var from, until *time.Time
			var fallback *string
			var priceMinor int64
			var currency string
			if scanErr := rows.Scan(&key, &displayName, &description, &period, &maxPCs, &maxSessions, &flags, &products, &sortOrder, &platforms, &from, &until, &fallback, &priceMinor, &currency); scanErr != nil {
				rows.Close()
				return PlanDraft{}, scanErr
			}
			copies = append(copies, planCopy{key: key, displayName: displayName, description: description, period: period, maxPCs: maxPCs, maxSessions: maxSessions, sortOrder: sortOrder, flags: flags, products: products, platforms: platforms, from: from, until: until, fallback: fallback, priceMinor: priceMinor, currency: currency})
		}
		if rowsErr := rows.Err(); rowsErr != nil {
			rows.Close()
			return PlanDraft{}, rowsErr
		}
		rows.Close()
		for _, copy := range copies {
			if _, insertErr := tx.Exec(ctx, `INSERT INTO plans(id,catalog_version_id,plan_key,display_name,description,billing_period,max_remote_pcs,max_live_sessions,feature_flags,channel_products,sort_order,visible_platforms,available_from,available_until,fallback_plan_key,price_minor,currency) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10::jsonb,$11,$12,$13,$14,$15,$16,$17)`, uuid.New(), catalogID, copy.key, copy.displayName, copy.description, copy.period, copy.maxPCs, copy.maxSessions, copy.flags, copy.products, copy.sortOrder, copy.platforms, copy.from, copy.until, copy.fallback, copy.priceMinor, copy.currency); insertErr != nil {
				return PlanDraft{}, insertErr
			}
		}
	} else if err != nil {
		return PlanDraft{}, err
	}
	itemID := uuid.New()
	existingErr := tx.QueryRow(ctx, `SELECT id FROM plans WHERE catalog_version_id=$1 AND plan_key=$2`, catalogID, strings.TrimSpace(input.PlanKey)).Scan(&itemID)
	if existingErr == nil {
		_, err = tx.Exec(ctx, `UPDATE plans SET display_name=$2,description=$3,billing_period=$4,max_remote_pcs=$5,max_live_sessions=$6,feature_flags=$7::jsonb,channel_products=$8::jsonb,sort_order=$9,visible_platforms=$10,available_from=$11,available_until=$12,fallback_plan_key=NULLIF($13,''),price_minor=$14,currency=$15 WHERE id=$1`,
			itemID, strings.TrimSpace(input.DisplayName), strings.TrimSpace(input.Description), strings.ToUpper(strings.TrimSpace(input.BillingPeriod)), input.MaxRemotePCs, input.MaxLiveSessions, input.FeatureFlags, input.ChannelProducts, input.SortOrder, input.VisiblePlatforms, input.AvailableFrom, input.AvailableUntil, strings.TrimSpace(input.FallbackPlanKey), input.PriceMinor, input.Currency)
	} else {
		if !errors.Is(existingErr, pgx.ErrNoRows) {
			return PlanDraft{}, existingErr
		}
		_, err = tx.Exec(ctx, `
			INSERT INTO plans(id,catalog_version_id,plan_key,display_name,description,billing_period,max_remote_pcs,max_live_sessions,feature_flags,channel_products,sort_order,visible_platforms,available_from,available_until,fallback_plan_key,price_minor,currency)
			VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10::jsonb,$11,$12,$13,$14,NULLIF($15,''),$16,$17)`,
			itemID, catalogID, strings.TrimSpace(input.PlanKey), strings.TrimSpace(input.DisplayName), strings.TrimSpace(input.Description), strings.ToUpper(strings.TrimSpace(input.BillingPeriod)),
			input.MaxRemotePCs, input.MaxLiveSessions, input.FeatureFlags, input.ChannelProducts, input.SortOrder, input.VisiblePlatforms, input.AvailableFrom, input.AvailableUntil, strings.TrimSpace(input.FallbackPlanKey), input.PriceMinor, input.Currency)
		if err != nil {
			if strings.Contains(strings.ToLower(err.Error()), "duplicate") {
				return PlanDraft{}, ErrConflict
			}
			return PlanDraft{}, err
		}
	}
	if err != nil {
		if strings.Contains(strings.ToLower(err.Error()), "duplicate") {
			return PlanDraft{}, ErrConflict
		}
		return PlanDraft{}, err
	}
	item, err := scanPlanDraft(tx.QueryRow(ctx, `SELECT id,$2::bigint,plan_key,display_name,description,billing_period,max_remote_pcs,max_live_sessions,feature_flags,channel_products,sort_order,visible_platforms,available_from,available_until,COALESCE(fallback_plan_key,''),price_minor,currency FROM plans WHERE id=$1`, itemID, version))
	if err != nil {
		return PlanDraft{}, err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,NULLIF($2,'00000000-0000-0000-0000-000000000000'::uuid),'billing.plan.create','plan',$3,'SUCCEEDED',$4,jsonb_build_object('catalog_version',$5::bigint))`, adminID, actorDeviceID, itemID, traceID, version); err != nil {
		return PlanDraft{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return PlanDraft{}, err
	}
	return item, nil
}

func (s *Postgres) UpdatePlanDraft(ctx context.Context, adminID, actorDeviceID, planID uuid.UUID, input PlanDraftInput, traceID string) (PlanDraft, error) {
	input = normalizePlanDraftInput(input)
	if planID == uuid.Nil {
		return PlanDraft{}, ErrNotFound
	}
	if err := validatePlanDraftInput(input); err != nil {
		return PlanDraft{}, err
	}
	tx, err := s.adminTx(ctx, adminID)
	if err != nil {
		return PlanDraft{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	command, err := tx.Exec(ctx, `
		UPDATE plans p SET plan_key=$2,display_name=$3,description=$4,billing_period=$5,max_remote_pcs=$6,max_live_sessions=$7,
		feature_flags=$8::jsonb,channel_products=$9::jsonb,sort_order=$10,visible_platforms=$11,available_from=$12,available_until=$13,fallback_plan_key=NULLIF($14,''),price_minor=$15,currency=$16
		FROM catalog_versions c WHERE p.catalog_version_id=c.id AND c.status='DRAFT' AND p.id=$1`,
		planID, strings.TrimSpace(input.PlanKey), strings.TrimSpace(input.DisplayName), strings.TrimSpace(input.Description), strings.ToUpper(strings.TrimSpace(input.BillingPeriod)),
		input.MaxRemotePCs, input.MaxLiveSessions, input.FeatureFlags, input.ChannelProducts, input.SortOrder, input.VisiblePlatforms, input.AvailableFrom, input.AvailableUntil, strings.TrimSpace(input.FallbackPlanKey), input.PriceMinor, input.Currency)
	if err != nil {
		if strings.Contains(strings.ToLower(err.Error()), "duplicate") {
			return PlanDraft{}, ErrConflict
		}
		return PlanDraft{}, err
	}
	if command.RowsAffected() != 1 {
		return PlanDraft{}, ErrNotFound
	}
	item, err := scanPlanDraft(tx.QueryRow(ctx, `SELECT p.id,c.version,p.plan_key,p.display_name,p.description,p.billing_period,p.max_remote_pcs,p.max_live_sessions,p.feature_flags,p.channel_products,p.sort_order,p.visible_platforms,p.available_from,p.available_until,COALESCE(p.fallback_plan_key,''),p.price_minor,p.currency FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE p.id=$1`, planID))
	if err != nil {
		return PlanDraft{}, err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id) VALUES($1,NULLIF($2,'00000000-0000-0000-0000-000000000000'::uuid),'billing.plan.update','plan',$3,'SUCCEEDED',$4)`, adminID, actorDeviceID, planID, traceID); err != nil {
		return PlanDraft{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return PlanDraft{}, err
	}
	return item, nil
}

// PublishCatalog atomically activates the current draft. Accounts on the
// default free plan are moved to the new free definition; paid subscriptions
// retain their historical catalog version.
func (s *Postgres) PublishCatalog(ctx context.Context, adminID, actorDeviceID uuid.UUID, traceID string) (int64, error) {
	tx, err := s.adminTx(ctx, adminID)
	if err != nil {
		return 0, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var draftID uuid.UUID
	var version int64
	if err := tx.QueryRow(ctx, `SELECT id,version FROM catalog_versions WHERE status='DRAFT' ORDER BY version DESC LIMIT 1 FOR UPDATE`).Scan(&draftID, &version); errors.Is(err, pgx.ErrNoRows) {
		return 0, ErrNotFound
	} else if err != nil {
		return 0, err
	}
	var freePlanID uuid.UUID
	if err := tx.QueryRow(ctx, `SELECT id FROM plans WHERE catalog_version_id=$1 AND plan_key='free'`, draftID).Scan(&freePlanID); errors.Is(err, pgx.ErrNoRows) {
		return 0, ErrConflict
	} else if err != nil {
		return 0, err
	}
	if _, err := tx.Exec(ctx, `UPDATE catalog_versions SET status='RETIRED' WHERE status='PUBLISHED'`); err != nil {
		return 0, err
	}
	if _, err := tx.Exec(ctx, `UPDATE catalog_versions SET status='PUBLISHED',published_at=now() WHERE id=$1`, draftID); err != nil {
		return 0, err
	}
	// DEFAULT grants are the durable fallback used by the commerce aggregator.
	// Move them with the published free plan so a later paid/promo expiration
	// cannot resurrect a retired catalog definition.
	if _, err := tx.Exec(ctx, `UPDATE entitlement_grants
		SET plan_key='free',catalog_version=$1,ends_at=NULL,grace_until=NULL,status='ACTIVE',updated_at=now()
		WHERE source_type='DEFAULT'`, version); err != nil {
		return 0, err
	}
	// Only default/free accounts are migrated. Existing paid subscriptions are
	// deliberately left on their purchase-time catalog definition.
	if _, err := tx.Exec(ctx, `
		WITH free_plan AS (
			SELECT p.plan_key,c.version,p.max_remote_pcs,p.max_live_sessions,p.feature_flags
			FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE p.id=$1
		), changed AS (
			UPDATE entitlements e SET plan_key=f.plan_key,catalog_version=f.version,max_remote_pcs=f.max_remote_pcs,max_live_sessions=f.max_live_sessions,
				feature_flags=f.feature_flags,source='DEFAULT',provider_reference_hash=NULL,grace_until=NULL,version=e.version+1,effective_at=now(),expires_at=NULL,updated_at=now()
			FROM free_plan f
			WHERE e.plan_key='free' AND NOT EXISTS (SELECT 1 FROM subscriptions s WHERE s.account_id=e.account_id AND s.status IN ('ACTIVE','GRACE'))
			RETURNING e.account_id,e.version,e.plan_key,e.catalog_version
		)
		INSERT INTO outbox(event_type,aggregate_id,payload)
		SELECT 'entitlement.changed',account_id,jsonb_build_object('account_id',account_id::text,'plan_key',plan_key,'catalog_version',catalog_version,'version',version)
		FROM changed`, freePlanID); err != nil {
		return 0, err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,NULLIF($2,'00000000-0000-0000-0000-000000000000'::uuid),'billing.catalog.publish','catalog',$3,'SUCCEEDED',$4,jsonb_build_object('version',$5::bigint))`, adminID, actorDeviceID, draftID, traceID, version); err != nil {
		return 0, err
	}
	if err := tx.Commit(ctx); err != nil {
		return 0, err
	}
	return version, nil
}

func (s *Postgres) PublishedPlans(ctx context.Context) (int64, []Plan, error) {
	var version int64
	if err := s.Pool.QueryRow(ctx, `SELECT version FROM catalog_versions WHERE status='PUBLISHED'`).Scan(&version); err != nil {
		return 0, nil, err
	}
	rows, err := s.Pool.Query(ctx, `SELECT p.plan_key,p.display_name,p.description,p.billing_period,p.max_remote_pcs,p.max_live_sessions,p.feature_flags,p.channel_products,p.sort_order,p.price_minor,p.currency FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.status='PUBLISHED' ORDER BY p.sort_order,p.plan_key`)
	if err != nil {
		return 0, nil, err
	}
	defer rows.Close()
	result := make([]Plan, 0)
	for rows.Next() {
		var i Plan
		if err := rows.Scan(&i.Key, &i.DisplayName, &i.Description, &i.BillingPeriod, &i.MaxRemotePCs, &i.MaxLiveSessions, &i.FeatureFlags, &i.ChannelProducts, &i.SortOrder, &i.PriceMinor, &i.Currency); err != nil {
			return 0, nil, err
		}
		i.MaxRemotePCs = UnlimitedQuota
		i.MaxLiveSessions = UnlimitedQuota
		result = append(result, i)
	}
	return version, result, rows.Err()
}

type Entitlement struct {
	PlanKey        string `json:"plan_key"`
	CatalogVersion int64  `json:"catalog_version"`
	// Quantity fields are always -1 on API responses; they are not enforcement
	// inputs anymore.
	MaxRemotePCs    int             `json:"max_remote_pcs"`
	MaxLiveSessions int             `json:"max_live_sessions"`
	FeatureFlags    json.RawMessage `json:"feature_flags"`
	Version         int64           `json:"version"`
	EffectiveAt     time.Time       `json:"effective_at"`
	ExpiresAt       *time.Time      `json:"expires_at,omitempty"`
	GraceUntil      *time.Time      `json:"grace_until,omitempty"`
	Source          string          `json:"source"`
	ProviderRefHash []byte          `json:"provider_reference_hash,omitempty"`
}

type AuditRecord struct {
	ID            int64           `json:"id"`
	ActorDeviceID *uuid.UUID      `json:"actor_device_id,omitempty"`
	Action        string          `json:"action"`
	TargetType    string          `json:"target_type"`
	TargetID      *uuid.UUID      `json:"target_id,omitempty"`
	Result        string          `json:"result"`
	TraceID       string          `json:"trace_id"`
	Metadata      json.RawMessage `json:"metadata"`
	CreatedAt     time.Time       `json:"created_at"`
}

type IdempotencyResult struct {
	Claimed     bool
	RequestHash [32]byte
	Status      *int
	Body        json.RawMessage
}

func (s *Postgres) ClaimIdempotency(ctx context.Context, userID uuid.UUID, key string, requestHash [32]byte) (IdempotencyResult, error) {
	command, err := s.Pool.Exec(ctx, `
		INSERT INTO idempotency_keys(user_id,idempotency_key,request_hash,expires_at)
		VALUES($1,$2,$3,now()+interval '24 hours')
		ON CONFLICT(user_id,idempotency_key) DO UPDATE
		SET request_hash=excluded.request_hash,response_status=NULL,response_body=NULL,
		    expires_at=excluded.expires_at,created_at=now()
		WHERE idempotency_keys.expires_at<now()
		   OR (idempotency_keys.response_status>=500
		       AND idempotency_keys.request_hash=excluded.request_hash)`, userID, key, requestHash[:])
	if err != nil {
		return IdempotencyResult{}, err
	}
	var stored []byte
	var status *int
	var body json.RawMessage
	if err := s.Pool.QueryRow(ctx, `SELECT request_hash,response_status,response_body FROM idempotency_keys WHERE user_id=$1 AND idempotency_key=$2`, userID, key).Scan(&stored, &status, &body); err != nil {
		return IdempotencyResult{}, err
	}
	var hash [32]byte
	copy(hash[:], stored)
	return IdempotencyResult{Claimed: command.RowsAffected() == 1, RequestHash: hash, Status: status, Body: body}, nil
}

func (s *Postgres) CompleteIdempotency(ctx context.Context, userID uuid.UUID, key string, requestHash [32]byte, status int, body json.RawMessage) error {
	command, err := s.Pool.Exec(ctx, `UPDATE idempotency_keys SET response_status=$4,response_body=$5 WHERE user_id=$1 AND idempotency_key=$2 AND request_hash=$3`, userID, key, requestHash[:], status, body)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrConflict
	}
	return nil
}

func (s *Postgres) ListAudit(ctx context.Context, userID uuid.UUID, before int64, limit int) ([]AuditRecord, error) {
	if limit < 1 || limit > 100 {
		limit = 50
	}
	if before <= 0 {
		before = 1<<63 - 1
	}
	rows, err := s.Pool.Query(ctx, `SELECT id,actor_device_id,action,target_type,target_id,result,trace_id,metadata,created_at FROM audit_events WHERE user_id=$1 AND id<$2 ORDER BY id DESC LIMIT $3`, userID, before, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]AuditRecord, 0)
	for rows.Next() {
		var item AuditRecord
		if err := rows.Scan(&item.ID, &item.ActorDeviceID, &item.Action, &item.TargetType, &item.TargetID, &item.Result, &item.TraceID, &item.Metadata, &item.CreatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) SaveLease(ctx context.Context, userID, actorID, leaseID, sessionID, holderID uuid.UUID, expiresAt time.Time, version int64, auditAction, traceID string) error {
	if version < 1 {
		return ErrDenied
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	command, err := tx.Exec(ctx, `
		INSERT INTO control_leases(session_id,holder_device_id,lease_id,expires_at,version)
		SELECT s.id,holder.id,$3,$4,$5
		FROM sessions s
		JOIN pcs p ON p.id=s.pc_id AND p.owner_user_id=$6 AND p.remote_routing_enabled
		JOIN users account ON account.id=p.owner_user_id AND account.status='ACTIVE'
		JOIN devices actor ON actor.id=$2 AND actor.user_id=$6 AND actor.device_type='ANDROID' AND actor.trust_state='TRUSTED'
		JOIN devices holder ON holder.id=$7 AND holder.user_id=$6 AND holder.device_type='ANDROID'
		LEFT JOIN device_grants grant_authority ON grant_authority.device_id=actor.id AND grant_authority.pc_id=p.id
		  AND grant_authority.permission IN ('CONTROL','MANAGE')
		  AND (grant_authority.expires_at IS NULL OR grant_authority.expires_at>now())
		WHERE s.id=$1 AND s.deleted_at IS NULL AND ($8 OR grant_authority.device_id IS NOT NULL)
		ON CONFLICT(session_id) DO UPDATE
		SET holder_device_id=excluded.holder_device_id,lease_id=excluded.lease_id,
			expires_at=CASE
				WHEN excluded.version=control_leases.version
				THEN GREATEST(control_leases.expires_at,excluded.expires_at)
				ELSE excluded.expires_at
			END,
			version=excluded.version,updated_at=now()
		WHERE excluded.version>control_leases.version
		   OR control_leases.expires_at<=now()
		   OR (excluded.version=control_leases.version
		       AND excluded.lease_id=control_leases.lease_id
		       AND excluded.holder_device_id=control_leases.holder_device_id)`,
		sessionID, actorID, leaseID, expiresAt, version, userID, holderID, s.AutoTrustDevices)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrConflict
	}
	if auditAction != "" {
		if _, err := tx.Exec(ctx, `
			INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
			VALUES($1,$2,$3,'session',$4,'SUCCEEDED',$5,jsonb_build_object('lease_id',$6::uuid,'version',$7::bigint))`,
			userID, actorID, auditAction, sessionID, traceID, leaseID, version); err != nil {
			return err
		}
	}
	return tx.Commit(ctx)
}

func (s *Postgres) DeleteLease(ctx context.Context, userID, actorID, sessionID, leaseID uuid.UUID, auditAction, traceID string) error {
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	command, err := tx.Exec(ctx, `
		DELETE FROM control_leases lease
		USING sessions s,pcs p,devices actor,users account
		WHERE lease.session_id=$1 AND lease.lease_id=$2
		  AND s.id=lease.session_id AND p.id=s.pc_id AND p.owner_user_id=$3
		  AND actor.id=$4 AND actor.user_id=$3 AND actor.trust_state='TRUSTED'
		  AND account.id=$3 AND account.status='ACTIVE'
		  AND (
			p.device_id=actor.id OR ($5 AND actor.device_type='ANDROID') OR EXISTS (
				SELECT 1 FROM device_grants controller
				WHERE controller.device_id=actor.id AND controller.pc_id=p.id
				  AND controller.permission IN ('CONTROL','MANAGE')
				  AND (controller.expires_at IS NULL OR controller.expires_at>now())
			)
		  )`, sessionID, leaseID, userID, actorID, s.AutoTrustDevices)
	if err != nil {
		return err
	}
	if command.RowsAffected() == 0 {
		return tx.Commit(ctx)
	}
	if auditAction != "" {
		if _, err := tx.Exec(ctx, `
			INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
			VALUES($1,$2,$3,'session',$4,'SUCCEEDED',$5,jsonb_build_object('lease_id',$6::uuid))`,
			userID, actorID, auditAction, sessionID, traceID, leaseID); err != nil {
			return err
		}
	}
	return tx.Commit(ctx)
}

// ForceDeleteLease performs the durable half of a MANAGE force release. The
// lease identity and version returned by Valkey are a fencing token: a
// replacement acquired after the cache deletion must never be removed by the
// older force-release request.
func (s *Postgres) ForceDeleteLease(ctx context.Context, userID, actorID, sessionID, leaseID uuid.UUID, version int64, auditAction, traceID string) error {
	if version < 1 {
		return ErrDenied
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var deletedLeaseID uuid.UUID
	err = tx.QueryRow(ctx, `
		DELETE FROM control_leases lease
		USING sessions s,pcs p,devices actor,users account
		WHERE lease.session_id=$1 AND lease.lease_id=$4 AND lease.version=$5
		  AND s.id=lease.session_id AND p.id=s.pc_id AND p.owner_user_id=$2
		  AND actor.id=$3 AND actor.user_id=$2 AND actor.trust_state='TRUSTED'
		  AND account.id=$2 AND account.status='ACTIVE'
		  AND (
			$6 OR p.device_id=actor.id OR EXISTS (
				SELECT 1 FROM device_grants manager
				WHERE manager.device_id=actor.id AND manager.pc_id=p.id
				  AND manager.permission='MANAGE'
				  AND (manager.expires_at IS NULL OR manager.expires_at>now())
			)
		  )
		RETURNING lease.lease_id`, sessionID, userID, actorID, leaseID, version, s.AutoTrustDevices).Scan(&deletedLeaseID)
	if errors.Is(err, pgx.ErrNoRows) {
		return tx.Commit(ctx)
	}
	if err != nil {
		return err
	}
	if auditAction != "" {
		if _, err := tx.Exec(ctx, `
			INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
			VALUES($1,$2,$3,'session',$4,'SUCCEEDED',$5,jsonb_build_object('lease_id',$6::uuid))`,
			userID, actorID, auditAction, sessionID, traceID, deletedLeaseID); err != nil {
			return err
		}
	}
	return tx.Commit(ctx)
}

func (s *Postgres) GetEntitlement(ctx context.Context, userID uuid.UUID) (Entitlement, error) {
	var e Entitlement
	err := s.Pool.QueryRow(ctx, `SELECT plan_key,catalog_version,max_remote_pcs,max_live_sessions,feature_flags,version,effective_at,expires_at,grace_until,source,provider_reference_hash FROM entitlements WHERE account_id=$1`, userID).Scan(&e.PlanKey, &e.CatalogVersion, &e.MaxRemotePCs, &e.MaxLiveSessions, &e.FeatureFlags, &e.Version, &e.EffectiveAt, &e.ExpiresAt, &e.GraceUntil, &e.Source, &e.ProviderRefHash)
	if errors.Is(err, pgx.ErrNoRows) {
		return Entitlement{}, ErrNotFound
	}
	if err != nil {
		return Entitlement{}, err
	}
	e.MaxRemotePCs = UnlimitedQuota
	e.MaxLiveSessions = UnlimitedQuota
	return e, nil
}

// ApplyVerifiedPurchase persists a provider-authoritative result. The raw
// provider reference is hashed immediately and only the digest is written to
// PostgreSQL. Replaying the same verified reference for the same account is a
// no-op; attempting to claim another account's reference is rejected.
func (s *Postgres) ApplyVerifiedPurchase(ctx context.Context, userID, actorDeviceID uuid.UUID, purchase billing.VerifiedPurchase, traceID string) (Entitlement, error) {
	provider := strings.ToUpper(strings.TrimSpace(purchase.Provider))
	if provider != billing.ProviderGooglePlay && provider != billing.ProviderWeb || strings.TrimSpace(purchase.ProductID) == "" || strings.TrimSpace(purchase.Reference) == "" || len(purchase.Reference) > 4096 {
		return Entitlement{}, ErrDenied
	}
	switch purchase.Status {
	case "ACTIVE", "GRACE", "CANCELED", "EXPIRED", "REVOKED":
	default:
		return Entitlement{}, ErrDenied
	}
	refHash := sha256.Sum256([]byte(purchase.Reference))
	now := time.Now().UTC()
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return Entitlement{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var planKey string
	var catalogVersion int64
	var maxRemotePCs, maxLiveSessions int
	var featureFlags json.RawMessage
	err = tx.QueryRow(ctx, `
		SELECT p.plan_key,c.version,p.max_remote_pcs,p.max_live_sessions,p.feature_flags
		FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id
		WHERE c.status='PUBLISHED' AND (p.channel_products ->> $1::text = $2::text OR p.channel_products ->> lower($1::text) = $2::text OR p.channel_products ->> replace(lower($1::text),'_','') = $2::text)
		ORDER BY p.sort_order,p.plan_key LIMIT 1`, provider, purchase.ProductID).Scan(&planKey, &catalogVersion, &maxRemotePCs, &maxLiveSessions, &featureFlags)
	if errors.Is(err, pgx.ErrNoRows) {
		return Entitlement{}, ErrNotFound
	}
	if err != nil {
		return Entitlement{}, fmt.Errorf("billing product lookup: %w", err)
	}
	var subscriptionID uuid.UUID
	var existingAccount uuid.UUID
	var existingPlan string
	var existingVersion int64
	var existingProduct string
	err = tx.QueryRow(ctx, `SELECT id,account_id,plan_key,catalog_version,product_id FROM subscriptions WHERE provider=$1 AND provider_ref_hash=$2 FOR UPDATE`, provider, refHash[:]).Scan(&subscriptionID, &existingAccount, &existingPlan, &existingVersion, &existingProduct)
	hasSubscription := err == nil
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return Entitlement{}, fmt.Errorf("subscription lookup: %w", err)
	}
	if hasSubscription {
		if existingAccount != userID || existingProduct != purchase.ProductID {
			return Entitlement{}, ErrConflict
		}
		// Existing subscriptions retain their purchase-time plan/version even
		// after a later catalog publish.
		planKey, catalogVersion = existingPlan, existingVersion
		if err := tx.QueryRow(ctx, `SELECT p.max_remote_pcs,p.max_live_sessions,p.feature_flags FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.version=$1 AND p.plan_key=$2`, catalogVersion, planKey).Scan(&maxRemotePCs, &maxLiveSessions, &featureFlags); err != nil {
			return Entitlement{}, fmt.Errorf("historical plan lookup: %w", err)
		}
	} else {
		subscriptionID = uuid.New()
		if _, err := tx.Exec(ctx, `INSERT INTO subscriptions(id,account_id,provider,product_id,provider_ref_hash,plan_key,catalog_version,status,renews_at,grace_until) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)`, subscriptionID, userID, provider, purchase.ProductID, refHash[:], planKey, catalogVersion, purchase.Status, purchase.RenewsAt, purchase.GraceUntil); err != nil {
			if strings.Contains(strings.ToLower(err.Error()), "duplicate") {
				return Entitlement{}, ErrConflict
			}
			return Entitlement{}, fmt.Errorf("subscription insert: %w", err)
		}
	}
	if hasSubscription {
		if _, err := tx.Exec(ctx, `UPDATE subscriptions SET status=$3,renews_at=$4,grace_until=$5,updated_at=now() WHERE provider=$1 AND provider_ref_hash=$2`, provider, refHash[:], purchase.Status, purchase.RenewsAt, purchase.GraceUntil); err != nil {
			return Entitlement{}, fmt.Errorf("subscription update: %w", err)
		}
	}
	active := purchase.Status == "ACTIVE" ||
		(purchase.Status == "GRACE" && purchase.GraceUntil != nil && purchase.GraceUntil.After(now)) ||
		(purchase.Status == "CANCELED" && purchase.RenewsAt != nil && purchase.RenewsAt.After(now))
	grantStatus := "ACTIVE"
	var grantEnd, grantGrace *time.Time
	if active {
		switch purchase.Status {
		case "GRACE":
			grantEnd, grantGrace = purchase.GraceUntil, purchase.GraceUntil
		case "CANCELED":
			grantEnd = purchase.RenewsAt
		}
	} else if purchase.Status == "REVOKED" {
		grantStatus = "REVOKED"
	} else {
		grantStatus = "EXPIRED"
	}
	if _, err := tx.Exec(ctx, `
		INSERT INTO entitlement_grants(
			id,account_id,source_type,source_id,plan_key,catalog_version,starts_at,
			ends_at,grace_until,provider_reference_hash,status,metadata
		) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,
			jsonb_build_object('product_id',$12::text))
		ON CONFLICT(source_type,source_id) WHERE source_id IS NOT NULL DO UPDATE SET
			plan_key=excluded.plan_key,catalog_version=excluded.catalog_version,
			ends_at=excluded.ends_at,grace_until=excluded.grace_until,
			provider_reference_hash=excluded.provider_reference_hash,
			status=excluded.status,metadata=excluded.metadata,updated_at=now()`,
		uuid.New(), userID, provider, subscriptionID, planKey, catalogVersion, now,
		grantEnd, grantGrace, refHash[:], grantStatus, purchase.ProductID); err != nil {
		return Entitlement{}, fmt.Errorf("subscription grant upsert: %w", err)
	}
	current, err := recomputeEntitlement(ctx, tx, userID)
	if err != nil {
		return Entitlement{}, fmt.Errorf("subscription entitlement recompute: %w", err)
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,$2,'billing.purchase.verify','subscription',$1,'SUCCEEDED',$3,jsonb_build_object('provider',$4::text,'product_id',$5::text,'status',$6::text,'reference_hash',$7::text))`, userID, actorDeviceID, traceID, provider, purchase.ProductID, purchase.Status, fmt.Sprintf("%x", refHash[:])); err != nil {
		return Entitlement{}, fmt.Errorf("entitlement audit: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return Entitlement{}, err
	}
	return current, nil
}

func bytesEqual(a, b []byte) bool {
	return string(a) == string(b)
}

func sameTime(a, b *time.Time) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return a.Equal(*b)
}

func (s *Postgres) Audit(ctx context.Context, userID, actorID uuid.UUID, action, targetType string, targetID uuid.UUID, result, traceID, metadata string) error {
	var actorDeviceID any
	if actorID != uuid.Nil {
		actorDeviceID = actorID
	}
	var optionalTargetID any
	if targetID != uuid.Nil {
		optionalTargetID = targetID
	}
	_, err := s.Pool.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,$2,$3,$4,$5,$6,$7,$8::jsonb)`, userID, actorDeviceID, action, targetType, optionalTargetID, result, traceID, metadata)
	return err
}

var _ auth.DeviceLoader = (*Postgres)(nil)
