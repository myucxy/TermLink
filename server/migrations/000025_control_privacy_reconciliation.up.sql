ALTER TABLE pcs
    ADD COLUMN updated_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN control_mode text NOT NULL DEFAULT 'SINGLE'
        CHECK (control_mode IN ('SINGLE', 'TAKEOVER', 'COLLABORATIVE')),
    ADD COLUMN local_priority boolean NOT NULL DEFAULT true,
    ADD COLUMN policy_version bigint NOT NULL DEFAULT 1 CHECK (policy_version > 0);

CREATE TABLE data_export_requests (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    scope text[] NOT NULL DEFAULT ARRAY[
        'ACCOUNT_PROFILE', 'DEVICE_METADATA', 'SESSION_METADATA', 'BILLING_RECORDS'
    ]::text[],
    status text NOT NULL DEFAULT 'REQUESTED'
        CHECK (status IN ('REQUESTED', 'PROCESSING', 'READY', 'EXPIRED', 'FAILED', 'CANCELED')),
    artifact_object_key text,
    artifact_sha256 bytea CHECK (artifact_sha256 IS NULL OR octet_length(artifact_sha256) = 32),
    artifact_size_bytes bigint CHECK (artifact_size_bytes IS NULL OR artifact_size_bytes >= 0),
    error_code text CHECK (error_code IS NULL OR error_code ~ '^[A-Z][A-Z0-9_]{0,127}$'),
    processor_id uuid,
    processor_lease_until timestamptz,
    processing_fence bigint NOT NULL DEFAULT 0 CHECK (processing_fence >= 0),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    requested_at timestamptz NOT NULL DEFAULT now(),
    processing_started_at timestamptz,
    completed_at timestamptz,
    expires_at timestamptz,
    failed_at timestamptz,
    canceled_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (cardinality(scope) BETWEEN 1 AND 8),
    CHECK (scope <@ ARRAY[
        'ACCOUNT_PROFILE', 'DEVICE_METADATA', 'SESSION_METADATA', 'BILLING_RECORDS'
    ]::text[]),
    CHECK (artifact_object_key IS NULL OR (
        char_length(artifact_object_key) BETWEEN 1 AND 512
        AND artifact_object_key ~ '^[A-Za-z0-9][A-Za-z0-9._/-]*$'
        AND artifact_object_key !~ '(^|/)\.\.(/|$)'
    )),
    CHECK (status <> 'PROCESSING' OR (
        processor_id IS NOT NULL AND processor_lease_until IS NOT NULL
        AND processing_started_at IS NOT NULL AND processing_fence > 0 AND attempt_count > 0
    )),
    CHECK (processor_lease_until IS NULL OR status = 'PROCESSING'),
    CHECK (status <> 'READY' OR (
        artifact_object_key IS NOT NULL AND artifact_sha256 IS NOT NULL
        AND artifact_size_bytes IS NOT NULL AND completed_at IS NOT NULL AND expires_at IS NOT NULL
    )),
    CHECK (status <> 'EXPIRED' OR (
        artifact_object_key IS NULL AND artifact_sha256 IS NULL AND artifact_size_bytes IS NULL
        AND completed_at IS NOT NULL AND expires_at IS NOT NULL
    )),
    CHECK (status <> 'FAILED' OR (failed_at IS NOT NULL AND error_code IS NOT NULL)),
    CHECK (error_code IS NULL OR status = 'FAILED'),
    CHECK (status <> 'CANCELED' OR canceled_at IS NOT NULL),
    CHECK (artifact_object_key IS NULL OR status = 'READY'),
    CHECK (artifact_sha256 IS NULL OR status = 'READY'),
    CHECK (artifact_size_bytes IS NULL OR status = 'READY'),
    CHECK (completed_at IS NULL OR completed_at >= requested_at),
    CHECK (expires_at IS NULL OR (completed_at IS NOT NULL AND expires_at > completed_at)),
    CHECK (failed_at IS NULL OR failed_at >= requested_at),
    CHECK (canceled_at IS NULL OR canceled_at >= requested_at),
    CHECK (updated_at >= created_at)
);

CREATE UNIQUE INDEX data_export_requests_one_active_account
    ON data_export_requests(account_id)
    WHERE status IN ('REQUESTED', 'PROCESSING', 'READY');

CREATE INDEX data_export_requests_account_created
    ON data_export_requests(account_id, created_at DESC, id DESC);

CREATE INDEX data_export_requests_processing_due
    ON data_export_requests(processor_lease_until, id)
    WHERE status = 'PROCESSING';

