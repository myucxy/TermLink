package store

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

type RedemptionPreview struct {
	PlanKey          string     `json:"plan_key"`
	CatalogVersion   int64      `json:"catalog_version"`
	DurationDays     int64      `json:"duration_days,omitempty"`
	FixedExpiresAt   *time.Time `json:"fixed_expires_at,omitempty"`
	StackingRule     string     `json:"stacking_rule"`
	CurrentPlanKey   string     `json:"current_plan_key,omitempty"`
	CurrentExpiresAt *time.Time `json:"current_expires_at,omitempty"`
	ProjectedEndAt   *time.Time `json:"projected_end_at,omitempty"`
}

// PreviewRedemption is advisory only. ClaimRedemption repeats every check
// under a serializable transaction and is the sole inventory-consuming path.
func (s *Postgres) PreviewRedemption(ctx context.Context, accountID uuid.UUID, codeHash [32]byte) (RedemptionPreview, error) {
	var active bool
	if err := s.Pool.QueryRow(ctx, `SELECT status='ACTIVE' FROM users WHERE id=$1`, accountID).Scan(&active); err != nil || !active {
		return RedemptionPreview{}, ErrDenied
	}
	var result RedemptionPreview
	var codeID uuid.UUID
	var codeStatus, batchStatus string
	var maxUses, usedCount, maxPerAccount int
	var validFrom, validUntil *time.Time
	var targetPrice int64
	err := s.Pool.QueryRow(ctx, `
		SELECT c.id,c.status,c.max_uses,c.used_count,b.status,b.plan_key,b.catalog_version,
		       coalesce(b.duration_days,0),b.fixed_expires_at,b.max_claims_per_account,
		       b.stacking_rule,b.valid_from,b.valid_until,p.price_minor
		FROM redemption_codes c
		JOIN redemption_batches b ON b.id=c.batch_id
		JOIN catalog_versions cv ON cv.version=b.catalog_version
		JOIN plans p ON p.catalog_version_id=cv.id AND p.plan_key=b.plan_key
		WHERE c.code_hash=$1`, codeHash[:]).Scan(
		&codeID, &codeStatus, &maxUses, &usedCount, &batchStatus, &result.PlanKey,
		&result.CatalogVersion, &result.DurationDays, &result.FixedExpiresAt,
		&maxPerAccount, &result.StackingRule, &validFrom, &validUntil, &targetPrice,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return RedemptionPreview{}, ErrRedemptionInvalid
	}
	if err != nil {
		return RedemptionPreview{}, err
	}
	now := time.Now().UTC()
	if codeStatus != "ACTIVE" || batchStatus != "ACTIVE" {
		return RedemptionPreview{}, ErrRedemptionInvalid
	}
	if (validFrom != nil && now.Before(*validFrom)) || (validUntil != nil && !now.Before(*validUntil)) {
		return RedemptionPreview{}, ErrRedemptionExpired
	}
	if usedCount >= maxUses {
		return RedemptionPreview{}, ErrRedemptionExhausted
	}
	var claimed int
	if err := s.Pool.QueryRow(ctx, `SELECT count(*) FROM redemption_claims WHERE code_id=$1 AND account_id=$2`, codeID, accountID).Scan(&claimed); err != nil {
		return RedemptionPreview{}, err
	}
	if claimed >= maxPerAccount {
		return RedemptionPreview{}, ErrRedemptionAlreadyUsed
	}
	var currentPrice int64
	err = s.Pool.QueryRow(ctx, `
		SELECT g.plan_key,p.price_minor,g.ends_at
		FROM entitlement_grants g
		JOIN catalog_versions cv ON cv.version=g.catalog_version
		JOIN plans p ON p.catalog_version_id=cv.id AND p.plan_key=g.plan_key
		WHERE g.account_id=$1 AND g.status='ACTIVE' AND g.starts_at<=now()
		  AND (g.ends_at IS NULL OR g.ends_at>now())
		ORDER BY p.price_minor DESC,p.sort_order DESC,g.ends_at DESC NULLS FIRST,g.created_at DESC
		LIMIT 1`, accountID).Scan(&result.CurrentPlanKey, &currentPrice, &result.CurrentExpiresAt)
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return RedemptionPreview{}, err
	}
	samePlan := err == nil && result.CurrentPlanKey == result.PlanKey
	if err == nil && targetPrice < currentPrice {
		return RedemptionPreview{}, ErrRedemptionPlanConflict
	}
	if err == nil && result.StackingRule == "REJECT_CONFLICT" && !samePlan {
		return RedemptionPreview{}, ErrRedemptionPlanConflict
	}
	result.ProjectedEndAt, err = computeGrantEnd(now, result.DurationDays, result.FixedExpiresAt, result.CurrentExpiresAt, samePlan)
	return result, err
}

type AccountRedemption struct {
	ClaimID, CodeID, GrantID uuid.UUID
	AccountID                uuid.UUID
	CodePrefix               string
	PlanKey                  string
	CatalogVersion           int64
	ClaimedAt                time.Time
	StartsAt                 time.Time
	EndsAt                   *time.Time
	Status                   string
}

