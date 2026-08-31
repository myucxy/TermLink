CREATE TABLE account_identity_profiles (
    user_id uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    username text NOT NULL DEFAULT '' CHECK (char_length(username) <= 128),
    nickname text NOT NULL DEFAULT '' CHECK (char_length(nickname) <= 128),
    email_masked text NOT NULL DEFAULT '' CHECK (char_length(email_masked) <= 320),
    synced_at timestamptz NOT NULL DEFAULT now(),
    CHECK (username <> '' OR nickname <> '' OR email_masked <> '')
);

CREATE INDEX account_identity_profiles_username
    ON account_identity_profiles(lower(username))
    WHERE username <> '';

-- This projection exists only while an invited registration has not yet been
-- linked to a TermLink account. It stores no raw e-mail address or credential.
CREATE TABLE invitation_registration_profiles (
    redemption_id uuid PRIMARY KEY REFERENCES invitation_redemptions(id) ON DELETE CASCADE,
    username text NOT NULL DEFAULT '' CHECK (char_length(username) <= 128),
    nickname text NOT NULL DEFAULT '' CHECK (char_length(nickname) <= 128),
    email_masked text NOT NULL DEFAULT '' CHECK (char_length(email_masked) <= 320),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (username <> '' OR nickname <> '' OR email_masked <> '')
);
