package store

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

var (
	ErrRegistrationClosed      = errors.New("registration is closed")
	ErrInvitationRequired      = errors.New("invitation is required")
	ErrInvitationInvalid       = errors.New("invitation is invalid")
	ErrInvitationExpired       = errors.New("invitation is expired")
	ErrInvitationExhausted     = errors.New("invitation is exhausted")
	ErrInvitationEmailMismatch = errors.New("invitation email does not match")
)

const (
	RegistrationOpen           = "OPEN"
	RegistrationInviteRequired = "INVITE_REQUIRED"
	RegistrationClosed         = "CLOSED"
)

type RegistrationPolicy struct {
	Mode      string     `json:"mode"`
	Version   int64      `json:"version"`
	UpdatedBy *uuid.UUID `json:"updated_by,omitempty"`
	UpdatedAt time.Time  `json:"updated_at"`
}

type InvitationBatchInput struct {
	Name, Status, EmailDomain, Note string
	DefaultMaxUses                 int
	ValidFrom, ValidUntil          *time.Time
	EmailHash                      *[32]byte
}

type InvitationBatch struct {
	ID                                  uuid.UUID
	Name, Status, EmailDomain, Note     string
	DefaultMaxUses                      int
	ValidFrom, ValidUntil               *time.Time
	EmailHash                           []byte
	CreatedBy                           uuid.UUID
	CreatedAt, UpdatedAt                time.Time
}

type CodeDigestRecord struct {
	ID         uuid.UUID
	Digest     [32]byte
	Prefix     string
	MaxUses    int
	ValidFrom  *time.Time
	ValidUntil *time.Time
}

type InvitationCode struct {
	ID, BatchID           uuid.UUID
	Prefix, Status        string
	MaxUses, UsedCount    int
	ValidFrom, ValidUntil *time.Time
	CreatedAt, UpdatedAt  time.Time
}

type InvitationReservation struct {
	ID, CodeID, AttemptID uuid.UUID
	State                 string
	ReservedUntil         time.Time
}

type InvitationRedemption struct {
	ID, CodeID, AttemptID uuid.UUID
	UserID                *uuid.UUID
	CodePrefix            string
	BatchName             string
	Username              string
	Nickname              string
	EmailMasked           string
	ProfileSource         string
	State                 string
	ReservedUntil         time.Time
	ProviderAcceptedAt    *time.Time
	RedeemedAt            *time.Time
	ReleasedAt            *time.Time
	CreatedAt             time.Time
}

func validRegistrationWindow(from, until *time.Time) bool {
	return from == nil || until == nil || until.After(*from)
}

func normalizeRegistrationPage(limit int) int {
	if limit < 1 || limit > 200 {
		return 50
	}
	return limit
}

func registrationRole(ctx context.Context, tx pgx.Tx, actorID uuid.UUID, write, superOnly bool) (Role, error) {
	query := `SELECT role,status FROM users WHERE id=$1`
	if write {
		query += ` FOR UPDATE`
	}
	var role Role
	var status string
	if err := tx.QueryRow(ctx, query, actorID).Scan(&role, &status); err != nil || status != "ACTIVE" {
		return "", ErrDenied
	}
	if superOnly && role != RoleSuperAdmin {
		return "", ErrDenied
	}
	if write && role != RoleSuperAdmin && role != RoleAdmin {
		return "", ErrDenied
	}
	if !write && !role.CanOpenConsole() {
		return "", ErrDenied
	}
	return role, nil
}

func (s *Postgres) registrationTx(ctx context.Context, actorID uuid.UUID, write, superOnly bool) (pgx.Tx, error) {
	options := pgx.TxOptions{IsoLevel: pgx.ReadCommitted, AccessMode: pgx.ReadOnly}
	if write {
		options = pgx.TxOptions{IsoLevel: pgx.Serializable, AccessMode: pgx.ReadWrite}
	}
	tx, err := s.Pool.BeginTx(ctx, options)
	if err != nil {
		return nil, err
	}
	if _, err = registrationRole(ctx, tx, actorID, write, superOnly); err != nil {
		_ = tx.Rollback(ctx)
		return nil, err
	}
	return tx, nil
}