func (s *Postgres) ListAccountRedemptions(ctx context.Context, accountID uuid.UUID, limit int) ([]AccountRedemption, error) {
	rows, err := s.Pool.Query(ctx, `
		SELECT claim.id,claim.code_id,claim.grant_id,claim.account_id,code.code_prefix,entitlement_grant.plan_key,
		       entitlement_grant.catalog_version,claim.claimed_at,entitlement_grant.starts_at,entitlement_grant.ends_at,entitlement_grant.status
		FROM redemption_claims claim
		JOIN redemption_codes code ON code.id=claim.code_id
		JOIN entitlement_grants entitlement_grant ON entitlement_grant.id=claim.grant_id
		WHERE claim.account_id=$1
		ORDER BY claim.claimed_at DESC,claim.id DESC LIMIT $2`, accountID, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []AccountRedemption
	for rows.Next() {
		var item AccountRedemption
		if err := rows.Scan(&item.ClaimID, &item.CodeID, &item.GrantID, &item.AccountID, &item.CodePrefix,
			&item.PlanKey, &item.CatalogVersion, &item.ClaimedAt, &item.StartsAt,
			&item.EndsAt, &item.Status); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) ListAccountEntitlementGrants(ctx context.Context, accountID uuid.UUID, limit int) ([]EntitlementGrant, error) {
	rows, err := s.Pool.Query(ctx, `
		SELECT id,account_id,source_type,source_id,plan_key,catalog_version,starts_at,
		       ends_at,grace_until,provider_reference_hash,status,metadata,created_at,updated_at
		FROM entitlement_grants WHERE account_id=$1
		ORDER BY created_at DESC,id DESC LIMIT $2`, accountID, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []EntitlementGrant
	for rows.Next() {
		var item EntitlementGrant
		if err := rows.Scan(&item.ID, &item.AccountID, &item.SourceType, &item.SourceID,
			&item.PlanKey, &item.CatalogVersion, &item.StartsAt, &item.EndsAt,
			&item.GraceUntil, &item.ProviderRefHash, &item.Status, &item.Metadata,
			&item.CreatedAt, &item.UpdatedAt); err != nil {
			return nil, err
		}
		// Provider digests are internal correlation data, never account output.
		item.ProviderRefHash = nil
		result = append(result, item)
	}
	return result, rows.Err()
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

func (s *Postgres) ListInvitationRedemptions(ctx context.Context, actorID uuid.UUID, limit int) ([]InvitationRedemption, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
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
		       CASE
		           WHEN account_profile.user_id IS NOT NULL THEN 'ACCOUNT'
		           WHEN registration_profile.redemption_id IS NOT NULL THEN 'REGISTRATION'
		           ELSE ''
		       END,
		       redemption.state,redemption.reserved_until,redemption.provider_accepted_at,
		       redemption.redeemed_at,redemption.released_at,redemption.created_at
		FROM invitation_redemptions redemption
		JOIN invitation_codes code ON code.id=redemption.code_id
		JOIN invitation_batches batch ON batch.id=code.batch_id
		LEFT JOIN account_identity_profiles account_profile ON account_profile.user_id=redemption.user_id
		LEFT JOIN invitation_registration_profiles registration_profile ON registration_profile.redemption_id=redemption.id
		ORDER BY redemption.created_at DESC,redemption.id DESC LIMIT $1`, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []InvitationRedemption
	for rows.Next() {
		var item InvitationRedemption
		if err := rows.Scan(&item.ID, &item.CodeID, &item.AttemptID, &item.UserID,
			&item.CodePrefix, &item.BatchName, &item.Username, &item.Nickname,
			&item.EmailMasked, &item.ProfileSource, &item.State, &item.ReservedUntil, &item.ProviderAcceptedAt,
			&item.RedeemedAt, &item.ReleasedAt, &item.CreatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) ListRedemptionClaims(ctx context.Context, actorID uuid.UUID, limit int) ([]AccountRedemption, error) {
	tx, err := s.commerceTx(ctx, actorID, false, false)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, `
		SELECT claim.id,claim.code_id,claim.grant_id,claim.account_id,code.code_prefix,entitlement_grant.plan_key,
		       entitlement_grant.catalog_version,claim.claimed_at,entitlement_grant.starts_at,entitlement_grant.ends_at,entitlement_grant.status
		FROM redemption_claims claim
		JOIN redemption_codes code ON code.id=claim.code_id
		JOIN entitlement_grants entitlement_grant ON entitlement_grant.id=claim.grant_id
		ORDER BY claim.claimed_at DESC,claim.id DESC LIMIT $1`, normalizePage(limit))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var result []AccountRedemption
	for rows.Next() {
		var item AccountRedemption
		if err := rows.Scan(&item.ClaimID, &item.CodeID, &item.GrantID, &item.AccountID, &item.CodePrefix,
			&item.PlanKey, &item.CatalogVersion, &item.ClaimedAt, &item.StartsAt,
			&item.EndsAt, &item.Status); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}
