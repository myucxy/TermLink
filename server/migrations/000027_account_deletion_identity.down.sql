-- A version-26 processor cannot consume an identity handle. Cancel every
-- nonterminal request before removing the ciphertext so downgrade cannot
-- revive work that has lost the only safe provider-identity reference.
UPDATE account_deletion_requests
SET status='FAILED',error_code='IDENTITY_HANDLE_DOWNGRADE',failed_at=now(),
    processor_id=NULL,processor_lease_until=NULL,version=version+1,updated_at=now()
WHERE status='PROCESSING';

UPDATE account_deletion_requests
SET status='CANCELED',error_code=NULL,failed_at=NULL,canceled_at=now(),
    processor_id=NULL,processor_lease_until=NULL,version=version+1,updated_at=now()
WHERE status IN ('REQUESTED','CONFIRMED','FAILED');

DROP TRIGGER IF EXISTS account_deletion_identity_handle_immutable
    ON account_deletion_requests;
DROP FUNCTION IF EXISTS enforce_account_deletion_identity_handle();

ALTER TABLE account_deletion_requests
    DROP CONSTRAINT IF EXISTS account_deletion_identity_handle_required,
    DROP CONSTRAINT IF EXISTS account_deletion_identity_handle_key_version,
    DROP CONSTRAINT IF EXISTS account_deletion_identity_handle_cipher_size,
    DROP CONSTRAINT IF EXISTS account_deletion_identity_handle_pair,
    DROP COLUMN IF EXISTS identity_handle_key_version,
    DROP COLUMN IF EXISTS identity_handle_cipher;
