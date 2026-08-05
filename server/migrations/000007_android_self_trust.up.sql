CREATE TABLE device_self_trust_tokens (
    jti_hash bytea PRIMARY KEY CHECK (octet_length(jti_hash) = 32),
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    consumed_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX device_self_trust_tokens_device
    ON device_self_trust_tokens(user_id, device_id, consumed_at DESC);
