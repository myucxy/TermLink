DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM event_chunks
        GROUP BY session_id, start_seq, end_seq
        HAVING count(*) > 1
    ) OR EXISTS (
        SELECT 1 FROM snapshots
        GROUP BY session_id, snapshot_seq, format_name, format_version
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'cannot downgrade history recipients: multiple recipient objects would violate the legacy uniqueness constraints';
    END IF;
END $$;

DROP INDEX IF EXISTS snapshots_recipient_history_page_v1;
DROP INDEX IF EXISTS event_chunks_recipient_history_page_v1;

ALTER TABLE snapshots
    DROP CONSTRAINT snapshots_session_recipient_sequence_format_key,
    ADD CONSTRAINT snapshots_session_id_snapshot_seq_format_name_format_versio_key
        UNIQUE (session_id, snapshot_seq, format_name, format_version),
    DROP COLUMN recipient_device_id;

ALTER TABLE event_chunks
    DROP CONSTRAINT event_chunks_session_recipient_range_key,
    ADD CONSTRAINT event_chunks_session_id_start_seq_end_seq_key
        UNIQUE (session_id, start_seq, end_seq),
    DROP COLUMN recipient_device_id;

ALTER TABLE history_uploads
    DROP COLUMN recipient_key_epoch,
    DROP COLUMN recipient_device_id;
