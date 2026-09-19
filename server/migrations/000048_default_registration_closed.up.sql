-- New deployments start with registration disabled. Preserve an administrator's
-- explicit policy choice on databases that have already been configured.
UPDATE registration_policies
SET mode='CLOSED', version=version+1, updated_at=now()
WHERE singleton AND mode='OPEN' AND version=1 AND updated_by IS NULL;
