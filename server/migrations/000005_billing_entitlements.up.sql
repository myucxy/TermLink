-- Item-6 billing metadata. Provider references remain hashes only; the raw
-- purchase token is never persisted by the server.
ALTER TABLE entitlements
    ADD COLUMN source text NOT NULL DEFAULT 'DEFAULT'
        CHECK (source IN ('DEFAULT', 'GOOGLE_PLAY', 'WEB', 'MANUAL')),
    ADD COLUMN provider_reference_hash bytea
        CHECK (provider_reference_hash IS NULL OR octet_length(provider_reference_hash) = 32),
    ADD COLUMN grace_until timestamptz;

CREATE INDEX entitlements_provider_reference
    ON entitlements(provider_reference_hash)
    WHERE provider_reference_hash IS NOT NULL;

CREATE INDEX subscriptions_provider_product
    ON subscriptions(provider, product_id, status);
