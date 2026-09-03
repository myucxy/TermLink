ALTER TABLE pcs
    ADD COLUMN machine_id_hash bytea,
    ADD CONSTRAINT pcs_machine_id_hash_length
        CHECK (machine_id_hash IS NULL OR octet_length(machine_id_hash) = 32);

CREATE UNIQUE INDEX pcs_owner_machine_identity_unique
    ON pcs(owner_user_id, machine_id_hash)
    WHERE machine_id_hash IS NOT NULL;
