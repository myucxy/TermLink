DO $$
BEGIN
    RAISE EXCEPTION 'migration 13 cannot be reversed: invalid legacy recipient associations cannot be reconstructed safely';
END $$;
