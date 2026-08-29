ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_shell_type_check,
    ADD CONSTRAINT pc_file_operations_shell_type_check CHECK (
        shell_type ~ '^[a-z][a-z0-9._-]{0,63}$'
    );
