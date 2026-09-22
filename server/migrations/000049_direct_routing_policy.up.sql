-- Direct transport data-plane routing policy: a runtime switch controlled by
-- SUPER_ADMIN from the management portal (replaces the static
-- TERMLINK_DIRECT_ROUTING_PERMITTED environment variable). The application
-- treats an absent singleton row as automatic Direct with Relay fallback;
-- an explicit row preserves the administrator's selected policy.
CREATE TABLE IF NOT EXISTS direct_routing_policy(
    singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
    routing_permitted boolean NOT NULL DEFAULT false,
    updated_by uuid,
    updated_at timestamptz NOT NULL DEFAULT now()
);
