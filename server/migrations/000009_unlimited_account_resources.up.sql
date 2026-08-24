-- PC and live-session quantities are now unlimited for every account. Keep
-- the legacy columns for catalog/client compatibility and use -1 as the
-- explicit wire/storage sentinel for unlimited.
ALTER TABLE plans
    DROP CONSTRAINT IF EXISTS plans_max_remote_pcs_check,
    DROP CONSTRAINT IF EXISTS plans_max_live_sessions_check;

ALTER TABLE entitlements
    DROP CONSTRAINT IF EXISTS entitlements_max_remote_pcs_check,
    DROP CONSTRAINT IF EXISTS entitlements_max_live_sessions_check;

ALTER TABLE plans
    ADD CONSTRAINT plans_max_remote_pcs_check CHECK (max_remote_pcs = -1 OR max_remote_pcs >= 0),
    ADD CONSTRAINT plans_max_live_sessions_check CHECK (max_live_sessions = -1 OR max_live_sessions >= 0);

ALTER TABLE entitlements
    ADD CONSTRAINT entitlements_max_remote_pcs_check CHECK (max_remote_pcs = -1 OR max_remote_pcs >= 0),
    ADD CONSTRAINT entitlements_max_live_sessions_check CHECK (max_live_sessions = -1 OR max_live_sessions >= 0);

UPDATE plans
SET max_remote_pcs = -1,
    max_live_sessions = -1;

UPDATE entitlements
SET max_remote_pcs = -1,
    max_live_sessions = -1;
