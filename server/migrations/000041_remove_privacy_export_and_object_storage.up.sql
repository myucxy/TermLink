-- Privacy exports and server-side file objects have been retired. File bytes
-- now move only through the authenticated E2EE realtime transfer channel.
DELETE FROM outbox
WHERE event_type='history.object.delete'
   OR event_type LIKE 'privacy.data_export.%';

-- OBJECT_V1 rows contain capabilities for an object service that no longer
-- exists. They are transient requests, so remove them instead of exposing a
-- permanently retrying operation to either client.
DELETE FROM pc_file_operations
WHERE object_key IS NOT NULL OR transfer_protocol='OBJECT_V1';

DROP TABLE data_export_requests;
DROP INDEX IF EXISTS outbox_history_object_delete_unique;

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_upload_shape,
    DROP CONSTRAINT pc_file_operations_transfer_protocol_check,
    DROP COLUMN object_key,
    ADD CONSTRAINT pc_file_operations_transfer_protocol_check CHECK (
        transfer_protocol IS NULL OR transfer_protocol='REALTIME_V2'
    ),
    ADD CONSTRAINT pc_file_operations_upload_shape CHECK (
        (filename IS NULL AND size_bytes IS NULL AND sha256 IS NULL
            AND directory_zip IS NULL AND transfer_protocol IS NULL)
        OR (operation='DOWNLOAD' AND filename IS NOT NULL AND size_bytes IS NOT NULL
            AND sha256 IS NOT NULL AND directory_zip IS NOT NULL
            AND transfer_protocol='REALTIME_V2')
    );
