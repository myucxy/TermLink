DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM refunds WHERE status = 'APPROVAL_PENDING') THEN
        RAISE EXCEPTION 'cannot roll back refund approval while pending approval requests exist';
    END IF;
END
$$;

ALTER TABLE refunds
    DROP CONSTRAINT refunds_approval_state_check,
    DROP CONSTRAINT refunds_approval_window_check,
    DROP CONSTRAINT refunds_approval_actor_time_check,
    DROP CONSTRAINT refunds_status_check,
    DROP COLUMN approval_expires_at,
    DROP COLUMN approved_at,
    DROP COLUMN approved_by,
    DROP COLUMN version;

ALTER TABLE refunds
    ADD CONSTRAINT refunds_status_check
        CHECK (status IN ('CREATED', 'PENDING', 'SUCCEEDED', 'FAILED', 'CLOSED'));
