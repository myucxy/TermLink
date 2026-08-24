-- Reverting the unlimited policy restores a safe positive default. Existing
-- rows are intentionally clamped because -1 is not valid in the old schema.
UPDATE entitlements
SET max_remote_pcs = GREATEST(max_remote_pcs, 1),
    max_live_sessions = GREATEST(max_live_sessions, 1);

UPDATE plans
SET max_remote_pcs = GREATEST(max_remote_pcs, 1),
    max_live_sessions = GREATEST(max_live_sessions, 1);

ALTER TABLE plans
    DROP CONSTRAINT IF EXISTS plans_max_remote_pcs_check,
    DROP CONSTRAINT IF EXISTS plans_max_live_sessions_check;

ALTER TABLE entitlements
    DROP CONSTRAINT IF EXISTS entitlements_max_remote_pcs_check,
    DROP CONSTRAINT IF EXISTS entitlements_max_live_sessions_check;

ALTER TABLE plans
    ADD CONSTRAINT plans_max_remote_pcs_check CHECK (max_remote_pcs >= 0),
    ADD CONSTRAINT plans_max_live_sessions_check CHECK (max_live_sessions >= 0);

ALTER TABLE entitlements
    ADD CONSTRAINT entitlements_max_remote_pcs_check CHECK (max_remote_pcs >= 0),
    ADD CONSTRAINT entitlements_max_live_sessions_check CHECK (max_live_sessions >= 0);
