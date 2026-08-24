CREATE TABLE pc_file_operations (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    pc_id uuid NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
    requester_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    operation text NOT NULL CHECK (operation IN ('LIST', 'DOWNLOAD', 'OPEN_TERMINAL')),
    path text NOT NULL CHECK (octet_length(convert_to(path, 'UTF8')) <= 4096),
    list_offset bigint CHECK (list_offset >= 0),
    shell_type text CHECK (shell_type IN ('command-prompt', 'windows-powershell')),
    status text NOT NULL DEFAULT 'PENDING'
        CHECK (status IN ('PENDING', 'PROCESSING', 'SUCCEEDED', 'FAILED', 'CANCELLED', 'EXPIRED')),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    lease_token_hash bytea CHECK (lease_token_hash IS NULL OR octet_length(lease_token_hash) = 32),
    lease_expires_at timestamptz,
    result jsonb CHECK (result IS NULL OR pg_column_size(result) <= 1048576),
    error_message text CHECK (error_message IS NULL OR octet_length(convert_to(error_message, 'UTF8')) BETWEEN 1 AND 1024),
    object_key text UNIQUE,
    filename text CHECK (filename IS NULL OR octet_length(convert_to(filename, 'UTF8')) BETWEEN 1 AND 255),
    size_bytes bigint CHECK (size_bytes BETWEEN 0 AND 8589934592),
    sha256 bytea CHECK (sha256 IS NULL OR octet_length(sha256) = 32),
    directory_zip boolean,
    expires_at timestamptz NOT NULL,
    completed_at timestamptz,
    cancelled_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pc_file_operations_request_shape CHECK (
        (operation = 'LIST' AND shell_type IS NULL)
        OR (operation = 'DOWNLOAD' AND list_offset IS NULL AND shell_type IS NULL AND path <> '')
        OR (operation = 'OPEN_TERMINAL' AND list_offset IS NULL AND shell_type IS NOT NULL AND path <> '')
    ),
    CONSTRAINT pc_file_operations_lease_shape CHECK (
        (status = 'PENDING' AND lease_token_hash IS NULL AND lease_expires_at IS NULL)
        OR (status <> 'PENDING' AND (attempt_count > 0 OR status IN ('CANCELLED', 'EXPIRED')))
    ),
    CONSTRAINT pc_file_operations_upload_shape CHECK (
        (object_key IS NULL AND filename IS NULL AND size_bytes IS NULL AND sha256 IS NULL AND directory_zip IS NULL)
        OR (operation = 'DOWNLOAD' AND object_key IS NOT NULL AND filename IS NOT NULL
            AND size_bytes IS NOT NULL AND sha256 IS NOT NULL AND directory_zip IS NOT NULL)
    ),
    CONSTRAINT pc_file_operations_terminal_shape CHECK (
        (status = 'SUCCEEDED' AND completed_at IS NOT NULL AND error_message IS NULL
            AND (operation <> 'LIST' OR result IS NOT NULL))
        OR (status = 'FAILED' AND completed_at IS NOT NULL AND error_message IS NOT NULL AND result IS NULL)
        OR (status = 'CANCELLED' AND cancelled_at IS NOT NULL)
        OR (status = 'EXPIRED')
        OR (status IN ('PENDING', 'PROCESSING') AND completed_at IS NULL AND cancelled_at IS NULL
            AND result IS NULL AND error_message IS NULL)
    ),
    CONSTRAINT pc_file_operations_expiry_check CHECK (expires_at > created_at)
);

CREATE INDEX pc_file_operations_claim
    ON pc_file_operations(pc_id, created_at, id)
    WHERE status IN ('PENDING', 'PROCESSING');

CREATE INDEX pc_file_operations_requester
    ON pc_file_operations(account_id, requester_device_id, pc_id, created_at DESC);
