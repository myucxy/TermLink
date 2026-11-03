package store

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"regexp"
	"sort"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/termlink/termlink/server/internal/pushnotify"
)

var ErrPushRateLimited = errors.New("push event rate limit exceeded")

type PushRegistrationInput struct {
	SoundEnabled           bool
	Topic, Environment     string
	TokenHash, TokenCipher []byte
	ExpectedGeneration     *uuid.UUID
}

type PushRegistration struct {
	SoundEnabled bool      `json:"sound_enabled"`
	ID           uuid.UUID `json:"id"`
	Generation   uuid.UUID `json:"generation"`
	Topic        string    `json:"topic"`
	Environment  string    `json:"environment"`
	Enabled      bool      `json:"enabled"`
	UpdatedAt    time.Time `json:"updated_at"`
}

type PushEventInput struct {
	EventID    uuid.UUID
	Kind       string
	SessionID  uuid.UUID
	Recipients []uuid.UUID
	OccurredAt time.Time
}

func pushDeviceAuthority(ctx context.Context, tx pgx.Tx, account, device uuid.UUID) error {
	var found uuid.UUID
	err := tx.QueryRow(ctx, `SELECT d.id FROM users u JOIN devices d ON d.user_id=u.id
        WHERE u.id=$1 AND u.status='ACTIVE' AND d.id=$2 AND d.device_type='IOS'
          AND d.trust_state='TRUSTED' AND d.revoked_at IS NULL AND d.disabled_at IS NULL
        FOR UPDATE OF u,d`, account, device).Scan(&found)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	}
	return err
}

func (s *Postgres) GetPushRegistration(ctx context.Context, account, device uuid.UUID, topic, environment string) (*PushRegistration, error) {
	var p PushRegistration
	err := s.Pool.QueryRow(ctx, `SELECT r.id,r.generation,r.topic,r.environment,r.enabled,r.updated_at,r.sound_enabled
        FROM device_push_registrations r JOIN users u ON u.id=r.account_id AND u.status='ACTIVE'
        JOIN devices d ON d.id=r.device_id AND d.user_id=r.account_id AND d.device_type='IOS'
          AND d.trust_state='TRUSTED' AND d.disabled_at IS NULL AND d.revoked_at IS NULL
        WHERE r.account_id=$1 AND r.device_id=$2 AND r.topic=$3 AND r.environment=$4`, account, device, topic, environment).
		Scan(&p.ID, &p.Generation, &p.Topic, &p.Environment, &p.Enabled, &p.UpdatedAt, &p.SoundEnabled)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	return &p, err
}

