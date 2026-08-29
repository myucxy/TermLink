-- Server Web commercial control plane. Codes are stored only as keyed
-- digests; payment secrets are represented by external secret references.

CREATE TABLE registration_policies (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    mode text NOT NULL CHECK (mode IN ('OPEN', 'INVITE_REQUIRED', 'CLOSED')),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_by uuid REFERENCES users(id) ON DELETE SET NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO registration_policies(singleton, mode) VALUES(true, 'OPEN');

CREATE TABLE invitation_batches (
    id uuid PRIMARY KEY,
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 128),
    status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('DRAFT', 'ACTIVE', 'DISABLED')),
    default_max_uses integer NOT NULL DEFAULT 1 CHECK (default_max_uses > 0),
    valid_from timestamptz,
    valid_until timestamptz,
    email_domain text CHECK (email_domain IS NULL OR char_length(email_domain) BETWEEN 1 AND 253),
    email_hash bytea CHECK (email_hash IS NULL OR octet_length(email_hash) = 32),
    gift_plan_key text CHECK (gift_plan_key IS NULL OR char_length(gift_plan_key) BETWEEN 1 AND 64),
    gift_duration_days integer CHECK (gift_duration_days IS NULL OR gift_duration_days > 0),
    note text NOT NULL DEFAULT '' CHECK (char_length(note) <= 1000),
    created_by uuid NOT NULL REFERENCES users(id),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (valid_until IS NULL OR valid_from IS NULL OR valid_until > valid_from),
    CHECK ((gift_plan_key IS NULL) = (gift_duration_days IS NULL))
);

CREATE TABLE invitation_codes (
    id uuid PRIMARY KEY,
    batch_id uuid NOT NULL REFERENCES invitation_batches(id) ON DELETE CASCADE,
    code_hash bytea NOT NULL UNIQUE CHECK (octet_length(code_hash) = 32),
    code_prefix text NOT NULL CHECK (char_length(code_prefix) BETWEEN 4 AND 16),
    max_uses integer NOT NULL CHECK (max_uses > 0),
    used_count integer NOT NULL DEFAULT 0 CHECK (used_count >= 0 AND used_count <= max_uses),
    status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'DISABLED')),
    valid_from timestamptz,
    valid_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (valid_until IS NULL OR valid_from IS NULL OR valid_until > valid_from)
);

CREATE INDEX invitation_codes_batch_status ON invitation_codes(batch_id, status, created_at DESC);

CREATE TABLE invitation_redemptions (
    id uuid PRIMARY KEY,
    code_id uuid NOT NULL REFERENCES invitation_codes(id) ON DELETE RESTRICT,
    registration_attempt_id uuid NOT NULL UNIQUE,
    email_hash bytea NOT NULL CHECK (octet_length(email_hash) = 32),
    user_id uuid REFERENCES users(id) ON DELETE SET NULL,
    state text NOT NULL CHECK (state IN ('RESERVED', 'PROVIDER_ACCEPTED', 'REDEEMED', 'RELEASED')),
    reserved_until timestamptz NOT NULL,
    provider_accepted_at timestamptz,
    redeemed_at timestamptz,
    released_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (reserved_until > created_at),
    CHECK (state <> 'PROVIDER_ACCEPTED' OR provider_accepted_at IS NOT NULL),
    CHECK (state <> 'REDEEMED' OR (user_id IS NOT NULL AND redeemed_at IS NOT NULL)),
    CHECK (state <> 'RELEASED' OR released_at IS NOT NULL)
);

CREATE INDEX invitation_redemptions_pending ON invitation_redemptions(reserved_until)
    WHERE state IN ('RESERVED', 'PROVIDER_ACCEPTED');

-- Keep the OIDC callback lookup unambiguous even when two registration
-- requests for the same normalized e-mail race each other.
CREATE UNIQUE INDEX invitation_redemptions_one_provider_accepted_email
    ON invitation_redemptions(email_hash)
    WHERE state = 'PROVIDER_ACCEPTED' AND user_id IS NULL;

