CREATE TABLE service_email_settings (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    service_enabled boolean NOT NULL DEFAULT false,
    registration_verification_enabled boolean NOT NULL DEFAULT false,
    password_reset_enabled boolean NOT NULL DEFAULT false,
    smtp_host text NOT NULL DEFAULT '' CHECK (char_length(smtp_host) <= 253),
    smtp_port integer NOT NULL DEFAULT 587 CHECK (smtp_port BETWEEN 1 AND 65535),
    smtp_security text NOT NULL DEFAULT 'STARTTLS' CHECK (smtp_security IN ('STARTTLS','TLS')),
    from_name text NOT NULL DEFAULT '' CHECK (char_length(from_name) <= 128),
    from_address text NOT NULL DEFAULT '' CHECK (char_length(from_address) <= 320),
    reply_to text NOT NULL DEFAULT '' CHECK (char_length(reply_to) <= 320),
    smtp_username text NOT NULL DEFAULT '' CHECK (char_length(smtp_username) <= 320),
    smtp_password_ciphertext bytea,
    status text NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','VERIFIED')),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    tested_at timestamptz,
    updated_by uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (NOT registration_verification_enabled OR service_enabled),
    CHECK (NOT password_reset_enabled OR service_enabled),
    CHECK (NOT service_enabled OR status='VERIFIED')
);

INSERT INTO service_email_settings(singleton) VALUES(true);

CREATE TABLE registration_email_challenges (
    id uuid PRIMARY KEY,
    email_hash bytea NOT NULL CHECK (octet_length(email_hash)=32),
    code_hash bytea NOT NULL CHECK (octet_length(code_hash)=32),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 5),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (expires_at > created_at)
);

CREATE INDEX registration_email_challenges_email_created
    ON registration_email_challenges(email_hash,created_at DESC);

CREATE INDEX registration_email_challenges_expiry
    ON registration_email_challenges(expires_at)
    WHERE consumed_at IS NULL;

