CREATE TABLE users (
    id uuid PRIMARY KEY,
    oidc_subject_hash bytea NOT NULL UNIQUE CHECK (octet_length(oidc_subject_hash) = 32),
    status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'LOCKED', 'DELETED')),
    is_admin boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE devices (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_type text NOT NULL CHECK (device_type IN ('WINDOWS_PC', 'ANDROID')),
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 128),
    signing_public_key bytea NOT NULL CHECK (octet_length(signing_public_key) = 32),
    exchange_public_key bytea NOT NULL CHECK (octet_length(exchange_public_key) = 32),
    key_id text NOT NULL CHECK (char_length(key_id) BETWEEN 8 AND 128),
    trust_state text NOT NULL DEFAULT 'PENDING' CHECK (trust_state IN ('PENDING', 'TRUSTED', 'REVOKED')),
    approved_by uuid REFERENCES devices(id),
    approved_at timestamptz,
    revoked_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz,
    UNIQUE (user_id, key_id),
    UNIQUE (user_id, signing_public_key)
);

CREATE UNIQUE INDEX devices_one_bootstrap_pc_per_user
    ON devices(user_id)
    WHERE device_type = 'WINDOWS_PC' AND approved_by IS NULL AND trust_state = 'TRUSTED';

CREATE TABLE pairing_challenges (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    approving_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    target_device_id uuid REFERENCES devices(id) ON DELETE CASCADE,
    code_hash bytea NOT NULL CHECK (octet_length(code_hash) = 32),
    challenge_nonce bytea NOT NULL CHECK (octet_length(challenge_nonce) = 32),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (expires_at > created_at)
);

CREATE UNIQUE INDEX pairing_challenges_active_code
    ON pairing_challenges(user_id, code_hash) WHERE consumed_at IS NULL;

CREATE TABLE pcs (
    id uuid PRIMARY KEY,
    owner_user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id uuid NOT NULL UNIQUE REFERENCES devices(id) ON DELETE CASCADE,
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 128),
    agent_version text NOT NULL,
    remote_routing_enabled boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz
);

CREATE TABLE device_grants (
    device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    pc_id uuid NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
    permission text NOT NULL CHECK (permission IN ('VIEW', 'CONTROL', 'MANAGE')),
    approved_by uuid NOT NULL REFERENCES devices(id),
    expires_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (device_id, pc_id)
);

