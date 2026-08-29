DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM history_uploads
        WHERE format_name = 'TERMLINK_TERMINAL_CORE_SNAPSHOT'
          AND format_version = 2
    ) THEN
        RAISE EXCEPTION
            'cannot remove terminal-core Snapshot V2 while pending V2 uploads exist';
    END IF;
END;
$$;

ALTER TABLE history_uploads
    DROP CONSTRAINT history_uploads_transcript_publication_check,
    ADD CONSTRAINT history_uploads_transcript_publication_check CHECK (
        (transcript_initial_before_line_id IS NULL AND transcript_page_count IS NULL)
        OR
        (kind = 'SNAPSHOT'
            AND format_name = 'TERMLINK_TERMINAL_SNAPSHOT'
            AND format_version = 1
            AND transcript_initial_before_line_id IS NOT NULL
            AND transcript_page_count IS NOT NULL)
    );
