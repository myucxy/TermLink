package store

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const (
	manualEntitlementDefaultLimit = 50
	manualEntitlementMaxLimit     = 200
	manualEntitlementMaxDays      = 365
)

type ManualEntitlementGrant struct {
	ID             uuid.UUID `json:"id"`
	AccountID      uuid.UUID `json:"account_id"`
	PlanKey        string    `json:"plan_key"`
	CatalogVersion int64     `json:"catalog_version"`
	StartsAt       time.Time `json:"starts_at"`
	EndsAt         time.Time `json:"ends_at"`
	Status         string    `json:"status"`
	CreatedAt      time.Time `json:"created_at"`
	UpdatedAt      time.Time `json:"updated_at"`
}

type ManualEntitlementGrantFilter struct {
	AccountID uuid.UUID
	Limit     int
}

type CreateManualEntitlementGrantInput struct {
	AccountID      uuid.UUID
	PlanKey        string
	DurationDays   int
	Reason         string
	IdempotencyKey string
	TraceID        string
}

type RevokeManualEntitlementGrantInput struct {
	GrantID        uuid.UUID
	Reason         string
	IdempotencyKey string
	TraceID        string
}

const manualEntitlementListSQL = `
	SELECT manual_grant.id,manual_grant.account_id,manual_grant.plan_key,manual_grant.catalog_version,
	       manual_grant.starts_at,manual_grant.ends_at,manual_grant.status,manual_grant.created_at,manual_grant.updated_at
	FROM entitlement_grants manual_grant
	WHERE manual_grant.source_type='MANUAL'
	  AND ($1::uuid='00000000-0000-0000-0000-000000000000'::uuid OR manual_grant.account_id=$1)
	ORDER BY manual_grant.created_at DESC,manual_grant.id DESC
	LIMIT $2`

const manualEntitlementTrustedActorDeviceSQL = `
	SELECT 1 FROM devices
	WHERE id=$1 AND user_id=$2 AND trust_state='TRUSTED'
	FOR SHARE`

const manualEntitlementTargetAccountSQL = `
	SELECT status FROM users WHERE id=$1 FOR UPDATE`

const manualEntitlementCurrentPlanSQL = `
	SELECT plan.plan_key,catalog.version
	FROM catalog_versions catalog
	JOIN plans plan ON plan.catalog_version_id=catalog.id
	WHERE catalog.status='PUBLISHED' AND plan.plan_key=$1
	  AND (plan.available_from IS NULL OR plan.available_from<=$2)
	  AND (plan.available_until IS NULL OR plan.available_until>$2)
	FOR SHARE OF catalog,plan`

const manualEntitlementInsertSQL = `
	INSERT INTO entitlement_grants(
		id,account_id,source_type,source_id,plan_key,catalog_version,
		starts_at,ends_at,status,metadata)
	VALUES($1,$2,'MANUAL',$1,$3,$4,$5,$6,'ACTIVE',
	       jsonb_build_object('actor_id',$7::uuid,'reason',$8::text))
	RETURNING id,account_id,plan_key,catalog_version,starts_at,ends_at,status,created_at,updated_at`

const manualEntitlementLookupSQL = `
	SELECT account_id,source_type
	FROM entitlement_grants WHERE id=$1`

const manualEntitlementLockSQL = `
	SELECT id,account_id,plan_key,catalog_version,starts_at,ends_at,status,created_at,updated_at
	FROM entitlement_grants
	WHERE id=$1 AND account_id=$2 AND source_type='MANUAL'
	FOR UPDATE`

const manualEntitlementRevokeSQL = `
	UPDATE entitlement_grants SET status='REVOKED',updated_at=now()
	WHERE id=$1 AND account_id=$2 AND source_type='MANUAL' AND status='ACTIVE'
	RETURNING id,account_id,plan_key,catalog_version,starts_at,ends_at,status,created_at,updated_at`

