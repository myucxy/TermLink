package store

// This file is the PostgreSQL authority for the web commercial control plane.
// Raw invitation/redemption codes, e-mail addresses, provider trade numbers,
// notification identifiers, and payment secrets are deliberately not accepted
// by this package. Callers must digest sensitive identifiers before crossing
// the store boundary and must resolve secrets outside PostgreSQL.

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
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
	ErrRedemptionInvalid       = errors.New("redemption code is invalid")
	ErrRedemptionExpired       = errors.New("redemption code is expired")
	ErrRedemptionExhausted     = errors.New("redemption code is exhausted")
	ErrRedemptionAlreadyUsed   = errors.New("redemption code was already used")
	ErrRedemptionNotApplicable = errors.New("redemption is not applicable")
	ErrRedemptionConflict      = errors.New("redemption conflicts with current plan")
	// ErrRedemptionPlanConflict is the descriptive compatibility name.
	ErrRedemptionPlanConflict    = ErrRedemptionConflict
	ErrOrderNotPayable           = errors.New("order is not payable")
	ErrOrderAmountMismatch       = errors.New("order amount does not match")
	ErrOrderSettlementMismatch   = errors.New("order settlement does not match existing transaction")
	ErrPaymentChannelUnavailable = errors.New("payment channel is unavailable")
	ErrRefundApprovalExpired     = errors.New("refund approval has expired")
	ErrRefundVersionConflict     = errors.New("refund version conflict")
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
	Name, Status, EmailDomain, GiftPlanKey, Note string
	DefaultMaxUses, GiftDurationDays             int
	ValidFrom, ValidUntil                        *time.Time
	EmailHash                                    *[32]byte
}

type InvitationBatch struct {
	ID                                           uuid.UUID
	Name, Status, EmailDomain, GiftPlanKey, Note string
	DefaultMaxUses, GiftDurationDays             int
	ValidFrom, ValidUntil                        *time.Time
	EmailHash                                    []byte
	CreatedBy                                    uuid.UUID
	CreatedAt, UpdatedAt                         time.Time
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
	GiftPlanKey           string
	GiftDurationDays      int
}

type RedemptionBatchInput struct {
	Name, PlanKey, StackingRule, Status, Note string
	CatalogVersion, DurationDays              int64
	FixedExpiresAt, ValidFrom, ValidUntil     *time.Time
	MaxClaimsPerAccount                       int
}

type RedemptionBatch struct {
	ID                                        uuid.UUID
	Name, PlanKey, StackingRule, Status, Note string
	CatalogVersion, DurationDays              int64
	FixedExpiresAt, ValidFrom, ValidUntil     *time.Time
	MaxClaimsPerAccount                       int
	CreatedBy                                 uuid.UUID
	CreatedAt, UpdatedAt                      time.Time
}

type RedemptionCode struct {
	ID, BatchID          uuid.UUID
	Prefix, Status       string
	MaxUses, UsedCount   int
	CreatedAt, UpdatedAt time.Time
}

type EntitlementGrant struct {
	ID, AccountID        uuid.UUID
	SourceType           string
	SourceID             *uuid.UUID
	PlanKey              string
	CatalogVersion       int64
	StartsAt             time.Time
	EndsAt               *time.Time
	GraceUntil           *time.Time
	ProviderRefHash      []byte
	Status               string
	Metadata             json.RawMessage
	CreatedAt, UpdatedAt time.Time
}

type RedemptionClaim struct {
	ID, CodeID, AccountID, GrantID uuid.UUID
	IdempotencyKey                 string
	ClaimedAt                      time.Time
}

type PaymentChannel struct {
	ID                   uuid.UUID
	Provider, Scene      string
	Enabled              bool
	ActiveVersionID      *uuid.UUID
	HealthStatus         string
	LastWebhookAt        *time.Time
	LastErrorCode        string
	CreatedAt, UpdatedAt time.Time
}

type PaymentChannelVersionInput struct {
	Environment              string
	PublicConfig, SecretRefs json.RawMessage
}

type PaymentChannelVersion struct {
	ID, ChannelID            uuid.UUID
	Version                  int64
	Environment              string
	PublicConfig, SecretRefs json.RawMessage
	Status                   string
	ValidatedAt              *time.Time
	ValidationSummary        string
	CreatedBy                uuid.UUID
	CreatedAt                time.Time
}

type PaymentChannelValidationTrace struct {
	Version int64
	TraceID string
}

type CreateOrderInput struct {
	PlanKey, Provider, Scene, IdempotencyKey string
	ExpiresAt                                time.Time
}

type Order struct {
	ID, AccountID                    uuid.UUID
	OrderNo, PlanKey                 string
	CatalogVersion                   int64
	Provider, PaymentScene           string
	AmountMinor                      int64
	Currency, Status, IdempotencyKey string
	ExpiresAt                        time.Time
	PaidAt, FulfilledAt, ClosedAt    *time.Time
	CreatedAt, UpdatedAt             time.Time
}

type PaymentTransaction struct {
	ID, OrderID         uuid.UUID
	Provider, Status    string
	ProviderTradeNoHash []byte
	AmountMinor         int64
	Currency            string
	PaymentPayload      json.RawMessage
	FailureCode         string
	RequestedAt         time.Time
	PaidAt              *time.Time
	UpdatedAt           time.Time
}

type PaymentNotificationInput struct {
	ID                                uuid.UUID
	Provider                          string
	NotificationIDHash, PayloadDigest [32]byte
	OrderNo                           string
	SignatureValid                    bool
	ProcessingState                   string
	PayloadCiphertext                 []byte
	FailureCode                       string
}

type PaymentNotification struct {
	ID                 uuid.UUID
	Provider           string
	NotificationIDHash []byte
	OrderNo            string
	SignatureValid     bool
	ProcessingState    string
	PayloadDigest      []byte
	FailureCode        string
	ReceivedAt         time.Time
	ProcessedAt        *time.Time
}

type Refund struct {
	ID, OrderID, TransactionID, RequestedBy    uuid.UUID
	RefundNo                                   string
	AmountMinor                                int64
	Reason, Status, FailureCode                string
	ProviderRefHash                            []byte
	Version                                    int64
	ApprovedBy                                 *uuid.UUID
	RequestedAt                                time.Time
	ApprovedAt, ApprovalExpiresAt, CompletedAt *time.Time
	UpdatedAt                                  time.Time
}

type ReconciliationRun struct {
	ID                     uuid.UUID
	Provider               string
	PeriodStart, PeriodEnd time.Time
	Status                 string
	ResultSummary          json.RawMessage
	RequestedBy            *uuid.UUID
	StartedAt, CompletedAt *time.Time
	CreatedAt              time.Time
}

func validWindow(from, until *time.Time) bool {
	return from == nil || until == nil || until.After(*from)
}

func validCodePrefix(s string) bool { return len(s) >= 4 && len(s) <= 16 }

func validProvider(p string) bool { return p == "ALIPAY" || p == "WECHAT_PAY" }

func validScene(s string) bool {
	return s == "NATIVE" || s == "WEB" || s == "H5" || s == "JSAPI"
}

func validJSONDocument(v json.RawMessage, max int) bool {
	if len(v) == 0 || len(v) > max || !json.Valid(v) {
		return false
	}
	trimmed := strings.TrimSpace(string(v))
	return strings.HasPrefix(trimmed, "{") && strings.HasSuffix(trimmed, "}")
}

func normalizePage(limit int) int {
	if limit < 1 || limit > 200 {
		return 50
	}
	return limit
}

const (
	commerceRoleReadSQL  = `SELECT role,status FROM users WHERE id=$1`
	commerceRoleWriteSQL = `SELECT role,status FROM users WHERE id=$1 FOR UPDATE`
)

