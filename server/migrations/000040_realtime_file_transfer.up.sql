ALTER TABLE pc_file_operations
    ADD COLUMN transfer_protocol text;

UPDATE pc_file_operations
SET transfer_protocol='OBJECT_V1'
WHERE object_key IS NOT NULL;

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_upload_shape;

ALTER TABLE pc_file_operations
    ADD CONSTRAINT pc_file_operations_transfer_protocol_check CHECK (
        transfer_protocol IS NULL OR transfer_protocol IN ('OBJECT_V1', 'REALTIME_V2')
    ),
    ADD CONSTRAINT pc_file_operations_upload_shape CHECK (
        (object_key IS NULL AND filename IS NULL AND size_bytes IS NULL AND sha256 IS NULL
            AND directory_zip IS NULL AND transfer_protocol IS NULL)
        OR (operation='DOWNLOAD' AND filename IS NOT NULL AND size_bytes IS NOT NULL
            AND sha256 IS NOT NULL AND directory_zip IS NOT NULL AND (
                (transfer_protocol='OBJECT_V1' AND object_key IS NOT NULL)
                OR (transfer_protocol='REALTIME_V2' AND object_key IS NULL)
            ))
    );

