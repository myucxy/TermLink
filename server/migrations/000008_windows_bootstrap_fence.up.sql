ALTER TABLE users
    ADD COLUMN windows_bootstrap_consumed_at timestamptz;

-- Existing accounts that ever registered a Windows device already consumed
-- the one-time local bootstrap, even if that device was later revoked. This
-- prevents an upgrade from silently reopening bootstrap authority.
UPDATE users account
SET windows_bootstrap_consumed_at = first_windows.created_at
FROM (
    SELECT user_id, min(created_at) AS created_at
    FROM devices
    WHERE device_type = 'WINDOWS_PC'
    GROUP BY user_id
) AS first_windows
WHERE account.id = first_windows.user_id;
