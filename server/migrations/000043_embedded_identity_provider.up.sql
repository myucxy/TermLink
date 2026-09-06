-- TermLink's embedded identity provider deliberately owns only the first-party
-- OIDC surface used by the Web, Windows, and Android clients.  Raw identity
-- data is isolated from the existing application-account projection so an
-- external provider can still be used during the migration window.
CREATE TABLE identity_accounts (
    subject uuid PRIMARY KEY,
    username text NOT NULL CHECK (char_length(username) BETWEEN 3 AND 128),
    nickname text NOT NULL CHECK (char_length(nickname) BETWEEN 1 AND 128),
    email text NOT NULL CHECK (char_length(email) BETWEEN 3 AND 320),
    email_verified boolean NOT NULL DEFAULT false,
    enabled boolean NOT NULL DEFAULT true,
    password_hash bytea CHECK (password_hash IS NULL OR octet_length(password_hash)=32),
    password_salt bytea CHECK (password_salt IS NULL OR octet_length(password_salt)=16),
    password_memory_kib integer NOT NULL DEFAULT 65536 CHECK (password_memory_kib BETWEEN 32768 AND 262144),
    password_iterations integer NOT NULL DEFAULT 3 CHECK (password_iterations BETWEEN 2 AND 10),
    password_parallelism smallint NOT NULL DEFAULT 2 CHECK (password_parallelism BETWEEN 1 AND 8),
    password_reset_required boolean NOT NULL DEFAULT false,
    failed_login_count integer NOT NULL DEFAULT 0 CHECK (failed_login_count BETWEEN 0 AND 1000),
    locked_until timestamptz,
    auth_epoch bigint NOT NULL DEFAULT 1 CHECK (auth_epoch > 0),
    realm_roles text[] NOT NULL DEFAULT ARRAY[]::text[],
    registration_attempt_id uuid UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    last_login_at timestamptz,
    CHECK ((password_hash IS NULL) = (password_salt IS NULL))
);

CREATE UNIQUE INDEX identity_accounts_username_unique
    ON identity_accounts(lower(username));
CREATE UNIQUE INDEX identity_accounts_email_unique
    ON identity_accounts(lower(email));

-- Fosite stores only token signatures and a bounded, credential-free request
-- snapshot.  Plain authorization codes, refresh tokens, passwords, and TOTP
-- secrets never enter this table.
CREATE TABLE identity_oauth_sessions (
    session_type text NOT NULL CHECK (session_type IN ('AUTHORIZE_CODE','PKCE','OPENID','ACCESS','REFRESH')),
    signature text NOT NULL CHECK (char_length(signature) BETWEEN 16 AND 512),
    request_id uuid NOT NULL,
    access_signature text NOT NULL DEFAULT '' CHECK (char_length(access_signature) <= 512),
    request_payload jsonb NOT NULL,
    active boolean NOT NULL DEFAULT true,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (session_type,signature)
);

CREATE INDEX identity_oauth_sessions_request
    ON identity_oauth_sessions(request_id,session_type);
CREATE INDEX identity_oauth_sessions_expiry
    ON identity_oauth_sessions(expires_at);

CREATE TABLE identity_client_assertion_jtis (
    jti_hash bytea PRIMARY KEY CHECK (octet_length(jti_hash)=32),
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX identity_client_assertion_jtis_expiry
    ON identity_client_assertion_jtis(expires_at);

-- The private PKCS#8 key is encrypted by a key derived from the existing
-- server secret.  Keeping the key in PostgreSQL permits restart-safe signing
-- and future overlap during rotation without adding a new deployment secret.
CREATE TABLE identity_signing_keys (
    key_id text PRIMARY KEY CHECK (char_length(key_id) BETWEEN 16 AND 128),
    algorithm text NOT NULL CHECK (algorithm='RS256'),
    public_jwk jsonb NOT NULL,
    private_key_ciphertext bytea NOT NULL CHECK (octet_length(private_key_ciphertext) BETWEEN 256 AND 16384),
    not_before timestamptz NOT NULL,
    sign_until timestamptz NOT NULL,
    verify_until timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (sign_until > not_before),
    CHECK (verify_until >= sign_until)
);

CREATE TABLE identity_totp_credentials (
    subject uuid PRIMARY KEY REFERENCES identity_accounts(subject) ON DELETE CASCADE,
    secret_ciphertext bytea NOT NULL CHECK (octet_length(secret_ciphertext) BETWEEN 32 AND 1024),
    last_accepted_counter bigint NOT NULL DEFAULT -1 CHECK (last_accepted_counter >= -1),
    confirmed_at timestamptz,
    enabled_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE identity_recovery_codes (
    subject uuid NOT NULL REFERENCES identity_accounts(subject) ON DELETE CASCADE,
    code_hash bytea NOT NULL CHECK (octet_length(code_hash)=32),
    consumed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (subject,code_hash)
);

CREATE TABLE identity_password_reset_tokens (
    token_hash bytea PRIMARY KEY CHECK (octet_length(token_hash)=32),
    subject uuid NOT NULL REFERENCES identity_accounts(subject) ON DELETE CASCADE,
    auth_epoch bigint NOT NULL CHECK (auth_epoch > 0),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (expires_at > created_at)
);

CREATE INDEX identity_password_reset_tokens_subject
    ON identity_password_reset_tokens(subject,created_at DESC);
CREATE INDEX identity_password_reset_tokens_expiry
    ON identity_password_reset_tokens(expires_at) WHERE consumed_at IS NULL;
