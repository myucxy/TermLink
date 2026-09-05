-- Irreversible by design: migration 41 deletes privacy-export metadata and
-- retired object capabilities. Recreating their schema would falsely imply
-- that the removed artifacts remain recoverable.
DO $$
BEGIN
    RAISE EXCEPTION 'migration 41 is irreversible';
END
$$;
