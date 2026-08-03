CREATE TABLE history_uploads (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    actor_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    kind text NOT NULL CHECK (kind IN ('CHUNK', 'SNAPSHOT')),
    object_key text NOT NULL UNIQUE,
    key_id text NOT NULL CHECK (char_length(key_id) BETWEEN 8 AND 128),
    ciphertext_sha256 bytea NOT NULL CHECK (octet_length(ciphertext_sha256) = 32),
    size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 67108864),
    start_seq bigint CHECK (start_seq > 0),
    end_seq bigint CHECK (end_seq >= start_seq),
    snapshot_seq bigint CHECK (snapshot_seq >= 0),
    rows integer CHECK (rows BETWEEN 1 AND 65535),
    columns integer CHECK (columns BETWEEN 1 AND 65535),
    format_name text,
    format_version integer CHECK (format_version > 0),
    expires_at timestamptz NOT NULL,
    committed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (
        (kind = 'CHUNK' AND start_seq IS NOT NULL AND end_seq IS NOT NULL AND snapshot_seq IS NULL AND rows IS NULL AND columns IS NULL AND format_name IS NULL AND format_version IS NULL)
        OR
        (kind = 'SNAPSHOT' AND start_seq IS NULL AND end_seq IS NULL AND snapshot_seq IS NOT NULL AND rows IS NOT NULL AND columns IS NOT NULL AND format_name IS NOT NULL AND format_version IS NOT NULL)
    )
);

CREATE INDEX history_uploads_pending
    ON history_uploads(expires_at, id)
    WHERE committed_at IS NULL;