func registrationAudit(ctx context.Context, tx pgx.Tx, actorID uuid.UUID, action, targetType string, targetID uuid.UUID, traceID string, metadata json.RawMessage) error {
	if len(metadata) == 0 {
		metadata = json.RawMessage(`{}`)
	}
	_, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,$2,$3,$4,'SUCCEEDED',$5,$6::jsonb)`, actorID, action, targetType, targetID, traceID, metadata)
	return err
}

func (s *Postgres) GetRegistrationPolicy(ctx context.Context) (RegistrationPolicy, error) {
	var policy RegistrationPolicy
	err := s.Pool.QueryRow(ctx, `SELECT mode,version,updated_by,updated_at FROM registration_policies WHERE singleton`).Scan(&policy.Mode, &policy.Version, &policy.UpdatedBy, &policy.UpdatedAt)
	return policy, err
}

func (s *Postgres) UpdateRegistrationPolicy(ctx context.Context, actorID uuid.UUID, mode string, expectedVersion int64, traceID string) (RegistrationPolicy, error) {
	mode = strings.ToUpper(strings.TrimSpace(mode))
	if mode != RegistrationOpen && mode != RegistrationInviteRequired && mode != RegistrationClosed {
		return RegistrationPolicy{}, ErrDenied
	}
	tx, err := s.registrationTx(ctx, actorID, true, true)
	if err != nil {
		return RegistrationPolicy{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var policy RegistrationPolicy
	err = tx.QueryRow(ctx, `UPDATE registration_policies SET mode=$1,version=version+1,updated_by=$2,updated_at=now() WHERE singleton AND version=$3 RETURNING mode,version,updated_by,updated_at`, mode, actorID, expectedVersion).Scan(&policy.Mode, &policy.Version, &policy.UpdatedBy, &policy.UpdatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return RegistrationPolicy{}, ErrConflict
	}
	if err != nil {
		return RegistrationPolicy{}, err
	}
	metadata := json.RawMessage(fmt.Sprintf(`{"mode":%q,"version":%d}`, mode, policy.Version))
	if err = registrationAudit(ctx, tx, actorID, "registration.policy.update", "registration_policy", uuid.Nil, traceID, metadata); err != nil {
		return RegistrationPolicy{}, err
	}
	return policy, tx.Commit(ctx)
}

func validateInvitationBatch(input InvitationBatchInput) error {
	name := strings.TrimSpace(input.Name)
	status := strings.ToUpper(strings.TrimSpace(input.Status))
	domain := strings.ToLower(strings.TrimSpace(input.EmailDomain))
	if name == "" || len(name) > 128 || input.DefaultMaxUses < 1 || len(input.Note) > 1000 || !validRegistrationWindow(input.ValidFrom, input.ValidUntil) {
		return ErrDenied
	}
	if status != "DRAFT" && status != "ACTIVE" && status != "DISABLED" {
		return ErrDenied
	}
	if len(domain) > 253 || strings.Contains(domain, "@") {
		return ErrDenied
	}
	return nil
}

func scanInvitationBatch(row pgx.Row) (InvitationBatch, error) {
	var batch InvitationBatch
	var domain *string
	err := row.Scan(&batch.ID, &batch.Name, &batch.Status, &batch.DefaultMaxUses,
		&batch.ValidFrom, &batch.ValidUntil, &domain, &batch.EmailHash, &batch.Note,
		&batch.CreatedBy, &batch.CreatedAt, &batch.UpdatedAt)
	if domain != nil {
		batch.EmailDomain = *domain
	}
	return batch, err
}

func (s *Postgres) CreateInvitationBatch(ctx context.Context, actorID uuid.UUID, input InvitationBatchInput, traceID string) (InvitationBatch, error) {
	if input.Status == "" {
		input.Status = "ACTIVE"
	}
	if input.DefaultMaxUses == 0 {
		input.DefaultMaxUses = 1
	}
	if err := validateInvitationBatch(input); err != nil {
		return InvitationBatch{}, err
	}
	tx, err := s.registrationTx(ctx, actorID, true, false)
	if err != nil {
		return InvitationBatch{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var emailHash any
	if input.EmailHash != nil {
		emailHash = input.EmailHash[:]
	}
	id := uuid.New()
	batch, err := scanInvitationBatch(tx.QueryRow(ctx, `
		INSERT INTO invitation_batches(id,name,status,default_max_uses,valid_from,valid_until,email_domain,email_hash,note,created_by)
		VALUES($1,$2,$3,$4,$5,$6,NULLIF($7,''),$8,$9,$10)
		RETURNING id,name,status,default_max_uses,valid_from,valid_until,email_domain,email_hash,note,created_by,created_at,updated_at`,
		id, strings.TrimSpace(input.Name), strings.ToUpper(input.Status), input.DefaultMaxUses,
		input.ValidFrom, input.ValidUntil, strings.ToLower(strings.TrimSpace(input.EmailDomain)), emailHash,
		input.Note, actorID))
	if err != nil {
		return InvitationBatch{}, err
	}
	if err = registrationAudit(ctx, tx, actorID, "invitation.batch.create", "invitation_batch", id, traceID, nil); err != nil {
		return InvitationBatch{}, err
	}
	return batch, tx.Commit(ctx)
}

func (s *Postgres) AddInvitationCodes(ctx context.Context, actorID, batchID uuid.UUID, codes []CodeDigestRecord, traceID string) ([]InvitationCode, error) {
	if len(codes) < 1 || len(codes) > 10000 {
		return nil, ErrDenied
	}
	seen := make(map[[32]byte]struct{}, len(codes))
	for _, code := range codes {
		if code.ID == uuid.Nil || code.MaxUses < 1 || len(code.Prefix) < 4 || len(code.Prefix) > 16 || !validRegistrationWindow(code.ValidFrom, code.ValidUntil) {
			return nil, ErrDenied
		}
		if _, exists := seen[code.Digest]; exists {
			return nil, ErrDenied
		}
		seen[code.Digest] = struct{}{}
	}
	tx, err := s.registrationTx(ctx, actorID, true, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var exists bool
	if err = tx.QueryRow(ctx, `SELECT true FROM invitation_batches WHERE id=$1 FOR UPDATE`, batchID).Scan(&exists); errors.Is(err, pgx.ErrNoRows) {
		return nil, ErrNotFound
	} else if err != nil {
		return nil, err
	}
	result := make([]InvitationCode, 0, len(codes))
	for _, code := range codes {
		var stored InvitationCode
		err = tx.QueryRow(ctx, `INSERT INTO invitation_codes(id,batch_id,code_hash,code_prefix,max_uses,valid_from,valid_until) VALUES($1,$2,$3,$4,$5,$6,$7) RETURNING id,batch_id,code_prefix,status,max_uses,used_count,valid_from,valid_until,created_at,updated_at`, code.ID, batchID, code.Digest[:], code.Prefix, code.MaxUses, code.ValidFrom, code.ValidUntil).Scan(&stored.ID, &stored.BatchID, &stored.Prefix, &stored.Status, &stored.MaxUses, &stored.UsedCount, &stored.ValidFrom, &stored.ValidUntil, &stored.CreatedAt, &stored.UpdatedAt)
		if err != nil {
			return nil, ErrConflict
		}
		result = append(result, stored)
	}
	metadata := json.RawMessage(fmt.Sprintf(`{"count":%d}`, len(codes)))
	if err = registrationAudit(ctx, tx, actorID, "invitation.codes.create", "invitation_batch", batchID, traceID, metadata); err != nil {
		return nil, err
	}
	return result, tx.Commit(ctx)
}

func (s *Postgres) ListInvitationBatches(ctx context.Context, actorID uuid.UUID, limit int) ([]InvitationBatch, error) {
	tx, err := s.registrationTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,name,status,default_max_uses,valid_from,valid_until,email_domain,email_hash,note,created_by,created_at,updated_at FROM invitation_batches ORDER BY created_at DESC,id DESC LIMIT $1`, normalizeRegistrationPage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []InvitationBatch
	for rows.Next() {
		batch, scanErr := scanInvitationBatch(rows)
		if scanErr != nil {
			return nil, scanErr
		}
		result = append(result, batch)
	}
	return result, rows.Err()
}

