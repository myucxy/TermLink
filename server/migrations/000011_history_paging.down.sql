DROP INDEX IF EXISTS snapshots_history_page_v1;
DROP INDEX IF EXISTS event_chunks_history_page_v1;

DROP TRIGGER IF EXISTS snapshots_assign_history_revision ON snapshots;
DROP TRIGGER IF EXISTS event_chunks_assign_history_revision ON event_chunks;
DROP FUNCTION IF EXISTS assign_session_history_revision();

ALTER TABLE snapshots DROP COLUMN IF EXISTS history_revision;
ALTER TABLE event_chunks DROP COLUMN IF EXISTS history_revision;
ALTER TABLE sessions DROP COLUMN IF EXISTS history_revision;
