ALTER TABLE pcs
    ADD COLUMN session_heartbeat_capable boolean NOT NULL DEFAULT false,
    ADD COLUMN session_heartbeat_enabled_at timestamptz,
    ADD CONSTRAINT pcs_session_heartbeat_capability_check CHECK (
        session_heartbeat_capable = (session_heartbeat_enabled_at IS NOT NULL)
    );

ALTER TABLE sessions
    ADD COLUMN last_seen_at timestamptz,
    ADD COLUMN deletion_reason text,
    ADD CONSTRAINT sessions_deletion_reason_check CHECK (
        deletion_reason IS NULL OR deletion_reason IN (
            'USER_REQUESTED', 'AUTO_ENDED', 'AUTO_STALE'
        )
    );

-- Existing terminal rows need a conservative server-known metadata timestamp.
-- Running rows on legacy Agents are never stale-cleaned because their PC has
-- not opted in to explicit per-session heartbeats.
UPDATE sessions
SET last_seen_at = COALESCE(ended_at, created_at);

-- All tombstones written before this migration came from the explicit delete
-- endpoint and therefore must remain permanent.
UPDATE sessions
SET deletion_reason = 'USER_REQUESTED'
WHERE deleted_at IS NOT NULL;

CREATE INDEX sessions_automatic_cleanup_candidates
    ON sessions(state, COALESCE(ended_at, last_seen_at), id)
    WHERE deleted_at IS NULL;
