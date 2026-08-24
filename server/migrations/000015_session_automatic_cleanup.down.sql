DROP INDEX IF EXISTS sessions_automatic_cleanup_candidates;

ALTER TABLE sessions
    DROP CONSTRAINT IF EXISTS sessions_deletion_reason_check,
    DROP COLUMN IF EXISTS deletion_reason,
    DROP COLUMN IF EXISTS last_seen_at;

ALTER TABLE pcs
    DROP CONSTRAINT IF EXISTS pcs_session_heartbeat_capability_check,
    DROP COLUMN IF EXISTS session_heartbeat_enabled_at,
    DROP COLUMN IF EXISTS session_heartbeat_capable;
