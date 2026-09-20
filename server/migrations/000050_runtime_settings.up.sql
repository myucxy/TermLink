-- Runtime deployment settings owned by the management portal. SUPER_ADMIN
-- can override these at runtime from the admin page; the matching
-- TERMLINK_* environment variables remain the bootstrap defaults and are
-- used whenever a setting has no row here.
CREATE TABLE IF NOT EXISTS runtime_settings(
    key text PRIMARY KEY,
    value text NOT NULL,
    updated_by uuid,
    updated_at timestamptz NOT NULL DEFAULT now()
);
