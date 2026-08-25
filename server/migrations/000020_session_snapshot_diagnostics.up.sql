ALTER TABLE sessions
    ADD COLUMN snapshot_status text,
    ADD COLUMN snapshot_reason_code text,
    ADD CONSTRAINT sessions_snapshot_status_allowed CHECK (
        snapshot_status IS NULL OR snapshot_status IN ('PENDING', 'READY', 'UNSUPPORTED', 'FAILED')
    ),
    ADD CONSTRAINT sessions_snapshot_reason_code_safe CHECK (
        snapshot_reason_code IS NULL OR snapshot_reason_code ~ '^[A-Z0-9_]{1,64}$'
    ),
    ADD CONSTRAINT sessions_snapshot_diagnostic_shape CHECK (
        (snapshot_status IS NULL AND snapshot_reason_code IS NULL) OR
        (snapshot_status = 'READY' AND snapshot_reason_code IS NULL) OR
        (snapshot_status IN ('PENDING', 'UNSUPPORTED', 'FAILED') AND snapshot_reason_code IS NOT NULL)
    );
