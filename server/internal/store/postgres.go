// Package store implements the PostgreSQL authority for TermLink metadata.
// Terminal plaintext is deliberately absent from every model and query.
package store

import (
	"bytes"
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
	"github.com/termlink/termlink/server/migrations"
)

var (
	ErrNotFound                 = errors.New("record not found")
	ErrConflict                 = errors.New("record conflict")
	ErrDenied                   = errors.New("operation denied")
	ErrSessionActive            = errors.New("session is active")
	ErrTerminalHistoryRetired   = errors.New("server terminal history is retired")
	ErrInvalidHistoryPage       = errors.New("invalid history page")
	ErrHistoryAnchorAhead       = errors.New("history anchor is ahead of the session")
	ErrInvalidHistorySnapshot   = errors.New("invalid history snapshot")
	ErrInvalidHistoryTranscript = errors.New("invalid history transcript")
	ErrInvalidHistoryRecipient  = errors.New("invalid history recipient")
)

const (
	HistoryFormatBoundedEventReplay              = "BOUNDED_EVENT_REPLAY"
	HistoryFormatTerminalSnapshot                = "TERMLINK_TERMINAL_SNAPSHOT"
	HistoryFormatTerminalCoreSnapshot            = "TERMLINK_TERMINAL_CORE_SNAPSHOT"
	HistoryFormatTerminalTranscript              = "TERMLINK_TERMINAL_TRANSCRIPT"
	MaxSnapshotCiphertextBytes             int64 = 2 << 20
	MaxTerminalCoreSnapshotCiphertextBytes int64 = 64 << 20
	MaxTranscriptCiphertextBytes           int64 = 512 << 10
	MaxTranscriptPublicationPages                = 100_000
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
	var (
		userID    uuid.UUID
		record    DeviceRecord
		bootstrap bool
		err       error
	)
	for attempt := 0; attempt < 5; attempt++ {
		userID, record, bootstrap, err = s.registerDeviceOnce(ctx, subjectHash, input, traceID)
		if !isPostgresRetryableTransactionError(err) {
			return userID, record, bootstrap, err
		}
		if ctx.Err() != nil {
			return uuid.Nil, DeviceRecord{}, false, ctx.Err()
		}
	}
	return uuid.Nil, DeviceRecord{}, false, err
}

// registerDeviceOnce contains one serializable registration attempt. Android
// startup can legitimately issue several independent API calls at once; each
// first verifies the same idempotent device registration. PostgreSQL may abort
// one waiter with 40001 after the account row lock is released, so the public
// method retries the complete transaction instead of surfacing a spurious 500.
func (s *Postgres) registerDeviceOnce(ctx context.Context, subjectHash [32]byte, input RegisterDeviceInput, traceID string) (uuid.UUID, DeviceRecord, bool, error) {
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
	// MachineIDHash is a client-derived, account-scoped correlation value. It
	// locates a PC across key rotation but never authorizes a request.
	MachineIDHash []byte `json:"-"`
}

type PCMachineResolution struct {
	ID       uuid.UUID
	Name     string
	KeyEpoch int64
}

type PCUpsertResult struct {
	ID               uuid.UUID
	KeyEpoch         int64
	ReplacedDeviceID *uuid.UUID
}

type TerminalCommandCapability struct {
	ID          string `json:"id"`
	DisplayName string `json:"display_name"`
	ShellType   string `json:"shell_type"`
	Source      string `json:"source"`
}

func (s *Postgres) UpsertPC(ctx context.Context, userID, deviceID uuid.UUID, item PCRecord) error {
	_, err := s.UpsertPCResolved(ctx, userID, deviceID, item, "")
	return err
}

func (s *Postgres) ResolvePCMachine(
	ctx context.Context,
	subjectHash [32]byte,
	machineIDHash []byte,
) (PCMachineResolution, bool, error) {
	if len(machineIDHash) != sha256.Size {
		return PCMachineResolution{}, false, ErrDenied
	}
	var result PCMachineResolution
	err := s.Pool.QueryRow(ctx, `
		SELECT p.id,p.name,p.key_epoch
		FROM pcs p
		JOIN users u ON u.id=p.owner_user_id
		WHERE u.oidc_subject_hash=$1 AND u.status='ACTIVE' AND p.machine_id_hash=$2`,
		subjectHash[:], machineIDHash,
	).Scan(&result.ID, &result.Name, &result.KeyEpoch)
	if errors.Is(err, pgx.ErrNoRows) {
		return PCMachineResolution{}, false, nil
	}
	return result, err == nil, err
}

