ALTER TABLE history_uploads
    ADD COLUMN recipient_device_id uuid REFERENCES devices(id) ON DELETE SET NULL,
    ADD COLUMN recipient_key_epoch bigint CHECK (recipient_key_epoch > 0);

ALTER TABLE event_chunks
    ADD COLUMN recipient_device_id uuid REFERENCES devices(id) ON DELETE SET NULL;

ALTER TABLE snapshots
    ADD COLUMN recipient_device_id uuid REFERENCES devices(id) ON DELETE SET NULL;

-- Preserve recognizable legacy objects. Unknown/private key IDs remain NULL
-- and are visible only to the owning Windows PC.
UPDATE history_uploads item
SET recipient_device_id=device.id,
    recipient_key_epoch=split_part(item.key_id, ':', 3)::bigint
FROM devices device
WHERE device.id=CASE WHEN (
    (item.kind='CHUNK' AND item.key_id ~ '^history-v1:[0-9a-f]{32}:[0-9]{8,19}$') OR
    (item.kind='SNAPSHOT' AND item.key_id ~ '^(history|snapshot)-v1:[0-9a-f]{32}:[0-9]{8,19}$')
  ) THEN split_part(item.key_id, ':', 2)::uuid END
  AND CASE WHEN (
    (item.kind='CHUNK' AND item.key_id ~ '^history-v1:[0-9a-f]{32}:[0-9]{8,19}$') OR
    (item.kind='SNAPSHOT' AND item.key_id ~ '^(history|snapshot)-v1:[0-9a-f]{32}:[0-9]{8,19}$')
  ) THEN split_part(item.key_id, ':', 3)::numeric END BETWEEN 1 AND 9223372036854775807;

UPDATE event_chunks item
SET recipient_device_id=device.id
FROM devices device
WHERE device.id=CASE
        WHEN item.key_id ~ '^history-v1:[0-9a-f]{32}:[0-9]{8,19}$'
        THEN split_part(item.key_id, ':', 2)::uuid
      END
  AND CASE WHEN item.key_id ~ '^history-v1:[0-9a-f]{32}:[0-9]{8,19}$'
        THEN split_part(item.key_id, ':', 3)::numeric
      END BETWEEN 1 AND 9223372036854775807;

UPDATE snapshots item
SET recipient_device_id=device.id
FROM devices device
WHERE device.id=CASE WHEN (
    (item.format_name='BOUNDED_EVENT_REPLAY' AND item.key_id ~ '^history-v1:[0-9a-f]{32}:[0-9]{8,19}$') OR
    (item.format_name='TERMLINK_TERMINAL_SNAPSHOT' AND item.key_id ~ '^snapshot-v1:[0-9a-f]{32}:[0-9]{8,19}$')
  ) THEN split_part(item.key_id, ':', 2)::uuid END
  AND CASE WHEN (
    (item.format_name='BOUNDED_EVENT_REPLAY' AND item.key_id ~ '^history-v1:[0-9a-f]{32}:[0-9]{8,19}$') OR
    (item.format_name='TERMLINK_TERMINAL_SNAPSHOT' AND item.key_id ~ '^snapshot-v1:[0-9a-f]{32}:[0-9]{8,19}$')
  ) THEN split_part(item.key_id, ':', 3)::numeric END BETWEEN 1 AND 9223372036854775807;

ALTER TABLE event_chunks
    DROP CONSTRAINT event_chunks_session_id_start_seq_end_seq_key,
    ADD CONSTRAINT event_chunks_session_recipient_range_key
        UNIQUE (session_id, recipient_device_id, start_seq, end_seq);

ALTER TABLE snapshots
    DROP CONSTRAINT snapshots_session_id_snapshot_seq_format_name_format_versio_key,
    ADD CONSTRAINT snapshots_session_recipient_sequence_format_key
        UNIQUE (session_id, recipient_device_id, snapshot_seq, format_name, format_version);

CREATE INDEX event_chunks_recipient_history_page_v1
    ON event_chunks(session_id, recipient_device_id, end_seq DESC, id DESC)
    INCLUDE(history_revision)
    WHERE deleted_at IS NULL AND recipient_device_id IS NOT NULL;

CREATE INDEX snapshots_recipient_history_page_v1
    ON snapshots(session_id, recipient_device_id, snapshot_seq DESC, id DESC)
    INCLUDE(history_revision)
    WHERE deleted_at IS NULL AND recipient_device_id IS NOT NULL;
