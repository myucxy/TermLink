ALTER TABLE pcs
    ADD COLUMN key_epoch bigint NOT NULL DEFAULT 1 CHECK (key_epoch > 0);
