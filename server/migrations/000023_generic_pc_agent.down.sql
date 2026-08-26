ALTER TABLE pcs
    DROP CONSTRAINT pcs_capabilities_bounded,
    DROP CONSTRAINT pcs_platform_version_bounded,
    DROP CONSTRAINT pcs_architecture_allowed,
    DROP CONSTRAINT pcs_platform_allowed,
    DROP COLUMN capabilities,
    DROP COLUMN platform_version,
    DROP COLUMN architecture,
    DROP COLUMN platform;

DROP INDEX devices_one_bootstrap_pc_per_user;

UPDATE devices
SET device_type = 'WINDOWS_PC'
WHERE device_type = 'PC_AGENT';

ALTER TABLE devices
    DROP CONSTRAINT devices_device_type_check,
    ADD CONSTRAINT devices_device_type_check
        CHECK (device_type IN ('WINDOWS_PC', 'ANDROID')) NOT VALID;

ALTER TABLE devices VALIDATE CONSTRAINT devices_device_type_check;

CREATE UNIQUE INDEX devices_one_bootstrap_pc_per_user
    ON devices(user_id)
    WHERE device_type = 'WINDOWS_PC'
      AND approved_by IS NULL
      AND trust_state = 'TRUSTED';

