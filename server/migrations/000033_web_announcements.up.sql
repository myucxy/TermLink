CREATE TABLE web_announcements (
    id uuid PRIMARY KEY,
    title varchar(120) NOT NULL CHECK (char_length(btrim(title)) BETWEEN 1 AND 120),
    body text NOT NULL CHECK (char_length(btrim(body)) BETWEEN 1 AND 8000),
    severity varchar(16) NOT NULL CHECK (severity IN ('INFO','WARNING','CRITICAL')),
    status varchar(16) NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','PUBLISHED','ARCHIVED')),
    starts_at timestamptz,
    ends_at timestamptz,
    published_at timestamptz,
    created_by uuid REFERENCES users(id) ON DELETE SET NULL,
    updated_by uuid REFERENCES users(id) ON DELETE SET NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at),
    CHECK (status <> 'PUBLISHED' OR published_at IS NOT NULL)
);

CREATE INDEX web_announcements_admin_updated
    ON web_announcements(updated_at DESC, id);

CREATE INDEX web_announcements_active_window
    ON web_announcements(starts_at, ends_at, published_at DESC)
    WHERE status = 'PUBLISHED';
