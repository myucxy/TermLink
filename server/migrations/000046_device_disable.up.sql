ALTER TABLE devices
  ADD COLUMN IF NOT EXISTS disabled_at timestamptz,
  ADD COLUMN IF NOT EXISTS disabled_reason text;

CREATE INDEX IF NOT EXISTS devices_user_disabled_idx
  ON devices(user_id, disabled_at);
