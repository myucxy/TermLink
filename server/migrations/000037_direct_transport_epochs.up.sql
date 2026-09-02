CREATE TABLE direct_transport_pair_epochs (
    account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    first_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    second_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    last_epoch bigint NOT NULL CHECK (last_epoch BETWEEN 1 AND 9223372036854775807),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, first_device_id, second_device_id),
	CHECK (first_device_id <> second_device_id),
    CHECK (first_device_id::text < second_device_id::text)
);

CREATE INDEX direct_transport_pair_epochs_updated_idx
    ON direct_transport_pair_epochs(updated_at);
