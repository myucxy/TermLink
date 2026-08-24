-- Migration 12 preserved recognizable legacy recipient metadata. Tighten that
-- metadata to the same canonical key and ownership rules enforced at runtime.
-- Invalid legacy objects remain available to the owning Windows PC only.
UPDATE history_uploads item
SET recipient_device_id=NULL,
    recipient_key_epoch=NULL
WHERE item.recipient_device_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM sessions session
    JOIN pcs pc ON pc.id=session.pc_id
    JOIN devices recipient ON recipient.id=item.recipient_device_id
    WHERE session.id=item.session_id
      AND pc.owner_user_id=item.user_id
      AND recipient.user_id=item.user_id
      AND recipient.device_type='ANDROID'
      AND item.recipient_key_epoch IS NOT NULL
      AND item.key_id=CASE
        WHEN item.kind='CHUNK' THEN
          'history-v1:' || replace(recipient.id::text, '-', '') || ':' ||
          CASE WHEN item.recipient_key_epoch < 100000000
            THEN lpad(item.recipient_key_epoch::text, 8, '0')
            ELSE item.recipient_key_epoch::text
          END
        WHEN item.kind='SNAPSHOT' AND item.format_name='BOUNDED_EVENT_REPLAY' THEN
          'history-v1:' || replace(recipient.id::text, '-', '') || ':' ||
          CASE WHEN item.recipient_key_epoch < 100000000
            THEN lpad(item.recipient_key_epoch::text, 8, '0')
            ELSE item.recipient_key_epoch::text
          END
        WHEN item.kind='SNAPSHOT'
          AND item.format_name='TERMLINK_TERMINAL_SNAPSHOT'
          AND item.recipient_key_epoch < 100000000 THEN
          'snapshot-v1:' || replace(recipient.id::text, '-', '') || ':' ||
          lpad(item.recipient_key_epoch::text, 8, '0')
        ELSE NULL
      END
  );

UPDATE event_chunks item
SET recipient_device_id=NULL
WHERE item.recipient_device_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM sessions session
    JOIN pcs pc ON pc.id=session.pc_id
    JOIN devices recipient ON recipient.id=item.recipient_device_id
    WHERE session.id=item.session_id
      AND recipient.user_id=pc.owner_user_id
      AND recipient.device_type='ANDROID'
      AND CASE
        WHEN item.key_id ~ '^history-v1:[0-9a-f]{32}:([0-9]{8}|[1-9][0-9]{8,18})$' THEN
          split_part(item.key_id, ':', 3)::numeric BETWEEN 1 AND 9223372036854775807
          AND item.key_id=
            'history-v1:' || replace(recipient.id::text, '-', '') || ':' ||
            CASE WHEN split_part(item.key_id, ':', 3)::numeric < 100000000
              THEN lpad((split_part(item.key_id, ':', 3)::numeric)::text, 8, '0')
              ELSE (split_part(item.key_id, ':', 3)::numeric)::text
            END
        ELSE false
      END
  );

UPDATE snapshots item
SET recipient_device_id=NULL
WHERE item.recipient_device_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM sessions session
    JOIN pcs pc ON pc.id=session.pc_id
    JOIN devices recipient ON recipient.id=item.recipient_device_id
    WHERE session.id=item.session_id
      AND recipient.user_id=pc.owner_user_id
      AND recipient.device_type='ANDROID'
      AND CASE
        WHEN item.format_name='BOUNDED_EVENT_REPLAY'
          AND item.key_id ~ '^history-v1:[0-9a-f]{32}:([0-9]{8}|[1-9][0-9]{8,18})$' THEN
          split_part(item.key_id, ':', 3)::numeric BETWEEN 1 AND 9223372036854775807
          AND item.key_id=
            'history-v1:' || replace(recipient.id::text, '-', '') || ':' ||
            CASE WHEN split_part(item.key_id, ':', 3)::numeric < 100000000
              THEN lpad((split_part(item.key_id, ':', 3)::numeric)::text, 8, '0')
              ELSE (split_part(item.key_id, ':', 3)::numeric)::text
            END
        WHEN item.format_name='TERMLINK_TERMINAL_SNAPSHOT'
          AND item.key_id ~ '^snapshot-v1:[0-9a-f]{32}:[0-9]{8}$' THEN
          split_part(item.key_id, ':', 3)::numeric BETWEEN 1 AND 99999999
          AND item.key_id=
            'snapshot-v1:' || replace(recipient.id::text, '-', '') || ':' ||
            lpad((split_part(item.key_id, ':', 3)::numeric)::text, 8, '0')
        ELSE false
      END
  );
