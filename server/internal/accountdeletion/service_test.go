package accountdeletion

import (
	"context"
	"errors"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/history"
	"github.com/termlink/termlink/server/internal/store"
)

var _ ObjectStore = (*history.Store)(nil)

func TestNewRequiresEveryHardDeletionDependency(t *testing.T) {
	t.Parallel()
	repository := &fakeRepository{}
	identity := &fakeIdentity{}
	objects := &fakeObjects{}
	processorID := uuid.New()
	for name, create := range map[string]func() (*Service, error){
		"repository": func() (*Service, error) { return New(nil, identity, objects, processorID) },
		"identity":   func() (*Service, error) { return New(repository, nil, objects, processorID) },
		"objects":    func() (*Service, error) { return New(repository, identity, nil, processorID) },
		"processor":  func() (*Service, error) { return New(repository, identity, objects, uuid.Nil) },
	} {
		if _, err := create(); !errors.Is(err, ErrInvalidConfiguration) {
			t.Fatalf("missing %s error=%v, want ErrInvalidConfiguration", name, err)
		}
	}
}

func TestProcessOnceRequiresIdentityAndObjectsBeforeFinalize(t *testing.T) {
	t.Parallel()
	accountID, requestID, processorID := uuid.New(), uuid.New(), uuid.New()
	events := make([]string, 0)
	objects := []store.UserPortalAccountDeletionObject{
		{Kind: store.UserPortalAccountDeletionStoredObject, ObjectKey: "accounts/hidden/sessions/hidden/chunk/hidden.bin"},
		{Kind: store.UserPortalAccountDeletionPrivacyExport, ObjectKey: "accounts/hidden/privacy-exports/hidden/1.bin"},
	}
	repository := &fakeRepository{
		jobs: []store.UserPortalAccountDeletionJob{{
			ID: requestID, AccountID: accountID, IdentityHandle: make([]byte, IdentityHandleCiphertextSize),
			IdentityHandleKeyVersion: 9, ProcessingFence: 7,
		}},
		objects: objects, events: &events,
	}
	identity := &fakeIdentity{events: &events}
	objectStore := &fakeObjects{events: &events}
	service, err := New(repository, identity, objectStore, processorID)
	if err != nil {
		t.Fatal(err)
	}
	completed, err := service.ProcessOnce(context.Background(), 1)
	if err != nil || completed != 1 {
		t.Fatalf("ProcessOnce completed=%d error=%v", completed, err)
	}
	want := []string{"prepare", "identity", "object", "privacy", "finalize"}
	if !reflect.DeepEqual(events, want) {
		t.Fatalf("processing order=%v, want %v", events, want)
	}
	if !reflect.DeepEqual(repository.finalizedObjects, objects) || repository.failedCode != "" {
		t.Fatalf("finalized=%v failed=%q", repository.finalizedObjects, repository.failedCode)
	}
	if len(identity.handle) != IdentityHandleCiphertextSize || identity.keyVersion != 9 {
		t.Fatalf("identity disabler received handle/version=%d/%d", len(identity.handle), identity.keyVersion)
	}
}

func TestProcessOnceNeverCompletesAfterAnyGateFailure(t *testing.T) {
	t.Parallel()
	secretKey := "accounts/secret-account/sessions/secret-session/chunk/secret-object.bin"
	for _, test := range []struct {
		name      string
		prepare   error
		identity  error
		object    error
		finalize  error
		wantCode  string
		wantFinal int
		handle    []byte
	}{
		{name: "prepare", prepare: errors.New("database detail"), wantCode: "DELETION_PREPARE_FAILED", handle: make([]byte, IdentityHandleCiphertextSize)},
		{name: "identity handle", wantCode: "IDENTITY_HANDLE_UNAVAILABLE"},
		{name: "identity", identity: errors.New("raw subject detail"), wantCode: "IDENTITY_DISABLE_FAILED", handle: make([]byte, IdentityHandleCiphertextSize)},
		{name: "object", object: errors.New(secretKey), wantCode: "OBJECT_DELETE_FAILED", handle: make([]byte, IdentityHandleCiphertextSize)},
		{name: "finalize", finalize: errors.New("database detail"), wantCode: "DELETION_FINALIZE_FAILED", wantFinal: 1, handle: make([]byte, IdentityHandleCiphertextSize)},
	} {
		test := test
		t.Run(test.name, func(t *testing.T) {
			requestID := uuid.New()
			repository := &fakeRepository{
				jobs: []store.UserPortalAccountDeletionJob{{
					ID: requestID, AccountID: uuid.New(), IdentityHandle: test.handle,
					IdentityHandleKeyVersion: 3, ProcessingFence: 2,
				}},
				objects:    []store.UserPortalAccountDeletionObject{{Kind: store.UserPortalAccountDeletionStoredObject, ObjectKey: secretKey}},
				prepareErr: test.prepare, finalizeErr: test.finalize,
			}
			service, err := New(repository, &fakeIdentity{err: test.identity}, &fakeObjects{err: test.object}, uuid.New())
			if err != nil {
				t.Fatal(err)
			}
			completed, processErr := service.ProcessOnce(context.Background(), 1)
			if completed != 0 || !errors.Is(processErr, ErrProcessingFailed) {
				t.Fatalf("completed=%d error=%v", completed, processErr)
			}
			if repository.finalizeCalls != test.wantFinal || repository.failedCode != test.wantCode {
				t.Fatalf("finalizeCalls=%d failure=%q, want %d/%q", repository.finalizeCalls, repository.failedCode, test.wantFinal, test.wantCode)
			}
			if strings.Contains(processErr.Error(), secretKey) {
				t.Fatal("object capability leaked through the service error")
			}
		})
	}
}