CREATE TABLE redemption_batches (
    id uuid PRIMARY KEY,
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 128),
    plan_key text NOT NULL CHECK (char_length(plan_key) BETWEEN 1 AND 64),
    catalog_version bigint NOT NULL CHECK (catalog_version > 0),
    duration_days integer,
    fixed_expires_at timestamptz,
    max_claims_per_account integer NOT NULL DEFAULT 1 CHECK (max_claims_per_account > 0),
    stacking_rule text NOT NULL DEFAULT 'EXTEND_SAME_OR_UPGRADE' CHECK (stacking_rule IN ('EXTEND_SAME_OR_UPGRADE', 'REJECT_CONFLICT')),
    status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('DRAFT', 'ACTIVE', 'DISABLED')),
    valid_from timestamptz,
    valid_until timestamptz,
    note text NOT NULL DEFAULT '' CHECK (char_length(note) <= 1000),
    created_by uuid NOT NULL REFERENCES users(id),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((duration_days IS NOT NULL) <> (fixed_expires_at IS NOT NULL)),
    CHECK (duration_days IS NULL OR duration_days > 0),
    CHECK (valid_until IS NULL OR valid_from IS NULL OR valid_until > valid_from)
);

CREATE TABLE redemption_codes (
    id uuid PRIMARY KEY,
    batch_id uuid NOT NULL REFERENCES redemption_batches(id) ON DELETE CASCADE,
    code_hash bytea NOT NULL UNIQUE CHECK (octet_length(code_hash) = 32),
    code_prefix text NOT NULL CHECK (char_length(code_prefix) BETWEEN 4 AND 16),
    max_uses integer NOT NULL DEFAULT 1 CHECK (max_uses > 0),
    used_count integer NOT NULL DEFAULT 0 CHECK (used_count >= 0 AND used_count <= max_uses),
    status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'DISABLED')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX redemption_codes_batch_status ON redemption_codes(batch_id, status, created_at DESC);

CREATE TABLE entitlement_grants (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    source_type text NOT NULL CHECK (source_type IN ('DEFAULT', 'GOOGLE_PLAY', 'WEB', 'ALIPAY', 'WECHAT_PAY', 'REDEMPTION', 'MANUAL')),
    source_id uuid,
    plan_key text NOT NULL CHECK (char_length(plan_key) BETWEEN 1 AND 64),
    catalog_version bigint NOT NULL CHECK (catalog_version > 0),
    starts_at timestamptz NOT NULL,
    ends_at timestamptz,
    grace_until timestamptz,
    provider_reference_hash bytea CHECK (provider_reference_hash IS NULL OR octet_length(provider_reference_hash) = 32),
    status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('PENDING', 'ACTIVE', 'EXPIRED', 'REVOKED')),
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (ends_at IS NULL OR ends_at > starts_at),
    CHECK (grace_until IS NULL OR grace_until > starts_at)
);

CREATE UNIQUE INDEX entitlement_grants_source ON entitlement_grants(source_type, source_id)
    WHERE source_id IS NOT NULL;
CREATE INDEX entitlement_grants_account_active ON entitlement_grants(account_id, starts_at, ends_at)
    WHERE status IN ('PENDING', 'ACTIVE');

-- Preserve every subscription, including an active source that is not the
-- currently materialized entitlement. Without this pass, an account with both
-- GOOGLE_PLAY and legacy WEB purchases could not restore the other source when
-- the currently selected one expires.
INSERT INTO entitlement_grants(
    id, account_id, source_type, source_id, plan_key, catalog_version, starts_at, ends_at,
    grace_until, provider_reference_hash,
    status, metadata, created_at, updated_at
)
SELECT md5(s.id::text || ':termlink:subscription-grant:migration:24')::uuid,
       s.account_id, s.provider, s.id, s.plan_key, s.catalog_version,
       s.created_at,
       CASE WHEN legacy.ends_at > s.created_at THEN legacy.ends_at END,
       CASE WHEN s.grace_until > s.created_at THEN s.grace_until END,
       s.provider_ref_hash,
       CASE
           WHEN s.status = 'REVOKED' THEN 'REVOKED'
           WHEN s.status = 'EXPIRED' THEN 'EXPIRED'
           WHEN s.status IN ('GRACE', 'CANCELED')
                AND (legacy.ends_at IS NULL OR legacy.ends_at <= now()) THEN 'EXPIRED'
           ELSE 'ACTIVE'
       END,
       jsonb_strip_nulls(jsonb_build_object(
           'migration', 24,
           'product_id', s.product_id,
           'legacy_subscription_status', s.status
       )),
       s.created_at, s.updated_at