CREATE TABLE account_deletion_requests (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    status text NOT NULL DEFAULT 'REQUESTED'
        CHECK (status IN ('REQUESTED', 'CONFIRMED', 'PROCESSING', 'COMPLETED', 'CANCELED', 'FAILED')),
    confirmation_expires_at timestamptz NOT NULL,
    scheduled_for timestamptz,
    retention_deadline timestamptz,
    error_code text CHECK (error_code IS NULL OR error_code ~ '^[A-Z][A-Z0-9_]{0,127}$'),
    processor_id uuid,
    processor_lease_until timestamptz,
    processing_fence bigint NOT NULL DEFAULT 0 CHECK (processing_fence >= 0),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    requested_at timestamptz NOT NULL DEFAULT now(),
    confirmed_at timestamptz,
    processing_started_at timestamptz,
    completed_at timestamptz,
    failed_at timestamptz,
    canceled_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (confirmation_expires_at > requested_at),
    CHECK (status NOT IN ('CONFIRMED', 'PROCESSING', 'COMPLETED', 'FAILED') OR (
        confirmed_at IS NOT NULL AND scheduled_for IS NOT NULL AND retention_deadline IS NOT NULL
    )),
    CHECK (scheduled_for IS NULL OR (confirmed_at IS NOT NULL AND scheduled_for >= confirmed_at)),
    CHECK (retention_deadline IS NULL OR (scheduled_for IS NOT NULL AND retention_deadline >= scheduled_for)),
    CHECK (status <> 'PROCESSING' OR (
        processor_id IS NOT NULL AND processor_lease_until IS NOT NULL
        AND processing_started_at IS NOT NULL AND processing_fence > 0 AND attempt_count > 0
    )),
    CHECK (processor_lease_until IS NULL OR status = 'PROCESSING'),
    CHECK (status <> 'COMPLETED' OR completed_at IS NOT NULL),
    CHECK (status <> 'FAILED' OR (failed_at IS NOT NULL AND error_code IS NOT NULL)),
    CHECK (error_code IS NULL OR status = 'FAILED'),
    CHECK (status <> 'CANCELED' OR canceled_at IS NOT NULL),
    CHECK (confirmed_at IS NULL OR confirmed_at >= requested_at),
    CHECK (processing_started_at IS NULL OR processing_started_at >= requested_at),
    CHECK (completed_at IS NULL OR completed_at >= requested_at),
    CHECK (failed_at IS NULL OR failed_at >= requested_at),
    CHECK (canceled_at IS NULL OR canceled_at >= requested_at),
    CHECK (updated_at >= created_at)
);

CREATE UNIQUE INDEX account_deletion_requests_one_active_account
    ON account_deletion_requests(account_id)
    WHERE status IN ('REQUESTED', 'CONFIRMED', 'PROCESSING', 'FAILED');

CREATE INDEX account_deletion_requests_account_created
    ON account_deletion_requests(account_id, created_at DESC, id DESC);

CREATE INDEX account_deletion_requests_processing_due
    ON account_deletion_requests(processor_lease_until, id)
    WHERE status = 'PROCESSING';

CREATE FUNCTION enforce_privacy_request_transition()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.account_id IS DISTINCT FROM OLD.account_id OR NEW.requested_at IS DISTINCT FROM OLD.requested_at THEN
        RAISE EXCEPTION 'privacy request identity is immutable' USING ERRCODE = '23514';
    END IF;

    IF TG_TABLE_NAME = 'data_export_requests'
       AND (to_jsonb(NEW)->'scope') IS DISTINCT FROM (to_jsonb(OLD)->'scope') THEN
        RAISE EXCEPTION 'data export scope is immutable' USING ERRCODE = '23514';
    END IF;

    IF NEW.status = OLD.status THEN
        RETURN NEW;
    END IF;

    IF TG_TABLE_NAME = 'data_export_requests' THEN
        IF NOT (
            (OLD.status = 'REQUESTED' AND NEW.status IN ('PROCESSING', 'CANCELED'))
            OR (OLD.status = 'PROCESSING' AND NEW.status IN ('READY', 'FAILED', 'CANCELED'))
            OR (OLD.status = 'READY' AND NEW.status = 'EXPIRED')
        ) THEN
            RAISE EXCEPTION 'invalid data export state transition' USING ERRCODE = '23514';
        END IF;
    ELSIF NOT (
        (OLD.status = 'REQUESTED' AND NEW.status IN ('CONFIRMED', 'CANCELED'))
        OR (OLD.status = 'CONFIRMED' AND NEW.status IN ('PROCESSING', 'CANCELED'))
        OR (OLD.status = 'PROCESSING' AND NEW.status IN ('COMPLETED', 'FAILED'))
        OR (OLD.status = 'FAILED' AND NEW.status IN ('PROCESSING', 'CANCELED'))
    ) THEN
        RAISE EXCEPTION 'invalid account deletion state transition' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER data_export_requests_transition
    BEFORE UPDATE ON data_export_requests
    FOR EACH ROW EXECUTE FUNCTION enforce_privacy_request_transition();

CREATE TRIGGER account_deletion_requests_transition
    BEFORE UPDATE ON account_deletion_requests
    FOR EACH ROW EXECUTE FUNCTION enforce_privacy_request_transition();

