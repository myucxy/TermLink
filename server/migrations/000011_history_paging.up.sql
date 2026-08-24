-- Give every committed history object a per-session revision. The session row
-- is the serialization point used by stable, stateless history pagination.
ALTER TABLE sessions
    ADD COLUMN history_revision bigint NOT NULL DEFAULT 0
    CHECK (history_revision >= 0);

ALTER TABLE event_chunks
    ADD COLUMN history_revision bigint;

ALTER TABLE snapshots
    ADD COLUMN history_revision bigint;

-- Existing rows predate commit revisions. Assign a deterministic, unique
-- revision across both history tables for each session.
WITH ranked AS (
    SELECT kind, id, session_id,
           row_number() OVER (
               PARTITION BY session_id
               ORDER BY created_at, kind_rank, id
           )::bigint AS history_revision
    FROM (
        SELECT 'chunk'::text AS kind, 1 AS kind_rank, id, session_id, created_at
        FROM event_chunks
        UNION ALL
        SELECT 'snapshot'::text AS kind, 2 AS kind_rank, id, session_id, created_at
        FROM snapshots
    ) existing
), updated_chunks AS (
    UPDATE event_chunks item
    SET history_revision=ranked.history_revision
    FROM ranked
    WHERE ranked.kind='chunk' AND ranked.id=item.id
    RETURNING item.id
)
UPDATE snapshots item
SET history_revision=ranked.history_revision
FROM ranked
WHERE ranked.kind='snapshot' AND ranked.id=item.id;

UPDATE sessions session
SET history_revision=history.maximum_revision
FROM (
    SELECT session_id, max(history_revision) AS maximum_revision
    FROM (
        SELECT session_id,history_revision FROM event_chunks
        UNION ALL
        SELECT session_id,history_revision FROM snapshots
    ) committed
    GROUP BY session_id
) history
WHERE history.session_id=session.id;

ALTER TABLE event_chunks
    ALTER COLUMN history_revision SET NOT NULL,
    ADD CONSTRAINT event_chunks_history_revision_positive CHECK (history_revision > 0);

ALTER TABLE snapshots
    ALTER COLUMN history_revision SET NOT NULL,
    ADD CONSTRAINT snapshots_history_revision_positive CHECK (history_revision > 0);

-- The trigger keeps revision allocation in the same transaction as the
-- object insert and also lets an older server binary omit the new column.
CREATE FUNCTION assign_session_history_revision()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE sessions
    SET history_revision=history_revision+1
    WHERE id=NEW.session_id AND deleted_at IS NULL
    RETURNING history_revision INTO NEW.history_revision;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'history session % is unavailable', NEW.session_id
            USING ERRCODE='23503';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER event_chunks_assign_history_revision
BEFORE INSERT ON event_chunks
FOR EACH ROW EXECUTE FUNCTION assign_session_history_revision();

CREATE TRIGGER snapshots_assign_history_revision
BEFORE INSERT ON snapshots
FOR EACH ROW EXECUTE FUNCTION assign_session_history_revision();

CREATE INDEX event_chunks_history_page_v1
    ON event_chunks(session_id, end_seq DESC, id DESC)
    INCLUDE(history_revision)
    WHERE deleted_at IS NULL;

CREATE INDEX snapshots_history_page_v1
    ON snapshots(session_id, snapshot_seq DESC, id DESC)
    INCLUDE(history_revision)
    WHERE deleted_at IS NULL;