FROM subscriptions s
CROSS JOIN LATERAL (
    SELECT
        CASE
            WHEN s.status = 'ACTIVE' THEN NULL::timestamptz
            WHEN s.status = 'GRACE' THEN s.grace_until
            WHEN s.status = 'CANCELED' THEN s.renews_at
            ELSE coalesce(s.renews_at, s.grace_until)
        END AS ends_at
) legacy;

-- Preserve materialized DEFAULT/MANUAL or inconsistent legacy state that has
-- no matching subscription. Times admitted by v23 may precede effective_at;
-- retain that source as EXPIRED without letting a formerly legal row abort the
-- v24 migration's stricter Grant constraints.
INSERT INTO entitlement_grants(
    id, account_id, source_type, source_id, plan_key, catalog_version, starts_at, ends_at,
    grace_until, provider_reference_hash,
    status, metadata, created_at, updated_at
)
SELECT md5(e.account_id::text || ':termlink:entitlement-grant:migration:24')::uuid,
       e.account_id, e.source, NULL, e.plan_key, e.catalog_version,
       e.effective_at,
       CASE WHEN legacy.ends_at > e.effective_at THEN legacy.ends_at END,
       CASE WHEN e.grace_until > e.effective_at THEN e.grace_until END,
       e.provider_reference_hash,
       CASE WHEN legacy.ends_at IS NOT NULL AND legacy.ends_at <= now()
            THEN 'EXPIRED' ELSE 'ACTIVE' END,
       jsonb_strip_nulls(jsonb_build_object(
           'migration', 24,
           'legacy_expires_at', e.expires_at,
           'legacy_grace_until', e.grace_until
       )),
       e.updated_at, e.updated_at
FROM entitlements e
CROSS JOIN LATERAL (
    SELECT coalesce(e.expires_at, e.grace_until) AS ends_at
) legacy
WHERE NOT EXISTS (
    SELECT 1
    FROM subscriptions s
    WHERE s.account_id=e.account_id
      AND s.provider=e.source
      AND s.provider_ref_hash=e.provider_reference_hash
);

CREATE TABLE redemption_claims (
    id uuid PRIMARY KEY,
    code_id uuid NOT NULL REFERENCES redemption_codes(id) ON DELETE RESTRICT,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    idempotency_key text NOT NULL CHECK (char_length(idempotency_key) BETWEEN 8 AND 128),
    grant_id uuid NOT NULL UNIQUE REFERENCES entitlement_grants(id) ON DELETE RESTRICT,
    claimed_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (account_id, idempotency_key)
);

CREATE INDEX redemption_claims_code_account ON redemption_claims(code_id, account_id, claimed_at DESC);

ALTER TABLE plans
    ADD COLUMN price_minor bigint NOT NULL DEFAULT 0 CHECK (price_minor >= 0),
    ADD COLUMN currency text NOT NULL DEFAULT 'CNY' CHECK (currency ~ '^[A-Z]{3}$');

CREATE TABLE payment_channels (
    id uuid PRIMARY KEY,
    provider text NOT NULL CHECK (provider IN ('ALIPAY', 'WECHAT_PAY')),
    scene text NOT NULL CHECK (scene IN ('NATIVE', 'WEB', 'H5', 'JSAPI')),
    enabled boolean NOT NULL DEFAULT false,
    active_version_id uuid,
    health_status text NOT NULL DEFAULT 'UNCONFIGURED' CHECK (health_status IN ('UNCONFIGURED', 'HEALTHY', 'DEGRADED', 'DISABLED')),
    last_webhook_at timestamptz,
    last_error_code text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (provider, scene),
    CHECK (NOT enabled OR active_version_id IS NOT NULL)
);

CREATE TABLE payment_channel_versions (
    id uuid PRIMARY KEY,
    channel_id uuid NOT NULL REFERENCES payment_channels(id) ON DELETE CASCADE,
    version bigint NOT NULL CHECK (version > 0),
    environment text NOT NULL CHECK (environment IN ('SANDBOX', 'PRODUCTION')),
    public_config jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(public_config) = 'object'),
    secret_refs jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(secret_refs) = 'object'),
    status text NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT', 'VALIDATED', 'RETIRED')),
    validated_at timestamptz,
    validation_summary text NOT NULL DEFAULT '',
    created_by uuid NOT NULL REFERENCES users(id),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (channel_id, version),
    UNIQUE (id, channel_id),
    CHECK (status = 'DRAFT' OR validated_at IS NOT NULL)
);

