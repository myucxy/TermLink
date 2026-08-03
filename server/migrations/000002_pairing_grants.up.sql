ALTER TABLE pairing_challenges
    ADD COLUMN pc_id uuid REFERENCES pcs(id) ON DELETE CASCADE,
    ADD COLUMN permission text CHECK (permission IN ('VIEW', 'CONTROL', 'MANAGE'));

CREATE INDEX pairing_challenges_active_target
    ON pairing_challenges(user_id, target_device_id, expires_at)
    WHERE consumed_at IS NULL;
