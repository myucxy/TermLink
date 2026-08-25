UPDATE pcs
SET supported_shells = array_remove(array_remove(supported_shells, 'powershell-7'), 'wsl')
WHERE supported_shells && ARRAY['powershell-7', 'wsl']::text[];

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pc_file_operations
        WHERE shell_type IN ('powershell-7', 'wsl')
    ) THEN
        RAISE EXCEPTION 'migration 21 cannot remove extended shell operations while records still exist';
    END IF;
END $$;

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_shell_type_check,
    ADD CONSTRAINT pc_file_operations_shell_type_check CHECK (
        shell_type IN ('command-prompt', 'windows-powershell')
    );

ALTER TABLE pcs
    DROP CONSTRAINT pcs_supported_shells_allowed,
    ADD CONSTRAINT pcs_supported_shells_allowed CHECK (
        supported_shells <@ ARRAY['command-prompt', 'windows-powershell']::text[]
        AND cardinality(supported_shells) <= 8
    );
