ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_request_shape,
    DROP CONSTRAINT pc_file_operations_list_query_check,
    DROP COLUMN list_query,
    ADD CONSTRAINT pc_file_operations_request_shape CHECK (
        (operation = 'LIST' AND shell_type IS NULL)
        OR (operation = 'DOWNLOAD' AND list_offset IS NULL AND shell_type IS NULL AND path <> '')
        OR (operation = 'OPEN_TERMINAL' AND list_offset IS NULL AND shell_type IS NOT NULL AND path <> '')
    );
