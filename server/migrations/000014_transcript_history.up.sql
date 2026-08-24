ALTER TABLE history_uploads
    DROP CONSTRAINT history_uploads_kind_check,
    DROP CONSTRAINT history_uploads_check1,
    ADD COLUMN as_of_snapshot_seq bigint CHECK (as_of_snapshot_seq > 0),
    ADD COLUMN first_line_id bigint CHECK (first_line_id > 0),
    ADD COLUMN last_line_id bigint CHECK (last_line_id >= first_line_id),
    ADD CONSTRAINT history_uploads_kind_check
        CHECK (kind IN ('CHUNK', 'SNAPSHOT', 'TRANSCRIPT')),
    ADD CONSTRAINT history_uploads_transcript_metadata_check CHECK (
        kind <> 'TRANSCRIPT' OR (
            size_bytes <= 524288
            AND format_name = 'TERMLINK_TERMINAL_TRANSCRIPT'
            AND format_version = 1
            AND recipient_device_id IS NOT NULL
            AND recipient_key_epoch IS NOT NULL
        )
    ),
    ADD CONSTRAINT history_uploads_shape_check CHECK (
        (kind = 'CHUNK'
            AND start_seq IS NOT NULL AND end_seq IS NOT NULL
            AND snapshot_seq IS NULL AND rows IS NULL AND columns IS NULL
            AND format_name IS NULL AND format_version IS NULL
            AND as_of_snapshot_seq IS NULL AND first_line_id IS NULL AND last_line_id IS NULL)
        OR
        (kind = 'SNAPSHOT'
            AND start_seq IS NULL AND end_seq IS NULL
            AND snapshot_seq IS NOT NULL AND rows IS NOT NULL AND columns IS NOT NULL
            AND format_name IS NOT NULL AND format_version IS NOT NULL
            AND as_of_snapshot_seq IS NULL AND first_line_id IS NULL AND last_line_id IS NULL)
        OR
        (kind = 'TRANSCRIPT'
            AND start_seq IS NULL AND end_seq IS NULL
            AND snapshot_seq IS NULL AND rows IS NULL AND columns IS NULL
            AND format_name IS NOT NULL AND format_version IS NOT NULL
            AND as_of_snapshot_seq IS NOT NULL AND first_line_id IS NOT NULL AND last_line_id IS NOT NULL)
    );

CREATE TABLE transcript_pages (
    id uuid PRIMARY KEY,
    session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    recipient_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    as_of_snapshot_seq bigint NOT NULL CHECK (as_of_snapshot_seq > 0),
    first_line_id bigint NOT NULL CHECK (first_line_id > 0),
    last_line_id bigint NOT NULL CHECK (last_line_id >= first_line_id),
    object_key text NOT NULL UNIQUE,
    key_id text NOT NULL CHECK (char_length(key_id) BETWEEN 8 AND 128),
    ciphertext_sha256 bytea NOT NULL CHECK (octet_length(ciphertext_sha256) = 32),
    size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 524288),
    format_name text NOT NULL CHECK (format_name = 'TERMLINK_TERMINAL_TRANSCRIPT'),
    format_version integer NOT NULL CHECK (format_version = 1),
    created_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    history_revision bigint NOT NULL CHECK (history_revision > 0),
    CONSTRAINT transcript_pages_session_recipient_anchor_range_key
        UNIQUE (session_id, recipient_device_id, as_of_snapshot_seq,
                first_line_id, last_line_id, format_name, format_version)
);

CREATE TRIGGER transcript_pages_assign_history_revision
BEFORE INSERT ON transcript_pages
FOR EACH ROW EXECUTE FUNCTION assign_session_history_revision();

CREATE INDEX transcript_pages_recipient_page_v1
    ON transcript_pages(session_id, recipient_device_id, as_of_snapshot_seq,
                        last_line_id DESC, first_line_id DESC, id DESC)
    INCLUDE(history_revision)
    WHERE deleted_at IS NULL;

CREATE UNIQUE INDEX outbox_history_object_delete_unique
    ON outbox(aggregate_id, (payload->>'object_key'))
    WHERE event_type = 'history.object.delete';
