ALTER TABLE pcs
    ADD COLUMN supported_shells text[] NOT NULL DEFAULT ARRAY[]::text[],
    ADD CONSTRAINT pcs_supported_shells_allowed CHECK (
        supported_shells <@ ARRAY['command-prompt', 'windows-powershell']::text[]
        AND cardinality(supported_shells) <= 8
    );
