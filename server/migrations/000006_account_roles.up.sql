ALTER TABLE users
    ADD COLUMN role text NOT NULL DEFAULT 'USER'
    CHECK (role IN ('SUPER_ADMIN', 'ADMIN', 'AUDITOR', 'USER'));

UPDATE users
SET role = 'ADMIN'
WHERE is_admin = true;

CREATE INDEX users_active_role
    ON users(role, created_at, id)
    WHERE status = 'ACTIVE';
