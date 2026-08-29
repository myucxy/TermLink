-- Persist only an AEAD ciphertext for the identity-provider subject. The
-- plaintext Keycloak subject must never enter PostgreSQL, logs, or audit data.
ALTER TABLE account_deletion_requests
    ADD COLUMN identity_handle_cipher bytea,
    ADD COLUMN identity_handle_key_version smallint;

-- Migration 25 could create confirmed/processing requests without a
-- reversible identity handle. Cancel those requests during upgrade rather
-- than allowing a processor to claim work it cannot safely finish.
UPDATE account_deletion_requests
SET status='FAILED',error_code='IDENTITY_HANDLE_UNAVAILABLE',failed_at=now(),
    processor_id=NULL,processor_lease_until=NULL,version=version+1,updated_at=now()
WHERE status='PROCESSING';

UPDATE account_deletion_requests
SET status='CANCELED',error_code=NULL,failed_at=NULL,canceled_at=now(),
    processor_id=NULL,processor_lease_until=NULL,version=version+1,updated_at=now()
WHERE status IN ('CONFIRMED','FAILED');

ALTER TABLE account_deletion_requests
    ADD CONSTRAINT account_deletion_identity_handle_pair CHECK (
        (identity_handle_cipher IS NULL) = (identity_handle_key_version IS NULL)
    ),
    ADD CONSTRAINT account_deletion_identity_handle_cipher_size CHECK (
        identity_handle_cipher IS NULL OR octet_length(identity_handle_cipher) = 64
    ),
    ADD CONSTRAINT account_deletion_identity_handle_key_version CHECK (
        identity_handle_key_version IS NULL OR identity_handle_key_version > 0
    ),
    ADD CONSTRAINT account_deletion_identity_handle_required CHECK (
        status NOT IN ('CONFIRMED','PROCESSING','FAILED')
        OR identity_handle_cipher IS NOT NULL
    );

CREATE FUNCTION enforce_account_deletion_identity_handle()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.identity_handle_cipher IS NOT NULL THEN
        IF NEW.identity_handle_cipher IS DISTINCT FROM OLD.identity_handle_cipher
           OR NEW.identity_handle_key_version IS DISTINCT FROM OLD.identity_handle_key_version THEN
            RAISE EXCEPTION 'account deletion identity handle is immutable'
                USING ERRCODE = '23514';
        END IF;
    ELSIF NEW.identity_handle_cipher IS NOT NULL
          AND (OLD.status <> 'REQUESTED' OR NEW.status <> 'REQUESTED') THEN
        RAISE EXCEPTION 'account deletion identity handle must be bound while requested'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER account_deletion_identity_handle_immutable
    BEFORE UPDATE ON account_deletion_requests
    FOR EACH ROW EXECUTE FUNCTION enforce_account_deletion_identity_handle();

COMMENT ON COLUMN account_deletion_requests.identity_handle_cipher IS
    'AES-256-GCM nonce+ciphertext for a canonical Keycloak UUID; AAD binds account, request, and key version; plaintext is prohibited';
COMMENT ON COLUMN account_deletion_requests.identity_handle_key_version IS
    'Positive server-side identity-handle encryption key version; never a provider subject identifier';