ALTER TABLE payment_channels
    ADD CONSTRAINT payment_channels_active_version_fk
    FOREIGN KEY (active_version_id, id) REFERENCES payment_channel_versions(id, channel_id)
    ON DELETE RESTRICT;

CREATE TABLE orders (
    id uuid PRIMARY KEY,
    order_no text NOT NULL UNIQUE CHECK (char_length(order_no) BETWEEN 16 AND 64),
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    plan_key text NOT NULL CHECK (char_length(plan_key) BETWEEN 1 AND 64),
    catalog_version bigint NOT NULL CHECK (catalog_version > 0),
    provider text NOT NULL CHECK (provider IN ('ALIPAY', 'WECHAT_PAY')),
    payment_scene text NOT NULL CHECK (payment_scene IN ('NATIVE', 'WEB', 'H5', 'JSAPI')),
    amount_minor bigint NOT NULL CHECK (amount_minor > 0),
    currency text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    status text NOT NULL CHECK (status IN ('CREATED', 'PENDING_PAYMENT', 'PAID', 'FULFILLING', 'FULFILLED', 'CLOSED', 'REFUNDING', 'REFUNDED', 'FAILED')),
    idempotency_key text NOT NULL CHECK (char_length(idempotency_key) BETWEEN 8 AND 128),
    expires_at timestamptz NOT NULL,
    paid_at timestamptz,
    fulfilled_at timestamptz,
    closed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (account_id, idempotency_key),
    UNIQUE (id, provider, amount_minor, currency),
    CHECK (expires_at > created_at),
    CHECK (paid_at IS NULL OR paid_at >= created_at),
    CHECK (fulfilled_at IS NULL OR (paid_at IS NOT NULL AND fulfilled_at >= paid_at)),
    CHECK (closed_at IS NULL OR closed_at >= created_at),
    CHECK (status NOT IN ('PAID', 'FULFILLING', 'FULFILLED', 'REFUNDING', 'REFUNDED') OR paid_at IS NOT NULL),
    CHECK (status NOT IN ('FULFILLED', 'REFUNDING', 'REFUNDED') OR fulfilled_at IS NOT NULL)
);

CREATE INDEX orders_account_created ON orders(account_id, created_at DESC, id DESC);
CREATE INDEX orders_pending ON orders(expires_at) WHERE status IN ('CREATED', 'PENDING_PAYMENT');
CREATE INDEX orders_fulfillment ON orders(updated_at) WHERE status IN ('PAID', 'FULFILLING', 'FAILED');

CREATE TABLE payment_transactions (
    id uuid PRIMARY KEY,
    order_id uuid NOT NULL,
    provider text NOT NULL CHECK (provider IN ('ALIPAY', 'WECHAT_PAY')),
    provider_trade_no_hash bytea CHECK (provider_trade_no_hash IS NULL OR octet_length(provider_trade_no_hash) = 32),
    status text NOT NULL CHECK (status IN ('INITIATED', 'PENDING', 'SUCCEEDED', 'FAILED', 'CLOSED', 'REFUNDED')),
    amount_minor bigint NOT NULL CHECK (amount_minor > 0),
    currency text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    payment_payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    failure_code text,
    requested_at timestamptz NOT NULL DEFAULT now(),
    paid_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (id, order_id),
    CONSTRAINT payment_transactions_order_terms_fk
        FOREIGN KEY (order_id, provider, amount_minor, currency)
        REFERENCES orders(id, provider, amount_minor, currency) ON DELETE RESTRICT,
    CHECK (paid_at IS NULL OR paid_at >= requested_at),
    CHECK (status NOT IN ('SUCCEEDED', 'REFUNDED') OR paid_at IS NOT NULL)
);

CREATE INDEX payment_transactions_order ON payment_transactions(order_id, requested_at DESC);
CREATE UNIQUE INDEX payment_transactions_provider_trade ON payment_transactions(provider, provider_trade_no_hash)
    WHERE provider_trade_no_hash IS NOT NULL;