const manualEntitlementIdempotencyInsertSQL = `
	INSERT INTO idempotency_keys(user_id,idempotency_key,request_hash,expires_at)
	VALUES($1,$2,$3,now()+interval '24 hours')
	ON CONFLICT(user_id,idempotency_key) DO UPDATE
	SET request_hash=excluded.request_hash,response_status=NULL,response_body=NULL,
	    expires_at=excluded.expires_at,created_at=now()
	WHERE idempotency_keys.expires_at<now()`

const manualEntitlementIdempotencyReadSQL = `
	SELECT request_hash,response_status,response_body
	FROM idempotency_keys WHERE user_id=$1 AND idempotency_key=$2`

const manualEntitlementIdempotencyCompleteSQL = `
	UPDATE idempotency_keys SET response_status=200,response_body=$4::jsonb
	WHERE user_id=$1 AND idempotency_key=$2 AND request_hash=$3`

const manualEntitlementEnsureOutboxSQL = `
	INSERT INTO outbox(event_type,aggregate_id,payload)
	SELECT 'entitlement.changed',$1,
	       jsonb_build_object('account_id',$1::uuid::text,'plan_key',$2::text,
	         'catalog_version',$3::bigint,'version',$4::bigint,'source',$5::text)
	WHERE NOT EXISTS (
		SELECT 1 FROM outbox
		WHERE event_type='entitlement.changed' AND aggregate_id=$1
		  AND payload->>'version'=$4::text
	)`

const manualEntitlementAuditSQL = `
	INSERT INTO audit_events(
		user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
	VALUES($1,$2,$3,'entitlement_grant',$4,'SUCCEEDED',$5,$6::jsonb)`

func normalizeManualEntitlementLimit(limit int) int {
	if limit < 1 {
		return manualEntitlementDefaultLimit
	}
	if limit > manualEntitlementMaxLimit {
		return manualEntitlementMaxLimit
	}
	return limit
}

func validateManualEntitlementReason(reason string) bool {
	if reason != strings.TrimSpace(reason) || !utf8.ValidString(reason) {
		return false
	}
	count := utf8.RuneCountInString(reason)
	if count < 3 || count > 500 {
		return false
	}
	for _, character := range reason {
		if unicode.IsControl(character) {
			return false
		}
	}
	return true
}

func validateManualEntitlementKey(key string) bool {
	if len(key) < 8 || len(key) > 128 || key != strings.TrimSpace(key) {
		return false
	}
	for _, character := range key {
		if character < 0x21 || character > 0x7e {
			return false
		}
	}
	return true
}

func manualEntitlementRequestHash(operation string, actorID uuid.UUID, values ...string) [32]byte {
	var material strings.Builder
	material.WriteString(operation)
	material.WriteByte(0)
	material.WriteString(actorID.String())
	for _, value := range values {
		material.WriteByte(0)
		material.WriteString(value)
	}
	return sha256.Sum256([]byte(material.String()))
}

func scanManualEntitlementGrant(row pgx.Row) (ManualEntitlementGrant, error) {
	var item ManualEntitlementGrant
	err := row.Scan(&item.ID, &item.AccountID, &item.PlanKey, &item.CatalogVersion,
		&item.StartsAt, &item.EndsAt, &item.Status, &item.CreatedAt, &item.UpdatedAt)
	return item, err
}

func (s *Postgres) ListManualEntitlementGrants(ctx context.Context, actorID uuid.UUID, filter ManualEntitlementGrantFilter) ([]ManualEntitlementGrant, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	limit := normalizeManualEntitlementLimit(filter.Limit)
	rows, err := tx.Query(ctx, manualEntitlementListSQL, filter.AccountID, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]ManualEntitlementGrant, 0, limit)
	for rows.Next() {
		item, err := scanManualEntitlementGrant(rows)
		if err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return result, tx.Commit(ctx)
}