func (s *Postgres) UpsertPCResolved(
	ctx context.Context,
	userID, deviceID uuid.UUID,
	item PCRecord,
	traceID string,
) (PCUpsertResult, error) {
	var (
		result PCUpsertResult
		err    error
	)
	for attempt := 0; attempt < 5; attempt++ {
		result, err = s.upsertPCResolvedOnce(ctx, userID, deviceID, item, traceID)
		if isStablePCMachineIdentityConflict(err) {
			return PCUpsertResult{}, ErrConflict
		}
		if !isPostgresRetryableTransactionError(err) {
			return result, err
		}
		if ctx.Err() != nil {
			return PCUpsertResult{}, ctx.Err()
		}
	}
	return PCUpsertResult{}, err
}

func (s *Postgres) upsertPCResolvedOnce(
	ctx context.Context,
	userID, deviceID uuid.UUID,
	item PCRecord,
	traceID string,
) (PCUpsertResult, error) {
	applyLegacyPCDefaults(&item)
	if len(item.MachineIDHash) != 0 && len(item.MachineIDHash) != sha256.Size {
		return PCUpsertResult{}, ErrDenied
	}
	if item.TerminalCommands == nil {
		item.TerminalCommands = []TerminalCommandCapability{}
	}
	terminalCommands, err := json.Marshal(item.TerminalCommands)
	if err != nil {
		return PCUpsertResult{}, fmt.Errorf("encode terminal command capabilities: %w", err)
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return PCUpsertResult{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	// PC routes are account-owned resources. One account may register any
	// number of trusted Windows PCs.
	var trustedDevice int
	if err := tx.QueryRow(ctx, `
		SELECT 1
		FROM devices d
		WHERE d.id=$1 AND d.user_id=$2 AND d.device_type IN ('WINDOWS_PC','PC_AGENT') AND d.trust_state='TRUSTED'
		FOR UPDATE`, deviceID, userID).Scan(&trustedDevice); errors.Is(err, pgx.ErrNoRows) {
		return PCUpsertResult{}, ErrDenied
	} else if err != nil {
		return PCUpsertResult{}, err
	}
	if len(item.MachineIDHash) == sha256.Size {
		var machinePCID uuid.UUID
		err = tx.QueryRow(ctx, `
			SELECT id FROM pcs
			WHERE owner_user_id=$1 AND machine_id_hash=$2
			FOR UPDATE`, userID, item.MachineIDHash).Scan(&machinePCID)
		if err == nil && machinePCID != item.ID {
			return PCUpsertResult{}, ErrConflict
		}
		if err != nil && !errors.Is(err, pgx.ErrNoRows) {
			return PCUpsertResult{}, err
		}
	}
	var existingOwner, existingDevice uuid.UUID
	var existingMachineID []byte
	var existingKeyEpoch int64
	err = tx.QueryRow(ctx, `
		SELECT owner_user_id,device_id,machine_id_hash,key_epoch
		FROM pcs WHERE id=$1 FOR UPDATE`, item.ID,
	).Scan(&existingOwner, &existingDevice, &existingMachineID, &existingKeyEpoch)
	hasExisting := err == nil
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return PCUpsertResult{}, err
	}
	if hasExisting && existingOwner != userID {
		return PCUpsertResult{}, ErrDenied
	}
	// The machine correlation value is required only when a different device
	// key is taking over the existing PC row. The currently bound device is
	// already authenticated by its signing key, so an older Agent may continue
	// heartbeating without clearing or knowing the stored machine identity.
	if hasExisting && existingDevice != deviceID &&
		(len(existingMachineID) != sha256.Size || len(item.MachineIDHash) != sha256.Size ||
			!bytes.Equal(existingMachineID, item.MachineIDHash)) {
		return PCUpsertResult{}, ErrDenied
	}
	var replacedDeviceID *uuid.UUID
	if hasExisting && existingDevice != deviceID {
		// A PC can change security keys only after the account-authenticated
		// resolve step selected the row by its stable machine correlation value.
		// The resolved epoch is also a compare-and-swap token: if another new
		// Agent already completed this replacement, a stale contender must not
		// immediately revoke the winner.
		if len(existingMachineID) != sha256.Size || !bytes.Equal(existingMachineID, item.MachineIDHash) {
			return PCUpsertResult{}, ErrDenied
		}
		if item.KeyEpoch != existingKeyEpoch {
			return PCUpsertResult{}, ErrConflict
		}
		var previousApprovedBy *uuid.UUID
		if err := tx.QueryRow(ctx, `
			SELECT approved_by FROM devices
			WHERE id=$1 AND user_id=$2 AND device_type IN ('WINDOWS_PC','PC_AGENT')
			FOR UPDATE`, existingDevice, userID).Scan(&previousApprovedBy); err != nil {
			return PCUpsertResult{}, err
		}
		command, err := tx.Exec(ctx, `
			UPDATE devices SET trust_state='REVOKED',revoked_at=COALESCE(revoked_at,now())
			WHERE id=$1 AND user_id=$2 AND id<>$3`, existingDevice, userID, deviceID)
		if err != nil || command.RowsAffected() != 1 {
			if err != nil {
				return PCUpsertResult{}, err
			}
			return PCUpsertResult{}, ErrDenied
		}
		if previousApprovedBy == nil {
			if _, err := tx.Exec(ctx, `
				UPDATE devices SET approved_by=NULL,approved_at=COALESCE(approved_at,now())
				WHERE id=$1 AND user_id=$2 AND trust_state='TRUSTED'`, deviceID, userID); err != nil {
				return PCUpsertResult{}, err
			}
			if _, err := tx.Exec(ctx, `
				UPDATE devices SET approved_by=$3
				WHERE user_id=$2 AND approved_by=$1 AND id<>$3`, existingDevice, userID, deviceID); err != nil {
				return PCUpsertResult{}, err
			}
		}
		if _, err := tx.Exec(ctx, `
			INSERT INTO outbox(event_type,aggregate_id,payload)
			VALUES('device.revoked',$1,jsonb_build_object('user_id',$2::uuid,'device_id',$1::uuid))`,
			existingDevice, userID); err != nil {
			return PCUpsertResult{}, err
		}
		if _, err := tx.Exec(ctx, `
			INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
			VALUES($1,$2,'pc.machine_identity.rotate','pc',$3,'SUCCEEDED',NULLIF($4,''),
			       jsonb_build_object('previous_device_id',$5::uuid))`,
			userID, deviceID, item.ID, traceID, existingDevice); err != nil {
			return PCUpsertResult{}, err
		}
		replaced := existingDevice
		replacedDeviceID = &replaced
	}
	var resolvedKeyEpoch int64
	err = tx.QueryRow(ctx, `
		INSERT INTO pcs(
			id,owner_user_id,device_id,name,agent_version,key_epoch,last_seen_at,
			session_heartbeat_capable,session_heartbeat_enabled_at,supported_shells,
			remote_agent_update_capable,terminal_commands,platform,architecture,platform_version,capabilities,
			machine_id_hash)
		VALUES($1,$2,$3,$4,$5,$6,now(),$7,CASE WHEN $7 THEN now() END,
		       COALESCE($8::text[],ARRAY[]::text[]),$9,$10::jsonb,$11,$12,$13,COALESCE($14::text[],ARRAY[]::text[]),$15)
		ON CONFLICT(id) DO UPDATE SET device_id=excluded.device_id,name=excluded.name,agent_version=excluded.agent_version,
		key_epoch=CASE WHEN pcs.device_id<>excluded.device_id
			THEN pcs.key_epoch+1
			ELSE GREATEST(pcs.key_epoch,excluded.key_epoch) END,last_seen_at=now(),
		session_heartbeat_capable=pcs.session_heartbeat_capable OR excluded.session_heartbeat_capable,
		session_heartbeat_enabled_at=COALESCE(pcs.session_heartbeat_enabled_at,excluded.session_heartbeat_enabled_at),
		supported_shells=excluded.supported_shells,
		remote_agent_update_capable=excluded.remote_agent_update_capable,
		terminal_commands=excluded.terminal_commands,
		platform=excluded.platform,architecture=excluded.architecture,
		platform_version=excluded.platform_version,capabilities=excluded.capabilities,
		machine_id_hash=COALESCE(pcs.machine_id_hash,excluded.machine_id_hash)
		WHERE pcs.owner_user_id=$2 AND (pcs.device_id=$3 OR pcs.machine_id_hash=$15)
		RETURNING key_epoch`, item.ID, userID, deviceID, item.Name, item.AgentVersion, item.KeyEpoch, item.SessionHeartbeatCapable, item.SupportedShells, item.RemoteAgentUpdateCapable, terminalCommands, item.Platform, item.Architecture, item.PlatformVersion, item.Capabilities, nullableBytes(item.MachineIDHash)).Scan(&resolvedKeyEpoch)
	if errors.Is(err, pgx.ErrNoRows) {
		return PCUpsertResult{}, ErrDenied
	}
	if err != nil {
		return PCUpsertResult{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return PCUpsertResult{}, err
	}
	return PCUpsertResult{ID: item.ID, KeyEpoch: resolvedKeyEpoch, ReplacedDeviceID: replacedDeviceID}, nil
}

func isStablePCMachineIdentityConflict(err error) bool {
	var postgresError *pgconn.PgError
	return errors.As(err, &postgresError) && postgresError.Code == "23505" &&
		postgresError.ConstraintName == "pcs_owner_machine_identity_unique"
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
	SigningPublicKey  []byte     `json:"signing_public_key"`
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
		SELECT d.id,d.device_type,d.name,d.signing_public_key,d.exchange_public_key,d.key_id,
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
		if err := rows.Scan(&item.DeviceID, &item.DeviceType, &item.DeviceName, &item.SigningPublicKey, &item.ExchangePublicKey, &item.KeyID, &item.Permission, &item.ExpiresAt); err != nil {
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
	case kind == "SNAPSHOT" && formatName == HistoryFormatTerminalCoreSnapshot:
		prefix = "terminal-core-snapshot-v2"
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
		((prefix == "snapshot-v1" || prefix == "terminal-core-snapshot-v2" || prefix == "transcript-v1") && len(parts[2]) != 8) {
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

func isTerminalTranscriptAnchorFormat(formatName string, formatVersion int) bool {
	return formatName == HistoryFormatTerminalSnapshot && formatVersion == 1 ||
		formatName == HistoryFormatTerminalCoreSnapshot && formatVersion == 2
}

func ValidateHistorySnapshotMetadata(sequence, sizeBytes int64, rows, columns int, formatName string, formatVersion int) error {
	if sequence < 0 || sizeBytes < 1 || rows < 1 || rows > 256 || columns < 1 || columns > 512 {
		return ErrInvalidHistorySnapshot
	}
	switch formatName {
	case HistoryFormatBoundedEventReplay:
		if formatVersion != 1 || sizeBytes > MaxSnapshotCiphertextBytes {
			return ErrInvalidHistorySnapshot
		}
		// Legacy bounded replay may describe the initial state at sequence zero.
		return nil
	case HistoryFormatTerminalSnapshot:
		if sequence == 0 || formatVersion != 1 || sizeBytes > MaxSnapshotCiphertextBytes {
			return ErrInvalidHistorySnapshot
		}
		return nil
	case HistoryFormatTerminalCoreSnapshot:
		if sequence == 0 || formatVersion != 2 || sizeBytes > MaxTerminalCoreSnapshotCiphertextBytes {
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
		!isTerminalTranscriptAnchorFormat(*item.FormatName, *item.FormatVersion)) {
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
	return ErrTerminalHistoryRetired
}

func (s *Postgres) PendingHistoryUpload(ctx context.Context, userID, actorID, sessionID, uploadID uuid.UUID) (HistoryUploadRecord, error) {
	return HistoryUploadRecord{}, ErrTerminalHistoryRetired
}

func (s *Postgres) CommitHistoryUpload(ctx context.Context, userID, actorID, sessionID, uploadID uuid.UUID) (HistoryItem, error) {
	return HistoryItem{}, ErrTerminalHistoryRetired
}

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
	return nil, nil
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
	if _, err := tx.Exec(ctx, `DELETE FROM control_leases WHERE session_id=$1`, sessionID); err != nil {
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

// CleanupSessions is retained only to keep the background-runner contract
// source-compatible during rollout. Session lifetime and host-owned history
// are explicit user/host decisions; elapsed time and connectivity loss must
// never tombstone EXITED, RUNNING, or INTERRUPTED session metadata.
func (s *Postgres) CleanupSessions(ctx context.Context, endedBefore, staleBefore time.Time, batchSize int) (int, error) {
	return 0, nil
}

func (s *Postgres) ListHistory(ctx context.Context, userID, deviceID, sessionID uuid.UUID) ([]HistoryItem, error) {
	return nil, ErrTerminalHistoryRetired
}

func (s *Postgres) ListHistoryPage(ctx context.Context, userID, deviceID, sessionID uuid.UUID, request HistoryPageRequest) (HistoryPage, error) {
	return HistoryPage{}, ErrTerminalHistoryRetired
}

func (s *Postgres) ListTranscriptPage(
	ctx context.Context,
	userID, deviceID, sessionID uuid.UUID,
	request TranscriptPageRequest,
) (TranscriptPage, error) {
	return TranscriptPage{}, ErrTerminalHistoryRetired
}

func (s *Postgres) IsAdmin(ctx context.Context, userID uuid.UUID) (bool, error) {
	var admin bool
	err := s.Pool.QueryRow(ctx, `SELECT is_admin FROM users WHERE id=$1 AND status='ACTIVE'`, userID).Scan(&admin)
	if errors.Is(err, pgx.ErrNoRows) {
		return false, ErrDenied
	}
	return admin, err
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