func (s *Postgres) ListInvitationCodes(ctx context.Context, actorID, batchID uuid.UUID, limit int) ([]InvitationCode, error) {
	tx, err := s.registrationTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,batch_id,code_prefix,status,max_uses,used_count,valid_from,valid_until,created_at,updated_at FROM invitation_codes WHERE batch_id=$1 ORDER BY created_at DESC,id DESC LIMIT $2`, batchID, normalizeRegistrationPage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []InvitationCode
	for rows.Next() {
		var code InvitationCode
		if err = rows.Scan(&code.ID, &code.BatchID, &code.Prefix, &code.Status, &code.MaxUses, &code.UsedCount, &code.ValidFrom, &code.ValidUntil, &code.CreatedAt, &code.UpdatedAt); err != nil {
			return nil, err
		}
		result = append(result, code)
	}
	return result, rows.Err()
}

func (s *Postgres) SetInvitationBatchStatus(ctx context.Context, actorID, batchID uuid.UUID, status, traceID string) error {
	status = strings.ToUpper(strings.TrimSpace(status))
	if status != "DRAFT" && status != "ACTIVE" && status != "DISABLED" {
		return ErrDenied
	}
	return s.setInvitationStatus(ctx, actorID, batchID, status, traceID, true)
}

func (s *Postgres) SetInvitationCodeStatus(ctx context.Context, actorID, codeID uuid.UUID, status, traceID string) error {
	status = strings.ToUpper(strings.TrimSpace(status))
	if status != "ACTIVE" && status != "DISABLED" {
		return ErrDenied
	}
	return s.setInvitationStatus(ctx, actorID, codeID, status, traceID, false)
}

func (s *Postgres) setInvitationStatus(ctx context.Context, actorID, id uuid.UUID, status, traceID string, batch bool) error {
	tx, err := s.registrationTx(ctx, actorID, true, false)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	query := `UPDATE invitation_codes SET status=$2,updated_at=now() WHERE id=$1`
	action, targetType := "invitation.code.status", "invitation_code"
	if batch {
		query = `UPDATE invitation_batches SET status=$2,updated_at=now() WHERE id=$1`
		action, targetType = "invitation.batch.status", "invitation_batch"
	}
	command, err := tx.Exec(ctx, query, id, status)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrNotFound
	}
	metadata := json.RawMessage(fmt.Sprintf(`{"status":%q}`, status))
	if err = registrationAudit(ctx, tx, actorID, action, targetType, id, traceID, metadata); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) ReserveInvitation(ctx context.Context, attemptID uuid.UUID, codeHash, emailHash [32]byte, emailDomain string, ttl time.Duration) (InvitationReservation, error) {
	if attemptID == uuid.Nil || ttl < time.Minute || ttl > 30*time.Minute {
		return InvitationReservation{}, ErrInvitationInvalid
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return InvitationReservation{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var mode string
	if err = tx.QueryRow(ctx, `SELECT mode FROM registration_policies WHERE singleton FOR UPDATE`).Scan(&mode); err != nil {
		return InvitationReservation{}, err
	}
	if mode == RegistrationClosed {
		return InvitationReservation{}, ErrRegistrationClosed
	}
	if mode == RegistrationInviteRequired && codeHash == ([32]byte{}) {
		return InvitationReservation{}, ErrInvitationRequired
	}
	var prior InvitationReservation
	err = tx.QueryRow(ctx, `SELECT id,code_id,registration_attempt_id,state,reserved_until FROM invitation_redemptions WHERE registration_attempt_id=$1 FOR UPDATE`, attemptID).Scan(&prior.ID, &prior.CodeID, &prior.AttemptID, &prior.State, &prior.ReservedUntil)
	if err == nil {
		if prior.State == "RELEASED" {
			return InvitationReservation{}, ErrInvitationInvalid
		}
		if err = tx.Commit(ctx); err != nil {
			return InvitationReservation{}, err
		}
		return prior, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return InvitationReservation{}, err
	}
	var codeID uuid.UUID
	var codeStatus, batchStatus, domain string
	var maxUses, usedCount int
	var codeFrom, codeUntil, batchFrom, batchUntil *time.Time
	var targetEmail []byte
	err = tx.QueryRow(ctx, `
		SELECT code.id,code.status,code.max_uses,code.used_count,code.valid_from,code.valid_until,
		       batch.status,batch.valid_from,batch.valid_until,coalesce(batch.email_domain,''),batch.email_hash
		FROM invitation_codes code JOIN invitation_batches batch ON batch.id=code.batch_id
		WHERE code.code_hash=$1 FOR UPDATE OF code,batch`, codeHash[:]).Scan(
		&codeID, &codeStatus, &maxUses, &usedCount, &codeFrom, &codeUntil,
		&batchStatus, &batchFrom, &batchUntil, &domain, &targetEmail)
	if errors.Is(err, pgx.ErrNoRows) {
		return InvitationReservation{}, ErrInvitationInvalid
	}
	if err != nil {
		return InvitationReservation{}, err
	}
	now := time.Now().UTC()
	if codeStatus != "ACTIVE" || batchStatus != "ACTIVE" {
		return InvitationReservation{}, ErrInvitationInvalid
	}
	if (codeFrom != nil && now.Before(*codeFrom)) || (batchFrom != nil && now.Before(*batchFrom)) || (codeUntil != nil && !now.Before(*codeUntil)) || (batchUntil != nil && !now.Before(*batchUntil)) {
		return InvitationReservation{}, ErrInvitationExpired
	}
	if usedCount >= maxUses {
		return InvitationReservation{}, ErrInvitationExhausted
	}
	if domain != "" && strings.ToLower(strings.TrimSpace(emailDomain)) != strings.ToLower(domain) {
		return InvitationReservation{}, ErrInvitationEmailMismatch
	}
	if len(targetEmail) == 32 && !bytes.Equal(targetEmail, emailHash[:]) {
		return InvitationReservation{}, ErrInvitationEmailMismatch
	}
	reservation := InvitationReservation{ID: uuid.New(), CodeID: codeID, AttemptID: attemptID, State: "RESERVED", ReservedUntil: now.Add(ttl)}
	if _, err = tx.Exec(ctx, `INSERT INTO invitation_redemptions(id,code_id,registration_attempt_id,email_hash,state,reserved_until) VALUES($1,$2,$3,$4,'RESERVED',$5)`, reservation.ID, codeID, attemptID, emailHash[:], reservation.ReservedUntil); err != nil {
		return InvitationReservation{}, err
	}
	if _, err = tx.Exec(ctx, `UPDATE invitation_codes SET used_count=used_count+1,updated_at=now() WHERE id=$1`, codeID); err != nil {
		return InvitationReservation{}, err
	}
	return reservation, tx.Commit(ctx)
}

func (s *Postgres) RecordInvitationRegistrationProfile(ctx context.Context, attemptID uuid.UUID, profile AccountIdentityProfile) error {
	profile = normalizeIdentityProfileProjection(profile)
	if attemptID == uuid.Nil || profile.empty() {
		return ErrInvitationInvalid
	}
	command, err := s.Pool.Exec(ctx, `
		INSERT INTO invitation_registration_profiles(redemption_id,username,nickname,email_masked)
		SELECT id,$2,$3,$4 FROM invitation_redemptions
		WHERE registration_attempt_id=$1 AND state IN ('RESERVED','PROVIDER_ACCEPTED')
		ON CONFLICT(redemption_id) DO UPDATE SET username=excluded.username,nickname=excluded.nickname,email_masked=excluded.email_masked,updated_at=now()`,
		attemptID, profile.Username, profile.Nickname, profile.EmailMasked)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrInvitationInvalid
	}
	return nil
}

func (s *Postgres) MarkInvitationProviderAccepted(ctx context.Context, attemptID uuid.UUID) error {
	command, err := s.Pool.Exec(ctx, `UPDATE invitation_redemptions SET state='PROVIDER_ACCEPTED',provider_accepted_at=coalesce(provider_accepted_at,now()),updated_at=now() WHERE registration_attempt_id=$1 AND state IN ('RESERVED','PROVIDER_ACCEPTED') AND (state='PROVIDER_ACCEPTED' OR reserved_until>now())`, attemptID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrInvitationInvalid
	}
	return nil
}

func invitationReleaseNeedsMutation(state string) (bool, error) {
	switch state {
	case "RELEASED":
		return false, nil
	case "RESERVED":
		return true, nil
	default:
		return false, ErrConflict
	}
}

func (s *Postgres) ReleaseInvitation(ctx context.Context, attemptID uuid.UUID) error {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var codeID uuid.UUID
	var state string
	err = tx.QueryRow(ctx, `SELECT code_id,state FROM invitation_redemptions WHERE registration_attempt_id=$1 FOR UPDATE`, attemptID).Scan(&codeID, &state)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrInvitationInvalid
	}
	if err != nil {
		return err
	}
	mutate, err := invitationReleaseNeedsMutation(state)
	if err != nil {
		return err
	}
	if !mutate {
		return tx.Commit(ctx)
	}
	command, err := tx.Exec(ctx, `UPDATE invitation_redemptions SET state='RELEASED',released_at=now(),updated_at=now() WHERE registration_attempt_id=$1 AND state='RESERVED'`, attemptID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrConflict
	}
	if _, err = tx.Exec(ctx, `DELETE FROM invitation_registration_profiles WHERE redemption_id IN (SELECT id FROM invitation_redemptions WHERE registration_attempt_id=$1)`, attemptID); err != nil {
		return err
	}
	if _, err = tx.Exec(ctx, `UPDATE invitation_codes SET used_count=greatest(used_count-1,0),updated_at=now() WHERE id=$1`, codeID); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

const (
	pendingInvitationCandidateCountSQL = `SELECT count(*) FROM invitation_redemptions WHERE email_hash=$1 AND state='PROVIDER_ACCEPTED' AND user_id IS NULL`
	pendingInvitationCandidateLockSQL  = `SELECT id FROM invitation_redemptions WHERE email_hash=$1 AND state='PROVIDER_ACCEPTED' AND user_id IS NULL FOR UPDATE`
)

func validatePendingInvitationCandidateCount(count int) error {
	if count == 0 {
		return ErrNotFound
	}
	if count != 1 {
		return ErrConflict
	}
	return nil
}

func (s *Postgres) ConfirmPendingInvitationForEmail(ctx context.Context, userID uuid.UUID, emailHash [32]byte) error {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var candidates int
	if err = tx.QueryRow(ctx, pendingInvitationCandidateCountSQL, emailHash[:]).Scan(&candidates); err != nil {
		return err
	}
	if err = validatePendingInvitationCandidateCount(candidates); err != nil {
		return err
	}
	var redemptionID uuid.UUID
	if err = tx.QueryRow(ctx, pendingInvitationCandidateLockSQL, emailHash[:]).Scan(&redemptionID); errors.Is(err, pgx.ErrNoRows) {
		return ErrNotFound
	} else if err != nil {
		return err
	}
	command, err := tx.Exec(ctx, `UPDATE invitation_redemptions SET state='REDEEMED',user_id=$2,redeemed_at=now(),updated_at=now() WHERE id=$1 AND state='PROVIDER_ACCEPTED' AND user_id IS NULL`, redemptionID, userID)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrConflict
	}
	if _, err = tx.Exec(ctx, `DELETE FROM invitation_registration_profiles WHERE redemption_id=$1`, redemptionID); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) ListInvitationRedemptions(ctx context.Context, actorID uuid.UUID, limit int) ([]InvitationRedemption, error) {
	tx, err := s.registrationTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `
		SELECT redemption.id,redemption.code_id,redemption.registration_attempt_id,redemption.user_id,
		       code.code_prefix,batch.name,
		       coalesce(nullif(account_profile.username,''),registration_profile.username,''),
		       coalesce(nullif(account_profile.nickname,''),registration_profile.nickname,''),
		       coalesce(nullif(account_profile.email_masked,''),registration_profile.email_masked,''),
		       CASE WHEN account_profile.user_id IS NOT NULL THEN 'ACCOUNT' WHEN registration_profile.redemption_id IS NOT NULL THEN 'REGISTRATION' ELSE '' END,
		       redemption.state,redemption.reserved_until,redemption.provider_accepted_at,
		       redemption.redeemed_at,redemption.released_at,redemption.created_at
		FROM invitation_redemptions redemption
		JOIN invitation_codes code ON code.id=redemption.code_id
		JOIN invitation_batches batch ON batch.id=code.batch_id
		LEFT JOIN account_identity_profiles account_profile ON account_profile.user_id=redemption.user_id
		LEFT JOIN invitation_registration_profiles registration_profile ON registration_profile.redemption_id=redemption.id
		ORDER BY redemption.created_at DESC,redemption.id DESC LIMIT $1`, normalizeRegistrationPage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []InvitationRedemption
	for rows.Next() {
		var item InvitationRedemption
		if err := rows.Scan(&item.ID, &item.CodeID, &item.AttemptID, &item.UserID,
			&item.CodePrefix, &item.BatchName, &item.Username, &item.Nickname,
			&item.EmailMasked, &item.ProfileSource, &item.State, &item.ReservedUntil,
			&item.ProviderAcceptedAt, &item.RedeemedAt, &item.ReleasedAt, &item.CreatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}