func (s *Postgres) RegisterPushDevice(ctx context.Context, account, device uuid.UUID, input PushRegistrationInput) (PushRegistration, error) {
	if len(input.TokenHash) != 32 || len(input.TokenCipher) < 32 || len(input.TokenCipher) > 2048 ||
		(input.Environment != "sandbox" && input.Environment != "production") || !pushTopicPattern.MatchString(input.Topic) {
		return PushRegistration{}, ErrDenied
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return PushRegistration{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err = pushDeviceAuthority(ctx, tx, account, device); err != nil {
		return PushRegistration{}, err
	}
	var old PushRegistration
	var oldHash []byte
	err = tx.QueryRow(ctx, `SELECT id,generation,enabled,token_hash FROM device_push_registrations
        WHERE account_id=$1 AND device_id=$2 AND topic=$3 AND environment=$4 FOR UPDATE`, account, device, input.Topic, input.Environment).
		Scan(&old.ID, &old.Generation, &old.Enabled, &oldHash)
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return PushRegistration{}, err
	}
	exists := err == nil
	// Identical retries preserve generation. Any rotation/reactivation must carry
	// the latest generation, so a delayed registration cannot restore an old token.
	if exists && (input.ExpectedGeneration != nil && *input.ExpectedGeneration != old.Generation ||
		input.ExpectedGeneration == nil && (!old.Enabled || !equalPushHash(oldHash, input.TokenHash))) {
		return PushRegistration{}, ErrConflict
	}
	if !exists && input.ExpectedGeneration != nil {
		return PushRegistration{}, ErrConflict
	}
	if exists && old.Enabled && equalPushHash(oldHash, input.TokenHash) {
		p, err := scanPushRegistration(tx.QueryRow(ctx, `UPDATE device_push_registrations SET registered_at=now(),updated_at=now(),sound_enabled=$2
            WHERE id=$1 RETURNING id,generation,topic,environment,enabled,updated_at,sound_enabled`, old.ID, input.SoundEnabled))
		if err != nil {
			return p, err
		}
		return p, tx.Commit(ctx)
	}
	// Disabled registrations relinquish their token hash. Active cross-device
	// registrations conflict; an unrelated account can never overwrite them.
	if _, err = tx.Exec(ctx, `DELETE FROM device_push_registrations r WHERE r.token_hash=$1 AND r.device_id<>$2 AND
        (NOT r.enabled OR NOT EXISTS(SELECT 1 FROM users u JOIN devices d ON d.user_id=u.id
          WHERE u.id=r.account_id AND u.status='ACTIVE' AND d.id=r.device_id AND d.trust_state='TRUSTED'
            AND d.disabled_at IS NULL AND d.revoked_at IS NULL))`, input.TokenHash, device); err != nil {
		return PushRegistration{}, err
	}
	id := old.ID
	if !exists {
		id = uuid.New()
	}
	generation := uuid.New()
	p, err := scanPushRegistration(tx.QueryRow(ctx, `INSERT INTO device_push_registrations
        (id,account_id,device_id,topic,environment,token_hash,token_cipher,generation,sound_enabled)
        VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
        ON CONFLICT(device_id,topic,environment) DO UPDATE SET token_hash=excluded.token_hash,
          token_cipher=excluded.token_cipher,generation=excluded.generation,enabled=true,sound_enabled=excluded.sound_enabled,registered_at=now(),updated_at=now()
        WHERE device_push_registrations.account_id=excluded.account_id
        RETURNING id,generation,topic,environment,enabled,updated_at,sound_enabled`, id, account, device, input.Topic, input.Environment, input.TokenHash, input.TokenCipher, generation, input.SoundEnabled))
	var pgerr *pgconn.PgError
	if errors.As(err, &pgerr) && pgerr.Code == "23505" {
		return PushRegistration{}, ErrConflict
	}
	if err != nil {
		return p, err
	}
	if _, err = tx.Exec(ctx, `UPDATE device_push_jobs SET state='CANCELLED',finished_at=now(),last_error_code='PUSH_TOKEN_ROTATED'
        WHERE registration_id=$1 AND registration_generation<>$2 AND state IN ('PENDING','PROCESSING')`, id, generation); err != nil {
		return p, err
	}
	return p, tx.Commit(ctx)
}

func equalPushHash(a, b []byte) bool { return len(a) == 32 && len(b) == 32 && string(a) == string(b) }

var pushTopicPattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9.-]{0,254}$`)

func scanPushRegistration(row pgx.Row) (PushRegistration, error) {
	var p PushRegistration
	err := row.Scan(&p.ID, &p.Generation, &p.Topic, &p.Environment, &p.Enabled, &p.UpdatedAt, &p.SoundEnabled)
	return p, err
}

func (s *Postgres) UnregisterPushDevice(ctx context.Context, account, device uuid.UUID, topic, environment string, generation uuid.UUID) error {
	if generation == uuid.Nil {
		return ErrConflict
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err = pushDeviceAuthority(ctx, tx, account, device); err != nil {
		return err
	}
	tag, err := tx.Exec(ctx, `UPDATE device_push_registrations SET enabled=false,updated_at=now()
        WHERE account_id=$1 AND device_id=$2 AND (topic=$3 OR $3='') AND (environment=$4 OR $4='') AND generation=$5`, account, device, topic, environment, generation)
	if err != nil {
		return err
	}
	if tag.RowsAffected() != 1 {
		return ErrConflict
	}
	if _, err = tx.Exec(ctx, `UPDATE device_push_jobs j SET state='CANCELLED',finished_at=now(),last_error_code='PUSH_UNREGISTERED'
        FROM device_push_registrations r WHERE j.registration_id=r.id AND r.account_id=$1 AND r.device_id=$2
          AND (r.topic=$3 OR $3='') AND (r.environment=$4 OR $4='') AND r.generation=$5 AND j.state IN ('PENDING','PROCESSING')`, account, device, topic, environment, generation); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) EnqueuePushEvent(ctx context.Context, account, agent, pc uuid.UUID, input PushEventInput) (int, error) {
	if input.EventID == uuid.Nil || input.SessionID == uuid.Nil || (input.Kind != "ai.completed" && input.Kind != "ai.needs_input") || len(input.Recipients) < 1 || len(input.Recipients) > 16 ||
		input.OccurredAt.Before(time.Now().Add(-5*time.Minute)) || input.OccurredAt.After(time.Now().Add(30*time.Second)) {
		return 0, ErrDenied
	}
	recipients := append([]uuid.UUID(nil), input.Recipients...)
	sort.Slice(recipients, func(i, j int) bool { return recipients[i].String() < recipients[j].String() })
	for i, id := range recipients {
		if id == uuid.Nil || i > 0 && id == recipients[i-1] {
			return 0, ErrDenied
		}
	}
	body, _ := json.Marshal(struct {
		Kind       string
		Session    uuid.UUID
		Recipients []uuid.UUID
		Time       time.Time
	}{input.Kind, input.SessionID, recipients, input.OccurredAt.UTC()})
	hash := sha256.Sum256(body)
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return 0, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var found uuid.UUID
	if err = tx.QueryRow(ctx, `SELECT id FROM users WHERE id=$1 AND status='ACTIVE' FOR UPDATE`, account).Scan(&found); errors.Is(err, pgx.ErrNoRows) {
		return 0, ErrDenied
	} else if err != nil {
		return 0, err
	}
	err = tx.QueryRow(ctx, `SELECT p.id FROM users u JOIN pcs p ON p.owner_user_id=u.id
        JOIN devices d ON d.id=p.device_id AND d.user_id=u.id
        WHERE p.id=$1 AND u.id=$2 AND u.status='ACTIVE' AND p.device_id=$3 AND p.remote_routing_enabled
          AND d.device_type IN ('PC_AGENT','WINDOWS_PC') AND d.trust_state='TRUSTED' AND d.disabled_at IS NULL AND d.revoked_at IS NULL
          AND 'ai_agent_v1'=ANY(p.capabilities) FOR UPDATE OF p`, pc, account, agent).Scan(&found)
	if errors.Is(err, pgx.ErrNoRows) {
		return 0, ErrDenied
	}
	if err != nil {
		return 0, err
	}
	var existing []byte
	err = tx.QueryRow(ctx, `SELECT request_hash FROM device_push_events WHERE pc_id=$1 AND event_id=$2 AND source='agent'`, pc, input.EventID).Scan(&existing)
	if err == nil {
		if !equalPushHash(existing, hash[:]) {
			return 0, ErrConflict
		}
		return 0, tx.Commit(ctx)
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return 0, err
	}
	var count int
	if err = tx.QueryRow(ctx, `SELECT count(*) FROM device_push_events WHERE pc_id=$1 AND source='agent' AND created_at>now()-interval '1 minute'`, pc).Scan(&count); err != nil {
		return 0, err
	}
	if count >= 60 {
		return 0, ErrPushRateLimited
	}
	if err = tx.QueryRow(ctx, `SELECT count(*) FROM device_push_jobs WHERE account_id=$1 AND state IN ('PENDING','PROCESSING') AND expires_at>now()`, account).Scan(&count); err != nil {
		return 0, err
	}
	if count >= 2000 {
		return 0, ErrPushRateLimited
	}
	// Explicit recipients are checked individually. A forged/foreign recipient
	// rejects the whole event, never silently widening its notification audience.
	for _, recipient := range recipients {
		var permitted bool
		err = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM devices d WHERE d.id=$1 AND d.user_id=$2 AND d.device_type='IOS'
            AND d.trust_state='TRUSTED' AND d.disabled_at IS NULL AND d.revoked_at IS NULL
            AND ($4 OR EXISTS(SELECT 1 FROM device_grants g WHERE g.pc_id=$3 AND g.device_id=d.id
                AND g.permission='MANAGE' AND (g.expires_at IS NULL OR g.expires_at>now()))))`, recipient, account, pc, s.AutoTrustDevices).Scan(&permitted)
		if err != nil {
			return 0, err
		}
		if !permitted {
			return 0, ErrDenied
		}
	}
	if _, err = tx.Exec(ctx, `INSERT INTO device_push_events(pc_id,event_id,account_id,sender_device_id,kind,session_id,request_hash)
        VALUES($1,$2,$3,$4,$5,$6,$7)`, pc, input.EventID, account, agent, input.Kind, input.SessionID, hash[:]); err != nil {
		return 0, err
	}
	tag, err := tx.Exec(ctx, `INSERT INTO device_push_jobs(id,account_id,registration_id,registration_generation,pc_id,event_id,expires_at)
        SELECT gen_random_uuid(),$1,r.id,r.generation,$2,$3,now()+interval '1 hour'
        FROM device_push_registrations r WHERE r.account_id=$1 AND r.device_id=ANY($4::uuid[]) AND r.enabled
        ON CONFLICT DO NOTHING`, account, pc, input.EventID, recipients)
	if err != nil {
		return 0, err
	}
	return int(tag.RowsAffected()), tx.Commit(ctx)
}

