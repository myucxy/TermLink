CREATE TABLE account_email_change_challenges (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    email_hash bytea NOT NULL CHECK (octet_length(email_hash)=32),
    code_hash bytea NOT NULL CHECK (octet_length(code_hash)=32),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 5),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (expires_at > created_at)
);

CREATE INDEX account_email_change_challenges_email_created
    ON account_email_change_challenges(email_hash,created_at DESC);

CREATE INDEX account_email_change_challenges_expiry
    ON account_email_change_challenges(expires_at)
    WHERE consumed_at IS NULL;
