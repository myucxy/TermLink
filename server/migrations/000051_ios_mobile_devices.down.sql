-- Do not relabel or delete real iOS device identities during rollback.
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM devices WHERE device_type = 'IOS') THEN
        RAISE EXCEPTION 'Cannot remove IOS device support while IOS identities exist';
    END IF;
END $$;
ALTER TABLE devices DROP CONSTRAINT devices_device_type_check;
ALTER TABLE devices ADD CONSTRAINT devices_device_type_check
    CHECK (device_type IN ('WINDOWS_PC', 'PC_AGENT', 'ANDROID')) NOT VALID;
ALTER TABLE devices VALIDATE CONSTRAINT devices_device_type_check;
