package accountdeletion

import (
	"bytes"
	"context"
	"errors"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type fakeBindingRepository struct {
	request      store.UserPortalAccountDeletion
	requestErr   error
	bindErr      error
	requestCalls int
	bindCalls    int
	accountID    uuid.UUID
	ciphers      [][]byte
	versions     []int16
	bindKeys     []string
}

func (r *fakeBindingRepository) RequestUserPortalAccountDeletionState(_ context.Context, accountID uuid.UUID, _, _ string) (store.UserPortalAccountDeletion, error) {
	r.requestCalls++
	r.accountID = accountID
	return r.request, r.requestErr
}

func (r *fakeBindingRepository) BindUserPortalAccountDeletionIdentityHandle(_ context.Context, accountID, _ uuid.UUID, cipher []byte, version int16, key, _ string) (store.UserPortalAccountDeletion, error) {
	r.bindCalls++
	r.accountID = accountID
	r.ciphers = append(r.ciphers, append([]byte(nil), cipher...))
	r.versions = append(r.versions, version)
	r.bindKeys = append(r.bindKeys, key)
	return r.request, r.bindErr
}

func TestIdentityRequestBinderBindsAndRetriesRandomizedCiphertext(t *testing.T) {
	t.Parallel()
	accountID, requestID := uuid.New(), uuid.New()
	repository := &fakeBindingRepository{request: store.UserPortalAccountDeletion{
		ID: requestID, Status: "REQUESTED", RequestedAt: time.Now(), ConfirmationExpiresAt: time.Now().Add(time.Minute),
	}}
	binder, err := NewIdentityRequestBinder(repository, testIdentityHandleCodec(t), 7)
	if err != nil {
		t.Fatal(err)
	}
	for index := 0; index < 2; index++ {
		result, err := binder.RequestAndBind(context.Background(), accountID,
			"30000000-0000-4000-8000-000000000003", "delete-request-0001", "trace-delete-request")
		if err != nil || result.ID != requestID {
			t.Fatalf("attempt %d result=%+v error=%v", index, result, err)
		}
	}
	if repository.requestCalls != 2 || repository.bindCalls != 2 ||
		repository.versions[0] != 7 || repository.versions[1] != 7 ||
		repository.bindKeys[0] != repository.bindKeys[1] || len(repository.bindKeys[0]) > 128 ||
		bytes.Equal(repository.ciphers[0], repository.ciphers[1]) {
		t.Fatalf("retry contract calls=%d/%d versions=%v keys=%v randomized=%t",
			repository.requestCalls, repository.bindCalls, repository.versions,
			repository.bindKeys, !bytes.Equal(repository.ciphers[0], repository.ciphers[1]))
	}
}

func TestIdentityRequestBinderRejectsInvalidSubjectBeforeCreatingRequest(t *testing.T) {
	t.Parallel()
	repository := &fakeBindingRepository{}
	binder, err := NewIdentityRequestBinder(repository, testIdentityHandleCodec(t), 7)
	if err != nil {
		t.Fatal(err)
	}
	_, err = binder.RequestAndBind(context.Background(), uuid.New(), "not-a-keycloak-uuid", "delete-request-0001", "trace-delete")
	if !errors.Is(err, ErrIdentityHandleInvalid) || repository.requestCalls != 0 || repository.bindCalls != 0 {
		t.Fatalf("invalid subject error=%v calls=%d/%d", err, repository.requestCalls, repository.bindCalls)
	}
}

func TestIdentityRequestBinderFoldsBackendErrorsAndPreservesStableStoreErrors(t *testing.T) {
	t.Parallel()
	for name, fixture := range map[string]struct {
		requestErr error
		bindErr    error
		want       error
	}{
		"request backend": {requestErr: errors.New("sql contains raw subject"), want: ErrServiceUnavailable},
		"bind backend":    {bindErr: errors.New("keycloak subject leaked"), want: ErrServiceUnavailable},
		"conflict":        {bindErr: store.ErrConflict, want: store.ErrConflict},
	} {
		fixture := fixture
		t.Run(name, func(t *testing.T) {
			repository := &fakeBindingRepository{
				request: store.UserPortalAccountDeletion{ID: uuid.New()}, requestErr: fixture.requestErr, bindErr: fixture.bindErr,
			}
			binder, err := NewIdentityRequestBinder(repository, testIdentityHandleCodec(t), 7)
			if err != nil {
				t.Fatal(err)
			}
			_, got := binder.RequestAndBind(context.Background(), uuid.New(), uuid.NewString(), "delete-request-0001", "trace-delete")
			if !errors.Is(got, fixture.want) || stringsContainSensitiveError(got) {
				t.Fatalf("error=%v, want stable %v", got, fixture.want)
			}
		})
	}
}

func stringsContainSensitiveError(err error) bool {
	return err != nil && (bytes.Contains([]byte(err.Error()), []byte("subject")) || bytes.Contains([]byte(err.Error()), []byte("sql")))
}
