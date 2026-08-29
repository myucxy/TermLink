DROP TABLE IF EXISTS reconciliation_discrepancies;

ALTER TABLE reconciliation_runs
    DROP CONSTRAINT IF EXISTS reconciliation_runs_id_provider_unique;

DROP TRIGGER IF EXISTS account_deletion_requests_transition ON account_deletion_requests;
DROP TRIGGER IF EXISTS data_export_requests_transition ON data_export_requests;
DROP FUNCTION IF EXISTS enforce_privacy_request_transition();

DROP TABLE IF EXISTS account_deletion_requests;
DROP TABLE IF EXISTS data_export_requests;

ALTER TABLE pcs
    DROP COLUMN IF EXISTS policy_version,
    DROP COLUMN IF EXISTS local_priority,
    DROP COLUMN IF EXISTS control_mode,
    DROP COLUMN IF EXISTS updated_at;
