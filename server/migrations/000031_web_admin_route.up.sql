CREATE TABLE web_portal_settings (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    admin_route_ciphertext bytea,
    admin_route_hmac bytea CHECK (admin_route_hmac IS NULL OR octet_length(admin_route_hmac)=32),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_by uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((admin_route_ciphertext IS NULL) = (admin_route_hmac IS NULL))
);

INSERT INTO web_portal_settings(singleton) VALUES(true);
