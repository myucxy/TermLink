-- This policy migration is intentionally irreversible. The previous PENDING
-- state cannot be reconstructed without reintroducing an approval prerequisite
-- or weakening explicit REVOKED state. A code rollback therefore keeps the
-- already-promoted account devices trusted.
SELECT 1;