CREATE TABLE sessions (
    id uuid PRIMARY KEY,
    pc_id uuid NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
    shell_type text NOT NULL CHECK (char_length(shell_type) BETWEEN 1 AND 64),
    title_cipher bytea,
    state text NOT NULL CHECK (state IN ('RUNNING', 'INTERRUPTED', 'EXITED', 'DELETED')),
    columns integer NOT NULL CHECK (columns BETWEEN 1 AND 65535),
    rows integer NOT NULL CHECK (rows BETWEEN 1 AND 65535),
    last_output_seq bigint NOT NULL DEFAULT 0 CHECK (last_output_seq >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    ended_at timestamptz,
    deleted_at timestamptz
);

CREATE INDEX sessions_pc_state_created ON sessions(pc_id, state, created_at DESC);

CREATE TABLE control_leases (
    session_id uuid PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
    holder_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    lease_id uuid NOT NULL UNIQUE,
    expires_at timestamptz NOT NULL,
    version bigint NOT NULL CHECK (version > 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE event_chunks (
    id uuid PRIMARY KEY,
    session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    start_seq bigint NOT NULL CHECK (start_seq > 0),
    end_seq bigint NOT NULL CHECK (end_seq >= start_seq),
    object_key text NOT NULL UNIQUE,
    key_id text NOT NULL,
    ciphertext_sha256 bytea NOT NULL CHECK (octet_length(ciphertext_sha256) = 32),
    size_bytes bigint NOT NULL CHECK (size_bytes > 0),
    expires_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    UNIQUE (session_id, start_seq, end_seq)
);

CREATE INDEX event_chunks_session_sequence ON event_chunks(session_id, start_seq, end_seq)
    WHERE deleted_at IS NULL;

CREATE TABLE snapshots (
    id uuid PRIMARY KEY,
    session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    snapshot_seq bigint NOT NULL CHECK (snapshot_seq >= 0),
    rows integer NOT NULL CHECK (rows BETWEEN 1 AND 65535),
    columns integer NOT NULL CHECK (columns BETWEEN 1 AND 65535),
    object_key text NOT NULL UNIQUE,
    key_id text NOT NULL,
    ciphertext_sha256 bytea NOT NULL CHECK (octet_length(ciphertext_sha256) = 32),
    size_bytes bigint NOT NULL CHECK (size_bytes > 0),
    format_name text NOT NULL DEFAULT 'BOUNDED_EVENT_REPLAY',
    format_version integer NOT NULL CHECK (format_version > 0),
    expires_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    UNIQUE (session_id, snapshot_seq, format_name, format_version)
);

CREATE INDEX snapshots_session_sequence ON snapshots(session_id, snapshot_seq DESC)
    WHERE deleted_at IS NULL;

CREATE TABLE audit_events (
    id bigserial PRIMARY KEY,
    user_id uuid REFERENCES users(id) ON DELETE SET NULL,
    actor_device_id uuid REFERENCES devices(id) ON DELETE SET NULL,
    action text NOT NULL,
    target_type text NOT NULL,
    target_id uuid,
    result text NOT NULL CHECK (result IN ('SUCCEEDED', 'DENIED', 'FAILED')),
    ip_hash bytea CHECK (ip_hash IS NULL OR octet_length(ip_hash) = 32),
    trace_id text NOT NULL,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX audit_events_user_cursor ON audit_events(user_id, id DESC);

CREATE TABLE catalog_versions (
    id uuid PRIMARY KEY,
    version bigint NOT NULL UNIQUE CHECK (version > 0),
    status text NOT NULL CHECK (status IN ('DRAFT', 'PUBLISHED', 'RETIRED')),
    published_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX catalog_one_published ON catalog_versions(status) WHERE status = 'PUBLISHED';

CREATE TABLE plans (
    id uuid PRIMARY KEY,
    catalog_version_id uuid NOT NULL REFERENCES catalog_versions(id) ON DELETE CASCADE,
    plan_key text NOT NULL CHECK (char_length(plan_key) BETWEEN 1 AND 64),
    display_name text NOT NULL,
    description text NOT NULL DEFAULT '',
    billing_period text NOT NULL CHECK (billing_period IN ('FREE', 'MONTHLY', 'YEARLY')),
    max_remote_pcs integer NOT NULL CHECK (max_remote_pcs >= 0),
    max_live_sessions integer NOT NULL CHECK (max_live_sessions >= 0),
    feature_flags jsonb NOT NULL DEFAULT '{}'::jsonb,
    channel_products jsonb NOT NULL DEFAULT '{}'::jsonb,
    sort_order integer NOT NULL DEFAULT 0,
    visible_platforms text[] NOT NULL DEFAULT ARRAY['ANDROID','WEB']::text[],
    available_from timestamptz,
    available_until timestamptz,
    fallback_plan_key text,
    UNIQUE (catalog_version_id, plan_key)
);

CREATE TABLE subscriptions (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    provider text NOT NULL CHECK (provider IN ('GOOGLE_PLAY', 'WEB', 'MANUAL')),
    product_id text NOT NULL,
    provider_ref_hash bytea NOT NULL CHECK (octet_length(provider_ref_hash) = 32),
    plan_key text NOT NULL,
    catalog_version bigint NOT NULL CHECK (catalog_version > 0),
    status text NOT NULL CHECK (status IN ('ACTIVE', 'GRACE', 'CANCELED', 'EXPIRED', 'REVOKED')),
    renews_at timestamptz,
    grace_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (provider, provider_ref_hash)
);

CREATE INDEX subscriptions_account_status ON subscriptions(account_id, status);

CREATE TABLE entitlements (
    account_id uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    plan_key text NOT NULL,
    catalog_version bigint NOT NULL CHECK (catalog_version > 0),
    max_remote_pcs integer NOT NULL CHECK (max_remote_pcs >= 0),
    max_live_sessions integer NOT NULL CHECK (max_live_sessions >= 0),
    feature_flags jsonb NOT NULL DEFAULT '{}'::jsonb,
    version bigint NOT NULL CHECK (version > 0),
    effective_at timestamptz NOT NULL,
    expires_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE quota_selections (
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    resource_type text NOT NULL CHECK (resource_type IN ('PC', 'SESSION')),
    resource_id uuid NOT NULL,
    selected_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, resource_type, resource_id)
);

CREATE TABLE idempotency_keys (
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    idempotency_key text NOT NULL CHECK (char_length(idempotency_key) BETWEEN 8 AND 128),
    request_hash bytea NOT NULL CHECK (octet_length(request_hash) = 32),
    response_status integer,
    response_body jsonb,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, idempotency_key)
);

CREATE TABLE outbox (
    id bigserial PRIMARY KEY,
    event_type text NOT NULL,
    aggregate_id uuid,
    payload jsonb NOT NULL,
    available_at timestamptz NOT NULL DEFAULT now(),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    processed_at timestamptz,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX outbox_pending ON outbox(available_at, id) WHERE processed_at IS NULL;

INSERT INTO catalog_versions (id, version, status, published_at)
VALUES ('00000000-0000-4000-8000-000000000001', 1, 'PUBLISHED', now());

INSERT INTO plans (
    id, catalog_version_id, plan_key, display_name, description, billing_period,
    max_remote_pcs, max_live_sessions, feature_flags, sort_order, fallback_plan_key
) VALUES (
    '00000000-0000-4000-8000-000000000002',
    '00000000-0000-4000-8000-000000000001',
    'free', 'Free', 'Default self-hosted plan', 'FREE', 1, 3,
    '{"cloud_history":false,"history_search":true,"snapshot_replay":true}'::jsonb,
    0, 'free'
);
