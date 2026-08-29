-- Promote terminal-core's canonical cross-platform Snapshot V2 while keeping
-- all existing Portable Snapshot V1 rows readable.  Transcript publications
-- continue to use the same immutable sequence/line-id catalog.
ALTER TABLE history_uploads
    DROP CONSTRAINT history_uploads_transcript_publication_check,
    ADD CONSTRAINT history_uploads_transcript_publication_check CHECK (
        (transcript_initial_before_line_id IS NULL AND transcript_page_count IS NULL)
        OR
        (kind = 'SNAPSHOT'
            AND (
                (format_name = 'TERMLINK_TERMINAL_SNAPSHOT' AND format_version = 1)
                OR
                (format_name = 'TERMLINK_TERMINAL_CORE_SNAPSHOT' AND format_version = 2)
            )
            AND transcript_initial_before_line_id IS NOT NULL
            AND transcript_page_count IS NOT NULL)
    );
