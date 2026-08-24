ALTER TABLE history_uploads
    ADD COLUMN transcript_initial_before_line_id bigint CHECK (transcript_initial_before_line_id > 0),
    ADD COLUMN transcript_page_count integer CHECK (transcript_page_count BETWEEN 0 AND 100000),
    ADD CONSTRAINT history_uploads_transcript_publication_check CHECK (
        (transcript_initial_before_line_id IS NULL AND transcript_page_count IS NULL)
        OR
        (kind = 'SNAPSHOT'
            AND format_name = 'TERMLINK_TERMINAL_SNAPSHOT'
            AND format_version = 1
            AND transcript_initial_before_line_id IS NOT NULL
            AND transcript_page_count IS NOT NULL)
    );

CREATE TABLE transcript_publications (
    session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    recipient_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    as_of_snapshot_seq bigint NOT NULL CHECK (as_of_snapshot_seq > 0),
    initial_before_line_id bigint NOT NULL CHECK (initial_before_line_id > 0),
    expected_page_count integer NOT NULL CHECK (expected_page_count BETWEEN 0 AND 100000),
    completed_revision bigint CHECK (completed_revision > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    PRIMARY KEY (session_id, recipient_device_id, as_of_snapshot_seq),
    CONSTRAINT transcript_publications_completion_check CHECK (
        (completed_revision IS NULL AND completed_at IS NULL)
        OR (completed_revision IS NOT NULL AND completed_at IS NOT NULL)
    )
);
