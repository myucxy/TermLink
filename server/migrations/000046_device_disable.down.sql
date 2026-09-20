-- Reverse 000046_device_disable: drop the device disable columns and index.
-- Disabled-device data is discarded with the columns; the audit trail in
-- audit_events remains the record of why devices were disabled.
DROP INDEX IF EXISTS devices_user_disabled_idx;
ALTER TABLE devices
  DROP COLUMN IF EXISTS disabled_reason,
  DROP COLUMN IF EXISTS disabled_at;
