-- Terminal history is now retained and served by the online Windows host.
-- Queue deletion of every legacy object before removing the server indexes;
-- the shared history.object.delete worker remains because file-transfer and
-- privacy workflows still depend on the same object-storage infrastructure.
INSERT INTO outbox(event_type,aggregate_id,payload)
SELECT 'history.object.delete',session_id,jsonb_build_object('object_key',object_key)
FROM (
    SELECT session_id,object_key FROM event_chunks
    UNION
    SELECT session_id,object_key FROM snapshots
    UNION
    SELECT session_id,object_key FROM transcript_pages
    UNION
    SELECT session_id,object_key FROM history_uploads
) legacy_terminal_history
ON CONFLICT DO NOTHING;

DROP TRIGGER IF EXISTS transcript_pages_assign_history_revision ON transcript_pages;
DROP TRIGGER IF EXISTS snapshots_assign_history_revision ON snapshots;
DROP TRIGGER IF EXISTS event_chunks_assign_history_revision ON event_chunks;

DROP TABLE transcript_publications;
DROP TABLE transcript_pages;
DROP TABLE history_uploads;
DROP TABLE snapshots;
DROP TABLE event_chunks;

DROP FUNCTION assign_session_history_revision();

ALTER TABLE sessions
    DROP COLUMN history_revision;
