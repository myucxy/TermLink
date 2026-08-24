-- The production product now treats the OIDC account as the access boundary.
-- Promote devices left PENDING by an older pairing/grant deployment so an
-- upgrade cannot retain a hidden approval prerequisite. REVOKED rows are never
-- selected and therefore remain revoked.

-- A Windows approver is retained only as a compatibility relationship. Prefer
-- the existing bootstrap PC; otherwise use the oldest non-revoked Windows
-- device as the account anchor. The anchor itself has no approver, while later
-- pending PCs point at it so the one-bootstrap partial unique index remains
-- satisfied.
WITH windows_anchor AS (
    SELECT DISTINCT ON (user_id)
        user_id,
        id
    FROM devices
    WHERE device_type = 'WINDOWS_PC'
      AND trust_state <> 'REVOKED'
    ORDER BY
        user_id,
        CASE WHEN trust_state = 'TRUSTED' AND approved_by IS NULL THEN 0 ELSE 1 END,
        created_at,
        id
)
UPDATE devices pending
SET trust_state = 'TRUSTED',
    approved_by = CASE WHEN pending.id = anchor.id THEN NULL ELSE anchor.id END,
    approved_at = COALESCE(pending.approved_at, now())
FROM windows_anchor anchor
WHERE pending.user_id = anchor.user_id
  AND pending.device_type = 'WINDOWS_PC'
  AND pending.trust_state = 'PENDING';

UPDATE devices
SET trust_state = 'TRUSTED',
    approved_at = COALESCE(approved_at, now())
WHERE device_type = 'ANDROID'
  AND trust_state = 'PENDING';
