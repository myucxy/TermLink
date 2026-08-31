CREATE TABLE human_verification_settings (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    enabled boolean NOT NULL DEFAULT false,
    site_key text NOT NULL DEFAULT '' CHECK (char_length(site_key) <= 256),
    secret_ciphertext bytea,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_by uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (secret_ciphertext IS NULL OR octet_length(secret_ciphertext) BETWEEN 29 AND 1024),
    CHECK (NOT enabled OR (site_key <> '' AND secret_ciphertext IS NOT NULL))
);

INSERT INTO human_verification_settings(singleton) VALUES(true);
