package accountdeletion

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/google/uuid"
)

type fakeIdentityAdminClient struct {
	subject string
	err     error
	calls   int
}

func (c *fakeIdentityAdminClient) DeleteRegistrationIdentity(_ context.Context, subject string) error {
	c.calls++
	c.subject = subject
	return c.err
}

func TestKeycloakIdentityDisablerDecryptsWithVersionAndRequestAAD(t *testing.T) {
	t.Parallel()
	codec := testIdentityHandleCodec(t)
	client := &fakeIdentityAdminClient{}
	disabler, err := NewKeycloakIdentityDisabler(codec, client)
	if err != nil {
		t.Fatal(err)
	}
	accountID, requestID := uuid.New(), uuid.New()
	subject := uuid.NewString()
	encrypted, err := codec.Encrypt(accountID, requestID, 8, subject)
	if err != nil {
		t.Fatal(err)
	}
	if err := disabler.DisableAccountIdentity(context.Background(), accountID, requestID, 8, encrypted); err != nil {
		t.Fatal(err)
	}
	if client.calls != 1 || client.subject != subject {
		t.Fatalf("identity client calls/subject=%d/%q", client.calls, client.subject)
	}
	if err := disabler.DisableAccountIdentity(context.Background(), accountID, requestID, 7, encrypted); !errors.Is(err, ErrIdentityHandleInvalid) {
		t.Fatalf("wrong version error=%v", err)
	}
	if client.calls != 1 {
		t.Fatal("invalid ciphertext reached the identity provider")
	}
}

func TestKeycloakIdentityDisablerFoldsProviderErrors(t *testing.T) {
	t.Parallel()
	codec := testIdentityHandleCodec(t)
	client := &fakeIdentityAdminClient{err: errors.New("raw keycloak response includes subject")}
	disabler, err := NewKeycloakIdentityDisabler(codec, client)
	if err != nil {
		t.Fatal(err)
	}
	accountID, requestID := uuid.New(), uuid.New()
	encrypted, err := codec.Encrypt(accountID, requestID, 7, uuid.NewString())
	if err != nil {
		t.Fatal(err)
	}
	err = disabler.DisableAccountIdentity(context.Background(), accountID, requestID, 7, encrypted)
	if !errors.Is(err, ErrServiceUnavailable) || strings.Contains(err.Error(), "keycloak") || strings.Contains(err.Error(), "subject") {
		t.Fatalf("provider error leaked as %v", err)
	}
}