func requireManualEntitlementActorDevice(ctx context.Context, tx pgx.Tx, actorID, deviceID uuid.UUID) error {
	var present int
	err := tx.QueryRow(ctx, manualEntitlementTrustedActorDeviceSQL, deviceID, actorID).Scan(&present)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrDenied
	}
	return err
}

func lockManualEntitlementTargetAccount(ctx context.Context, tx pgx.Tx, accountID uuid.UUID) error {
	var status string
	err := tx.QueryRow(ctx, manualEntitlementTargetAccountSQL, accountID).Scan(&status)
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

func claimManualEntitlementIdempotency(ctx context.Context, tx pgx.Tx, actorID uuid.UUID, key string, hash [32]byte) (*ManualEntitlementGrant, error) {
	command, err := tx.Exec(ctx, manualEntitlementIdempotencyInsertSQL, actorID, key, hash[:])
	if err != nil {
		return nil, err
	}
	var stored []byte
	var status *int
	var body json.RawMessage
	if err := tx.QueryRow(ctx, manualEntitlementIdempotencyReadSQL, actorID, key).Scan(&stored, &status, &body); err != nil {
		return nil, err
	}
	if !equalBytes(stored, hash[:]) {
		return nil, ErrConflict
	}
	if command.RowsAffected() == 1 {
		return nil, nil
	}
	if status == nil || *status != 200 || len(body) == 0 {
		return nil, ErrConflict
	}
	var replay ManualEntitlementGrant
	if err := json.Unmarshal(body, &replay); err != nil {
		return nil, fmt.Errorf("decode manual entitlement replay: %w", err)
	}
	return &replay, nil
}

func completeManualEntitlementIdempotency(ctx context.Context, tx pgx.Tx, actorID uuid.UUID, key string, hash [32]byte, result ManualEntitlementGrant) error {
	body, err := json.Marshal(result)
	if err != nil {
		return err
	}
	command, err := tx.Exec(ctx, manualEntitlementIdempotencyCompleteSQL, actorID, key, hash[:], body)
	if err != nil {
		return err
	}
	if command.RowsAffected() != 1 {
		return ErrConflict
	}
	return nil
}

func ensureManualEntitlementChangedOutbox(ctx context.Context, tx pgx.Tx, accountID uuid.UUID, entitlement Entitlement) error {
	_, err := tx.Exec(ctx, manualEntitlementEnsureOutboxSQL, accountID, entitlement.PlanKey,
		entitlement.CatalogVersion, entitlement.Version, entitlement.Source)
	return err
}

func auditManualEntitlement(ctx context.Context, tx pgx.Tx, actorID uuid.UUID, actorDeviceID any, action string, grantID uuid.UUID, traceID string, metadata json.RawMessage) error {
	_, err := tx.Exec(ctx, manualEntitlementAuditSQL, actorID, actorDeviceID, action, grantID, traceID, metadata)
	return err
}

func (s *Postgres) CreateManualEntitlementGrant(ctx context.Context, actorID, actorDeviceID uuid.UUID, input CreateManualEntitlementGrantInput) (ManualEntitlementGrant, error) {
	return s.createManualEntitlementGrant(ctx, actorID, actorDeviceID, input, false)
}

// CreateManualEntitlementGrantFromWeb is the browser-console capability. The
// Web adapter is responsible for the authenticated server-side session, CSRF,
// recent MFA and request idempotency checks. Browser sessions do not own a
// TermLink device UUID, so the audit row uses actor_device_id=NULL and records
// the explicit WEB actor channel.
func (s *Postgres) CreateManualEntitlementGrantFromWeb(ctx context.Context, actorID uuid.UUID, input CreateManualEntitlementGrantInput) (ManualEntitlementGrant, error) {
	return s.createManualEntitlementGrant(ctx, actorID, uuid.Nil, input, true)
}

func (s *Postgres) createManualEntitlementGrant(ctx context.Context, actorID, actorDeviceID uuid.UUID, input CreateManualEntitlementGrantInput, allowDeviceLessWeb bool) (ManualEntitlementGrant, error) {
	input.PlanKey = strings.TrimSpace(input.PlanKey)
	if actorID == uuid.Nil || (!allowDeviceLessWeb && actorDeviceID == uuid.Nil) || input.AccountID == uuid.Nil ||
		len(input.PlanKey) < 1 || len(input.PlanKey) > 64 || input.DurationDays < 1 ||
		input.DurationDays > manualEntitlementMaxDays || !validateManualEntitlementReason(input.Reason) ||
		!validateManualEntitlementKey(input.IdempotencyKey) || len(input.TraceID) > 256 {
		return ManualEntitlementGrant{}, ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if !allowDeviceLessWeb {
		if err := requireManualEntitlementActorDevice(ctx, tx, actorID, actorDeviceID); err != nil {
			return ManualEntitlementGrant{}, err
		}
	}
	hash := manualEntitlementRequestHash("manual-entitlement-create-v1", actorID,
		input.AccountID.String(), input.PlanKey, fmt.Sprint(input.DurationDays), input.Reason)
	replay, err := claimManualEntitlementIdempotency(ctx, tx, actorID, input.IdempotencyKey, hash)
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	if replay != nil {
		return *replay, tx.Commit(ctx)
	}
	if err := lockManualEntitlementTargetAccount(ctx, tx, input.AccountID); err != nil {
		return ManualEntitlementGrant{}, err
	}
	now := time.Now().UTC()
	var planKey string
	var catalogVersion int64
	err = tx.QueryRow(ctx, manualEntitlementCurrentPlanSQL, input.PlanKey, now).Scan(&planKey, &catalogVersion)
	if errors.Is(err, pgx.ErrNoRows) {
		return ManualEntitlementGrant{}, ErrNotFound
	}
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	grantID := uuid.New()
	endsAt := now.AddDate(0, 0, input.DurationDays)
	item, err := scanManualEntitlementGrant(tx.QueryRow(ctx, manualEntitlementInsertSQL,
		grantID, input.AccountID, planKey, catalogVersion, now, endsAt, actorID, input.Reason))
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	entitlement, err := recomputeEntitlement(ctx, tx, input.AccountID)
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	if err := ensureManualEntitlementChangedOutbox(ctx, tx, input.AccountID, entitlement); err != nil {
		return ManualEntitlementGrant{}, err
	}
	actorChannel := "DEVICE"
	var auditDeviceID any = actorDeviceID
	if allowDeviceLessWeb {
		actorChannel = "WEB"
		auditDeviceID = nil
	}
	metadata, _ := json.Marshal(map[string]any{
		"account_id": input.AccountID, "plan_key": planKey,
		"catalog_version": catalogVersion, "duration_days": input.DurationDays,
		"reason": input.Reason, "actor_channel": actorChannel,
	})
	if err := auditManualEntitlement(ctx, tx, actorID, auditDeviceID, "admin.entitlement.manual.create", item.ID, input.TraceID, metadata); err != nil {
		return ManualEntitlementGrant{}, err
	}
	if err := completeManualEntitlementIdempotency(ctx, tx, actorID, input.IdempotencyKey, hash, item); err != nil {
		return ManualEntitlementGrant{}, err
	}
	return item, tx.Commit(ctx)
}

func (s *Postgres) RevokeManualEntitlementGrant(ctx context.Context, actorID, actorDeviceID uuid.UUID, input RevokeManualEntitlementGrantInput) (ManualEntitlementGrant, error) {
	return s.revokeManualEntitlementGrant(ctx, actorID, actorDeviceID, input, false)
}

// RevokeManualEntitlementGrantFromWeb mirrors the strong Web trust boundary
// used by CreateManualEntitlementGrantFromWeb and never weakens the
// trusted-device requirement of the public device API.
func (s *Postgres) RevokeManualEntitlementGrantFromWeb(ctx context.Context, actorID uuid.UUID, input RevokeManualEntitlementGrantInput) (ManualEntitlementGrant, error) {
	return s.revokeManualEntitlementGrant(ctx, actorID, uuid.Nil, input, true)
}

func (s *Postgres) revokeManualEntitlementGrant(ctx context.Context, actorID, actorDeviceID uuid.UUID, input RevokeManualEntitlementGrantInput, allowDeviceLessWeb bool) (ManualEntitlementGrant, error) {
	if actorID == uuid.Nil || (!allowDeviceLessWeb && actorDeviceID == uuid.Nil) || input.GrantID == uuid.Nil ||
		!validateManualEntitlementReason(input.Reason) || !validateManualEntitlementKey(input.IdempotencyKey) ||
		len(input.TraceID) > 256 {
		return ManualEntitlementGrant{}, ErrDenied
	}
	tx, err := s.commerceTx(ctx, actorID, true, false)
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if !allowDeviceLessWeb {
		if err := requireManualEntitlementActorDevice(ctx, tx, actorID, actorDeviceID); err != nil {
			return ManualEntitlementGrant{}, err
		}
	}
	hash := manualEntitlementRequestHash("manual-entitlement-revoke-v1", actorID, input.GrantID.String(), input.Reason)
	replay, err := claimManualEntitlementIdempotency(ctx, tx, actorID, input.IdempotencyKey, hash)
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	if replay != nil {
		return *replay, tx.Commit(ctx)
	}
	var accountID uuid.UUID
	var source string
	err = tx.QueryRow(ctx, manualEntitlementLookupSQL, input.GrantID).Scan(&accountID, &source)
	if errors.Is(err, pgx.ErrNoRows) {
		return ManualEntitlementGrant{}, ErrNotFound
	}
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	if source != "MANUAL" {
		return ManualEntitlementGrant{}, ErrDenied
	}
	if err := lockManualEntitlementTargetAccount(ctx, tx, accountID); err != nil {
		return ManualEntitlementGrant{}, err
	}
	locked, err := scanManualEntitlementGrant(tx.QueryRow(ctx, manualEntitlementLockSQL, input.GrantID, accountID))
	if errors.Is(err, pgx.ErrNoRows) {
		return ManualEntitlementGrant{}, ErrDenied
	}
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	if locked.Status != "ACTIVE" {
		return ManualEntitlementGrant{}, ErrConflict
	}
	item, err := scanManualEntitlementGrant(tx.QueryRow(ctx, manualEntitlementRevokeSQL, input.GrantID, accountID))
	if errors.Is(err, pgx.ErrNoRows) {
		return ManualEntitlementGrant{}, ErrConflict
	}
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	entitlement, err := recomputeEntitlement(ctx, tx, accountID)
	if err != nil {
		return ManualEntitlementGrant{}, err
	}
	if err := ensureManualEntitlementChangedOutbox(ctx, tx, accountID, entitlement); err != nil {
		return ManualEntitlementGrant{}, err
	}
	actorChannel := "DEVICE"
	var auditDeviceID any = actorDeviceID
	if allowDeviceLessWeb {
		actorChannel = "WEB"
		auditDeviceID = nil
	}
	metadata, _ := json.Marshal(map[string]any{
		"account_id": accountID, "reason": input.Reason, "actor_channel": actorChannel,
	})
	if err := auditManualEntitlement(ctx, tx, actorID, auditDeviceID, "admin.entitlement.manual.revoke", item.ID, input.TraceID, metadata); err != nil {
		return ManualEntitlementGrant{}, err
	}
	if err := completeManualEntitlementIdempotency(ctx, tx, actorID, input.IdempotencyKey, hash, item); err != nil {
		return ManualEntitlementGrant{}, err
	}
	return item, tx.Commit(ctx)
}
