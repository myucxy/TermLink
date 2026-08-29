ALTER TABLE refunds
    DROP CONSTRAINT refunds_status_check,
    ADD COLUMN version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    ADD COLUMN approved_by uuid REFERENCES users(id) ON DELETE RESTRICT,
    ADD COLUMN approved_at timestamptz,
    ADD COLUMN approval_expires_at timestamptz;

-- Versions 24 and 25 issued provider requests immediately. Preserve those
-- historical rows as already approved by their requester before tightening
-- the approval invariant; no legacy money-moving operation is re-opened.
UPDATE refunds
SET approved_by = requested_by,
    approved_at = requested_at
WHERE status <> 'APPROVAL_PENDING';

ALTER TABLE refunds
    ADD CONSTRAINT refunds_status_check
        CHECK (status IN ('APPROVAL_PENDING', 'CREATED', 'PENDING', 'SUCCEEDED', 'FAILED', 'CLOSED')),
    ADD CONSTRAINT refunds_approval_actor_time_check
        CHECK ((approved_by IS NULL) = (approved_at IS NULL)),
    ADD CONSTRAINT refunds_approval_window_check
        CHECK (approval_expires_at IS NULL OR approval_expires_at > requested_at),
    ADD CONSTRAINT refunds_approval_state_check
        CHECK (
            (status = 'APPROVAL_PENDING'
                AND approved_by IS NULL
                AND approved_at IS NULL
                AND approval_expires_at IS NOT NULL
                AND completed_at IS NULL
                AND provider_ref_hash IS NULL
                AND failure_code IS NULL)
            OR
            (status <> 'APPROVAL_PENDING'
                AND approved_by IS NOT NULL
                AND approved_at IS NOT NULL
                AND approved_at >= requested_at)
        );
