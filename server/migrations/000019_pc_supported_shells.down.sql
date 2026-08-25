ALTER TABLE pcs
    DROP CONSTRAINT IF EXISTS pcs_supported_shells_allowed,
    DROP COLUMN IF EXISTS supported_shells;
