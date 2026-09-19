-- Single-node deployments keep the server master secret in PostgreSQL so a
-- fresh Compose start needs only database credentials and a public URL.
CREATE TABLE server_runtime_secrets (
    name text PRIMARY KEY CHECK (name ~ '^[a-z][a-z0-9_-]{2,127}$'),
    value bytea NOT NULL CHECK (octet_length(value) BETWEEN 32 AND 128),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
