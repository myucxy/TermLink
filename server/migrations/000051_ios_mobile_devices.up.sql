ALTER TABLE devices DROP CONSTRAINT devices_device_type_check;
ALTER TABLE devices ADD CONSTRAINT devices_device_type_check
    CHECK (device_type IN ('WINDOWS_PC', 'PC_AGENT', 'ANDROID', 'IOS')) NOT VALID;
ALTER TABLE devices VALIDATE CONSTRAINT devices_device_type_check;
