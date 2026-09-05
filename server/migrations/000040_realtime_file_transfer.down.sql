DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pc_file_operations WHERE transfer_protocol='REALTIME_V2'
    ) THEN
        RAISE EXCEPTION 'migration 40 cannot be reverted while realtime file-transfer metadata exists';
    END IF;
END
$$;

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_upload_shape,
    DROP CONSTRAINT pc_file_operations_transfer_protocol_check;

ALTER TABLE pc_file_operations
    ADD CONSTRAINT pc_file_operations_upload_shape CHECK (
        (object_key IS NULL AND filename IS NULL AND size_bytes IS NULL AND sha256 IS NULL AND directory_zip IS NULL)
        OR (operation='DOWNLOAD' AND object_key IS NOT NULL AND filename IS NOT NULL
            AND size_bytes IS NOT NULL AND sha256 IS NOT NULL AND directory_zip IS NOT NULL)
    );

ALTER TABLE pc_file_operations
    DROP COLUMN transfer_protocol;

