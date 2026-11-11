ALTER TABLE pcs
    DROP CONSTRAINT pcs_supported_shells_allowed,
    ADD CONSTRAINT pcs_supported_shells_allowed CHECK (
        supported_shells <@ ARRAY[
            'command-prompt', 'windows-powershell', 'powershell-7', 'wsl',
            'bash', 'zsh', 'sh', 'fish'
        ]::text[]
        AND cardinality(supported_shells) <= 8
    );

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_open_terminal_result_shape,
    ADD CONSTRAINT pc_file_operations_open_terminal_result_shape CHECK (
        operation <> 'OPEN_TERMINAL' OR status <> 'SUCCEEDED' OR (
            jsonb_typeof(result) = 'object'
            AND result ?& ARRAY['session_id', 'shell_type', 'cwd']
            AND (
                (result - 'session_id' - 'shell_type' - 'cwd' = '{}'::jsonb
                    AND result->>'shell_type' = shell_type)
                OR
                (result ? 'terminal_command_id'
                    AND result - 'session_id' - 'terminal_command_id' - 'shell_type' - 'cwd' = '{}'::jsonb
                    AND result->>'terminal_command_id' = shell_type
                    AND result->>'shell_type' IN (
                        'command-prompt','windows-powershell','powershell-7','wsl','custom',
                        'bash','zsh','sh','fish'
                    )
                    AND (shell_type NOT IN ('bash','zsh','sh','fish')
                        OR result->>'shell_type' NOT IN ('bash','zsh','sh','fish')
                        OR result->>'shell_type' = shell_type))
            )
            AND (result->>'session_id') ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND result->>'session_id' <> '00000000-0000-0000-0000-000000000000'
            AND result->>'cwd' = path
        )
    );

