-- Irreversible by design: migration 39 deletes legacy terminal-history indexes
-- after their object keys have been queued for deletion. Restoring the tables
-- would falsely imply that their already-retired contents can be recovered.
DO $$
BEGIN
    RAISE EXCEPTION 'migration 39 is irreversible';
END
$$;
