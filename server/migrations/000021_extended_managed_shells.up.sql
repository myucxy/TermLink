ALTER TABLE pcs
    DROP CONSTRAINT pcs_supported_shells_allowed,
    ADD CONSTRAINT pcs_supported_shells_allowed CHECK (
        supported_shells <@ ARRAY[
            'command-prompt', 'windows-powershell', 'powershell-7', 'wsl'
        ]::text[]
        AND cardinality(supported_shells) <= 8
    );

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_shell_type_check,
    ADD CONSTRAINT pc_file_operations_shell_type_check CHECK (
        shell_type IN ('command-prompt', 'windows-powershell', 'powershell-7', 'wsl')
    );
