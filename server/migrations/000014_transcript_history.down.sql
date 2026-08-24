DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM transcript_pages)
       OR EXISTS (SELECT 1 FROM history_uploads WHERE kind='TRANSCRIPT')
       OR EXISTS (
           SELECT 1 FROM outbox
           WHERE event_type='history.object.delete'
             AND payload->>'object_key' LIKE '%/transcript/%'
             AND processed_at IS NULL
       ) THEN
        RAISE EXCEPTION 'migration 14 cannot be reversed while transcript object references exist';
    END IF;
END $$;

DROP INDEX IF EXISTS outbox_history_object_delete_unique;
DROP TABLE IF EXISTS transcript_pages;

ALTER TABLE history_uploads
    DROP CONSTRAINT history_uploads_shape_check,
    DROP CONSTRAINT history_uploads_transcript_metadata_check,
    DROP CONSTRAINT history_uploads_kind_check,
    DROP COLUMN last_line_id,
    DROP COLUMN first_line_id,
    DROP COLUMN as_of_snapshot_seq,
    ADD CONSTRAINT history_uploads_kind_check CHECK (kind IN ('CHUNK', 'SNAPSHOT')),
    ADD CONSTRAINT history_uploads_check1 CHECK (
        (kind = 'CHUNK'
            AND start_seq IS NOT NULL AND end_seq IS NOT NULL
            AND snapshot_seq IS NULL AND rows IS NULL AND columns IS NULL
            AND format_name IS NULL AND format_version IS NULL)
        OR
        (kind = 'SNAPSHOT'
            AND start_seq IS NULL AND end_seq IS NULL
            AND snapshot_seq IS NOT NULL AND rows IS NOT NULL AND columns IS NOT NULL
            AND format_name IS NOT NULL AND format_version IS NOT NULL)
    );
