-- Existing jobs were exclusively Windows x64 EXE installations. Keep their
-- immutable artifact identity instead of inferring it from future PC metadata.
ALTER TABLE agent_update_jobs
    ADD COLUMN artifact_platform text NOT NULL DEFAULT 'windows',
    ADD COLUMN artifact_architecture text NOT NULL DEFAULT 'x64',
    ADD COLUMN artifact_type text NOT NULL DEFAULT 'exe',
    ADD COLUMN artifact_apple_team_id text,
    ADD CONSTRAINT agent_update_jobs_platform_artifact CHECK (
        (artifact_platform='windows' AND artifact_architecture='x64'
            AND artifact_type='exe' AND artifact_apple_team_id IS NULL)
        OR
        (artifact_platform='macos' AND artifact_architecture IN ('arm64','x64')
            AND artifact_type='pkg' AND right(lower(artifact_filename),4)='.pkg'
            AND artifact_apple_team_id IS NOT NULL
            AND artifact_apple_team_id ~ '^[A-Z0-9]{10}$'
            AND artifact_signer_fingerprint IS NULL)
    );

ALTER TABLE agent_update_jobs DROP CONSTRAINT agent_update_jobs_terminal_shape,
    ADD CONSTRAINT agent_update_jobs_terminal_shape CHECK (
        (status='SUCCEEDED' AND phase='SUCCEEDED' AND completed_at IS NOT NULL
            AND error_code IS NULL AND error_detail IS NULL AND observed_agent_version IS NOT NULL
            AND observed_agent_build IS NOT NULL AND observed_agent_build >= target_build)
        OR (status='ROLLED_BACK' AND phase='ROLLED_BACK' AND completed_at IS NOT NULL
            AND error_code IS NOT NULL AND observed_agent_version IS NOT NULL
            AND observed_agent_build IS NOT NULL AND observed_agent_build=current_build)
        OR (status='FAILED' AND phase='FAILED' AND completed_at IS NOT NULL AND error_code IS NOT NULL)
        OR (status='ROLLBACK_FAILED' AND phase='ROLLBACK_FAILED' AND completed_at IS NOT NULL AND error_code IS NOT NULL)
        OR (status='CANCELLED' AND cancelled_at IS NOT NULL)
        OR status='EXPIRED'
        OR (status IN ('PENDING','PROCESSING') AND completed_at IS NULL AND cancelled_at IS NULL)
    );
