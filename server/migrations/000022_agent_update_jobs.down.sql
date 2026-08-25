DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM agent_update_jobs)
       OR EXISTS (SELECT 1 FROM pcs WHERE terminal_commands <> '[]'::jsonb)
       OR EXISTS (
           SELECT 1 FROM pc_file_operations
           WHERE operation = 'OPEN_TERMINAL' AND result ? 'terminal_command_id'
       ) THEN
        RAISE EXCEPTION 'migration 22 cannot be reversed while Agent update or terminal-command data exists';
    END IF;
END $$;

DROP TABLE agent_update_jobs;

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_open_terminal_result_shape,
    ADD CONSTRAINT pc_file_operations_open_terminal_result_shape CHECK (
        operation <> 'OPEN_TERMINAL' OR status <> 'SUCCEEDED' OR (
            jsonb_typeof(result) = 'object'
            AND result ?& ARRAY['session_id', 'shell_type', 'cwd']
            AND result - 'session_id' - 'shell_type' - 'cwd' = '{}'::jsonb
            AND (result->>'session_id') ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND result->>'session_id' <> '00000000-0000-0000-0000-000000000000'
            AND result->>'shell_type' = shell_type
            AND result->>'cwd' = path
        )
    );

ALTER TABLE pcs
    DROP COLUMN terminal_commands,
    DROP COLUMN remote_agent_update_capable;
