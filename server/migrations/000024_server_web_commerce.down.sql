DROP TABLE IF EXISTS reconciliation_runs;
DROP TABLE IF EXISTS refunds;
DROP TABLE IF EXISTS payment_notifications;
DROP TABLE IF EXISTS payment_transactions;
DROP TABLE IF EXISTS orders;
ALTER TABLE payment_channels DROP CONSTRAINT IF EXISTS payment_channels_active_version_fk;
DROP TABLE IF EXISTS payment_channel_versions;
DROP TABLE IF EXISTS payment_channels;

ALTER TABLE plans DROP COLUMN IF EXISTS currency, DROP COLUMN IF EXISTS price_minor;

DROP TABLE IF EXISTS redemption_claims;
DROP TABLE IF EXISTS entitlement_grants;
DROP TABLE IF EXISTS redemption_codes;
DROP TABLE IF EXISTS redemption_batches;
DROP TABLE IF EXISTS invitation_redemptions;
DROP TABLE IF EXISTS invitation_codes;
DROP TABLE IF EXISTS invitation_batches;
DROP TABLE IF EXISTS registration_policies;

-- v23 has no native ALIPAY/WECHAT_PAY provider. Preserve those rows as MANUAL.
-- Re-key compatibility references with the unique subscription id so hashes
-- scoped to two different v24 providers cannot collide after provider collapse.
-- The table lock and collision loop also cover a legal pre-existing MANUAL
-- digest that happens to equal the deterministic compatibility digest.
LOCK TABLE subscriptions IN SHARE ROW EXCLUSIVE MODE;
DO $$
DECLARE
    item record;
    compatibility_hash bytea;
BEGIN
    FOR item IN
        SELECT id, account_id, provider, provider_ref_hash
        FROM subscriptions
        WHERE provider IN ('ALIPAY', 'WECHAT_PAY')
        ORDER BY id
    LOOP
        compatibility_hash := uuid_send(item.id) || substring(item.provider_ref_hash FROM 1 FOR 16);
        WHILE EXISTS (
            SELECT 1
            FROM subscriptions
            WHERE provider='MANUAL' AND provider_ref_hash=compatibility_hash
        ) LOOP
            compatibility_hash := uuid_send(item.id) || uuid_send(gen_random_uuid());
        END LOOP;
        UPDATE subscriptions
        SET provider='MANUAL', provider_ref_hash=compatibility_hash
        WHERE id=item.id;
        UPDATE entitlements
        SET source='MANUAL', provider_reference_hash=compatibility_hash
        WHERE account_id=item.account_id
          AND source=item.provider
          AND provider_reference_hash=item.provider_ref_hash;
    END LOOP;
END $$;
ALTER TABLE subscriptions
    DROP CONSTRAINT IF EXISTS subscriptions_provider_check,
    ADD CONSTRAINT subscriptions_provider_check CHECK (provider IN ('GOOGLE_PLAY', 'WEB', 'MANUAL'));

UPDATE entitlements SET source='MANUAL' WHERE source IN ('ALIPAY', 'WECHAT_PAY', 'REDEMPTION');
ALTER TABLE entitlements
    DROP CONSTRAINT IF EXISTS entitlements_source_check,
    ADD CONSTRAINT entitlements_source_check CHECK (source IN ('DEFAULT', 'GOOGLE_PLAY', 'WEB', 'MANUAL'));
