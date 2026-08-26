ALTER TABLE devices
    DROP CONSTRAINT devices_device_type_check,
    ADD CONSTRAINT devices_device_type_check
        CHECK (device_type IN ('WINDOWS_PC', 'PC_AGENT', 'ANDROID')) NOT VALID;

ALTER TABLE devices VALIDATE CONSTRAINT devices_device_type_check;

DROP INDEX devices_one_bootstrap_pc_per_user;
CREATE UNIQUE INDEX devices_one_bootstrap_pc_per_user
    ON devices(user_id)
    WHERE device_type IN ('WINDOWS_PC', 'PC_AGENT')
      AND approved_by IS NULL
      AND trust_state = 'TRUSTED';

ALTER TABLE pcs
    ADD COLUMN platform text NOT NULL DEFAULT 'windows',
    ADD COLUMN architecture text NOT NULL DEFAULT 'unknown',
    ADD COLUMN platform_version text NOT NULL DEFAULT 'legacy',
    ADD COLUMN capabilities text[] NOT NULL
        DEFAULT ARRAY['realtime_terminal_v1', 'file_operations_v1']::text[];

UPDATE pcs
SET capabilities = ARRAY['realtime_terminal_v1', 'file_operations_v1']::text[]
    || CASE WHEN session_heartbeat_capable
        THEN ARRAY['terminal_snapshot_v1']::text[] ELSE ARRAY[]::text[] END
    || CASE WHEN cardinality(supported_shells) > 0 OR jsonb_array_length(terminal_commands) > 0
        THEN ARRAY['terminal_commands_v1']::text[] ELSE ARRAY[]::text[] END
    || CASE WHEN remote_agent_update_capable
        THEN ARRAY['remote_agent_update_v1']::text[] ELSE ARRAY[]::text[] END;

ALTER TABLE pcs
    ADD CONSTRAINT pcs_platform_allowed
        CHECK (platform IN ('windows', 'linux', 'macos')),
    ADD CONSTRAINT pcs_architecture_allowed
        CHECK (architecture IN ('x64', 'arm64', 'unknown')),
    ADD CONSTRAINT pcs_platform_version_bounded
        CHECK (octet_length(convert_to(platform_version, 'UTF8')) BETWEEN 1 AND 160),
    ADD CONSTRAINT pcs_capabilities_bounded
        CHECK (cardinality(capabilities) <= 64 AND array_position(capabilities, NULL) IS NULL);