func (s *Postgres) ClaimPushNotifications(ctx context.Context, lease uuid.UUID, limit int, duration time.Duration) ([]uuid.UUID, error) {
	if lease == uuid.Nil || limit < 1 || limit > 10 || duration < time.Minute || duration > 5*time.Minute {
		return nil, ErrDenied
	}
	if _, err := s.Pool.Exec(ctx, `UPDATE device_push_jobs SET state='CANCELLED',finished_at=now(),last_error_code='PUSH_EXPIRED'
        WHERE id IN (SELECT id FROM device_push_jobs WHERE state IN ('PENDING','PROCESSING')
          AND (expires_at<=now() OR attempts>=10 AND (state='PENDING' OR lease_until<=now())) LIMIT 500)`); err != nil {
		return nil, err
	}
	// Keep dedupe records longer than the accepted five-minute event timestamp.
	if _, err := s.Pool.Exec(ctx, `DELETE FROM device_push_events WHERE (pc_id,event_id,source) IN
        (SELECT pc_id,event_id,source FROM device_push_events WHERE created_at<now()-interval '7 days' LIMIT 500)`); err != nil {
		return nil, err
	}
	rows, err := s.Pool.Query(ctx, `WITH due AS (SELECT id FROM device_push_jobs WHERE expires_at>now() AND attempts<10
          AND next_attempt_at<=now() AND (state='PENDING' OR state='PROCESSING' AND lease_until<=now())
          ORDER BY next_attempt_at,created_at,id FOR UPDATE SKIP LOCKED LIMIT $1)
        UPDATE device_push_jobs j SET state='PROCESSING',attempts=attempts+1,lease_id=$2,lease_until=now()+$3*interval '1 millisecond'
        FROM due WHERE j.id=due.id RETURNING j.id`, limit, lease, duration.Milliseconds())
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var ids []uuid.UUID
	for rows.Next() {
		var id uuid.UUID
		if err = rows.Scan(&id); err != nil {
			return nil, err
		}
		ids = append(ids, id)
	}
	return ids, rows.Err()
}

