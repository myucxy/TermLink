DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM transcript_publications)
       OR EXISTS (
           SELECT 1 FROM history_uploads
           WHERE transcript_initial_before_line_id IS NOT NULL
              OR transcript_page_count IS NOT NULL
       ) THEN
        RAISE EXCEPTION 'migration 16 cannot be reversed while transcript publication metadata exists';
    END IF;
END $$;

DROP TABLE transcript_publications;

ALTER TABLE history_uploads
    DROP CONSTRAINT history_uploads_transcript_publication_check,
    DROP COLUMN transcript_page_count,
    DROP COLUMN transcript_initial_before_line_id;
