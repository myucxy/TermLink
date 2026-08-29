package accountdeletion

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"strings"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

// RequestBinder is the only account-deletion creation capability exposed to
// HTTP adapters. Implementations must bind a provider identity handle before
// returning success.
type RequestBinder interface {
	RequestAndBind(context.Context, uuid.UUID, string, string, string) (store.UserPortalAccountDeletion, error)
}

type RequestBindingRepository interface {
	RequestUserPortalAccountDeletionState(context.Context, uuid.UUID, string, string) (store.UserPortalAccountDeletion, error)
	BindUserPortalAccountDeletionIdentityHandle(context.Context, uuid.UUID, uuid.UUID, []byte, int16, string, string) (store.UserPortalAccountDeletion, error)
}

type IdentityRequestBinder struct {
	repository RequestBindingRepository
	codec      *IdentityHandleCodec
	keyVersion int16
}

func NewIdentityRequestBinder(repository RequestBindingRepository, codec *IdentityHandleCodec, keyVersion int16) (*IdentityRequestBinder, error) {
	if repository == nil || codec == nil || keyVersion < 1 {
		return nil, ErrInvalidConfiguration
	}
	if _, exists := codec.keys[keyVersion]; !exists {
		return nil, ErrInvalidConfiguration
	}
	return &IdentityRequestBinder{repository: repository, codec: codec, keyVersion: keyVersion}, nil
}

// RequestAndBind first obtains the durable request ID, then encrypts the
// trusted OIDC subject with AAD bound to that ID and atomically binds it. A
// retry with the same client idempotency key derives the same binding key;
// randomized AES-GCM ciphertext is therefore safely replayed by the Store.
func (b *IdentityRequestBinder) RequestAndBind(ctx context.Context, accountID uuid.UUID, subject, idempotencyKey, traceID string) (store.UserPortalAccountDeletion, error) {
	if b == nil || b.repository == nil || b.codec == nil || accountID == uuid.Nil ||
		b.keyVersion < 1 || len(idempotencyKey) < 8 || len(idempotencyKey) > 128 ||
		strings.TrimSpace(traceID) == "" || len(traceID) > 256 {
		return store.UserPortalAccountDeletion{}, ErrInvalidConfiguration
	}
	if !canonicalKeycloakSubject(subject) {
		return store.UserPortalAccountDeletion{}, ErrIdentityHandleInvalid
	}
	request, err := b.repository.RequestUserPortalAccountDeletionState(ctx, accountID, idempotencyKey, traceID)
	if err != nil {
		return store.UserPortalAccountDeletion{}, safeRequestBindingError(err)
	}
	encrypted, err := b.codec.Encrypt(accountID, request.ID, b.keyVersion, subject)
	if err != nil {
		return store.UserPortalAccountDeletion{}, ErrIdentityHandleInvalid
	}
	defer clear(encrypted)
	bound, err := b.repository.BindUserPortalAccountDeletionIdentityHandle(
		ctx, accountID, request.ID, encrypted, b.keyVersion, identityBindingIdempotencyKey(idempotencyKey), traceID,
	)
	if err != nil {
		return store.UserPortalAccountDeletion{}, safeRequestBindingError(err)
	}
	return bound, nil
}

func identityBindingIdempotencyKey(requestKey string) string {
	digest := sha256.Sum256([]byte("termlink/account-deletion/identity-bind-key/v1\x00" + requestKey))
	return "deletion-identity-" + hex.EncodeToString(digest[:])
}

func safeRequestBindingError(err error) error {
	for _, stable := range []error{
		store.ErrDenied, store.ErrNotFound, store.ErrConflict, store.ErrSessionActive,
		store.ErrUserPortalActionUnsupported,
	} {
		if errors.Is(err, stable) {
			return stable
		}
	}
	return ErrServiceUnavailable
}

var _ RequestBindingRepository = (*store.Postgres)(nil)
