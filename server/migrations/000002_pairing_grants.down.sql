DROP INDEX IF EXISTS pairing_challenges_active_target;

ALTER TABLE pairing_challenges
    DROP COLUMN IF EXISTS permission,
    DROP COLUMN IF EXISTS pc_id;
