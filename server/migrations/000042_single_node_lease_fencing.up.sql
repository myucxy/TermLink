-- Reserve a durable, JavaScript-safe fencing epoch above every version the
-- former per-session Valkey counters could realistically have issued. All
-- single-node and rollback-mode acquisitions allocate from this sequence.
CREATE SEQUENCE control_lease_fence_version_seq
    AS bigint
    MINVALUE 1
    MAXVALUE 9007199254740991
    START WITH 281474976710656;

SELECT setval(
    'control_lease_fence_version_seq',
    GREATEST(
        281474976710656::bigint,
        COALESCE((SELECT MAX(version) FROM control_leases), 0::bigint)
    ),
    true
);
