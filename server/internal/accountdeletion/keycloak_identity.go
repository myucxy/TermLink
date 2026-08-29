package accountdeletion

import (
	"context"

	"github.com/google/uuid"
)

// IdentityAdminClient is intentionally minimal so the account-deletion
// package never receives Keycloak credentials, tokens, URLs, or response
// bodies. The production Keycloak client treats HTTP 404 as idempotent
// success.
type IdentityAdminClient interface {
	DeleteRegistrationIdentity(context.Context, string) error
}

type KeycloakIdentityDisabler struct {
	codec  *IdentityHandleCodec
	client IdentityAdminClient
}

func NewKeycloakIdentityDisabler(codec *IdentityHandleCodec, client IdentityAdminClient) (*KeycloakIdentityDisabler, error) {
	if codec == nil || client == nil {
		return nil, ErrInvalidConfiguration
	}
	return &KeycloakIdentityDisabler{codec: codec, client: client}, nil
}

func (d *KeycloakIdentityDisabler) DisableAccountIdentity(ctx context.Context, accountID, requestID uuid.UUID, keyVersion int16, encrypted []byte) error {
	if d == nil || d.codec == nil || d.client == nil {
		return ErrInvalidConfiguration
	}
	subject, err := d.codec.Decrypt(accountID, requestID, keyVersion, encrypted)
	if err != nil {
		return ErrIdentityHandleInvalid
	}
	if err := d.client.DeleteRegistrationIdentity(ctx, subject); err != nil {
		return ErrServiceUnavailable
	}
	return nil
}

var _ IdentityDisabler = (*KeycloakIdentityDisabler)(nil)
