-- APNs tokens are encrypted by the application with a dedicated derived key.
-- No plaintext token, conversation body or user-supplied alert text is stored.
CREATE TABLE device_push_registrations (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    topic text NOT NULL CHECK (topic ~ '^[A-Za-z0-9][A-Za-z0-9.-]{0,254}$'),
    environment text NOT NULL CHECK (environment IN ('sandbox','production')),
    token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash)=32),
    token_cipher bytea NOT NULL CHECK (octet_length(token_cipher) BETWEEN 32 AND 2048),
    generation uuid NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    sound_enabled boolean NOT NULL DEFAULT false,
    registered_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(device_id,topic,environment)
);

CREATE TABLE device_push_events (
    pc_id uuid NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
    event_id uuid NOT NULL,
    source text NOT NULL DEFAULT 'agent' CHECK (source IN ('agent','server')),
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    sender_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    kind text NOT NULL CHECK (kind IN ('ai.completed','ai.needs_input','upgrade.succeeded','upgrade.failed')),
    session_id uuid,
    request_hash bytea NOT NULL CHECK (octet_length(request_hash)=32),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(pc_id,event_id,source)
);
CREATE INDEX device_push_events_recent ON device_push_events(pc_id,created_at);

CREATE TABLE device_push_jobs (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    registration_id uuid NOT NULL REFERENCES device_push_registrations(id) ON DELETE CASCADE,
    registration_generation uuid NOT NULL,
    pc_id uuid NOT NULL,
    event_id uuid NOT NULL,
    event_source text NOT NULL DEFAULT 'agent' CHECK (event_source IN ('agent','server')),
    state text NOT NULL DEFAULT 'PENDING' CHECK (state IN ('PENDING','PROCESSING','SENT','CANCELLED','FAILED')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 10),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    lease_id uuid,
    lease_until timestamptz,
    last_error_code text CHECK (last_error_code IS NULL OR last_error_code ~ '^[A-Z0-9_]{1,64}$'),
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    FOREIGN KEY(pc_id,event_id,event_source) REFERENCES device_push_events(pc_id,event_id,source) ON DELETE CASCADE,
    UNIQUE(registration_id,registration_generation,pc_id,event_id,event_source)
);
CREATE INDEX device_push_jobs_due ON device_push_jobs(next_attempt_at,created_at) WHERE state IN ('PENDING','PROCESSING');

-- An update transition and its notification are one database transaction.
-- Existing jobs intentionally have no backfilled notifications.
CREATE FUNCTION enqueue_agent_update_push() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    notification_kind text;
    event_uuid uuid;
    sender_id uuid;
BEGIN
    IF NEW.status = OLD.status OR NEW.status NOT IN ('SUCCEEDED','FAILED','ROLLED_BACK','ROLLBACK_FAILED') THEN RETURN NEW; END IF;
    SELECT device_id INTO sender_id FROM pcs WHERE id=NEW.pc_id;
    IF sender_id IS NULL OR NOT EXISTS(SELECT 1 FROM device_push_registrations WHERE account_id=NEW.account_id AND device_id=NEW.requester_device_id AND enabled) THEN RETURN NEW; END IF;
    notification_kind := CASE WHEN NEW.status='SUCCEEDED' THEN 'upgrade.succeeded' ELSE 'upgrade.failed' END;
    event_uuid := NEW.id;
    INSERT INTO device_push_events(pc_id,event_id,source,account_id,sender_device_id,kind,request_hash)
        VALUES(NEW.pc_id,event_uuid,'server',NEW.account_id,sender_id,notification_kind,NEW.artifact_sha256)
        ON CONFLICT(pc_id,event_id,source) DO NOTHING;
    INSERT INTO device_push_jobs(id,account_id,registration_id,registration_generation,pc_id,event_id,event_source,expires_at)
        SELECT gen_random_uuid(),NEW.account_id,r.id,r.generation,NEW.pc_id,event_uuid,'server',now()+interval '24 hours'
        FROM device_push_registrations r JOIN devices d ON d.id=r.device_id AND d.user_id=NEW.account_id
        WHERE r.account_id=NEW.account_id AND r.device_id=NEW.requester_device_id AND r.enabled
          AND d.device_type='IOS' AND d.trust_state='TRUSTED' AND d.disabled_at IS NULL
        ON CONFLICT DO NOTHING;
    RETURN NEW;
END $$;
CREATE TRIGGER agent_update_push AFTER UPDATE OF status ON agent_update_jobs
    FOR EACH ROW EXECUTE FUNCTION enqueue_agent_update_push();