func (s *Postgres) PreparePushNotification(ctx context.Context, id, lease uuid.UUID) (*pushnotify.Delivery, error) {
	var d pushnotify.Delivery
	err := s.Pool.QueryRow(ctx, `SELECT j.id,r.id,r.generation,j.account_id,r.device_id,j.pc_id,COALESCE(e.session_id,'00000000-0000-0000-0000-000000000000'::uuid),
          r.token_cipher,r.topic,r.environment,e.kind,j.expires_at,j.attempts,r.sound_enabled
        FROM device_push_jobs j JOIN device_push_registrations r ON r.id=j.registration_id AND r.generation=j.registration_generation AND r.enabled
        JOIN device_push_events e ON e.pc_id=j.pc_id AND e.event_id=j.event_id AND e.source=j.event_source AND e.account_id=j.account_id
        JOIN users u ON u.id=j.account_id AND u.status='ACTIVE'
        JOIN devices recipient ON recipient.id=r.device_id AND recipient.user_id=u.id AND r.account_id=u.id
          AND recipient.device_type='IOS' AND recipient.trust_state='TRUSTED' AND recipient.disabled_at IS NULL AND recipient.revoked_at IS NULL
        JOIN pcs p ON p.id=j.pc_id AND p.owner_user_id=u.id AND p.remote_routing_enabled
        JOIN devices sender ON sender.id=p.device_id AND sender.id=e.sender_device_id AND sender.user_id=u.id
          AND sender.device_type IN ('PC_AGENT','WINDOWS_PC') AND sender.trust_state='TRUSTED' AND sender.disabled_at IS NULL AND sender.revoked_at IS NULL
        WHERE j.id=$1 AND j.lease_id=$2 AND j.state='PROCESSING' AND j.lease_until>now() AND j.expires_at>now()
          AND (e.source='server' OR 'ai_agent_v1'=ANY(p.capabilities))
          AND ($3 OR EXISTS(SELECT 1 FROM device_grants g WHERE g.pc_id=p.id AND g.device_id=recipient.id
            AND g.permission='MANAGE' AND (g.expires_at IS NULL OR g.expires_at>now())))`, id, lease, s.AutoTrustDevices).
		Scan(&d.ID, &d.RegistrationID, &d.Generation, &d.AccountID, &d.DeviceID, &d.PCID, &d.SessionID, &d.TokenCipher, &d.Topic, &d.Environment, &d.Kind, &d.ExpiresAt, &d.Attempt, &d.SoundEnabled)
	if errors.Is(err, pgx.ErrNoRows) {
		_, err = s.Pool.Exec(ctx, `UPDATE device_push_jobs SET state='CANCELLED',finished_at=now(),last_error_code='PUSH_AUTHORITY_CHANGED'
            WHERE id=$1 AND lease_id=$2 AND state='PROCESSING'`, id, lease)
		return nil, err
	}
	return &d, err
}

