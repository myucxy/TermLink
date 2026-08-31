package store

import (
	"context"
	"errors"
	"testing"

	"github.com/google/uuid"
)

func TestSaveHumanVerificationSettingsRejectsMalformedCiphertextBeforeDatabaseAccess(t *testing.T) {
	repository := &Postgres{}
	_, err := repository.SaveHumanVerificationSettings(context.Background(), uuid.New(), HumanVerificationSettingsInput{
		SiteKey: "site-key", SecretCiphertext: make([]byte, 28), ExpectedVersion: 1,
	})
	if !errors.Is(err, ErrInvalidHumanVerificationSettings) {
		t.Fatalf("short ciphertext error = %v, want %v", err, ErrInvalidHumanVerificationSettings)
	}
}
