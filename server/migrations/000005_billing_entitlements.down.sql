DROP INDEX IF EXISTS subscriptions_provider_product;
DROP INDEX IF EXISTS entitlements_provider_reference;
ALTER TABLE entitlements
    DROP COLUMN IF EXISTS grace_until,
    DROP COLUMN IF EXISTS provider_reference_hash,
    DROP COLUMN IF EXISTS source;
