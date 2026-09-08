ALTER TABLE pc_file_operations
    ADD COLUMN list_query text NOT NULL DEFAULT '';

ALTER TABLE pc_file_operations
    ADD CONSTRAINT pc_file_operations_list_query_check CHECK (
        octet_length(convert_to(list_query, 'UTF8')) <= 512
        AND char_length(list_query) <= 128
    ),
    DROP CONSTRAINT pc_file_operations_request_shape,
    ADD CONSTRAINT pc_file_operations_request_shape CHECK (
        (operation = 'LIST' AND shell_type IS NULL)
        OR (operation = 'DOWNLOAD' AND list_offset IS NULL AND list_query = '' AND shell_type IS NULL AND path <> '')
        OR (operation = 'OPEN_TERMINAL' AND list_offset IS NULL AND list_query = '' AND shell_type IS NOT NULL AND path <> '')
    );
