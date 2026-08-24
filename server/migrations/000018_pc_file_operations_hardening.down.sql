DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pc_file_operations) THEN
        RAISE EXCEPTION 'migration 18 cannot be reversed while PC file-operation metadata exists';
    END IF;
END $$;

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_size_bytes_check,
    DROP CONSTRAINT pc_file_operations_terminal_shape,
    DROP CONSTRAINT pc_file_operations_open_terminal_result_shape,
    DROP CONSTRAINT pc_file_operations_expiry_check,
    ADD CONSTRAINT pc_file_operations_size_bytes_check
        CHECK (size_bytes BETWEEN 0 AND 8589934592),
    ADD CONSTRAINT pc_file_operations_terminal_shape CHECK (
        (status = 'SUCCEEDED' AND completed_at IS NOT NULL AND error_message IS NULL
            AND (operation <> 'LIST' OR result IS NOT NULL))
        OR (status = 'FAILED' AND completed_at IS NOT NULL AND error_message IS NOT NULL AND result IS NULL)
        OR (status = 'CANCELLED' AND cancelled_at IS NOT NULL)
        OR (status = 'EXPIRED')
        OR (status IN ('PENDING', 'PROCESSING') AND completed_at IS NULL AND cancelled_at IS NULL
            AND result IS NULL AND error_message IS NULL)
    ),
    ADD CONSTRAINT pc_file_operations_expiry_check CHECK (expires_at > created_at);
