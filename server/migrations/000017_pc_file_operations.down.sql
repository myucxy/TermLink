DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pc_file_operations) THEN
        RAISE EXCEPTION 'migration 17 cannot be reversed while PC file-operation metadata exists';
    END IF;
END $$;

DROP TABLE pc_file_operations;