func TestProcessOnceUsesBoundedLeaseAndStableTrace(t *testing.T) {
	t.Parallel()
	requestID := uuid.New()
	repository := &fakeRepository{
		jobs: []store.UserPortalAccountDeletionJob{{
			ID: requestID, AccountID: uuid.New(), IdentityHandle: make([]byte, IdentityHandleCiphertextSize),
			IdentityHandleKeyVersion: 4, ProcessingFence: 1,
		}},
		prepareErr: errors.New("prepare"),
	}
	service, err := New(repository, &fakeIdentity{}, &fakeObjects{}, uuid.New())
	if err != nil {
		t.Fatal(err)
	}
	_, _ = service.ProcessOnce(context.Background(), 4)
	if repository.claimLimit != 4 || repository.claimLease != DefaultLease || repository.claimLease < 5*time.Second || repository.claimLease > 10*time.Minute {
		t.Fatalf("claim limit/lease=%d/%s", repository.claimLimit, repository.claimLease)
	}
	if repository.failedTrace != "account-deletion-processor:"+requestID.String() {
		t.Fatalf("stable trace=%q", repository.failedTrace)
	}
}

type fakeRepository struct {
	jobs             []store.UserPortalAccountDeletionJob
	objects          []store.UserPortalAccountDeletionObject
	prepareErr       error
	finalizeErr      error
	events           *[]string
	claimLimit       int
	claimLease       time.Duration
	finalizeCalls    int
	finalizedObjects []store.UserPortalAccountDeletionObject
	failedCode       string
	failedTrace      string
}

func (r *fakeRepository) ClaimUserPortalAccountDeletions(_ context.Context, _ uuid.UUID, lease time.Duration, limit int) ([]store.UserPortalAccountDeletionJob, error) {
	r.claimLease, r.claimLimit = lease, limit
	return append([]store.UserPortalAccountDeletionJob(nil), r.jobs...), nil
}

func (r *fakeRepository) PrepareUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID, int64) ([]store.UserPortalAccountDeletionObject, error) {
	if r.events != nil {
		*r.events = append(*r.events, "prepare")
	}
	return append([]store.UserPortalAccountDeletionObject(nil), r.objects...), r.prepareErr
}

func (r *fakeRepository) FinalizeUserPortalAccountDeletion(_ context.Context, _ uuid.UUID, _ uuid.UUID, _ int64, objects []store.UserPortalAccountDeletionObject, _ string) error {
	r.finalizeCalls++
	if r.events != nil {
		*r.events = append(*r.events, "finalize")
	}
	r.finalizedObjects = append([]store.UserPortalAccountDeletionObject(nil), objects...)
	return r.finalizeErr
}

func (r *fakeRepository) FailUserPortalAccountDeletion(_ context.Context, _ uuid.UUID, _ uuid.UUID, _ int64, code, trace string) error {
	r.failedCode, r.failedTrace = code, trace
	return nil
}

type fakeIdentity struct {
	err        error
	events     *[]string
	handle     []byte
	keyVersion int16
}

func (i *fakeIdentity) DisableAccountIdentity(_ context.Context, _ uuid.UUID, _ uuid.UUID, keyVersion int16, handle []byte) error {
	if i.events != nil {
		*i.events = append(*i.events, "identity")
	}
	i.handle = append([]byte(nil), handle...)
	i.keyVersion = keyVersion
	return i.err
}

type fakeObjects struct {
	err    error
	events *[]string
}

func (o *fakeObjects) Delete(context.Context, string) error {
	if o.events != nil {
		*o.events = append(*o.events, "object")
	}
	return o.err
}

func (o *fakeObjects) DeletePrivacyExport(context.Context, string) error {
	if o.events != nil {
		*o.events = append(*o.events, "privacy")
	}
	return o.err
}