func (s *Postgres) FinishPushNotification(ctx context.Context, id, lease uuid.UUID, result pushnotify.Result) error {
	if !pushResultCodePattern.MatchString(result.Code) {
		result.Code = "PUSH_DELIVERY_FAILED"
	}
	tx, err := s.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var registration, generation uuid.UUID
	// Registration mutations lock registration before jobs. Keep the same order
	// here, including invalid-token cleanup, to avoid rotation/Finish deadlocks.
	err = tx.QueryRow(ctx, `SELECT registration_id FROM device_push_jobs WHERE id=$1 AND lease_id=$2 AND state='PROCESSING'`, id, lease).Scan(&registration)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil
	}
	if err != nil {
		return err
	}
	var locked uuid.UUID
	err = tx.QueryRow(ctx, `SELECT id FROM device_push_registrations WHERE id=$1 FOR UPDATE`, registration).Scan(&locked)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil
	}
	if err != nil {
		return err
	}
	var attempts int
	var expires time.Time
	err = tx.QueryRow(ctx, `SELECT registration_id,registration_generation,attempts,expires_at FROM device_push_jobs
        WHERE id=$1 AND lease_id=$2 AND state='PROCESSING' AND lease_until>now() FOR UPDATE`, id, lease).Scan(&registration, &generation, &attempts, &expires)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil
	}
	if err != nil {
		return err
	}
	state := "FAILED"
	var retryAt *time.Time
	switch result.Status {
	case pushnotify.Sent:
		state = "SENT"
	case pushnotify.Retry:
		delay := max(result.RetryAfter, 15*time.Second)
		delay = min(delay, 24*time.Hour)
		next := time.Now().Add(delay)
		if attempts < 10 && next.Before(expires) {
			state = "PENDING"
			retryAt = &next
		}
	case pushnotify.InvalidToken:
		if _, err = tx.Exec(ctx, `UPDATE device_push_registrations SET enabled=false,updated_at=now()
            WHERE id=$1 AND generation=$2 AND ($3::timestamptz IS NULL OR registered_at<=$3)`, registration, generation, result.InvalidatedAt); err != nil {
			return err
		}
	}
	if _, err = tx.Exec(ctx, `UPDATE device_push_jobs SET state=$3,next_attempt_at=COALESCE($4,next_attempt_at),
        lease_id=NULL,lease_until=NULL,last_error_code=$5,finished_at=CASE WHEN $3='PENDING' THEN NULL ELSE now() END
        WHERE id=$1 AND lease_id=$2`, id, lease, state, retryAt, result.Code); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

var pushResultCodePattern = regexp.MustCompile(`^[A-Z0-9_]{1,64}$`)
