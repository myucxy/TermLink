DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pc_file_operations
        WHERE size_bytes > 5368709120
    ) THEN
        RAISE EXCEPTION 'migration 18 cannot tighten the file-transfer limit while oversized metadata exists';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pc_file_operations
        WHERE operation = 'OPEN_TERMINAL' AND status = 'SUCCEEDED'
          AND (
              jsonb_typeof(result) IS DISTINCT FROM 'object'
              OR NOT (result ?& ARRAY['session_id', 'shell_type'])
              OR result - 'session_id' - 'shell_type' <> '{}'::jsonb
              OR (result->>'session_id') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
              OR result->>'session_id' = '00000000-0000-0000-0000-000000000000'
              OR result->>'shell_type' IS DISTINCT FROM shell_type
          )
    ) THEN
        RAISE EXCEPTION 'migration 18 cannot harden malformed OPEN_TERMINAL results';
    END IF;
END $$;

UPDATE pc_file_operations
SET result = result || jsonb_build_object('cwd', path)
WHERE operation = 'OPEN_TERMINAL' AND status = 'SUCCEEDED';

UPDATE pc_file_operations
SET lease_expires_at = expires_at
WHERE lease_expires_at > expires_at;

ALTER TABLE pc_file_operations
    DROP CONSTRAINT pc_file_operations_size_bytes_check,
    DROP CONSTRAINT pc_file_operations_terminal_shape,
    DROP CONSTRAINT pc_file_operations_expiry_check,
    ADD CONSTRAINT pc_file_operations_size_bytes_check
        CHECK (size_bytes BETWEEN 0 AND 5368709120),
    ADD CONSTRAINT pc_file_operations_terminal_shape CHECK (
        (status = 'SUCCEEDED' AND completed_at IS NOT NULL AND error_message IS NULL
            AND (operation = 'DOWNLOAD' OR result IS NOT NULL))
        OR (status = 'FAILED' AND completed_at IS NOT NULL AND error_message IS NOT NULL AND result IS NULL)
        OR (status = 'CANCELLED' AND cancelled_at IS NOT NULL)
        OR (status = 'EXPIRED')
        OR (status IN ('PENDING', 'PROCESSING') AND completed_at IS NULL AND cancelled_at IS NULL
            AND result IS NULL AND error_message IS NULL)
    ),
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
    ),
    ADD CONSTRAINT pc_file_operations_expiry_check CHECK (
        expires_at > created_at AND (lease_expires_at IS NULL OR lease_expires_at <= expires_at)
    );