CREATE UNIQUE INDEX payment_transactions_one_settled_per_order ON payment_transactions(order_id)
    WHERE status IN ('SUCCEEDED', 'REFUNDED');

CREATE TABLE payment_notifications (
    id uuid PRIMARY KEY,
    provider text NOT NULL CHECK (provider IN ('ALIPAY', 'WECHAT_PAY')),
    notification_id_hash bytea NOT NULL CHECK (octet_length(notification_id_hash) = 32),
    order_no text,
    signature_valid boolean NOT NULL,
    processing_state text NOT NULL CHECK (processing_state IN ('RECEIVED', 'PROCESSED', 'REJECTED', 'RETRY')),
    payload_ciphertext bytea,
    payload_digest bytea NOT NULL CHECK (octet_length(payload_digest) = 32),
    failure_code text,
    received_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz,
    UNIQUE (provider, notification_id_hash)
);

CREATE INDEX payment_notifications_recent ON payment_notifications(received_at DESC);

CREATE TABLE refunds (
    id uuid PRIMARY KEY,
    refund_no text NOT NULL UNIQUE CHECK (char_length(refund_no) BETWEEN 16 AND 64),
    order_id uuid NOT NULL REFERENCES orders(id) ON DELETE RESTRICT,
    transaction_id uuid NOT NULL,
    amount_minor bigint NOT NULL CHECK (amount_minor > 0),
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 500),
    status text NOT NULL CHECK (status IN ('CREATED', 'PENDING', 'SUCCEEDED', 'FAILED', 'CLOSED')),
    failure_code text CHECK (failure_code IS NULL OR char_length(failure_code) BETWEEN 1 AND 128),
    provider_ref_hash bytea CHECK (provider_ref_hash IS NULL OR octet_length(provider_ref_hash) = 32),
    requested_by uuid NOT NULL REFERENCES users(id),
    requested_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT refunds_transaction_order_fk
        FOREIGN KEY (transaction_id, order_id)
        REFERENCES payment_transactions(id, order_id) ON DELETE RESTRICT,
    CHECK (status <> 'SUCCEEDED' OR (completed_at IS NOT NULL AND provider_ref_hash IS NOT NULL)),
    CHECK ((status = 'FAILED') = (failure_code IS NOT NULL)),
    CHECK (completed_at IS NULL OR completed_at >= requested_at)
);

CREATE INDEX refunds_order ON refunds(order_id, requested_at DESC);
CREATE UNIQUE INDEX refunds_provider_reference ON refunds(provider_ref_hash)
    WHERE provider_ref_hash IS NOT NULL;

CREATE TABLE reconciliation_runs (
    id uuid PRIMARY KEY,
    provider text NOT NULL CHECK (provider IN ('ALIPAY', 'WECHAT_PAY')),
    period_start date NOT NULL,
    period_end date NOT NULL,
    status text NOT NULL CHECK (status IN ('PENDING', 'RUNNING', 'SUCCEEDED', 'FAILED')),
    result_summary jsonb NOT NULL DEFAULT '{}'::jsonb,
    requested_by uuid REFERENCES users(id) ON DELETE SET NULL,
    started_at timestamptz,
    completed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (period_end >= period_start),
    UNIQUE (provider, period_start, period_end)
);

ALTER TABLE subscriptions
    DROP CONSTRAINT IF EXISTS subscriptions_provider_check,
    ADD CONSTRAINT subscriptions_provider_check CHECK (provider IN ('GOOGLE_PLAY', 'WEB', 'ALIPAY', 'WECHAT_PAY', 'MANUAL'));

ALTER TABLE entitlements
    DROP CONSTRAINT IF EXISTS entitlements_source_check,
    ADD CONSTRAINT entitlements_source_check CHECK (source IN ('DEFAULT', 'GOOGLE_PLAY', 'WEB', 'ALIPAY', 'WECHAT_PAY', 'REDEMPTION', 'MANUAL'));

INSERT INTO payment_channels(id, provider, scene)
VALUES
    ('00000000-0000-4000-8000-000000000101', 'ALIPAY', 'NATIVE'),
    ('00000000-0000-4000-8000-000000000102', 'WECHAT_PAY', 'NATIVE');
