ALTER TABLE pcs
    ADD COLUMN remote_agent_update_capable boolean NOT NULL DEFAULT false,
    ADD COLUMN terminal_commands jsonb NOT NULL DEFAULT '[]'::jsonb
        CHECK (jsonb_typeof(terminal_commands) = 'array' AND jsonb_array_length(terminal_commands) <= 24);

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
                        'command-prompt','windows-powershell','powershell-7','wsl','custom'
                    ))
            )
            AND (result->>'session_id') ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND result->>'session_id' <> '00000000-0000-0000-0000-000000000000'
            AND result->>'cwd' = path
        )
    );

CREATE TABLE agent_update_jobs (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    pc_id uuid NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
    requester_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    current_version text NOT NULL
        CHECK (octet_length(convert_to(current_version, 'UTF8')) BETWEEN 1 AND 64),
    current_build bigint NOT NULL CHECK (current_build >= 0),
    target_version text NOT NULL
        CHECK (octet_length(convert_to(target_version, 'UTF8')) BETWEEN 1 AND 64),
    target_build bigint NOT NULL CHECK (target_build > current_build),
    artifact_url text NOT NULL
        CHECK (octet_length(convert_to(artifact_url, 'UTF8')) BETWEEN 8 AND 2048),
    artifact_filename text NOT NULL
        CHECK (octet_length(convert_to(artifact_filename, 'UTF8')) BETWEEN 1 AND 255),
    artifact_size_bytes bigint NOT NULL CHECK (artifact_size_bytes BETWEEN 1 AND 2147483648),
    artifact_sha256 bytea NOT NULL CHECK (octet_length(artifact_sha256) = 32),
    artifact_signer_fingerprint text
        CHECK (artifact_signer_fingerprint IS NULL OR artifact_signer_fingerprint ~ '^[0-9a-f]{40}$'),
    status text NOT NULL DEFAULT 'PENDING' CHECK (status IN (
        'PENDING','PROCESSING','SUCCEEDED','FAILED','CANCELLED','EXPIRED','ROLLED_BACK','ROLLBACK_FAILED'
    )),
    phase text NOT NULL DEFAULT 'REQUESTED' CHECK (phase IN (
        'REQUESTED','PRECHECKING','DOWNLOADING','VERIFIED','DRAINING_SESSIONS','INSTALLING',
        'STARTING_NEW_AGENT','VERIFYING_CONNECTION','SUCCEEDED','ROLLING_BACK','ROLLED_BACK',
        'FAILED','ROLLBACK_FAILED'
    )),
    progress_percent integer NOT NULL DEFAULT 0 CHECK (progress_percent BETWEEN 0 AND 100),
    active_session_count integer CHECK (active_session_count >= 0),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    lease_token_hash bytea CHECK (lease_token_hash IS NULL OR octet_length(lease_token_hash) = 32),
    lease_expires_at timestamptz,
    error_code text CHECK (error_code IS NULL OR error_code ~ '^[A-Z0-9_]{1,64}$'),
    error_detail text CHECK (
        error_detail IS NULL OR octet_length(convert_to(error_detail, 'UTF8')) BETWEEN 1 AND 1024
    ),
    observed_agent_version text
        CHECK (observed_agent_version IS NULL OR octet_length(convert_to(observed_agent_version, 'UTF8')) BETWEEN 1 AND 64),
    observed_agent_build bigint CHECK (observed_agent_build IS NULL OR observed_agent_build >= 0),
    expires_at timestamptz NOT NULL,
    started_at timestamptz,
    completed_at timestamptz,
    cancelled_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT agent_update_jobs_expiry CHECK (
        expires_at > created_at AND (lease_expires_at IS NULL OR lease_expires_at <= expires_at)
    ),
    CONSTRAINT agent_update_jobs_lease_shape CHECK (
        (status = 'PENDING' AND lease_token_hash IS NULL AND lease_expires_at IS NULL)
        OR (status <> 'PENDING' AND (attempt_count > 0 OR status IN ('CANCELLED','EXPIRED')))
    ),
    CONSTRAINT agent_update_jobs_terminal_shape CHECK (
        (status = 'SUCCEEDED' AND phase = 'SUCCEEDED' AND completed_at IS NOT NULL
            AND error_code IS NULL AND error_detail IS NULL AND observed_agent_version IS NOT NULL
            AND observed_agent_build = target_build)
        OR (status = 'ROLLED_BACK' AND phase = 'ROLLED_BACK' AND completed_at IS NOT NULL
            AND error_code IS NOT NULL AND observed_agent_version IS NOT NULL
            AND observed_agent_build = current_build)
        OR (status = 'FAILED' AND phase = 'FAILED' AND completed_at IS NOT NULL
            AND error_code IS NOT NULL)
        OR (status = 'ROLLBACK_FAILED' AND phase = 'ROLLBACK_FAILED' AND completed_at IS NOT NULL
            AND error_code IS NOT NULL)
        OR (status = 'CANCELLED' AND cancelled_at IS NOT NULL)
        OR (status = 'EXPIRED')
        OR (status IN ('PENDING','PROCESSING') AND completed_at IS NULL AND cancelled_at IS NULL)
    )
);

CREATE UNIQUE INDEX agent_update_jobs_one_active_per_pc
    ON agent_update_jobs(pc_id)
    WHERE status IN ('PENDING','PROCESSING');

CREATE INDEX agent_update_jobs_claim
    ON agent_update_jobs(pc_id, created_at, id)
    WHERE status IN ('PENDING','PROCESSING');

CREATE INDEX agent_update_jobs_requester
    ON agent_update_jobs(account_id, requester_device_id, pc_id, created_at DESC);
