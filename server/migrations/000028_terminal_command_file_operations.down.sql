DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pc_file_operations
        WHERE shell_type IS NOT NULL
          AND shell_type NOT IN ('command-prompt', 'windows-powershell', 'powershell-7', 'wsl')
    ) THEN
        RAISE EXCEPTION 'migration 28 cannot be reversed while custom terminal-command operations exist';
    END IF;
END $$;

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_shell_type_check,
    ADD CONSTRAINT pc_file_operations_shell_type_check CHECK (
        shell_type IN ('command-prompt', 'windows-powershell', 'powershell-7', 'wsl')
    );
