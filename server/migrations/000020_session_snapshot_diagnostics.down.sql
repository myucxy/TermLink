ALTER TABLE sessions
    DROP CONSTRAINT IF EXISTS sessions_snapshot_diagnostic_shape,
    DROP CONSTRAINT IF EXISTS sessions_snapshot_reason_code_safe,
    DROP CONSTRAINT IF EXISTS sessions_snapshot_status_allowed,
    DROP COLUMN IF EXISTS snapshot_reason_code,
    DROP COLUMN IF EXISTS snapshot_status;
