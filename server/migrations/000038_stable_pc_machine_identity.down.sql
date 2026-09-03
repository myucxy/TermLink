DROP INDEX IF EXISTS pcs_owner_machine_identity_unique;

ALTER TABLE pcs
    DROP CONSTRAINT IF EXISTS pcs_machine_id_hash_length,
    DROP COLUMN IF EXISTS machine_id_hash;
