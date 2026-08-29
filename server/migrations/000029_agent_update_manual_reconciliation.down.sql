UPDATE agent_update_jobs
SET observed_agent_build=target_build
WHERE status='SUCCEEDED' AND observed_agent_build>target_build;

ALTER TABLE agent_update_jobs
    DROP CONSTRAINT agent_update_jobs_terminal_shape,
    ADD CONSTRAINT agent_update_jobs_terminal_shape CHECK (
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
    );