ALTER TABLE reconciliation_runs
    ADD CONSTRAINT reconciliation_runs_id_provider_unique UNIQUE (id, provider);

CREATE TABLE reconciliation_discrepancies (
    id uuid PRIMARY KEY,
    run_id uuid NOT NULL,
    discrepancy_key bytea NOT NULL UNIQUE CHECK (octet_length(discrepancy_key) = 32),
    provider text NOT NULL CHECK (provider IN ('ALIPAY', 'WECHAT_PAY')),
    payment_channel_id uuid REFERENCES payment_channels(id) ON DELETE SET NULL,
    category text NOT NULL CHECK (category IN (
        'MISSING_LOCAL', 'MISSING_PROVIDER', 'AMOUNT_MISMATCH', 'CURRENCY_MISMATCH',
        'STATUS_MISMATCH', 'REFUND_MISMATCH', 'DUPLICATE_PROVIDER_REFERENCE',
        'DUPLICATE_LOCAL_REFERENCE', 'OTHER'
    )),
    internal_order_id uuid REFERENCES orders(id) ON DELETE SET NULL,
    internal_transaction_id uuid REFERENCES payment_transactions(id) ON DELETE SET NULL,
    external_reference_hmac bytea NOT NULL CHECK (octet_length(external_reference_hmac) = 32),
    local_status text CHECK (local_status IS NULL OR local_status ~ '^[A-Z][A-Z0-9_]{0,31}$'),
    provider_status text CHECK (provider_status IS NULL OR provider_status ~ '^[A-Z][A-Z0-9_]{0,31}$'),
    summary_code text NOT NULL CHECK (summary_code ~ '^[A-Z][A-Z0-9_]{0,63}$'),
    local_amount_minor bigint CHECK (local_amount_minor IS NULL OR local_amount_minor >= 0),
    provider_amount_minor bigint CHECK (provider_amount_minor IS NULL OR provider_amount_minor >= 0),
    local_currency text CHECK (local_currency IS NULL OR local_currency ~ '^[A-Z]{3}$'),
    provider_currency text CHECK (provider_currency IS NULL OR provider_currency ~ '^[A-Z]{3}$'),
    status text NOT NULL DEFAULT 'OPEN'
        CHECK (status IN ('OPEN', 'ACKNOWLEDGED', 'RESOLVED', 'IGNORED')),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    acknowledged_at timestamptz,
    resolved_at timestamptz,
    resolved_by uuid REFERENCES users(id) ON DELETE SET NULL,
    resolution_code text CHECK (resolution_code IS NULL OR resolution_code ~ '^[A-Z][A-Z0-9_]{0,127}$'),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT reconciliation_discrepancies_run_provider_fk
        FOREIGN KEY (run_id, provider) REFERENCES reconciliation_runs(id, provider) ON DELETE RESTRICT,
    CHECK (internal_order_id IS NOT NULL OR external_reference_hmac IS NOT NULL),
    CHECK (last_seen_at >= first_seen_at),
    CHECK (status <> 'ACKNOWLEDGED' OR acknowledged_at IS NOT NULL),
    CHECK (status NOT IN ('RESOLVED', 'IGNORED') OR (resolved_at IS NOT NULL AND resolution_code IS NOT NULL)),
    CHECK (resolved_at IS NULL OR resolved_at >= first_seen_at),
    CHECK (updated_at >= created_at)
);

CREATE INDEX reconciliation_discrepancies_run
    ON reconciliation_discrepancies(run_id, status, id);

CREATE INDEX reconciliation_discrepancies_actionable
    ON reconciliation_discrepancies(provider, status, last_seen_at DESC, id)
    WHERE status IN ('OPEN', 'ACKNOWLEDGED');

CREATE INDEX reconciliation_discrepancies_order
    ON reconciliation_discrepancies(internal_order_id, last_seen_at DESC)
    WHERE internal_order_id IS NOT NULL;

CREATE INDEX reconciliation_discrepancies_transaction
    ON reconciliation_discrepancies(internal_transaction_id, last_seen_at DESC)
    WHERE internal_transaction_id IS NOT NULL;

COMMENT ON COLUMN reconciliation_discrepancies.discrepancy_key IS
    'HMAC-SHA256 canonical discrepancy key including provider; never a raw or unkeyed external identifier';
COMMENT ON COLUMN reconciliation_discrepancies.external_reference_hmac IS
    'HMAC-SHA256 of the external reference using the server commerce provider key; never raw billing data';
COMMENT ON TABLE reconciliation_discrepancies IS
    'Actionable redacted reconciliation state only; raw statements, external transaction text, credentials, and payment account data are prohibited';
COMMENT ON TABLE data_export_requests IS
    'Account-scoped export lifecycle metadata only; terminal content and credentials are prohibited';
COMMENT ON TABLE account_deletion_requests IS
    'Account-scoped deletion lifecycle metadata only; terminal content and credentials are prohibited';