func commerceRole(ctx context.Context, tx pgx.Tx, actorID uuid.UUID, write, superOnly bool) (Role, error) {
	var role Role
	var status string
	query := commerceRoleReadSQL
	if write {
		query = commerceRoleWriteSQL
	}
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

func commerceTxOptions(write bool) pgx.TxOptions {
	if write {
		return pgx.TxOptions{IsoLevel: pgx.Serializable, AccessMode: pgx.ReadWrite}
	}
	return pgx.TxOptions{IsoLevel: pgx.ReadCommitted, AccessMode: pgx.ReadOnly}
}

func (s *Postgres) commerceTx(ctx context.Context, actorID uuid.UUID, write, superOnly bool) (pgx.Tx, error) {
	tx, err := s.Pool.BeginTx(ctx, commerceTxOptions(write))
	if err != nil {
		return nil, err
	}
	if _, err = commerceRole(ctx, tx, actorID, write, superOnly); err != nil {
		_ = tx.Rollback(ctx)
		return nil, err
	}
	return tx, nil
}

func commerceAudit(ctx context.Context, tx pgx.Tx, actorID uuid.UUID, action, targetType string, targetID uuid.UUID, traceID string, metadata json.RawMessage) error {
	if len(metadata) == 0 {
		metadata = json.RawMessage(`{}`)
	}
	_, err := tx.Exec(ctx, `INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata) VALUES($1,$2,$3,$4,'SUCCEEDED',$5,$6::jsonb)`, actorID, action, targetType, targetID, traceID, metadata)
	return err
}

func (s *Postgres) GetRegistrationPolicy(ctx context.Context) (RegistrationPolicy, error) {
	var p RegistrationPolicy
	err := s.Pool.QueryRow(ctx, `SELECT mode,version,updated_by,updated_at FROM registration_policies WHERE singleton`).Scan(&p.Mode, &p.Version, &p.UpdatedBy, &p.UpdatedAt)
	return p, err
}

func (s *Postgres) UpdateRegistrationPolicy(ctx context.Context, actorID uuid.UUID, mode string, expectedVersion int64, traceID string) (RegistrationPolicy, error) {
	mode = strings.ToUpper(strings.TrimSpace(mode))
	if mode != RegistrationOpen && mode != RegistrationInviteRequired && mode != RegistrationClosed {
		return RegistrationPolicy{}, ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, true)
	if err != nil {
		return RegistrationPolicy{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var p RegistrationPolicy
	err = tx.QueryRow(ctx, `UPDATE registration_policies SET mode=$1,version=version+1,updated_by=$2,updated_at=now() WHERE singleton AND version=$3 RETURNING mode,version,updated_by,updated_at`, mode, actorID, expectedVersion).Scan(&p.Mode, &p.Version, &p.UpdatedBy, &p.UpdatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return RegistrationPolicy{}, ErrConflict
	}
	if err != nil {
		return RegistrationPolicy{}, err
	}
	if err = commerceAudit(ctx, tx, actorID, "registration.policy.update", "registration_policy", uuid.Nil, traceID, json.RawMessage(fmt.Sprintf(`{"mode":%q,"version":%d}`, mode, p.Version))); err != nil {
		return RegistrationPolicy{}, err
	}
	return p, tx.Commit(ctx)
}

func validateInvitationBatch(in InvitationBatchInput) error {
	in.Name = strings.TrimSpace(in.Name)
	in.Status = strings.ToUpper(strings.TrimSpace(in.Status))
	in.EmailDomain = strings.ToLower(strings.TrimSpace(in.EmailDomain))
	if len(in.Name) < 1 || len(in.Name) > 128 || in.DefaultMaxUses < 1 || len(in.Note) > 1000 || !validWindow(in.ValidFrom, in.ValidUntil) {
		return ErrDenied
	}
	if in.Status != "DRAFT" && in.Status != "ACTIVE" && in.Status != "DISABLED" {
		return ErrDenied
	}
	if (strings.TrimSpace(in.GiftPlanKey) == "") != (in.GiftDurationDays == 0) || in.GiftDurationDays < 0 {
		return ErrDenied
	}
	if len(in.EmailDomain) > 253 || strings.Contains(in.EmailDomain, "@") {
		return ErrDenied
	}
	return nil
}

func scanInvitationBatch(row pgx.Row) (InvitationBatch, error) {
	var b InvitationBatch
	var domain, plan *string
	var gift *int
	err := row.Scan(&b.ID, &b.Name, &b.Status, &b.DefaultMaxUses, &b.ValidFrom, &b.ValidUntil, &domain, &b.EmailHash, &plan, &gift, &b.Note, &b.CreatedBy, &b.CreatedAt, &b.UpdatedAt)
	if domain != nil {
		b.EmailDomain = *domain
	}
	if plan != nil {
		b.GiftPlanKey = *plan
	}
	if gift != nil {
		b.GiftDurationDays = *gift
	}
	return b, err
}

func (s *Postgres) CreateInvitationBatch(ctx context.Context, actorID uuid.UUID, in InvitationBatchInput, traceID string) (InvitationBatch, error) {
	if in.Status == "" {
		in.Status = "ACTIVE"
	}
	if in.DefaultMaxUses == 0 {
		in.DefaultMaxUses = 1
	}
	if err := validateInvitationBatch(in); err != nil {
		return InvitationBatch{}, err
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return InvitationBatch{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	id := uuid.New()
	var email any
	if in.EmailHash != nil {
		email = in.EmailHash[:]
	}
	b, err := scanInvitationBatch(tx.QueryRow(ctx, `INSERT INTO invitation_batches(id,name,status,default_max_uses,valid_from,valid_until,email_domain,email_hash,gift_plan_key,gift_duration_days,note,created_by) VALUES($1,$2,$3,$4,$5,$6,NULLIF($7,''),$8,NULLIF($9,''),NULLIF($10,0),$11,$12) RETURNING id,name,status,default_max_uses,valid_from,valid_until,email_domain,email_hash,gift_plan_key,gift_duration_days,note,created_by,created_at,updated_at`, id, strings.TrimSpace(in.Name), strings.ToUpper(in.Status), in.DefaultMaxUses, in.ValidFrom, in.ValidUntil, strings.ToLower(strings.TrimSpace(in.EmailDomain)), email, strings.TrimSpace(in.GiftPlanKey), in.GiftDurationDays, in.Note, actorID))
	if err != nil {
		return InvitationBatch{}, err
	}
	if err = commerceAudit(ctx, tx, actorID, "invitation.batch.create", "invitation_batch", id, traceID, nil); err != nil {
		return InvitationBatch{}, err
	}
	return b, tx.Commit(ctx)
}

func (s *Postgres) AddInvitationCodes(ctx context.Context, actorID, batchID uuid.UUID, codes []CodeDigestRecord, traceID string) ([]InvitationCode, error) {
	if len(codes) < 1 || len(codes) > 10000 {
		return nil, ErrDenied
	}
	seen := map[[32]byte]bool{}
	for _, c := range codes {
		if c.ID == uuid.Nil || c.MaxUses < 1 || !validCodePrefix(c.Prefix) || !validWindow(c.ValidFrom, c.ValidUntil) || seen[c.Digest] {
			return nil, ErrDenied
		}
		seen[c.Digest] = true
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
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
	for _, c := range codes {
		var out InvitationCode
		err = tx.QueryRow(ctx, `INSERT INTO invitation_codes(id,batch_id,code_hash,code_prefix,max_uses,valid_from,valid_until) VALUES($1,$2,$3,$4,$5,$6,$7) RETURNING id,batch_id,code_prefix,status,max_uses,used_count,valid_from,valid_until,created_at,updated_at`, c.ID, batchID, c.Digest[:], c.Prefix, c.MaxUses, c.ValidFrom, c.ValidUntil).Scan(&out.ID, &out.BatchID, &out.Prefix, &out.Status, &out.MaxUses, &out.UsedCount, &out.ValidFrom, &out.ValidUntil, &out.CreatedAt, &out.UpdatedAt)
		if err != nil {
			return nil, ErrConflict
		}
		result = append(result, out)
	}
	if err = commerceAudit(ctx, tx, actorID, "invitation.codes.create", "invitation_batch", batchID, traceID, json.RawMessage(fmt.Sprintf(`{"count":%d}`, len(codes)))); err != nil {
		return nil, err
	}
	return result, tx.Commit(ctx)
}

func (s *Postgres) ListInvitationBatches(ctx context.Context, actorID uuid.UUID, limit int) ([]InvitationBatch, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,name,status,default_max_uses,valid_from,valid_until,email_domain,email_hash,gift_plan_key,gift_duration_days,note,created_by,created_at,updated_at FROM invitation_batches ORDER BY created_at DESC,id DESC LIMIT $1`, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []InvitationBatch
	for rows.Next() {
		b, e := scanInvitationBatch(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, b)
	}
	return out, rows.Err()
}

func (s *Postgres) ListInvitationCodes(ctx context.Context, actorID, batchID uuid.UUID, limit int) ([]InvitationCode, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,batch_id,code_prefix,status,max_uses,used_count,valid_from,valid_until,created_at,updated_at FROM invitation_codes WHERE batch_id=$1 ORDER BY created_at DESC,id DESC LIMIT $2`, batchID, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []InvitationCode
	for rows.Next() {
		var c InvitationCode
		if err = rows.Scan(&c.ID, &c.BatchID, &c.Prefix, &c.Status, &c.MaxUses, &c.UsedCount, &c.ValidFrom, &c.ValidUntil, &c.CreatedAt, &c.UpdatedAt); err != nil {
			return nil, err
		}
		out = append(out, c)
	}
	return out, rows.Err()
}

func (s *Postgres) SetInvitationBatchStatus(ctx context.Context, actorID, batchID uuid.UUID, status, traceID string) error {
	status = strings.ToUpper(strings.TrimSpace(status))
	if status != "DRAFT" && status != "ACTIVE" && status != "DISABLED" {
		return ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	cmd, err := tx.Exec(ctx, `UPDATE invitation_batches SET status=$2,updated_at=now() WHERE id=$1`, batchID, status)
	if err != nil {
		return err
	}
	if cmd.RowsAffected() != 1 {
		return ErrNotFound
	}
	if err = commerceAudit(ctx, tx, actorID, "invitation.batch.status", "invitation_batch", batchID, traceID, json.RawMessage(fmt.Sprintf(`{"status":%q}`, status))); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) SetInvitationCodeStatus(ctx context.Context, actorID, codeID uuid.UUID, status, traceID string) error {
	status = strings.ToUpper(strings.TrimSpace(status))
	if status != "ACTIVE" && status != "DISABLED" {
		return ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	cmd, err := tx.Exec(ctx, `UPDATE invitation_codes SET status=$2,updated_at=now() WHERE id=$1`, codeID, status)
	if err != nil {
		return err
	}
	if cmd.RowsAffected() != 1 {
		return ErrNotFound
	}
	if err = commerceAudit(ctx, tx, actorID, "invitation.code.status", "invitation_code", codeID, traceID, json.RawMessage(fmt.Sprintf(`{"status":%q}`, status))); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

// ReserveInvitation consumes stock before the external identity provider is
// called. ReleaseInvitation compensates a failed provider call. The attempt ID
// makes retries idempotent.
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
	var old InvitationReservation
	err = tx.QueryRow(ctx, `SELECT r.id,r.code_id,r.registration_attempt_id,r.state,r.reserved_until,coalesce(b.gift_plan_key,''),coalesce(b.gift_duration_days,0) FROM invitation_redemptions r JOIN invitation_codes c ON c.id=r.code_id JOIN invitation_batches b ON b.id=c.batch_id WHERE r.registration_attempt_id=$1 FOR UPDATE OF r`, attemptID).Scan(&old.ID, &old.CodeID, &old.AttemptID, &old.State, &old.ReservedUntil, &old.GiftPlanKey, &old.GiftDurationDays)
	if err == nil {
		if old.State != "RELEASED" {
			if e := tx.Commit(ctx); e != nil {
				return InvitationReservation{}, e
			}
			return old, nil
		}
		return InvitationReservation{}, ErrInvitationInvalid
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return InvitationReservation{}, err
	}
	// Do not reclaim an expired reservation opportunistically here. There is a
	// narrow but real crash window after the identity provider creates the user
	// and before the caller records PROVIDER_ACCEPTED. Releasing that row during
	// another registration would allow the same invitation inventory to be
	// consumed twice. Explicit provider rejection uses ReleaseInvitation;
	// ambiguous expired rows are left for the reconciliation job/admin review.
	var codeID uuid.UUID
	var status, bstatus, domain, gift string
	var max, used, giftDays int
	var cf, cu, bf, bu *time.Time
	var targetEmail []byte
	err = tx.QueryRow(ctx, `SELECT c.id,c.status,c.max_uses,c.used_count,c.valid_from,c.valid_until,b.status,b.valid_from,b.valid_until,coalesce(b.email_domain,''),b.email_hash,coalesce(b.gift_plan_key,''),coalesce(b.gift_duration_days,0) FROM invitation_codes c JOIN invitation_batches b ON b.id=c.batch_id WHERE c.code_hash=$1 FOR UPDATE OF c,b`, codeHash[:]).Scan(&codeID, &status, &max, &used, &cf, &cu, &bstatus, &bf, &bu, &domain, &targetEmail, &gift, &giftDays)
	if errors.Is(err, pgx.ErrNoRows) {
		return InvitationReservation{}, ErrInvitationInvalid
	}
	if err != nil {
		return InvitationReservation{}, err
	}
	now := time.Now().UTC()
	if status != "ACTIVE" || bstatus != "ACTIVE" {
		return InvitationReservation{}, ErrInvitationInvalid
	}
	if (cf != nil && now.Before(*cf)) || (bf != nil && now.Before(*bf)) || (cu != nil && !now.Before(*cu)) || (bu != nil && !now.Before(*bu)) {
		return InvitationReservation{}, ErrInvitationExpired
	}
	if used >= max {
		return InvitationReservation{}, ErrInvitationExhausted
	}
	if domain != "" && strings.ToLower(strings.TrimSpace(emailDomain)) != strings.ToLower(domain) {
		return InvitationReservation{}, ErrInvitationEmailMismatch
	}
	if len(targetEmail) == 32 && !bytesEqual(targetEmail, emailHash[:]) {
		return InvitationReservation{}, ErrInvitationEmailMismatch
	}
	r := InvitationReservation{ID: uuid.New(), CodeID: codeID, AttemptID: attemptID, State: "RESERVED", ReservedUntil: now.Add(ttl), GiftPlanKey: gift, GiftDurationDays: giftDays}
	if _, err = tx.Exec(ctx, `INSERT INTO invitation_redemptions(id,code_id,registration_attempt_id,email_hash,state,reserved_until)VALUES($1,$2,$3,$4,'RESERVED',$5)`, r.ID, codeID, attemptID, emailHash[:], r.ReservedUntil); err != nil {
		return InvitationReservation{}, err
	}
	if _, err = tx.Exec(ctx, `UPDATE invitation_codes SET used_count=used_count+1,updated_at=now() WHERE id=$1`, codeID); err != nil {
		return InvitationReservation{}, err
	}
	return r, tx.Commit(ctx)
}

// RecordInvitationRegistrationProfile stores only the bounded display fields
// needed while an invited identity is waiting for its first OIDC login.
func (s *Postgres) RecordInvitationRegistrationProfile(ctx context.Context, attemptID uuid.UUID, profile AccountIdentityProfile) error {
	profile = normalizeIdentityProfileProjection(profile)
	if attemptID == uuid.Nil || profile.empty() {
		return ErrInvitationInvalid
	}
	command, err := s.Pool.Exec(ctx, `
		INSERT INTO invitation_registration_profiles(redemption_id,username,nickname,email_masked)
		SELECT id,$2,$3,$4 FROM invitation_redemptions
		WHERE registration_attempt_id=$1 AND state IN ('RESERVED','PROVIDER_ACCEPTED')
		ON CONFLICT(redemption_id) DO UPDATE SET
			username=excluded.username,nickname=excluded.nickname,
			email_masked=excluded.email_masked,updated_at=now()`,
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
	cmd, err := s.Pool.Exec(ctx, `UPDATE invitation_redemptions SET state='PROVIDER_ACCEPTED',provider_accepted_at=coalesce(provider_accepted_at,now()),updated_at=now() WHERE registration_attempt_id=$1 AND state IN ('RESERVED','PROVIDER_ACCEPTED') AND (state='PROVIDER_ACCEPTED' OR reserved_until>now())`, attemptID)
	if err != nil {
		return err
	}
	if cmd.RowsAffected() != 1 {
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

func (s *Postgres) ConfirmInvitation(ctx context.Context, attemptID, userID uuid.UUID) (Entitlement, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return Entitlement{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var redemptionID uuid.UUID
	var state, gift string
	var redeemedUser *uuid.UUID
	var days int
	err = tx.QueryRow(ctx, `SELECT r.id,r.state,r.user_id,coalesce(b.gift_plan_key,''),coalesce(b.gift_duration_days,0) FROM invitation_redemptions r JOIN invitation_codes c ON c.id=r.code_id JOIN invitation_batches b ON b.id=c.batch_id WHERE r.registration_attempt_id=$1 FOR UPDATE OF r`, attemptID).Scan(&redemptionID, &state, &redeemedUser, &gift, &days)
	if errors.Is(err, pgx.ErrNoRows) {
		return Entitlement{}, ErrInvitationInvalid
	}
	if err != nil {
		return Entitlement{}, err
	}
	if state == "RELEASED" || state == "RESERVED" {
		return Entitlement{}, ErrInvitationInvalid
	}
	if state == "REDEEMED" && (redeemedUser == nil || *redeemedUser != userID) {
		return Entitlement{}, ErrConflict
	}
	if state != "REDEEMED" {
		if _, err = tx.Exec(ctx, `UPDATE invitation_redemptions SET state='REDEEMED',user_id=$2,redeemed_at=now(),updated_at=now() WHERE registration_attempt_id=$1`, attemptID, userID); err != nil {
			return Entitlement{}, err
		}
		if gift != "" {
			var cv int64
			err = tx.QueryRow(ctx, `SELECT c.version FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.status='PUBLISHED' AND p.plan_key=$1`, gift).Scan(&cv)
			if err != nil {
				return Entitlement{}, ErrRedemptionNotApplicable
			}
			now := time.Now().UTC()
			_, err = tx.Exec(ctx, `INSERT INTO entitlement_grants(id,account_id,source_type,source_id,plan_key,catalog_version,starts_at,ends_at,status,metadata) VALUES($1,$2,'MANUAL',$3,$4,$5,$6,$7,'ACTIVE',jsonb_build_object('invitation',true)) ON CONFLICT DO NOTHING`, uuid.New(), userID, redemptionID, gift, cv, now, now.Add(time.Duration(days)*24*time.Hour))
			if err != nil {
				return Entitlement{}, err
			}
		}
	}
	if _, err = tx.Exec(ctx, `DELETE FROM invitation_registration_profiles WHERE redemption_id=$1`, redemptionID); err != nil {
		return Entitlement{}, err
	}
	e, err := recomputeEntitlement(ctx, tx, userID)
	if err != nil {
		return Entitlement{}, err
	}
	return e, tx.Commit(ctx)
}

// ConfirmPendingInvitationForEmail links an invitation reservation after the
// browser returns from OIDC. The web callback knows the authenticated account
// and a keyed e-mail digest, but does not need to retain or expose the
// registration attempt identifier. Only a provider-accepted reservation can
// be consumed. A serializable candidate-count check rejects ambiguous e-mail
// matches, then FOR UPDATE serializes callbacks for the sole candidate.
const (
	pendingInvitationCandidateCountSQL = `
		SELECT count(*)
		FROM invitation_redemptions
		WHERE email_hash=$1 AND state='PROVIDER_ACCEPTED' AND user_id IS NULL`
	pendingInvitationCandidateLockSQL = `
		SELECT r.id,coalesce(b.gift_plan_key,''),coalesce(b.gift_duration_days,0)
		FROM invitation_redemptions r
		JOIN invitation_codes c ON c.id=r.code_id
		JOIN invitation_batches b ON b.id=c.batch_id
		WHERE r.email_hash=$1 AND r.state='PROVIDER_ACCEPTED' AND r.user_id IS NULL
		FOR UPDATE OF r`
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

func (s *Postgres) ConfirmPendingInvitationForEmail(ctx context.Context, userID uuid.UUID, emailHash [32]byte) (Entitlement, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return Entitlement{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	var redemptionID uuid.UUID
	var gift string
	var days int
	var candidates int
	err = tx.QueryRow(ctx, pendingInvitationCandidateCountSQL, emailHash[:]).Scan(&candidates)
	if err != nil {
		return Entitlement{}, err
	}
	if err = validatePendingInvitationCandidateCount(candidates); err != nil {
		return Entitlement{}, err
	}
	err = tx.QueryRow(ctx, pendingInvitationCandidateLockSQL, emailHash[:]).Scan(&redemptionID, &gift, &days)
	if errors.Is(err, pgx.ErrNoRows) {
		return Entitlement{}, ErrNotFound
	}
	if err != nil {
		return Entitlement{}, err
	}
	command, err := tx.Exec(ctx, `
		UPDATE invitation_redemptions
		SET state='REDEEMED',user_id=$2,redeemed_at=now(),updated_at=now()
		WHERE id=$1 AND state='PROVIDER_ACCEPTED' AND user_id IS NULL`, redemptionID, userID)
	if err != nil {
		return Entitlement{}, err
	}
	if command.RowsAffected() != 1 {
		return Entitlement{}, ErrConflict
	}
	if _, err = tx.Exec(ctx, `DELETE FROM invitation_registration_profiles WHERE redemption_id=$1`, redemptionID); err != nil {
		return Entitlement{}, err
	}
	if gift != "" {
		var catalogVersion int64
		err = tx.QueryRow(ctx, `
			SELECT c.version FROM plans p
			JOIN catalog_versions c ON c.id=p.catalog_version_id
			WHERE c.status='PUBLISHED' AND p.plan_key=$1`, gift).Scan(&catalogVersion)
		if errors.Is(err, pgx.ErrNoRows) {
			return Entitlement{}, ErrRedemptionNotApplicable
		}
		if err != nil {
			return Entitlement{}, err
		}
		now := time.Now().UTC()
		if _, err = tx.Exec(ctx, `
			INSERT INTO entitlement_grants(
				id,account_id,source_type,source_id,plan_key,catalog_version,
				starts_at,ends_at,status,metadata
			) VALUES($1,$2,'MANUAL',$3,$4,$5,$6,$7,'ACTIVE',jsonb_build_object('invitation',true))
			ON CONFLICT DO NOTHING`, uuid.New(), userID, redemptionID, gift, catalogVersion,
			now, now.Add(time.Duration(days)*24*time.Hour)); err != nil {
			return Entitlement{}, err
		}
	}
	entitlement, err := recomputeEntitlement(ctx, tx, userID)
	if err != nil {
		return Entitlement{}, err
	}
	return entitlement, tx.Commit(ctx)
}

func validateRedemptionBatch(in RedemptionBatchInput) error {
	in.Name = strings.TrimSpace(in.Name)
	in.PlanKey = strings.TrimSpace(in.PlanKey)
	in.Status = strings.ToUpper(strings.TrimSpace(in.Status))
	in.StackingRule = strings.ToUpper(strings.TrimSpace(in.StackingRule))
	if len(in.Name) < 1 || len(in.Name) > 128 || len(in.PlanKey) < 1 || len(in.PlanKey) > 64 || in.CatalogVersion < 1 || in.MaxClaimsPerAccount < 1 || len(in.Note) > 1000 || !validWindow(in.ValidFrom, in.ValidUntil) {
		return ErrDenied
	}
	if (in.DurationDays > 0) == (in.FixedExpiresAt != nil) || in.DurationDays < 0 {
		return ErrDenied
	}
	if in.Status != "DRAFT" && in.Status != "ACTIVE" && in.Status != "DISABLED" {
		return ErrDenied
	}
	if in.StackingRule != "EXTEND_SAME_OR_UPGRADE" && in.StackingRule != "REJECT_CONFLICT" {
		return ErrDenied
	}
	return nil
}

func scanRedemptionBatch(row pgx.Row) (RedemptionBatch, error) {
	var b RedemptionBatch
	var d *int64
	err := row.Scan(&b.ID, &b.Name, &b.PlanKey, &b.CatalogVersion, &d, &b.FixedExpiresAt, &b.MaxClaimsPerAccount, &b.StackingRule, &b.Status, &b.ValidFrom, &b.ValidUntil, &b.Note, &b.CreatedBy, &b.CreatedAt, &b.UpdatedAt)
	if d != nil {
		b.DurationDays = *d
	}
	return b, err
}

func (s *Postgres) CreateRedemptionBatch(ctx context.Context, actorID uuid.UUID, in RedemptionBatchInput, traceID string) (RedemptionBatch, error) {
	if in.Status == "" {
		in.Status = "ACTIVE"
	}
	if in.StackingRule == "" {
		in.StackingRule = "EXTEND_SAME_OR_UPGRADE"
	}
	if err := validateRedemptionBatch(in); err != nil {
		return RedemptionBatch{}, err
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return RedemptionBatch{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var exists bool
	if err = tx.QueryRow(ctx, `SELECT true FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.version=$1 AND p.plan_key=$2`, in.CatalogVersion, in.PlanKey).Scan(&exists); errors.Is(err, pgx.ErrNoRows) {
		return RedemptionBatch{}, ErrRedemptionNotApplicable
	} else if err != nil {
		return RedemptionBatch{}, err
	}
	id := uuid.New()
	b, err := scanRedemptionBatch(tx.QueryRow(ctx, `INSERT INTO redemption_batches(id,name,plan_key,catalog_version,duration_days,fixed_expires_at,max_claims_per_account,stacking_rule,status,valid_from,valid_until,note,created_by)VALUES($1,$2,$3,$4,NULLIF($5,0),$6,$7,$8,$9,$10,$11,$12,$13)RETURNING id,name,plan_key,catalog_version,duration_days,fixed_expires_at,max_claims_per_account,stacking_rule,status,valid_from,valid_until,note,created_by,created_at,updated_at`, id, strings.TrimSpace(in.Name), strings.TrimSpace(in.PlanKey), in.CatalogVersion, in.DurationDays, in.FixedExpiresAt, in.MaxClaimsPerAccount, strings.ToUpper(in.StackingRule), strings.ToUpper(in.Status), in.ValidFrom, in.ValidUntil, in.Note, actorID))
	if err != nil {
		return RedemptionBatch{}, err
	}
	if err = commerceAudit(ctx, tx, actorID, "redemption.batch.create", "redemption_batch", id, traceID, nil); err != nil {
		return RedemptionBatch{}, err
	}
	return b, tx.Commit(ctx)
}

func (s *Postgres) AddRedemptionCodes(ctx context.Context, actorID, batchID uuid.UUID, codes []CodeDigestRecord, traceID string) ([]RedemptionCode, error) {
	if len(codes) < 1 || len(codes) > 10000 {
		return nil, ErrDenied
	}
	seen := map[[32]byte]bool{}
	for _, c := range codes {
		if c.ID == uuid.Nil || c.MaxUses < 1 || !validCodePrefix(c.Prefix) || seen[c.Digest] {
			return nil, ErrDenied
		}
		seen[c.Digest] = true
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var exists bool
	if err = tx.QueryRow(ctx, `SELECT true FROM redemption_batches WHERE id=$1 FOR UPDATE`, batchID).Scan(&exists); errors.Is(err, pgx.ErrNoRows) {
		return nil, ErrNotFound
	} else if err != nil {
		return nil, err
	}
	out := make([]RedemptionCode, 0, len(codes))
	for _, c := range codes {
		var x RedemptionCode
		err = tx.QueryRow(ctx, `INSERT INTO redemption_codes(id,batch_id,code_hash,code_prefix,max_uses)VALUES($1,$2,$3,$4,$5)RETURNING id,batch_id,code_prefix,status,max_uses,used_count,created_at,updated_at`, c.ID, batchID, c.Digest[:], c.Prefix, c.MaxUses).Scan(&x.ID, &x.BatchID, &x.Prefix, &x.Status, &x.MaxUses, &x.UsedCount, &x.CreatedAt, &x.UpdatedAt)
		if err != nil {
			return nil, ErrConflict
		}
		out = append(out, x)
	}
	if err = commerceAudit(ctx, tx, actorID, "redemption.codes.create", "redemption_batch", batchID, traceID, json.RawMessage(fmt.Sprintf(`{"count":%d}`, len(codes)))); err != nil {
		return nil, err
	}
	return out, tx.Commit(ctx)
}

func (s *Postgres) ListRedemptionBatches(ctx context.Context, actorID uuid.UUID, limit int) ([]RedemptionBatch, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,name,plan_key,catalog_version,duration_days,fixed_expires_at,max_claims_per_account,stacking_rule,status,valid_from,valid_until,note,created_by,created_at,updated_at FROM redemption_batches ORDER BY created_at DESC,id DESC LIMIT $1`, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []RedemptionBatch
	for rows.Next() {
		b, e := scanRedemptionBatch(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, b)
	}
	return out, rows.Err()
}

func (s *Postgres) ListRedemptionCodes(ctx context.Context, actorID, batchID uuid.UUID, limit int) ([]RedemptionCode, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,batch_id,code_prefix,status,max_uses,used_count,created_at,updated_at FROM redemption_codes WHERE batch_id=$1 ORDER BY created_at DESC,id DESC LIMIT $2`, batchID, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []RedemptionCode
	for rows.Next() {
		var x RedemptionCode
		if err = rows.Scan(&x.ID, &x.BatchID, &x.Prefix, &x.Status, &x.MaxUses, &x.UsedCount, &x.CreatedAt, &x.UpdatedAt); err != nil {
			return nil, err
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (s *Postgres) SetRedemptionBatchStatus(ctx context.Context, actorID, batchID uuid.UUID, status, traceID string) error {
	status = strings.ToUpper(strings.TrimSpace(status))
	if status != "DRAFT" && status != "ACTIVE" && status != "DISABLED" {
		return ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	cmd, err := tx.Exec(ctx, `UPDATE redemption_batches SET status=$2,updated_at=now() WHERE id=$1`, batchID, status)
	if err != nil {
		return err
	}
	if cmd.RowsAffected() != 1 {
		return ErrNotFound
	}
	if err = commerceAudit(ctx, tx, actorID, "redemption.batch.status", "redemption_batch", batchID, traceID, json.RawMessage(fmt.Sprintf(`{"status":%q}`, status))); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (s *Postgres) SetRedemptionCodeStatus(ctx context.Context, actorID, codeID uuid.UUID, status, traceID string) error {
	status = strings.ToUpper(strings.TrimSpace(status))
	if status != "ACTIVE" && status != "DISABLED" {
		return ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	cmd, err := tx.Exec(ctx, `UPDATE redemption_codes SET status=$2,updated_at=now() WHERE id=$1`, codeID, status)
	if err != nil {
		return err
	}
	if cmd.RowsAffected() != 1 {
		return ErrNotFound
	}
	if err = commerceAudit(ctx, tx, actorID, "redemption.code.status", "redemption_code", codeID, traceID, json.RawMessage(fmt.Sprintf(`{"status":%q}`, status))); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func computeGrantEnd(now time.Time, durationDays int64, fixed, currentEnd *time.Time, samePlan bool) (*time.Time, error) {
	if fixed != nil {
		if !fixed.After(now) {
			return nil, ErrRedemptionExpired
		}
		v := fixed.UTC()
		return &v, nil
	}
	if durationDays < 1 {
		return nil, ErrRedemptionInvalid
	}
	start := now
	if samePlan && currentEnd != nil && currentEnd.After(start) {
		start = *currentEnd
	}
	v := start.Add(time.Duration(durationDays) * 24 * time.Hour)
	return &v, nil
}

func (s *Postgres) ClaimRedemption(ctx context.Context, accountID uuid.UUID, codeHash [32]byte, idempotencyKey string) (RedemptionClaim, Entitlement, error) {
	if len(idempotencyKey) < 8 || len(idempotencyKey) > 128 {
		return RedemptionClaim{}, Entitlement{}, ErrRedemptionInvalid
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return RedemptionClaim{}, Entitlement{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var active bool
	if err = tx.QueryRow(ctx, `SELECT status='ACTIVE' FROM users WHERE id=$1 FOR UPDATE`, accountID).Scan(&active); err != nil || !active {
		return RedemptionClaim{}, Entitlement{}, ErrDenied
	}
	var prior RedemptionClaim
	var priorCodeHash []byte
	err = tx.QueryRow(ctx, `SELECT claim.id,claim.code_id,claim.account_id,claim.idempotency_key,claim.grant_id,claim.claimed_at,code.code_hash FROM redemption_claims claim JOIN redemption_codes code ON code.id=claim.code_id WHERE claim.account_id=$1 AND claim.idempotency_key=$2`, accountID, idempotencyKey).Scan(&prior.ID, &prior.CodeID, &prior.AccountID, &prior.IdempotencyKey, &prior.GrantID, &prior.ClaimedAt, &priorCodeHash)
	if err == nil {
		if !bytesEqual(priorCodeHash, codeHash[:]) {
			return RedemptionClaim{}, Entitlement{}, ErrConflict
		}
		e, x := recomputeEntitlement(ctx, tx, accountID)
		if x != nil {
			return RedemptionClaim{}, Entitlement{}, x
		}
		return prior, e, tx.Commit(ctx)
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return RedemptionClaim{}, Entitlement{}, err
	}
	var codeID uuid.UUID
	var codeStatus, batchStatus, plan, rule string
	var max, used, per int
	var version, duration int64
	var fixed, from, until *time.Time
	var targetPrice int64
	err = tx.QueryRow(ctx, `SELECT c.id,c.status,c.max_uses,c.used_count,b.status,b.plan_key,b.catalog_version,coalesce(b.duration_days,0),b.fixed_expires_at,b.max_claims_per_account,b.stacking_rule,b.valid_from,b.valid_until,p.price_minor FROM redemption_codes c JOIN redemption_batches b ON b.id=c.batch_id JOIN catalog_versions cv ON cv.version=b.catalog_version JOIN plans p ON p.catalog_version_id=cv.id AND p.plan_key=b.plan_key WHERE c.code_hash=$1 FOR UPDATE OF c,b`, codeHash[:]).Scan(&codeID, &codeStatus, &max, &used, &batchStatus, &plan, &version, &duration, &fixed, &per, &rule, &from, &until, &targetPrice)
	if errors.Is(err, pgx.ErrNoRows) {
		return RedemptionClaim{}, Entitlement{}, ErrRedemptionInvalid
	}
	if err != nil {
		return RedemptionClaim{}, Entitlement{}, err
	}
	now := time.Now().UTC()
	if codeStatus != "ACTIVE" || batchStatus != "ACTIVE" {
		return RedemptionClaim{}, Entitlement{}, ErrRedemptionInvalid
	}
	if (from != nil && now.Before(*from)) || (until != nil && !now.Before(*until)) {
		return RedemptionClaim{}, Entitlement{}, ErrRedemptionExpired
	}
	if used >= max {
		return RedemptionClaim{}, Entitlement{}, ErrRedemptionExhausted
	}
	var claimed int
	if err = tx.QueryRow(ctx, `SELECT count(*) FROM redemption_claims WHERE code_id=$1 AND account_id=$2`, codeID, accountID).Scan(&claimed); err != nil {
		return RedemptionClaim{}, Entitlement{}, err
	}
	if claimed >= per {
		return RedemptionClaim{}, Entitlement{}, ErrRedemptionAlreadyUsed
	}
	var currentPlan string
	var currentPrice int64
	var currentEnd *time.Time
	err = tx.QueryRow(ctx, `SELECT g.plan_key,p.price_minor,g.ends_at FROM entitlement_grants g JOIN catalog_versions cv ON cv.version=g.catalog_version JOIN plans p ON p.catalog_version_id=cv.id AND p.plan_key=g.plan_key WHERE g.account_id=$1 AND g.status='ACTIVE' AND g.starts_at<=now() AND (g.ends_at IS NULL OR g.ends_at>now()) ORDER BY p.price_minor DESC,g.ends_at DESC NULLS FIRST,g.created_at DESC LIMIT 1`, accountID).Scan(&currentPlan, &currentPrice, &currentEnd)
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return RedemptionClaim{}, Entitlement{}, err
	}
	same := err == nil && currentPlan == plan
	if err == nil && targetPrice < currentPrice {
		return RedemptionClaim{}, Entitlement{}, ErrRedemptionPlanConflict
	}
	if err == nil && rule == "REJECT_CONFLICT" && !same {
		return RedemptionClaim{}, Entitlement{}, ErrRedemptionPlanConflict
	}
	end, err := computeGrantEnd(now, duration, fixed, currentEnd, same)
	if err != nil {
		return RedemptionClaim{}, Entitlement{}, err
	}
	claim := RedemptionClaim{ID: uuid.New(), CodeID: codeID, AccountID: accountID, GrantID: uuid.New(), IdempotencyKey: idempotencyKey, ClaimedAt: now}
	if _, err = tx.Exec(ctx, `INSERT INTO entitlement_grants(id,account_id,source_type,source_id,plan_key,catalog_version,starts_at,ends_at,status,metadata)VALUES($1,$2,'REDEMPTION',$3,$4,$5,$6,$7,'ACTIVE',jsonb_build_object('code_id',$8::uuid))`, claim.GrantID, accountID, claim.ID, plan, version, now, end, codeID); err != nil {
		return RedemptionClaim{}, Entitlement{}, err
	}
	if _, err = tx.Exec(ctx, `INSERT INTO redemption_claims(id,code_id,account_id,idempotency_key,grant_id,claimed_at)VALUES($1,$2,$3,$4,$5,$6)`, claim.ID, codeID, accountID, idempotencyKey, claim.GrantID, now); err != nil {
		return RedemptionClaim{}, Entitlement{}, ErrConflict
	}
	if _, err = tx.Exec(ctx, `UPDATE redemption_codes SET used_count=used_count+1,updated_at=now() WHERE id=$1`, codeID); err != nil {
		return RedemptionClaim{}, Entitlement{}, err
	}
	e, err := recomputeEntitlement(ctx, tx, accountID)
	if err != nil {
		return RedemptionClaim{}, Entitlement{}, err
	}
	return claim, e, tx.Commit(ctx)
}

// recomputeEntitlement materializes the highest-priced active grant. Equal
// plans prefer the longest remaining grant. This preserves an underlying paid
// grant when a temporary promotional grant expires.
func entitlementStateEqual(left, right Entitlement) bool {
	return left.PlanKey == right.PlanKey &&
		left.CatalogVersion == right.CatalogVersion &&
		left.MaxRemotePCs == right.MaxRemotePCs &&
		left.MaxLiveSessions == right.MaxLiveSessions &&
		bytesEqual(left.FeatureFlags, right.FeatureFlags) &&
		left.EffectiveAt.Equal(right.EffectiveAt) &&
		sameTime(left.ExpiresAt, right.ExpiresAt) &&
		sameTime(left.GraceUntil, right.GraceUntil) &&
		left.Source == right.Source &&
		bytesEqual(left.ProviderRefHash, right.ProviderRefHash)
}

func entitlementStateEqualExceptEffective(left, right Entitlement) bool {
	right.EffectiveAt = left.EffectiveAt
	return entitlementStateEqual(left, right)
}

func entitlementForAPI(value Entitlement) Entitlement {
	value.MaxRemotePCs = UnlimitedQuota
	value.MaxLiveSessions = UnlimitedQuota
	return value
}

func recomputeEntitlement(ctx context.Context, tx pgx.Tx, accountID uuid.UUID) (Entitlement, error) {
	return recomputeEntitlementWithGrantExpiry(ctx, tx, accountID, true)
}

// recomputeEntitlementFromMaterializedGrants only rebuilds the account's
// materialized entitlement. Maintenance callers use it after expiring an
// explicitly bounded set of grants so a single cleanup batch cannot mutate an
// unbounded number of additional source rows.
func recomputeEntitlementFromMaterializedGrants(ctx context.Context, tx pgx.Tx, accountID uuid.UUID) (Entitlement, error) {
	return recomputeEntitlementWithGrantExpiry(ctx, tx, accountID, false)
}

func recomputeEntitlementWithGrantExpiry(ctx context.Context, tx pgx.Tx, accountID uuid.UUID, expireSources bool) (Entitlement, error) {
	if expireSources {
		if _, err := tx.Exec(ctx, `UPDATE entitlement_grants SET status='EXPIRED',updated_at=now() WHERE account_id=$1 AND status='ACTIVE' AND ends_at<=now()`, accountID); err != nil {
			return Entitlement{}, err
		}
	}
	var e Entitlement
	var grantID uuid.UUID
	err := tx.QueryRow(ctx, `SELECT g.id,g.plan_key,g.catalog_version,p.max_remote_pcs,p.max_live_sessions,p.feature_flags,g.starts_at,g.ends_at,g.grace_until,g.source_type,g.provider_reference_hash FROM entitlement_grants g JOIN catalog_versions cv ON cv.version=g.catalog_version JOIN plans p ON p.catalog_version_id=cv.id AND p.plan_key=g.plan_key WHERE g.account_id=$1 AND g.status='ACTIVE' AND g.starts_at<=now() AND (g.ends_at IS NULL OR g.ends_at>now()) ORDER BY p.price_minor DESC,p.sort_order DESC,(g.source_type='DEFAULT') ASC,g.ends_at DESC NULLS FIRST,g.created_at DESC LIMIT 1 FOR UPDATE OF g`, accountID).Scan(&grantID, &e.PlanKey, &e.CatalogVersion, &e.MaxRemotePCs, &e.MaxLiveSessions, &e.FeatureFlags, &e.EffectiveAt, &e.ExpiresAt, &e.GraceUntil, &e.Source, &e.ProviderRefHash)
	hasGrant := err == nil
	if errors.Is(err, pgx.ErrNoRows) {
		err = tx.QueryRow(ctx, `SELECT p.plan_key,c.version,p.max_remote_pcs,p.max_live_sessions,p.feature_flags FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.status='PUBLISHED' AND p.plan_key='free'`).Scan(&e.PlanKey, &e.CatalogVersion, &e.MaxRemotePCs, &e.MaxLiveSessions, &e.FeatureFlags)
		if err != nil {
			return Entitlement{}, err
		}
		e.EffectiveAt = time.Now().UTC()
		e.ExpiresAt = nil
		e.Source = "DEFAULT"
	}
	if err != nil {
		return Entitlement{}, err
	}
	var current Entitlement
	err = tx.QueryRow(ctx, `SELECT plan_key,catalog_version,max_remote_pcs,max_live_sessions,feature_flags,version,effective_at,expires_at,grace_until,source,provider_reference_hash FROM entitlements WHERE account_id=$1 FOR UPDATE`, accountID).Scan(
		&current.PlanKey, &current.CatalogVersion, &current.MaxRemotePCs, &current.MaxLiveSessions,
		&current.FeatureFlags, &current.Version, &current.EffectiveAt, &current.ExpiresAt,
		&current.GraceUntil, &current.Source, &current.ProviderRefHash,
	)
	hasCurrent := err == nil
	if errors.Is(err, pgx.ErrNoRows) {
		current = Entitlement{}
	} else if err != nil {
		return Entitlement{}, err
	}
	// The default/free state has no grant timestamp. Preserve effective_at when
	// every other materialized field is unchanged so maintenance retries remain
	// exact no-ops rather than manufacturing a new effective state.
	if hasCurrent && !hasGrant && entitlementStateEqualExceptEffective(current, e) {
		e.EffectiveAt = current.EffectiveAt
	}
	if hasCurrent && entitlementStateEqual(current, e) {
		return entitlementForAPI(current), nil
	}
	e.Version = current.Version + 1
	_, err = tx.Exec(ctx, `INSERT INTO entitlements(account_id,plan_key,catalog_version,max_remote_pcs,max_live_sessions,feature_flags,version,effective_at,expires_at,grace_until,source,provider_reference_hash)VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) ON CONFLICT(account_id)DO UPDATE SET plan_key=excluded.plan_key,catalog_version=excluded.catalog_version,max_remote_pcs=excluded.max_remote_pcs,max_live_sessions=excluded.max_live_sessions,feature_flags=excluded.feature_flags,version=excluded.version,effective_at=excluded.effective_at,expires_at=excluded.expires_at,grace_until=excluded.grace_until,source=excluded.source,provider_reference_hash=excluded.provider_reference_hash,updated_at=now()`, accountID, e.PlanKey, e.CatalogVersion, e.MaxRemotePCs, e.MaxLiveSessions, e.FeatureFlags, e.Version, e.EffectiveAt, e.ExpiresAt, e.GraceUntil, e.Source, e.ProviderRefHash)
	if err != nil {
		return Entitlement{}, err
	}
	_, err = tx.Exec(ctx, `INSERT INTO outbox(event_type,aggregate_id,payload)VALUES('entitlement.changed',$1,jsonb_build_object('account_id',$1::uuid::text,'plan_key',$2::text,'catalog_version',$3::bigint,'version',$4::bigint,'source',$5::text))`, accountID, e.PlanKey, e.CatalogVersion, e.Version, e.Source)
	return entitlementForAPI(e), err
}

func validatePaymentChannelVersionInput(in PaymentChannelVersionInput) error {
	in.Environment = strings.ToUpper(strings.TrimSpace(in.Environment))
	if in.Environment != "SANDBOX" && in.Environment != "PRODUCTION" {
		return ErrDenied
	}
	if !validJSONDocument(in.PublicConfig, 64<<10) || !validJSONDocument(in.SecretRefs, 16<<10) {
		return ErrDenied
	}
	var public map[string]any
	if err := json.Unmarshal(in.PublicConfig, &public); err != nil || !validPublicConfigurationTree(public) {
		return ErrDenied
	}
	var refs map[string]any
	if err := json.Unmarshal(in.SecretRefs, &refs); err != nil {
		return ErrDenied
	}
	if !validSecretReferenceTree(refs) {
		return ErrDenied
	}
	return nil
}

func validPublicConfigurationTree(v any) bool {
	switch x := v.(type) {
	case map[string]any:
		for key, value := range x {
			name := strings.ToLower(strings.TrimSpace(key))
			if name == "" || len(name) > 128 || strings.Contains(name, "private") ||
				strings.Contains(name, "password") || strings.Contains(name, "secret") ||
				strings.Contains(name, "api_v3_key") || !validPublicConfigurationTree(value) {
				return false
			}
		}
		return true
	case []any:
		if len(x) > 128 {
			return false
		}
		for _, value := range x {
			if !validPublicConfigurationTree(value) {
				return false
			}
		}
		return true
	case string:
		return len(x) <= 4096 && !strings.Contains(strings.ToLower(x), "-----begin private key-----")
	case float64, bool, nil:
		return true
	default:
		return false
	}
}

func validSecretReferenceTree(v any) bool {
	switch x := v.(type) {
	case map[string]any:
		for k, v := range x {
			if strings.TrimSpace(k) == "" || len(k) > 128 || !validSecretReferenceTree(v) {
				return false
			}
		}
		return true
	case []any:
		if len(x) > 32 {
			return false
		}
		for _, v := range x {
			if !validSecretReferenceTree(v) {
				return false
			}
		}
		return true
	case string:
		s := strings.TrimSpace(x)
		lower := strings.ToLower(s)
		if len(s) == 0 || len(s) > 512 || strings.Contains(lower, "-----begin") ||
			strings.Contains(lower, "private_key=") || strings.Contains(lower, "api_v3_key=") {
			return false
		}
		for _, prefix := range []string{"secret://", "docker-secret://", "vault://", "env://", "file://", "/run/secrets/"} {
			if strings.HasPrefix(lower, prefix) && len(lower) > len(prefix) {
				return true
			}
		}
		return false
	default:
		return false
	}
}

func scanPaymentChannel(row pgx.Row) (PaymentChannel, error) {
	var x PaymentChannel
	var last *string
	err := row.Scan(&x.ID, &x.Provider, &x.Scene, &x.Enabled, &x.ActiveVersionID, &x.HealthStatus, &x.LastWebhookAt, &last, &x.CreatedAt, &x.UpdatedAt)
	if last != nil {
		x.LastErrorCode = *last
	}
	return x, err
}

func scanPaymentChannelVersion(row pgx.Row) (PaymentChannelVersion, error) {
	var x PaymentChannelVersion
	err := row.Scan(&x.ID, &x.ChannelID, &x.Version, &x.Environment, &x.PublicConfig, &x.SecretRefs, &x.Status, &x.ValidatedAt, &x.ValidationSummary, &x.CreatedBy, &x.CreatedAt)
	return x, err
}

func (s *Postgres) ListPaymentChannels(ctx context.Context, actorID uuid.UUID) ([]PaymentChannel, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,provider,scene,enabled,active_version_id,health_status,last_webhook_at,last_error_code,created_at,updated_at FROM payment_channels ORDER BY provider,scene`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []PaymentChannel
	for rows.Next() {
		x, e := scanPaymentChannel(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (s *Postgres) ListPaymentChannelVersions(ctx context.Context, actorID, channelID uuid.UUID) ([]PaymentChannelVersion, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,channel_id,version,environment,public_config,secret_refs,status,validated_at,validation_summary,created_by,created_at FROM payment_channel_versions WHERE channel_id=$1 ORDER BY version DESC LIMIT 50`, channelID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []PaymentChannelVersion
	for rows.Next() {
		x, e := scanPaymentChannelVersion(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

// ListPaymentChannelValidationTraces returns only the latest trace identifier
// for each channel configuration version. Validation details and provider
// errors remain excluded so the admin UI can correlate an incident without
// turning the audit log into a credential or response-body oracle.
func (s *Postgres) ListPaymentChannelValidationTraces(ctx context.Context, actorID, channelID uuid.UUID) ([]PaymentChannelValidationTrace, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `
		SELECT metadata->>'version',trace_id
		FROM audit_events
		WHERE target_type='payment_channel' AND target_id=$1
		  AND action='payment.channel.version.validate'
		ORDER BY id DESC
		LIMIT 200`, channelID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	seen := make(map[int64]struct{})
	result := make([]PaymentChannelValidationTrace, 0, 50)
	for rows.Next() {
		var rawVersion, traceID string
		if err := rows.Scan(&rawVersion, &traceID); err != nil {
			return nil, err
		}
		version, parseErr := strconv.ParseInt(rawVersion, 10, 64)
		if parseErr != nil || version < 1 || len(traceID) < 1 || len(traceID) > 256 {
			continue
		}
		if _, duplicate := seen[version]; duplicate {
			continue
		}
		seen[version] = struct{}{}
		result = append(result, PaymentChannelValidationTrace{Version: version, TraceID: traceID})
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return result, tx.Commit(ctx)
}

func (s *Postgres) EnsurePaymentChannel(ctx context.Context, actorID uuid.UUID, provider, scene, traceID string) (PaymentChannel, error) {
	provider = strings.ToUpper(strings.TrimSpace(provider))
	scene = strings.ToUpper(strings.TrimSpace(scene))
	if !validProvider(provider) || !validScene(scene) {
		return PaymentChannel{}, ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, true)
	if err != nil {
		return PaymentChannel{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	id := uuid.New()
	x, err := scanPaymentChannel(tx.QueryRow(ctx, `INSERT INTO payment_channels(id,provider,scene) VALUES($1,$2,$3) ON CONFLICT(provider,scene) DO UPDATE SET updated_at=payment_channels.updated_at RETURNING id,provider,scene,enabled,active_version_id,health_status,last_webhook_at,last_error_code,created_at,updated_at`, id, provider, scene))
	if err != nil {
		return PaymentChannel{}, err
	}
	if err = commerceAudit(ctx, tx, actorID, "payment.channel.ensure", "payment_channel", x.ID, traceID, nil); err != nil {
		return PaymentChannel{}, err
	}
	return x, tx.Commit(ctx)
}

func (s *Postgres) CreatePaymentChannelVersion(ctx context.Context, actorID, channelID uuid.UUID, in PaymentChannelVersionInput, traceID string) (PaymentChannelVersion, error) {
	if err := validatePaymentChannelVersionInput(in); err != nil {
		return PaymentChannelVersion{}, err
	}
	tx, err := s.commerceTx(ctx, actorID, true, true)
	if err != nil {
		return PaymentChannelVersion{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var version int64
	var exists bool
	if err = tx.QueryRow(ctx, `SELECT true FROM payment_channels WHERE id=$1 FOR UPDATE`, channelID).Scan(&exists); errors.Is(err, pgx.ErrNoRows) {
		return PaymentChannelVersion{}, ErrNotFound
	} else if err != nil {
		return PaymentChannelVersion{}, err
	}
	if err = tx.QueryRow(ctx, `SELECT coalesce(max(version),0)+1 FROM payment_channel_versions WHERE channel_id=$1`, channelID).Scan(&version); err != nil {
		return PaymentChannelVersion{}, err
	}
	id := uuid.New()
	x, err := scanPaymentChannelVersion(tx.QueryRow(ctx, `INSERT INTO payment_channel_versions(id,channel_id,version,environment,public_config,secret_refs,status,created_by)VALUES($1,$2,$3,$4,$5::jsonb,$6::jsonb,'DRAFT',$7)RETURNING id,channel_id,version,environment,public_config,secret_refs,status,validated_at,validation_summary,created_by,created_at`, id, channelID, version, strings.ToUpper(strings.TrimSpace(in.Environment)), in.PublicConfig, in.SecretRefs, actorID))
	if err != nil {
		return PaymentChannelVersion{}, err
	}
	if err = commerceAudit(ctx, tx, actorID, "payment.channel.version.create", "payment_channel", channelID, traceID, json.RawMessage(fmt.Sprintf(`{"version":%d}`, version))); err != nil {
		return PaymentChannelVersion{}, err
	}
	return x, tx.Commit(ctx)
}

// ValidatePaymentChannelVersion records the result of an out-of-store signer,
// certificate, and connectivity check. It never receives secret material.
func (s *Postgres) ValidatePaymentChannelVersion(ctx context.Context, actorID, versionID uuid.UUID, valid bool, summary, traceID string) (PaymentChannelVersion, error) {
	if len(summary) > 2000 {
		return PaymentChannelVersion{}, ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, true)
	if err != nil {
		return PaymentChannelVersion{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	status := "DRAFT"
	var at *time.Time
	if valid {
		status = "VALIDATED"
		now := time.Now().UTC()
		at = &now
	}
	x, err := scanPaymentChannelVersion(tx.QueryRow(ctx, `UPDATE payment_channel_versions SET status=$2,validated_at=$3,validation_summary=$4 WHERE id=$1 AND status IN ('DRAFT','VALIDATED') RETURNING id,channel_id,version,environment,public_config,secret_refs,status,validated_at,validation_summary,created_by,created_at`, versionID, status, at, summary))
	if errors.Is(err, pgx.ErrNoRows) {
		return PaymentChannelVersion{}, ErrNotFound
	}
	if err != nil {
		return PaymentChannelVersion{}, err
	}
	if err = commerceAudit(ctx, tx, actorID, "payment.channel.version.validate", "payment_channel", x.ChannelID, traceID, json.RawMessage(fmt.Sprintf(`{"version":%d,"valid":%t}`, x.Version, valid))); err != nil {
		return PaymentChannelVersion{}, err
	}
	return x, tx.Commit(ctx)
}

func (s *Postgres) ActivatePaymentChannelVersion(ctx context.Context, actorID, channelID, versionID uuid.UUID, traceID string) (PaymentChannel, error) {
	tx, err := s.commerceTx(ctx, actorID, true, true)
	if err != nil {
		return PaymentChannel{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var ok bool
	if err = tx.QueryRow(ctx, `SELECT true FROM payment_channel_versions WHERE id=$1 AND channel_id=$2 AND status='VALIDATED' FOR UPDATE`, versionID, channelID).Scan(&ok); errors.Is(err, pgx.ErrNoRows) {
		return PaymentChannel{}, ErrConflict
	} else if err != nil {
		return PaymentChannel{}, err
	}
	x, err := scanPaymentChannel(tx.QueryRow(ctx, `UPDATE payment_channels SET active_version_id=$2,enabled=true,health_status='HEALTHY',last_error_code=NULL,updated_at=now() WHERE id=$1 RETURNING id,provider,scene,enabled,active_version_id,health_status,last_webhook_at,last_error_code,created_at,updated_at`, channelID, versionID))
	if err != nil {
		return PaymentChannel{}, err
	}
	if _, err = tx.Exec(ctx, `UPDATE payment_channel_versions SET status='RETIRED' WHERE channel_id=$1 AND id<>$2 AND status='VALIDATED'`, channelID, versionID); err != nil {
		return PaymentChannel{}, err
	}
	if err = commerceAudit(ctx, tx, actorID, "payment.channel.activate", "payment_channel", channelID, traceID, nil); err != nil {
		return PaymentChannel{}, err
	}
	return x, tx.Commit(ctx)
}

func (s *Postgres) DisablePaymentChannel(ctx context.Context, actorID, channelID uuid.UUID, traceID string) (PaymentChannel, error) {
	tx, err := s.commerceTx(ctx, actorID, true, true)
	if err != nil {
		return PaymentChannel{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	x, err := scanPaymentChannel(tx.QueryRow(ctx, `UPDATE payment_channels SET enabled=false,health_status='DISABLED',updated_at=now() WHERE id=$1 RETURNING id,provider,scene,enabled,active_version_id,health_status,last_webhook_at,last_error_code,created_at,updated_at`, channelID))
	if errors.Is(err, pgx.ErrNoRows) {
		return PaymentChannel{}, ErrNotFound
	}
	if err != nil {
		return PaymentChannel{}, err
	}
	if err = commerceAudit(ctx, tx, actorID, "payment.channel.disable", "payment_channel", channelID, traceID, nil); err != nil {
		return PaymentChannel{}, err
	}
	return x, tx.Commit(ctx)
}

func scanOrder(row pgx.Row) (Order, error) {
	var x Order
	err := row.Scan(&x.ID, &x.OrderNo, &x.AccountID, &x.PlanKey, &x.CatalogVersion, &x.Provider, &x.PaymentScene, &x.AmountMinor, &x.Currency, &x.Status, &x.IdempotencyKey, &x.ExpiresAt, &x.PaidAt, &x.FulfilledAt, &x.ClosedAt, &x.CreatedAt, &x.UpdatedAt)
	return x, err
}

const orderColumns = `id,order_no,account_id,plan_key,catalog_version,provider,payment_scene,amount_minor,currency,status,idempotency_key,expires_at,paid_at,fulfilled_at,closed_at,created_at,updated_at`

func (s *Postgres) CreateOrder(ctx context.Context, accountID uuid.UUID, in CreateOrderInput) (Order, error) {
	in.PlanKey = strings.TrimSpace(in.PlanKey)
	in.Provider = strings.ToUpper(strings.TrimSpace(in.Provider))
	in.Scene = strings.ToUpper(strings.TrimSpace(in.Scene))
	now := time.Now().UTC()
	if len(in.IdempotencyKey) < 8 || len(in.IdempotencyKey) > 128 || !validProvider(in.Provider) || !validScene(in.Scene) || in.ExpiresAt.Before(now.Add(time.Minute)) || in.ExpiresAt.After(now.Add(24*time.Hour)) {
		return Order{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return Order{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var active bool
	if err = tx.QueryRow(ctx, `SELECT status='ACTIVE' FROM users WHERE id=$1 FOR UPDATE`, accountID).Scan(&active); err != nil || !active {
		return Order{}, ErrDenied
	}
	prior, err := scanOrder(tx.QueryRow(ctx, `SELECT `+orderColumns+` FROM orders WHERE account_id=$1 AND idempotency_key=$2`, accountID, in.IdempotencyKey))
	if err == nil {
		if prior.PlanKey != in.PlanKey || prior.Provider != in.Provider || prior.PaymentScene != in.Scene {
			return Order{}, ErrConflict
		}
		return prior, tx.Commit(ctx)
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return Order{}, err
	}
	var plan string
	var version, amount int64
	var currency string
	err = tx.QueryRow(ctx, `SELECT p.plan_key,c.version,p.price_minor,p.currency FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.status='PUBLISHED' AND p.plan_key=$1 AND 'WEB'=ANY(p.visible_platforms) AND p.price_minor>0 AND (p.available_from IS NULL OR p.available_from<=now()) AND (p.available_until IS NULL OR p.available_until>now())`, in.PlanKey).Scan(&plan, &version, &amount, &currency)
	if errors.Is(err, pgx.ErrNoRows) {
		return Order{}, ErrNotFound
	}
	if err != nil {
		return Order{}, err
	}
	var channelID uuid.UUID
	err = tx.QueryRow(ctx, `SELECT id FROM payment_channels WHERE provider=$1 AND scene=$2 AND enabled AND active_version_id IS NOT NULL AND health_status='HEALTHY'`, in.Provider, in.Scene).Scan(&channelID)
	if errors.Is(err, pgx.ErrNoRows) {
		return Order{}, ErrPaymentChannelUnavailable
	}
	if err != nil {
		return Order{}, err
	}
	id := uuid.New()
	orderNo := "TL" + strings.ReplaceAll(id.String(), "-", "")
	x, err := scanOrder(tx.QueryRow(ctx, `INSERT INTO orders(id,order_no,account_id,plan_key,catalog_version,provider,payment_scene,amount_minor,currency,status,idempotency_key,expires_at)VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,'CREATED',$10,$11)RETURNING `+orderColumns, id, orderNo, accountID, plan, version, in.Provider, in.Scene, amount, currency, in.IdempotencyKey, in.ExpiresAt))
	if err != nil {
		return Order{}, err
	}
	return x, tx.Commit(ctx)
}

func (s *Postgres) ListOrders(ctx context.Context, accountID uuid.UUID, limit int) ([]Order, error) {
	rows, err := s.Pool.Query(ctx, `SELECT `+orderColumns+` FROM orders WHERE account_id=$1 ORDER BY created_at DESC,id DESC LIMIT $2`, accountID, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Order
	for rows.Next() {
		x, e := scanOrder(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

const accountRefundColumns = `r.id,r.refund_no,r.order_id,r.transaction_id,r.amount_minor,r.reason,r.status,r.failure_code,r.provider_ref_hash,r.requested_by,r.version,r.approved_by,r.requested_at,r.approved_at,r.approval_expires_at,r.completed_at,r.updated_at`

// ListAccountRefunds returns only refunds attached to orders owned by the
// account. The ownership predicate is part of the first database query so a
// caller cannot use this method as a cross-account existence oracle.
func (s *Postgres) ListAccountRefunds(ctx context.Context, accountID uuid.UUID, limit int) ([]Refund, error) {
	if accountID == uuid.Nil {
		return nil, ErrDenied
	}
	rows, err := s.Pool.Query(ctx, `SELECT `+accountRefundColumns+`
		FROM refunds r JOIN orders o ON o.id=r.order_id
		WHERE o.account_id=$1
		ORDER BY r.requested_at DESC,r.id DESC LIMIT $2`, accountID, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Refund
	for rows.Next() {
		item, scanErr := scanRefund(rows)
		if scanErr != nil {
			return nil, scanErr
		}
		out = append(out, item)
	}
	return out, rows.Err()
}

func scanPaymentTransaction(row pgx.Row) (PaymentTransaction, error) {
	var x PaymentTransaction
	var failure *string
	err := row.Scan(&x.ID, &x.OrderID, &x.Provider, &x.ProviderTradeNoHash, &x.Status, &x.AmountMinor, &x.Currency, &x.PaymentPayload, &failure, &x.RequestedAt, &x.PaidAt, &x.UpdatedAt)
	if failure != nil {
		x.FailureCode = *failure
	}
	return x, err
}

func (s *Postgres) PersistPaymentInitiation(ctx context.Context, accountID, orderID, transactionID uuid.UUID, payload json.RawMessage) (PaymentTransaction, error) {
	if transactionID == uuid.Nil || !validJSONDocument(payload, 64<<10) {
		return PaymentTransaction{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return PaymentTransaction{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var provider, currency, status string
	var amount int64
	var expires time.Time
	err = tx.QueryRow(ctx, `SELECT provider,currency,status,amount_minor,expires_at FROM orders WHERE id=$1 AND account_id=$2 FOR UPDATE`, orderID, accountID).Scan(&provider, &currency, &status, &amount, &expires)
	if errors.Is(err, pgx.ErrNoRows) {
		return PaymentTransaction{}, ErrNotFound
	}
	if err != nil {
		return PaymentTransaction{}, err
	}
	if status != "CREATED" || !expires.After(time.Now().UTC()) {
		return PaymentTransaction{}, ErrOrderNotPayable
	}
	x, err := scanPaymentTransaction(tx.QueryRow(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency,payment_payload)VALUES($1,$2,$3,'PENDING',$4,$5,$6::jsonb)RETURNING id,order_id,provider,provider_trade_no_hash,status,amount_minor,currency,payment_payload,failure_code,requested_at,paid_at,updated_at`, transactionID, orderID, provider, amount, currency, payload))
	if err != nil {
		return PaymentTransaction{}, err
	}
	if _, err = tx.Exec(ctx, `UPDATE orders SET status='PENDING_PAYMENT',updated_at=now() WHERE id=$1`, orderID); err != nil {
		return PaymentTransaction{}, err
	}
	return x, tx.Commit(ctx)
}

func (s *Postgres) MarkOrderPaid(ctx context.Context, orderNo, provider string, transactionID uuid.UUID, tradeHash [32]byte, amount int64, currency string, paidAt time.Time) (Order, error) {
	provider = strings.ToUpper(strings.TrimSpace(provider))
	currency = strings.ToUpper(strings.TrimSpace(currency))
	if !validProvider(provider) || transactionID == uuid.Nil || amount < 1 {
		return Order{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return Order{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	x, err := scanOrder(tx.QueryRow(ctx, `SELECT `+orderColumns+` FROM orders WHERE order_no=$1 FOR UPDATE`, orderNo))
	if errors.Is(err, pgx.ErrNoRows) {
		return Order{}, ErrNotFound
	}
	if err != nil {
		return Order{}, err
	}
	if x.Provider != provider || x.AmountMinor != amount || x.Currency != currency {
		return Order{}, ErrOrderAmountMismatch
	}
	if x.Status == "PAID" || x.Status == "FULFILLING" || x.Status == "FULFILLED" {
		var settled bool
		err = tx.QueryRow(ctx, `SELECT true FROM payment_transactions
			WHERE id=$1 AND order_id=$2 AND provider=$3 AND amount_minor=$4 AND currency=$5
			  AND provider_trade_no_hash=$6 AND status IN('SUCCEEDED','REFUNDED')`,
			transactionID, x.ID, provider, amount, currency, tradeHash[:]).Scan(&settled)
		if errors.Is(err, pgx.ErrNoRows) {
			return Order{}, ErrOrderSettlementMismatch
		}
		if err != nil {
			return Order{}, err
		}
		return x, tx.Commit(ctx)
	}
	if x.Status != "CREATED" && x.Status != "PENDING_PAYMENT" {
		return Order{}, ErrOrderNotPayable
	}
	now := paidAt.UTC()
	if now.IsZero() {
		now = time.Now().UTC()
	}
	cmd, err := tx.Exec(ctx, `UPDATE payment_transactions SET provider_trade_no_hash=$2,status='SUCCEEDED',paid_at=$3,updated_at=now() WHERE id=$1 AND order_id=$4 AND provider=$5 AND amount_minor=$6 AND currency=$7 AND status='PENDING'`, transactionID, tradeHash[:], now, x.ID, provider, amount, currency)
	if err != nil {
		return Order{}, err
	}
	if cmd.RowsAffected() != 1 {
		return Order{}, ErrNotFound
	}
	x, err = scanOrder(tx.QueryRow(ctx, `UPDATE orders SET status='PAID',paid_at=$2,updated_at=now() WHERE id=$1 RETURNING `+orderColumns, x.ID, now))
	if err != nil {
		return Order{}, err
	}
	return x, tx.Commit(ctx)
}

func (s *Postgres) FulfillOrder(ctx context.Context, orderID uuid.UUID) (Order, Entitlement, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return Order{}, Entitlement{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	x, err := scanOrder(tx.QueryRow(ctx, `SELECT `+orderColumns+` FROM orders WHERE id=$1 FOR UPDATE`, orderID))
	if errors.Is(err, pgx.ErrNoRows) {
		return Order{}, Entitlement{}, ErrNotFound
	}
	if err != nil {
		return Order{}, Entitlement{}, err
	}
	if x.Status == "FULFILLED" {
		e, eerr := recomputeEntitlement(ctx, tx, x.AccountID)
		if eerr != nil {
			return Order{}, Entitlement{}, eerr
		}
		return x, e, tx.Commit(ctx)
	}
	if x.Status != "PAID" && x.Status != "FULFILLING" {
		return Order{}, Entitlement{}, ErrConflict
	}
	if _, err = tx.Exec(ctx, `UPDATE orders SET status='FULFILLING',updated_at=now() WHERE id=$1`, x.ID); err != nil {
		return Order{}, Entitlement{}, err
	}
	var tradeHash []byte
	err = tx.QueryRow(ctx, `SELECT provider_trade_no_hash FROM payment_transactions WHERE order_id=$1 AND status='SUCCEEDED' ORDER BY paid_at DESC NULLS LAST LIMIT 1 FOR UPDATE`, x.ID).Scan(&tradeHash)
	if err != nil || len(tradeHash) != 32 {
		return Order{}, Entitlement{}, ErrConflict
	}
	var period string
	err = tx.QueryRow(ctx, `SELECT p.billing_period FROM plans p JOIN catalog_versions c ON c.id=p.catalog_version_id WHERE c.version=$1 AND p.plan_key=$2`, x.CatalogVersion, x.PlanKey).Scan(&period)
	if err != nil {
		return Order{}, Entitlement{}, err
	}
	start := time.Now().UTC()
	var existingEnd *time.Time
	_ = tx.QueryRow(ctx, `SELECT max(ends_at) FROM entitlement_grants WHERE account_id=$1 AND plan_key=$2 AND status='ACTIVE' AND ends_at>now()`, x.AccountID, x.PlanKey).Scan(&existingEnd)
	if existingEnd != nil && existingEnd.After(start) {
		start = *existingEnd
	}
	var end time.Time
	switch period {
	case "MONTHLY":
		end = start.AddDate(0, 1, 0)
	case "YEARLY":
		end = start.AddDate(1, 0, 0)
	default:
		return Order{}, Entitlement{}, ErrConflict
	}
	grantID := uuid.New()
	_, err = tx.Exec(ctx, `INSERT INTO entitlement_grants(id,account_id,source_type,source_id,plan_key,catalog_version,starts_at,ends_at,provider_reference_hash,status,metadata)VALUES($1,$2,$3,$4,$5,$6,now(),$7,$8,'ACTIVE',jsonb_build_object('order_no',$9::text)) ON CONFLICT(source_type,source_id) WHERE source_id IS NOT NULL DO NOTHING`, grantID, x.AccountID, x.Provider, x.ID, x.PlanKey, x.CatalogVersion, end, tradeHash, x.OrderNo)
	if err != nil {
		return Order{}, Entitlement{}, err
	}
	_, err = tx.Exec(ctx, `INSERT INTO subscriptions(id,account_id,provider,product_id,provider_ref_hash,plan_key,catalog_version,status,renews_at)VALUES($1,$2,$3,$4,$5,$6,$7,'ACTIVE',$8) ON CONFLICT(provider,provider_ref_hash)DO NOTHING`, uuid.New(), x.AccountID, x.Provider, x.PlanKey, tradeHash, x.PlanKey, x.CatalogVersion, end)
	if err != nil {
		return Order{}, Entitlement{}, err
	}
	e, err := recomputeEntitlement(ctx, tx, x.AccountID)
	if err != nil {
		return Order{}, Entitlement{}, err
	}
	x, err = scanOrder(tx.QueryRow(ctx, `UPDATE orders SET status='FULFILLED',fulfilled_at=coalesce(fulfilled_at,now()),updated_at=now() WHERE id=$1 RETURNING `+orderColumns, x.ID))
	if err != nil {
		return Order{}, Entitlement{}, err
	}
	return x, e, tx.Commit(ctx)
}

func (s *Postgres) CloseExpiredOrders(ctx context.Context, limit int) (int64, error) {
	limit = normalizePage(limit)
	cmd, err := s.Pool.Exec(ctx, `WITH due AS (SELECT id FROM orders WHERE status IN ('CREATED','PENDING_PAYMENT') AND expires_at<=now() ORDER BY expires_at LIMIT $1 FOR UPDATE SKIP LOCKED), closed_tx AS (UPDATE payment_transactions SET status='CLOSED',updated_at=now() WHERE order_id IN(SELECT id FROM due) AND status IN('INITIATED','PENDING')) UPDATE orders SET status='CLOSED',closed_at=now(),updated_at=now() WHERE id IN(SELECT id FROM due)`, limit)
	if err != nil {
		return 0, err
	}
	return cmd.RowsAffected(), nil
}

func (s *Postgres) CancelOrder(ctx context.Context, accountID, orderID uuid.UUID) (Order, error) {
	x, err := scanOrder(s.Pool.QueryRow(ctx, `UPDATE orders SET status='CLOSED',closed_at=now(),updated_at=now() WHERE id=$1 AND account_id=$2 AND status IN ('CREATED','PENDING_PAYMENT') RETURNING `+orderColumns, orderID, accountID))
	if errors.Is(err, pgx.ErrNoRows) {
		return Order{}, ErrOrderNotPayable
	}
	return x, err
}

func scanPaymentNotification(row pgx.Row) (PaymentNotification, error) {
	var x PaymentNotification
	var orderNo, failure *string
	err := row.Scan(&x.ID, &x.Provider, &x.NotificationIDHash, &orderNo, &x.SignatureValid, &x.ProcessingState, &x.PayloadDigest, &failure, &x.ReceivedAt, &x.ProcessedAt)
	if orderNo != nil {
		x.OrderNo = *orderNo
	}
	if failure != nil {
		x.FailureCode = *failure
	}
	return x, err
}

// RecordPaymentNotification is the replay fence. The first verified transport
// delivery wins; duplicates return the existing row and created=false.
func (s *Postgres) RecordPaymentNotification(ctx context.Context, in PaymentNotificationInput) (PaymentNotification, bool, error) {
	in.Provider = strings.ToUpper(strings.TrimSpace(in.Provider))
	if in.ID == uuid.Nil || !validProvider(in.Provider) || len(in.PayloadCiphertext) > 1<<20 {
		return PaymentNotification{}, false, ErrDenied
	}
	switch in.ProcessingState {
	case "RECEIVED", "REJECTED":
	default:
		return PaymentNotification{}, false, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return PaymentNotification{}, false, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	x, err := scanPaymentNotification(tx.QueryRow(ctx, `INSERT INTO payment_notifications(id,provider,notification_id_hash,order_no,signature_valid,processing_state,payload_ciphertext,payload_digest,failure_code,processed_at)VALUES($1,$2,$3,NULLIF($4,''),$5,$6,$7,$8,NULLIF($9,''),CASE WHEN $6='REJECTED' THEN now() END) ON CONFLICT(provider,notification_id_hash)DO NOTHING RETURNING id,provider,notification_id_hash,order_no,signature_valid,processing_state,payload_digest,failure_code,received_at,processed_at`, in.ID, in.Provider, in.NotificationIDHash[:], in.OrderNo, in.SignatureValid, in.ProcessingState, in.PayloadCiphertext, in.PayloadDigest[:], in.FailureCode))
	created := err == nil
	if errors.Is(err, pgx.ErrNoRows) {
		x, err = scanPaymentNotification(tx.QueryRow(ctx, `SELECT id,provider,notification_id_hash,order_no,signature_valid,processing_state,payload_digest,failure_code,received_at,processed_at FROM payment_notifications WHERE provider=$1 AND notification_id_hash=$2 FOR UPDATE`, in.Provider, in.NotificationIDHash[:]))
	}
	if err != nil {
		return PaymentNotification{}, false, err
	}
	if in.SignatureValid {
		if _, err = tx.Exec(ctx, `UPDATE payment_channels SET last_webhook_at=now(),health_status='HEALTHY',last_error_code=NULL,updated_at=now()
			WHERE provider=$1 AND enabled`, in.Provider); err != nil {
			return PaymentNotification{}, false, err
		}
	}
	return x, created, tx.Commit(ctx)
}

func (s *Postgres) CompletePaymentNotification(ctx context.Context, notificationID uuid.UUID, success bool, failureCode string) error {
	state := "PROCESSED"
	if !success {
		state = "RETRY"
	}
	cmd, err := s.Pool.Exec(ctx, `UPDATE payment_notifications SET processing_state=$2,failure_code=NULLIF($3,''),processed_at=CASE WHEN $2='PROCESSED' THEN now() ELSE NULL END WHERE id=$1 AND processing_state IN('RECEIVED','RETRY')`, notificationID, state, failureCode)
	if err != nil {
		return err
	}
	if cmd.RowsAffected() != 1 {
		return ErrConflict
	}
	return nil
}

const refundColumns = `id,refund_no,order_id,transaction_id,amount_minor,reason,status,failure_code,provider_ref_hash,requested_by,version,approved_by,requested_at,approved_at,approval_expires_at,completed_at,updated_at`

const refundApprovalRequestedOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES('refund.approval.changed',$1,jsonb_build_object(
		'refund_id',$1::uuid::text,'order_id',$2::uuid::text,
		'status','APPROVAL_PENDING','version',$3::bigint))`

const refundApprovalConfirmedOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES('refund.approval.changed',$1,jsonb_build_object(
		'refund_id',$1::uuid::text,'order_id',$2::uuid::text,
		'status','CREATED','version',$3::bigint))`

func scanRefund(row pgx.Row) (Refund, error) {
	var x Refund
	var failureCode *string
	err := row.Scan(&x.ID, &x.RefundNo, &x.OrderID, &x.TransactionID, &x.AmountMinor, &x.Reason, &x.Status, &failureCode, &x.ProviderRefHash, &x.RequestedBy, &x.Version, &x.ApprovedBy, &x.RequestedAt, &x.ApprovedAt, &x.ApprovalExpiresAt, &x.CompletedAt, &x.UpdatedAt)
	if failureCode != nil {
		x.FailureCode = *failureCode
	}
	return x, err
}

func validFullRefundAmount(orderAmount, requestedAmount, alreadyRefunded int64) bool {
	return orderAmount > 0 && requestedAmount == orderAmount && alreadyRefunded == 0
}

func refundApprovalIsReplay(item Refund, expectedVersion int64) bool {
	return expectedVersion > 0 && item.Status != "APPROVAL_PENDING" && item.Version >= expectedVersion+1 && item.ApprovedBy != nil && item.ApprovedAt != nil
}

func refundApprovalIsLive(now time.Time, expiresAt *time.Time) bool {
	return expiresAt != nil && expiresAt.After(now)
}

func (s *Postgres) CreateRefund(ctx context.Context, actorID, orderID uuid.UUID, amount int64, reason, traceID string, approvalExpiresAt time.Time) (Refund, error) {
	reason = strings.TrimSpace(reason)
	if amount < 1 || len(reason) < 1 || len(reason) > 500 || approvalExpiresAt.IsZero() {
		return Refund{}, ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return Refund{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var orderAmount int64
	var status string
	if err = tx.QueryRow(ctx, `SELECT amount_minor,status FROM orders WHERE id=$1 FOR UPDATE`, orderID).Scan(&orderAmount, &status); errors.Is(err, pgx.ErrNoRows) {
		return Refund{}, ErrNotFound
	} else if err != nil {
		return Refund{}, err
	}
	// Until a proration policy is explicitly configured, only a full refund is
	// safe: partial money reversal while retaining the complete entitlement is
	// an ambiguous commercial state. Failed attempts are excluded below so a
	// full refund may be retried.
	if status != "FULFILLED" || amount != orderAmount {
		return Refund{}, ErrConflict
	}
	var refunded int64
	if err = tx.QueryRow(ctx, `SELECT coalesce(sum(amount_minor),0) FROM refunds WHERE order_id=$1 AND status IN('APPROVAL_PENDING','CREATED','PENDING','SUCCEEDED')`, orderID).Scan(&refunded); err != nil {
		return Refund{}, err
	}
	if !validFullRefundAmount(orderAmount, amount, refunded) {
		return Refund{}, ErrConflict
	}
	var transactionID uuid.UUID
	if err = tx.QueryRow(ctx, `SELECT id FROM payment_transactions WHERE order_id=$1 AND status='SUCCEEDED' ORDER BY paid_at DESC LIMIT 1 FOR UPDATE`, orderID).Scan(&transactionID); err != nil {
		return Refund{}, ErrConflict
	}
	id := uuid.New()
	refundNo := "RF" + strings.ReplaceAll(id.String(), "-", "")
	x, err := scanRefund(tx.QueryRow(ctx, `INSERT INTO refunds(id,refund_no,order_id,transaction_id,amount_minor,reason,status,requested_by,approval_expires_at)VALUES($1,$2,$3,$4,$5,$6,'APPROVAL_PENDING',$7,$8)RETURNING `+refundColumns, id, refundNo, orderID, transactionID, amount, reason, actorID, approvalExpiresAt.UTC()))
	if err != nil {
		return Refund{}, err
	}
	if err = commerceAudit(ctx, tx, actorID, "payment.refund.request", "refund", id, traceID, json.RawMessage(fmt.Sprintf(`{"amount_minor":%d,"status":"APPROVAL_PENDING"}`, amount))); err != nil {
		return Refund{}, err
	}
	if _, err = tx.Exec(ctx, refundApprovalRequestedOutboxSQL, id, orderID, x.Version); err != nil {
		return Refund{}, err
	}
	return x, tx.Commit(ctx)
}

// ApproveRefund atomically consumes one unexpired approval version and moves
// the order into REFUNDING before any provider request is allowed. A replay of
// the immediately preceding version returns the already-approved row without
// advancing it again, allowing callers to avoid a duplicate provider call.
func (s *Postgres) ApproveRefund(ctx context.Context, actorID, refundID uuid.UUID, expectedVersion int64, traceID string) (Refund, bool, error) {
	if actorID == uuid.Nil || refundID == uuid.Nil || expectedVersion < 1 || len(strings.TrimSpace(traceID)) > 256 {
		return Refund{}, false, ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return Refund{}, false, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	x, err := scanRefund(tx.QueryRow(ctx, `SELECT `+refundColumns+` FROM refunds WHERE id=$1 FOR UPDATE`, refundID))
	if errors.Is(err, pgx.ErrNoRows) {
		return Refund{}, false, ErrNotFound
	}
	if err != nil {
		return Refund{}, false, err
	}
	if x.Status != "APPROVAL_PENDING" {
		if refundApprovalIsReplay(x, expectedVersion) {
			return x, false, tx.Commit(ctx)
		}
		return Refund{}, false, ErrRefundVersionConflict
	}
	if x.Version != expectedVersion {
		return Refund{}, false, ErrRefundVersionConflict
	}
	if !refundApprovalIsLive(time.Now().UTC(), x.ApprovalExpiresAt) {
		return Refund{}, false, ErrRefundApprovalExpired
	}
	var orderStatus string
	if err = tx.QueryRow(ctx, `SELECT status FROM orders WHERE id=$1 FOR UPDATE`, x.OrderID).Scan(&orderStatus); err != nil {
		return Refund{}, false, err
	}
	if orderStatus != "FULFILLED" {
		return Refund{}, false, ErrConflict
	}
	x, err = scanRefund(tx.QueryRow(ctx, `UPDATE refunds
		SET status='CREATED',version=version+1,approved_by=$2,approved_at=now(),updated_at=now()
		WHERE id=$1 AND status='APPROVAL_PENDING' AND version=$3 AND approval_expires_at>now()
		RETURNING `+refundColumns, refundID, actorID, expectedVersion))
	if errors.Is(err, pgx.ErrNoRows) {
		return Refund{}, false, ErrRefundApprovalExpired
	}
	if err != nil {
		return Refund{}, false, err
	}
	if _, err = tx.Exec(ctx, `UPDATE orders SET status='REFUNDING',updated_at=now() WHERE id=$1 AND status='FULFILLED'`, x.OrderID); err != nil {
		return Refund{}, false, err
	}
	if err = commerceAudit(ctx, tx, actorID, "payment.refund.approve", "refund", refundID, traceID, json.RawMessage(fmt.Sprintf(`{"from":"APPROVAL_PENDING","to":"CREATED","version":%d}`, x.Version))); err != nil {
		return Refund{}, false, err
	}
	if _, err = tx.Exec(ctx, refundApprovalConfirmedOutboxSQL, refundID, x.OrderID, x.Version); err != nil {
		return Refund{}, false, err
	}
	return x, true, tx.Commit(ctx)
}

func (s *Postgres) CompleteRefund(ctx context.Context, refundID uuid.UUID, providerRefHash [32]byte, success bool, failureCode string) (Refund, error) {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return Refund{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	x, err := scanRefund(tx.QueryRow(ctx, `SELECT `+refundColumns+` FROM refunds WHERE id=$1 FOR UPDATE`, refundID))
	if errors.Is(err, pgx.ErrNoRows) {
		return Refund{}, ErrNotFound
	}
	if err != nil {
		return Refund{}, err
	}
	if x.Status == "SUCCEEDED" {
		return x, tx.Commit(ctx)
	}
	if x.Status != "CREATED" && x.Status != "PENDING" && x.Status != "FAILED" {
		return Refund{}, ErrConflict
	}
	if !success {
		failureCode = strings.ToUpper(strings.TrimSpace(failureCode))
		if failureCode == "" || len(failureCode) > 128 {
			return Refund{}, ErrDenied
		}
		x, err = scanRefund(tx.QueryRow(ctx, `UPDATE refunds SET status='FAILED',failure_code=$2,version=version+1,updated_at=now() WHERE id=$1 RETURNING `+refundColumns, refundID, failureCode))
		if err != nil {
			return Refund{}, err
		}
		_, err = tx.Exec(ctx, `UPDATE orders SET status='FULFILLED',updated_at=now() WHERE id=$1`, x.OrderID)
		if err != nil {
			return Refund{}, err
		}
		return x, tx.Commit(ctx)
	}
	x, err = scanRefund(tx.QueryRow(ctx, `UPDATE refunds SET status='SUCCEEDED',failure_code=NULL,provider_ref_hash=$2,completed_at=now(),version=version+1,updated_at=now() WHERE id=$1 RETURNING `+refundColumns, refundID, providerRefHash[:]))
	if err != nil {
		return Refund{}, err
	}
	var total, refunded int64
	if err = tx.QueryRow(ctx, `SELECT o.amount_minor,coalesce(sum(r.amount_minor)FILTER(WHERE r.status='SUCCEEDED'),0) FROM orders o LEFT JOIN refunds r ON r.order_id=o.id WHERE o.id=$1 GROUP BY o.amount_minor`, x.OrderID).Scan(&total, &refunded); err != nil {
		return Refund{}, err
	}
	if refunded >= total {
		if _, err = tx.Exec(ctx, `UPDATE orders SET status='REFUNDED',updated_at=now() WHERE id=$1`, x.OrderID); err != nil {
			return Refund{}, err
		}
		if _, err = tx.Exec(ctx, `UPDATE payment_transactions SET status='REFUNDED',updated_at=now() WHERE id=$1`, x.TransactionID); err != nil {
			return Refund{}, err
		}
		var accountID uuid.UUID
		if err = tx.QueryRow(ctx, `SELECT account_id FROM orders WHERE id=$1`, x.OrderID).Scan(&accountID); err != nil {
			return Refund{}, err
		}
		if _, err = tx.Exec(ctx, `UPDATE entitlement_grants SET status='REVOKED',updated_at=now() WHERE source_id=$1 AND source_type IN('ALIPAY','WECHAT_PAY')`, x.OrderID); err != nil {
			return Refund{}, err
		}
		if _, err = recomputeEntitlement(ctx, tx, accountID); err != nil {
			return Refund{}, err
		}
	} else {
		if _, err = tx.Exec(ctx, `UPDATE orders SET status='FULFILLED',updated_at=now() WHERE id=$1`, x.OrderID); err != nil {
			return Refund{}, err
		}
	}
	return x, tx.Commit(ctx)
}

func scanReconciliationRun(row pgx.Row) (ReconciliationRun, error) {
	var x ReconciliationRun
	err := row.Scan(&x.ID, &x.Provider, &x.PeriodStart, &x.PeriodEnd, &x.Status, &x.ResultSummary, &x.RequestedBy, &x.StartedAt, &x.CompletedAt, &x.CreatedAt)
	return x, err
}

func (s *Postgres) CreateReconciliationRun(ctx context.Context, actorID uuid.UUID, provider string, start, end time.Time, traceID string) (ReconciliationRun, error) {
	provider = strings.ToUpper(strings.TrimSpace(provider))
	if !validProvider(provider) || end.Before(start) {
		return ReconciliationRun{}, ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return ReconciliationRun{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	id := uuid.New()
	x, err := scanReconciliationRun(tx.QueryRow(ctx, `INSERT INTO reconciliation_runs(id,provider,period_start,period_end,status,requested_by)VALUES($1,$2,$3,$4,'PENDING',$5)RETURNING id,provider,period_start,period_end,status,result_summary,requested_by,started_at,completed_at,created_at`, id, provider, start, end, actorID))
	if err != nil {
		return ReconciliationRun{}, ErrConflict
	}
	if err = commerceAudit(ctx, tx, actorID, "payment.reconciliation.create", "reconciliation", id, traceID, nil); err != nil {
		return ReconciliationRun{}, err
	}
	return x, tx.Commit(ctx)
}

func (s *Postgres) CompleteReconciliationRun(ctx context.Context, id uuid.UUID, success bool, summary json.RawMessage) (ReconciliationRun, error) {
	if !validJSONDocument(summary, 256<<10) {
		return ReconciliationRun{}, ErrDenied
	}
	status := "SUCCEEDED"
	if !success {
		status = "FAILED"
	}
	x, err := scanReconciliationRun(s.Pool.QueryRow(ctx, `UPDATE reconciliation_runs SET status=$2,result_summary=$3::jsonb,started_at=coalesce(started_at,now()),completed_at=now() WHERE id=$1 AND status IN('PENDING','RUNNING') RETURNING id,provider,period_start,period_end,status,result_summary,requested_by,started_at,completed_at,created_at`, id, status, summary))
	if errors.Is(err, pgx.ErrNoRows) {
		return ReconciliationRun{}, ErrConflict
	}
	return x, err
}

func (s *Postgres) ListCommerceOrders(ctx context.Context, actorID uuid.UUID, limit int) ([]Order, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT `+orderColumns+` FROM orders ORDER BY created_at DESC,id DESC LIMIT $1`, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Order
	for rows.Next() {
		x, e := scanOrder(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (s *Postgres) ListOrderTransactions(ctx context.Context, accountID, orderID uuid.UUID) ([]PaymentTransaction, error) {
	rows, err := s.Pool.Query(ctx, `SELECT id,order_id,provider,provider_trade_no_hash,status,amount_minor,currency,payment_payload,failure_code,requested_at,paid_at,updated_at FROM payment_transactions WHERE order_id=$1 AND EXISTS(SELECT 1 FROM orders WHERE id=$1 AND account_id=$2) ORDER BY requested_at DESC LIMIT 50`, orderID, accountID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []PaymentTransaction
	for rows.Next() {
		x, e := scanPaymentTransaction(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (s *Postgres) ListCommerceTransactions(ctx context.Context, actorID uuid.UUID, limit int) ([]PaymentTransaction, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,order_id,provider,provider_trade_no_hash,status,amount_minor,currency,payment_payload,failure_code,requested_at,paid_at,updated_at FROM payment_transactions ORDER BY requested_at DESC,id DESC LIMIT $1`, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []PaymentTransaction
	for rows.Next() {
		x, e := scanPaymentTransaction(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (s *Postgres) ListPaymentNotifications(ctx context.Context, actorID uuid.UUID, limit int) ([]PaymentNotification, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,provider,notification_id_hash,order_no,signature_valid,processing_state,payload_digest,failure_code,received_at,processed_at FROM payment_notifications ORDER BY received_at DESC LIMIT $1`, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []PaymentNotification
	for rows.Next() {
		x, e := scanPaymentNotification(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (s *Postgres) ListRefunds(ctx context.Context, actorID uuid.UUID, limit int) ([]Refund, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT `+refundColumns+` FROM refunds ORDER BY requested_at DESC LIMIT $1`, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Refund
	for rows.Next() {
		x, e := scanRefund(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (s *Postgres) ListReconciliationRuns(ctx context.Context, actorID uuid.UUID, limit int) ([]ReconciliationRun, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `SELECT id,provider,period_start,period_end,status,result_summary,requested_by,started_at,completed_at,created_at FROM reconciliation_runs ORDER BY created_at DESC LIMIT $1`, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []ReconciliationRun
	for rows.Next() {
		x, e := scanReconciliationRun(rows)
		if e != nil {
			return nil, e
		}
		out = append(out, x)
	}
	return out, rows.Err()
}
