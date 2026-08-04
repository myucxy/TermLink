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
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/billing"
)

var (
	ErrNotFound    = errors.New("record not found")
	ErrConflict    = errors.New("record conflict")
	ErrDenied      = errors.New("operation denied")
	ErrEntitlement = errors.New("entitlement limit reached")
)

type Postgres struct{ Pool *pgxpool.Pool }

func Open(ctx context.Context, databaseURL string) (*Postgres, error) {
	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		return nil, fmt.Errorf("parse PostgreSQL URL: %w", err)
	}
	config.MaxConns = 20
	config.MinConns = 2
	config.MaxConnLifetime = 30 * time.Minute
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
	if version != 5 {
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

// RegisterDevice creates the user on first OIDC login. The first Windows PC
// is the only bootstrap exception to PENDING, because a trusted local device
// is required to approve every later device.
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
	var trustedCount int
	if err := tx.QueryRow(ctx, `SELECT count(*) FROM devices WHERE user_id=$1 AND trust_state='TRUSTED'`, userID).Scan(&trustedCount); err != nil {
		return uuid.Nil, DeviceRecord{}, false, err
	}
	bootstrap := input.Type == "WINDOWS_PC" && trustedCount == 0
	trustState := "PENDING"
	var approvedAt *time.Time
	if bootstrap {
		trustState = "TRUSTED"
		now := time.Now().UTC()
		approvedAt = &now
	}
	var record DeviceRecord
	err = tx.QueryRow(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_at)
		VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
		ON CONFLICT (id) DO NOTHING
		RETURNING id,device_type,name,key_id,signing_public_key,exchange_public_key,trust_state,approved_by,approved_at,revoked_at,created_at`,
		input.ID, userID, input.Type, input.Name, input.SigningPublicKey, input.ExchangePublicKey, input.KeyID, trustState, approvedAt,
	).Scan(&record.ID, &record.Type, &record.Name, &record.KeyID, &record.SigningPublicKey, &record.ExchangePublicKey, &record.TrustState, &record.ApprovedBy, &record.ApprovedAt, &record.RevokedAt, &record.CreatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return uuid.Nil, DeviceRecord{}, false, ErrConflict
	}
	if err != nil {
		return uuid.Nil, DeviceRecord{}, false, err
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

func (s *Postgres) RevokeDevice(ctx context.Context, userID, actorID, targetID uuid.UUID, traceID string) error {
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	command, err := tx.Exec(ctx, `
		WITH actor AS (SELECT 1 FROM devices WHERE id=$1 AND user_id=$2 AND trust_state='TRUSTED')
		UPDATE devices SET trust_state='REVOKED',revoked_at=now()
		WHERE id=$3 AND user_id=$2 AND trust_state<>'REVOKED' AND EXISTS(SELECT 1 FROM actor)`, actorID, userID, targetID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	if _, err := tx.Exec(ctx, `DELETE FROM device_grants WHERE device_id=$1`, targetID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `DELETE FROM control_leases WHERE holder_device_id=$1`, targetID); err != nil {
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
	ID                   uuid.UUID  `json:"id"`
	DeviceID             uuid.UUID  `json:"device_id"`
	Name                 string     `json:"name"`
	AgentVersion         string     `json:"agent_version"`
	KeyEpoch             int64      `json:"key_epoch"`
	RemoteRoutingEnabled bool       `json:"remote_routing_enabled"`
	LastSeenAt           *time.Time `json:"last_seen_at,omitempty"`
}

func (s *Postgres) UpsertPC(ctx context.Context, userID, deviceID uuid.UUID, item PCRecord) error {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var maxRemotePCs int
	if err := tx.QueryRow(ctx, `
		SELECT e.max_remote_pcs
		FROM devices d JOIN entitlements e ON e.account_id=d.user_id
		WHERE d.id=$1 AND d.user_id=$2 AND d.device_type='WINDOWS_PC' AND d.trust_state='TRUSTED'
		FOR UPDATE OF d`, deviceID, userID).Scan(&maxRemotePCs); errors.Is(err, pgx.ErrNoRows) {
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
	if !hasExisting {
		var count int
		if err := tx.QueryRow(ctx, `SELECT count(*) FROM pcs WHERE owner_user_id=$1 AND remote_routing_enabled`, userID).Scan(&count); err != nil {
			return err
		}
		if count >= maxRemotePCs {
			return ErrEntitlement
		}
	}
	_, err = tx.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch,last_seen_at)
		VALUES($1,$2,$3,$4,$5,$6,now())
		ON CONFLICT(id) DO UPDATE SET name=excluded.name,agent_version=excluded.agent_version,
		key_epoch=GREATEST(pcs.key_epoch,excluded.key_epoch),last_seen_at=now()
		WHERE pcs.owner_user_id=$2 AND pcs.device_id=$3`, item.ID, userID, deviceID, item.Name, item.AgentVersion, item.KeyEpoch)
	if err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) ListPCs(ctx context.Context, userID, deviceID uuid.UUID) ([]PCRecord, error) {
	rows, err := s.Pool.Query(ctx, `SELECT p.id,p.device_id,p.name,p.agent_version,p.key_epoch,p.remote_routing_enabled,p.last_seen_at FROM pcs p WHERE p.owner_user_id=$1 AND (p.device_id=$2 OR EXISTS(SELECT 1 FROM device_grants g WHERE g.pc_id=p.id AND g.device_id=$2 AND (g.expires_at IS NULL OR g.expires_at>now()))) ORDER BY p.name,p.id`, userID, deviceID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]PCRecord, 0)
	for rows.Next() {
		var item PCRecord
		if err := rows.Scan(&item.ID, &item.DeviceID, &item.Name, &item.AgentVersion, &item.KeyEpoch, &item.RemoteRoutingEnabled, &item.LastSeenAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

type SessionRecord struct {
	ID                 uuid.UUID  `json:"id"`
	PCID               uuid.UUID  `json:"pc_id"`
	ShellType          string     `json:"shell_type"`
	TitleCipher        []byte     `json:"title_cipher,omitempty"`
	State              string     `json:"state"`
	Columns            int        `json:"columns"`
	Rows               int        `json:"rows"`
	LastOutputSequence uint64     `json:"last_output_seq"`
	CreatedAt          time.Time  `json:"created_at"`
	EndedAt            *time.Time `json:"ended_at,omitempty"`
}

func (s *Postgres) UpsertSession(ctx context.Context, userID, deviceID uuid.UUID, item SessionRecord) error {
	command, err := s.Pool.Exec(ctx, `INSERT INTO sessions(id,pc_id,shell_type,title_cipher,state,columns,rows,last_output_seq,ended_at) SELECT $1,$2,$3,$4,$5,$6,$7,$8,$9 FROM pcs WHERE id=$2 AND owner_user_id=$10 AND device_id=$11 ON CONFLICT(id) DO UPDATE SET title_cipher=excluded.title_cipher,state=excluded.state,columns=excluded.columns,rows=excluded.rows,last_output_seq=GREATEST(sessions.last_output_seq,excluded.last_output_seq),ended_at=excluded.ended_at WHERE sessions.pc_id=$2`, item.ID, item.PCID, item.ShellType, item.TitleCipher, item.State, item.Columns, item.Rows, int64(item.LastOutputSequence), item.EndedAt, userID, deviceID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	return nil
}

func (s *Postgres) ListSessions(ctx context.Context, userID, deviceID, pcID uuid.UUID) ([]SessionRecord, error) {
	rows, err := s.Pool.Query(ctx, `SELECT s.id,s.pc_id,s.shell_type,s.title_cipher,s.state,s.columns,s.rows,s.last_output_seq,s.created_at,s.ended_at FROM sessions s JOIN pcs p ON p.id=s.pc_id WHERE p.id=$1 AND p.owner_user_id=$2 AND s.deleted_at IS NULL AND (p.device_id=$3 OR EXISTS(SELECT 1 FROM device_grants g WHERE g.pc_id=p.id AND g.device_id=$3 AND (g.expires_at IS NULL OR g.expires_at>now()))) ORDER BY s.created_at DESC,s.id`, pcID, userID, deviceID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]SessionRecord, 0)
	for rows.Next() {
		var i SessionRecord
		var seq int64
		if err := rows.Scan(&i.ID, &i.PCID, &i.ShellType, &i.TitleCipher, &i.State, &i.Columns, &i.Rows, &seq, &i.CreatedAt, &i.EndedAt); err != nil {
			return nil, err
		}
		i.LastOutputSequence = uint64(seq)
		result = append(result, i)
	}
	return result, rows.Err()
}

func (s *Postgres) PutGrant(ctx context.Context, userID, actorID, deviceID, pcID uuid.UUID, permission string, expiresAt *time.Time) error {
	command, err := s.Pool.Exec(ctx, `INSERT INTO device_grants(device_id,pc_id,permission,approved_by,expires_at) SELECT $1,$2,$3,$4,$5 FROM pcs p JOIN devices a ON a.id=$4 WHERE p.id=$2 AND p.owner_user_id=$6 AND a.user_id=$6 AND a.trust_state='TRUSTED' ON CONFLICT(device_id,pc_id) DO UPDATE SET permission=excluded.permission,approved_by=excluded.approved_by,expires_at=excluded.expires_at,updated_at=now()`, deviceID, pcID, permission, actorID, expiresAt, userID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	return nil
}

func (s *Postgres) SessionPermission(ctx context.Context, userID, deviceID, sessionID uuid.UUID) (string, error) {
	var permission string
	err := s.Pool.QueryRow(ctx, `SELECT CASE WHEN p.device_id=$2 THEN 'MANAGE' ELSE g.permission END FROM sessions s JOIN pcs p ON p.id=s.pc_id LEFT JOIN device_grants g ON g.pc_id=p.id AND g.device_id=$2 AND (g.expires_at IS NULL OR g.expires_at>now()) WHERE s.id=$1 AND p.owner_user_id=$3 AND s.deleted_at IS NULL AND (p.device_id=$2 OR g.device_id IS NOT NULL)`, sessionID, deviceID, userID).Scan(&permission)
	if errors.Is(err, pgx.ErrNoRows) {
		return "", ErrDenied
	}
	return permission, err
}

func (s *Postgres) SessionPCDevice(ctx context.Context, userID, sessionID uuid.UUID) (uuid.UUID, error) {
	var id uuid.UUID
	err := s.Pool.QueryRow(ctx, `SELECT p.device_id FROM sessions s JOIN pcs p ON p.id=s.pc_id WHERE s.id=$1 AND p.owner_user_id=$2 AND s.deleted_at IS NULL`, sessionID, userID).Scan(&id)
	if errors.Is(err, pgx.ErrNoRows) {
		return uuid.Nil, ErrNotFound
	}
	return id, err
}

type HistoryItem struct {
	Kind             string    `json:"kind"`
	ID               uuid.UUID `json:"id"`
	StartSequence    uint64    `json:"start_seq,omitempty"`
	EndSequence      uint64    `json:"end_seq,omitempty"`
	SnapshotSequence uint64    `json:"snapshot_seq,omitempty"`
	ObjectKey        string    `json:"object_key"`
	KeyID            string    `json:"key_id"`
	CiphertextSHA256 []byte    `json:"ciphertext_sha256"`
	SizeBytes        int64     `json:"size_bytes"`
	FormatName       string    `json:"format_name,omitempty"`
	FormatVersion    int       `json:"format_version,omitempty"`
	CreatedAt        time.Time `json:"created_at"`
}

type HistoryUploadRecord struct {
	ID               uuid.UUID
	SessionID        uuid.UUID
	Kind             string
	ObjectKey        string
	KeyID            string
	CiphertextSHA256 []byte
	SizeBytes        int64
	StartSequence    *int64
	EndSequence      *int64
	SnapshotSequence *int64
	Rows             *int
	Columns          *int
	FormatName       *string
	FormatVersion    *int
	ExpiresAt        time.Time
}

func (s *Postgres) CreateHistoryUpload(ctx context.Context, userID, actorID uuid.UUID, item HistoryUploadRecord) error {
	command, err := s.Pool.Exec(ctx, `
		INSERT INTO history_uploads(id,user_id,actor_device_id,session_id,kind,object_key,key_id,ciphertext_sha256,size_bytes,start_seq,end_seq,snapshot_seq,rows,columns,format_name,format_version,expires_at)
		SELECT $1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17
		FROM sessions s
		JOIN pcs p ON p.id=s.pc_id AND p.owner_user_id=$2 AND p.device_id=$3 AND p.remote_routing_enabled
		JOIN devices d ON d.id=$3 AND d.user_id=$2 AND d.device_type='WINDOWS_PC' AND d.trust_state='TRUSTED'
		WHERE s.id=$4 AND s.deleted_at IS NULL`,
		item.ID, userID, actorID, item.SessionID, item.Kind, item.ObjectKey, item.KeyID, item.CiphertextSHA256,
		item.SizeBytes, item.StartSequence, item.EndSequence, item.SnapshotSequence, item.Rows, item.Columns,
		item.FormatName, item.FormatVersion, item.ExpiresAt)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrDenied
	}
	return nil
}

func (s *Postgres) PendingHistoryUpload(ctx context.Context, userID, actorID, sessionID, uploadID uuid.UUID) (HistoryUploadRecord, error) {
	var item HistoryUploadRecord
	err := s.Pool.QueryRow(ctx, `
		SELECT id,session_id,kind,object_key,key_id,ciphertext_sha256,size_bytes,start_seq,end_seq,snapshot_seq,rows,columns,format_name,format_version,expires_at
		FROM history_uploads
		WHERE id=$1 AND user_id=$2 AND actor_device_id=$3 AND session_id=$4 AND committed_at IS NULL AND expires_at>now()`,
		uploadID, userID, actorID, sessionID,
	).Scan(&item.ID, &item.SessionID, &item.Kind, &item.ObjectKey, &item.KeyID, &item.CiphertextSHA256,
		&item.SizeBytes, &item.StartSequence, &item.EndSequence, &item.SnapshotSequence, &item.Rows, &item.Columns,
		&item.FormatName, &item.FormatVersion, &item.ExpiresAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return HistoryUploadRecord{}, ErrNotFound
	}
	return item, err
}

func (s *Postgres) CommitHistoryUpload(ctx context.Context, userID, actorID, sessionID, uploadID uuid.UUID) (HistoryItem, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return HistoryItem{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var upload HistoryUploadRecord
	err = tx.QueryRow(ctx, `
		SELECT id,session_id,kind,object_key,key_id,ciphertext_sha256,size_bytes,start_seq,end_seq,snapshot_seq,rows,columns,format_name,format_version,expires_at
		FROM history_uploads
		WHERE id=$1 AND user_id=$2 AND actor_device_id=$3 AND session_id=$4 AND committed_at IS NULL AND expires_at>now()
		FOR UPDATE`, uploadID, userID, actorID, sessionID,
	).Scan(&upload.ID, &upload.SessionID, &upload.Kind, &upload.ObjectKey, &upload.KeyID, &upload.CiphertextSHA256,
		&upload.SizeBytes, &upload.StartSequence, &upload.EndSequence, &upload.SnapshotSequence, &upload.Rows,
		&upload.Columns, &upload.FormatName, &upload.FormatVersion, &upload.ExpiresAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return HistoryItem{}, ErrConflict
	}
	if err != nil {
		return HistoryItem{}, err
	}
	itemID := uuid.New()
	if upload.Kind == "CHUNK" {
		_, err = tx.Exec(ctx, `
			INSERT INTO event_chunks(id,session_id,start_seq,end_seq,object_key,key_id,ciphertext_sha256,size_bytes)
			VALUES($1,$2,$3,$4,$5,$6,$7,$8)`, itemID, sessionID, upload.StartSequence, upload.EndSequence,
			upload.ObjectKey, upload.KeyID, upload.CiphertextSHA256, upload.SizeBytes)
	} else {
		_, err = tx.Exec(ctx, `
			INSERT INTO snapshots(id,session_id,snapshot_seq,rows,columns,object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version)
			VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)`, itemID, sessionID, upload.SnapshotSequence,
			upload.Rows, upload.Columns, upload.ObjectKey, upload.KeyID, upload.CiphertextSHA256, upload.SizeBytes,
			upload.FormatName, upload.FormatVersion)
	}
	if err != nil {
		return HistoryItem{}, err
	}
	if _, err := tx.Exec(ctx, `UPDATE history_uploads SET committed_at=now() WHERE id=$1`, uploadID); err != nil {
		return HistoryItem{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return HistoryItem{}, err
	}
	item := HistoryItem{Kind: strings.ToLower(upload.Kind), ID: itemID, ObjectKey: upload.ObjectKey, KeyID: upload.KeyID,
		CiphertextSHA256: upload.CiphertextSHA256, SizeBytes: upload.SizeBytes, CreatedAt: time.Now().UTC()}
	if upload.StartSequence != nil {
		item.StartSequence = uint64(*upload.StartSequence)
	}
	if upload.EndSequence != nil {
		item.EndSequence = uint64(*upload.EndSequence)
	}
	if upload.SnapshotSequence != nil {
		item.SnapshotSequence = uint64(*upload.SnapshotSequence)
	}
	if upload.FormatName != nil {
		item.FormatName = *upload.FormatName
	}
	if upload.FormatVersion != nil {
		item.FormatVersion = *upload.FormatVersion
	}
	return item, nil
}

func (s *Postgres) SessionHistoryObjectKeys(ctx context.Context, userID, actorID, sessionID uuid.UUID) ([]string, error) {
	permission, err := s.SessionPermission(ctx, userID, actorID, sessionID)
	if err != nil || permission != "MANAGE" {
		return nil, ErrDenied
	}
	rows, err := s.Pool.Query(ctx, `
		SELECT object_key FROM event_chunks WHERE session_id=$1 AND deleted_at IS NULL
		UNION SELECT object_key FROM snapshots WHERE session_id=$1 AND deleted_at IS NULL
		UNION SELECT object_key FROM history_uploads WHERE session_id=$1 AND committed_at IS NULL`, sessionID)
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
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var permission string
	err = tx.QueryRow(ctx, `SELECT CASE WHEN p.device_id=$2 THEN 'MANAGE' ELSE g.permission END FROM sessions s JOIN pcs p ON p.id=s.pc_id LEFT JOIN device_grants g ON g.pc_id=p.id AND g.device_id=$2 AND (g.expires_at IS NULL OR g.expires_at>now()) WHERE s.id=$1 AND p.owner_user_id=$3 AND s.deleted_at IS NULL AND (p.device_id=$2 OR g.device_id IS NOT NULL) FOR UPDATE OF s,p`, sessionID, actorID, userID).Scan(&permission)
	if errors.Is(err, pgx.ErrNoRows) || permission != "MANAGE" {
		return ErrDenied
	}
	if err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `UPDATE event_chunks SET deleted_at=now() WHERE session_id=$1 AND deleted_at IS NULL`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `UPDATE snapshots SET deleted_at=now() WHERE session_id=$1 AND deleted_at IS NULL`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `DELETE FROM history_uploads WHERE session_id=$1`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `UPDATE sessions SET state='DELETED',deleted_at=now() WHERE id=$1`, sessionID); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id) VALUES($1,$2,'session.delete','session',$3,'SUCCEEDED',$4)`, userID, actorID, sessionID, traceID); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) ListHistory(ctx context.Context, userID, deviceID, sessionID uuid.UUID) ([]HistoryItem, error) {
	if _, err := s.SessionPermission(ctx, userID, deviceID, sessionID); err != nil {
		return nil, err
	}
	rows, err := s.Pool.Query(ctx, `SELECT 'chunk',id,start_seq,end_seq,NULL::bigint,object_key,key_id,ciphertext_sha256,size_bytes,''::text,0,created_at FROM event_chunks WHERE session_id=$1 AND deleted_at IS NULL UNION ALL SELECT 'snapshot',id,NULL::bigint,NULL::bigint,snapshot_seq,object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version,created_at FROM snapshots WHERE session_id=$1 AND deleted_at IS NULL ORDER BY 12,2`, sessionID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]HistoryItem, 0)
	for rows.Next() {
		var i HistoryItem
		var start, end, snapshot *int64
		if err := rows.Scan(&i.Kind, &i.ID, &start, &end, &snapshot, &i.ObjectKey, &i.KeyID, &i.CiphertextSHA256, &i.SizeBytes, &i.FormatName, &i.FormatVersion, &i.CreatedAt); err != nil {
			return nil, err
		}
		if start != nil {
			i.StartSequence = uint64(*start)
		}
		if end != nil {
			i.EndSequence = uint64(*end)
		}
		if snapshot != nil {
			i.SnapshotSequence = uint64(*snapshot)
		}
		result = append(result, i)
	}
	return result, rows.Err()
}

type Plan struct {
	Key             string          `json:"key"`
	DisplayName     string          `json:"display_name"`
	Description     string          `json:"description"`
	BillingPeriod   string          `json:"billing_period"`
	MaxRemotePCs    int             `json:"max_remote_pcs"`
	MaxLiveSessions int             `json:"max_live_sessions"`
	FeatureFlags    json.RawMessage `json:"feature_flags"`
	ChannelProducts json.RawMessage `json:"channel_products"`
	SortOrder       int             `json:"sort_order"`
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
	if input.MaxRemotePCs < 0 || input.MaxRemotePCs > 10000 || input.MaxLiveSessions < 0 || input.MaxLiveSessions > 100000 || input.SortOrder < -100000 || input.SortOrder > 100000 {
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
	if len(input.FeatureFlags) == 0 {
		input.FeatureFlags = json.RawMessage(`{}`)
	}
	if len(input.ChannelProducts) == 0 {
		input.ChannelProducts = json.RawMessage(`{}`)
	}
	if len(input.VisiblePlatforms) == 0 {
		input.VisiblePlatforms = []string{"ANDROID", "WEB"}
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
		&item.AvailableUntil, &item.FallbackPlanKey)
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
		rows, queryErr := tx.Query(ctx, `SELECT p.plan_key,p.display_name,p.description,p.billing_period,p.max_remote_pcs,p.max_live_sessions,p.feature_flags,p.channel_products,p.sort_order,p.visible_platforms,p.available_from,p.available_until,p.fallback_plan_key FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.status='PUBLISHED'`)
		if queryErr != nil {
			return PlanDraft{}, queryErr
		}
		type planCopy struct {
			key, displayName, description, period string
			maxPCs, maxSessions, sortOrder        int
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
			if scanErr := rows.Scan(&key, &displayName, &description, &period, &maxPCs, &maxSessions, &flags, &products, &sortOrder, &platforms, &from, &until, &fallback); scanErr != nil {
				rows.Close()
				return PlanDraft{}, scanErr
			}
			copies = append(copies, planCopy{key: key, displayName: displayName, description: description, period: period, maxPCs: maxPCs, maxSessions: maxSessions, sortOrder: sortOrder, flags: flags, products: products, platforms: platforms, from: from, until: until, fallback: fallback})
		}
		if rowsErr := rows.Err(); rowsErr != nil {
			rows.Close()
			return PlanDraft{}, rowsErr
		}
		rows.Close()
		for _, copy := range copies {
			if _, insertErr := tx.Exec(ctx, `INSERT INTO plans(id,catalog_version_id,plan_key,display_name,description,billing_period,max_remote_pcs,max_live_sessions,feature_flags,channel_products,sort_order,visible_platforms,available_from,available_until,fallback_plan_key) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10::jsonb,$11,$12,$13,$14,$15)`, uuid.New(), catalogID, copy.key, copy.displayName, copy.description, copy.period, copy.maxPCs, copy.maxSessions, copy.flags, copy.products, copy.sortOrder, copy.platforms, copy.from, copy.until, copy.fallback); insertErr != nil {
				return PlanDraft{}, insertErr
			}
		}
	} else if err != nil {
		return PlanDraft{}, err
	}
	itemID := uuid.New()
	existingErr := tx.QueryRow(ctx, `SELECT id FROM plans WHERE catalog_version_id=$1 AND plan_key=$2`, catalogID, strings.TrimSpace(input.PlanKey)).Scan(&itemID)
	if existingErr == nil {
		_, err = tx.Exec(ctx, `UPDATE plans SET display_name=$2,description=$3,billing_period=$4,max_remote_pcs=$5,max_live_sessions=$6,feature_flags=$7::jsonb,channel_products=$8::jsonb,sort_order=$9,visible_platforms=$10,available_from=$11,available_until=$12,fallback_plan_key=NULLIF($13,'') WHERE id=$1`,
			itemID, strings.TrimSpace(input.DisplayName), strings.TrimSpace(input.Description), strings.ToUpper(strings.TrimSpace(input.BillingPeriod)), input.MaxRemotePCs, input.MaxLiveSessions, input.FeatureFlags, input.ChannelProducts, input.SortOrder, input.VisiblePlatforms, input.AvailableFrom, input.AvailableUntil, strings.TrimSpace(input.FallbackPlanKey))
	} else {
		if !errors.Is(existingErr, pgx.ErrNoRows) {
			return PlanDraft{}, existingErr
		}
		_, err = tx.Exec(ctx, `
			INSERT INTO plans(id,catalog_version_id,plan_key,display_name,description,billing_period,max_remote_pcs,max_live_sessions,feature_flags,channel_products,sort_order,visible_platforms,available_from,available_until,fallback_plan_key)
			VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10::jsonb,$11,$12,$13,$14,NULLIF($15,''))`,
			itemID, catalogID, strings.TrimSpace(input.PlanKey), strings.TrimSpace(input.DisplayName), strings.TrimSpace(input.Description), strings.ToUpper(strings.TrimSpace(input.BillingPeriod)),
			input.MaxRemotePCs, input.MaxLiveSessions, input.FeatureFlags, input.ChannelProducts, input.SortOrder, input.VisiblePlatforms, input.AvailableFrom, input.AvailableUntil, strings.TrimSpace(input.FallbackPlanKey))
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
	item, err := scanPlanDraft(tx.QueryRow(ctx, `SELECT id,$2::bigint,plan_key,display_name,description,billing_period,max_remote_pcs,max_live_sessions,feature_flags,channel_products,sort_order,visible_platforms,available_from,available_until,COALESCE(fallback_plan_key,'') FROM plans WHERE id=$1`, itemID, version))
	if err != nil {
		return PlanDraft{}, err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,$2,'billing.plan.create','plan',$3,'SUCCEEDED',$4,jsonb_build_object('catalog_version',$5::bigint))`, adminID, actorDeviceID, itemID, traceID, version); err != nil {
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
		feature_flags=$8::jsonb,channel_products=$9::jsonb,sort_order=$10,visible_platforms=$11,available_from=$12,available_until=$13,fallback_plan_key=NULLIF($14,'')
		FROM catalog_versions c WHERE p.catalog_version_id=c.id AND c.status='DRAFT' AND p.id=$1`,
		planID, strings.TrimSpace(input.PlanKey), strings.TrimSpace(input.DisplayName), strings.TrimSpace(input.Description), strings.ToUpper(strings.TrimSpace(input.BillingPeriod)),
		input.MaxRemotePCs, input.MaxLiveSessions, input.FeatureFlags, input.ChannelProducts, input.SortOrder, input.VisiblePlatforms, input.AvailableFrom, input.AvailableUntil, strings.TrimSpace(input.FallbackPlanKey))
	if err != nil {
		if strings.Contains(strings.ToLower(err.Error()), "duplicate") {
			return PlanDraft{}, ErrConflict
		}
		return PlanDraft{}, err
	}
	if command.RowsAffected() != 1 {
		return PlanDraft{}, ErrNotFound
	}
	item, err := scanPlanDraft(tx.QueryRow(ctx, `SELECT p.id,c.version,p.plan_key,p.display_name,p.description,p.billing_period,p.max_remote_pcs,p.max_live_sessions,p.feature_flags,p.channel_products,p.sort_order,p.visible_platforms,p.available_from,p.available_until,COALESCE(p.fallback_plan_key,'') FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE p.id=$1`, planID))
	if err != nil {
		return PlanDraft{}, err
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id) VALUES($1,$2,'billing.plan.update','plan',$3,'SUCCEEDED',$4)`, adminID, actorDeviceID, planID, traceID); err != nil {
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
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,$2,'billing.catalog.publish','catalog',$3,'SUCCEEDED',$4,jsonb_build_object('version',$5::bigint))`, adminID, actorDeviceID, draftID, traceID, version); err != nil {
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
	rows, err := s.Pool.Query(ctx, `SELECT p.plan_key,p.display_name,p.description,p.billing_period,p.max_remote_pcs,p.max_live_sessions,p.feature_flags,p.channel_products,p.sort_order FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.status='PUBLISHED' ORDER BY p.sort_order,p.plan_key`)
	if err != nil {
		return 0, nil, err
	}
	defer rows.Close()
	result := make([]Plan, 0)
	for rows.Next() {
		var i Plan
		if err := rows.Scan(&i.Key, &i.DisplayName, &i.Description, &i.BillingPeriod, &i.MaxRemotePCs, &i.MaxLiveSessions, &i.FeatureFlags, &i.ChannelProducts, &i.SortOrder); err != nil {
			return 0, nil, err
		}
		result = append(result, i)
	}
	return version, result, rows.Err()
}

type Entitlement struct {
	PlanKey         string          `json:"plan_key"`
	CatalogVersion  int64           `json:"catalog_version"`
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
	command, err := s.Pool.Exec(ctx, `INSERT INTO idempotency_keys(user_id,idempotency_key,request_hash,expires_at) VALUES($1,$2,$3,now()+interval '24 hours') ON CONFLICT(user_id,idempotency_key) DO UPDATE SET request_hash=excluded.request_hash,response_status=NULL,response_body=NULL,expires_at=excluded.expires_at,created_at=now() WHERE idempotency_keys.expires_at<now()`, userID, key, requestHash[:])
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

func (s *Postgres) SaveLease(ctx context.Context, leaseID, sessionID, holderID uuid.UUID, expiresAt time.Time, version int64) error {
	_, err := s.Pool.Exec(ctx, `INSERT INTO control_leases(session_id,holder_device_id,lease_id,expires_at,version) VALUES($1,$2,$3,$4,$5) ON CONFLICT(session_id) DO UPDATE SET holder_device_id=excluded.holder_device_id,lease_id=excluded.lease_id,expires_at=excluded.expires_at,version=excluded.version,updated_at=now()`, sessionID, holderID, leaseID, expiresAt, version)
	return err
}

func (s *Postgres) DeleteLease(ctx context.Context, sessionID, leaseID uuid.UUID) error {
	_, err := s.Pool.Exec(ctx, `DELETE FROM control_leases WHERE session_id=$1 AND lease_id=$2`, sessionID, leaseID)
	return err
}

func (s *Postgres) GetEntitlement(ctx context.Context, userID uuid.UUID) (Entitlement, error) {
	var e Entitlement
	err := s.Pool.QueryRow(ctx, `SELECT plan_key,catalog_version,max_remote_pcs,max_live_sessions,feature_flags,version,effective_at,expires_at,grace_until,source,provider_reference_hash FROM entitlements WHERE account_id=$1`, userID).Scan(&e.PlanKey, &e.CatalogVersion, &e.MaxRemotePCs, &e.MaxLiveSessions, &e.FeatureFlags, &e.Version, &e.EffectiveAt, &e.ExpiresAt, &e.GraceUntil, &e.Source, &e.ProviderRefHash)
	if errors.Is(err, pgx.ErrNoRows) {
		return Entitlement{}, ErrNotFound
	}
	return e, err
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
	var existingAccount uuid.UUID
	var existingPlan string
	var existingVersion int64
	var existingProduct string
	err = tx.QueryRow(ctx, `SELECT account_id,plan_key,catalog_version,product_id FROM subscriptions WHERE provider=$1 AND provider_ref_hash=$2 FOR UPDATE`, provider, refHash[:]).Scan(&existingAccount, &existingPlan, &existingVersion, &existingProduct)
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
		if _, err := tx.Exec(ctx, `INSERT INTO subscriptions(id,account_id,provider,product_id,provider_ref_hash,plan_key,catalog_version,status,renews_at,grace_until) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)`, uuid.New(), userID, provider, purchase.ProductID, refHash[:], planKey, catalogVersion, purchase.Status, purchase.RenewsAt, purchase.GraceUntil); err != nil {
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
	active := purchase.Status == "ACTIVE" || purchase.Status == "GRACE" || (purchase.Status == "CANCELED" && purchase.RenewsAt != nil && purchase.RenewsAt.After(now))
	if !active {
		if err := tx.QueryRow(ctx, `SELECT p.plan_key,c.version,p.max_remote_pcs,p.max_live_sessions,p.feature_flags FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.status='PUBLISHED' AND p.plan_key='free'`).Scan(&planKey, &catalogVersion, &maxRemotePCs, &maxLiveSessions, &featureFlags); err != nil {
			return Entitlement{}, err
		}
	}
	source := "DEFAULT"
	var providerHash []byte
	graceUntil := (*time.Time)(nil)
	if active {
		source = provider
		providerHash = refHash[:]
		graceUntil = purchase.GraceUntil
	}
	var current Entitlement
	err = tx.QueryRow(ctx, `SELECT plan_key,catalog_version,max_remote_pcs,max_live_sessions,feature_flags,version,effective_at,expires_at,grace_until,source,provider_reference_hash FROM entitlements WHERE account_id=$1 FOR UPDATE`, userID).Scan(&current.PlanKey, &current.CatalogVersion, &current.MaxRemotePCs, &current.MaxLiveSessions, &current.FeatureFlags, &current.Version, &current.EffectiveAt, &current.ExpiresAt, &current.GraceUntil, &current.Source, &current.ProviderRefHash)
	hasEntitlement := err == nil
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return Entitlement{}, fmt.Errorf("entitlement lookup: %w", err)
	}
	changed := !hasEntitlement || current.PlanKey != planKey || current.CatalogVersion != catalogVersion || current.MaxRemotePCs != maxRemotePCs || current.MaxLiveSessions != maxLiveSessions || !bytesEqual(current.FeatureFlags, featureFlags) || current.Source != source || !bytesEqual(current.ProviderRefHash, providerHash) || !sameTime(current.ExpiresAt, nil) || !sameTime(current.GraceUntil, graceUntil)
	if !changed {
		if err := tx.Commit(ctx); err != nil {
			return Entitlement{}, err
		}
		return current, nil
	}
	version := int64(1)
	if hasEntitlement {
		version = current.Version + 1
	}
	if hasEntitlement {
		if _, err := tx.Exec(ctx, `UPDATE entitlements SET plan_key=$2,catalog_version=$3,max_remote_pcs=$4,max_live_sessions=$5,feature_flags=$6::jsonb,version=$7,effective_at=now(),expires_at=NULL,grace_until=$8,source=$9,provider_reference_hash=$10,updated_at=now() WHERE account_id=$1`, userID, planKey, catalogVersion, maxRemotePCs, maxLiveSessions, featureFlags, version, graceUntil, source, providerHash); err != nil {
			return Entitlement{}, fmt.Errorf("entitlement update: %w", err)
		}
	} else if _, err := tx.Exec(ctx, `INSERT INTO entitlements(account_id,plan_key,catalog_version,max_remote_pcs,max_live_sessions,feature_flags,version,effective_at,grace_until,source,provider_reference_hash) VALUES($1,$2,$3,$4,$5,$6,$7,now(),$8,$9,$10)`, userID, planKey, catalogVersion, maxRemotePCs, maxLiveSessions, featureFlags, version, graceUntil, source, providerHash); err != nil {
		return Entitlement{}, fmt.Errorf("entitlement insert: %w", err)
	}
	if _, err := tx.Exec(ctx, `INSERT INTO outbox(event_type,aggregate_id,payload) VALUES('entitlement.changed',$1::uuid,jsonb_build_object('account_id',$1::uuid::text,'plan_key',$2::text,'catalog_version',$3::bigint,'version',$4::bigint,'source',$5::text))`, userID, planKey, catalogVersion, version, source); err != nil {
		return Entitlement{}, fmt.Errorf("entitlement outbox: %w", err)
	}
	if _, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,$2,'billing.purchase.verify','subscription',$1,'SUCCEEDED',$3,jsonb_build_object('provider',$4::text,'product_id',$5::text,'status',$6::text,'reference_hash',$7::text))`, userID, actorDeviceID, traceID, provider, purchase.ProductID, purchase.Status, fmt.Sprintf("%x", refHash[:])); err != nil {
		return Entitlement{}, fmt.Errorf("entitlement audit: %w", err)
	}
	current = Entitlement{PlanKey: planKey, CatalogVersion: catalogVersion, MaxRemotePCs: maxRemotePCs, MaxLiveSessions: maxLiveSessions, FeatureFlags: featureFlags, Version: version, EffectiveAt: now, GraceUntil: graceUntil, Source: source, ProviderRefHash: providerHash}
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
	_, err := s.Pool.Exec(ctx, `INSERT INTO audit_events(user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,$2,$3,$4,$5,$6,$7,$8::jsonb)`, userID, actorID, action, targetType, targetID, result, traceID, metadata)
	return err
}

var _ auth.DeviceLoader = (*Postgres)(nil)
