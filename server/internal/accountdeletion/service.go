// Package accountdeletion performs the external and durable halves of an
// account erasure. It deliberately has no optional identity-provider mode: an
// account is never marked completed unless identity disablement succeeds.
package accountdeletion

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const DefaultLease = 5 * time.Minute

var (
	ErrInvalidConfiguration = errors.New("account deletion configuration invalid")
	ErrServiceUnavailable   = errors.New("account deletion service unavailable")
	ErrProcessingFailed     = errors.New("account deletion processing failed")
)

type Repository interface {
	ClaimUserPortalAccountDeletions(context.Context, uuid.UUID, time.Duration, int) ([]store.UserPortalAccountDeletionJob, error)
	PrepareUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID, int64) ([]store.UserPortalAccountDeletionObject, error)
	FinalizeUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID, int64, []store.UserPortalAccountDeletionObject, string) error
	FailUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID, int64, string, string) error
}

// IdentityDisabler must be idempotent. It must synchronously revoke/disable
// the account's login identity and active identity-provider sessions; already
// disabled/missing identities must return success. Implementations must not
// log or return a raw subject identifier.
type IdentityDisabler interface {
	DisableAccountIdentity(context.Context, uuid.UUID, uuid.UUID, int16, []byte) error
}

// ObjectStore deletion methods must be idempotent: a missing object is a
// successful deletion. Implementations must never include the object key in
// logs, metrics, audit records, or returned error text.
type ObjectStore interface {
	Delete(context.Context, string) error
	DeletePrivacyExport(context.Context, string) error
}

type Service struct {
	repository  Repository
	identity    IdentityDisabler
	objects     ObjectStore
	processorID uuid.UUID
	lease       time.Duration
}

func New(repository Repository, identity IdentityDisabler, objects ObjectStore, processorID uuid.UUID) (*Service, error) {
	if repository == nil || identity == nil || objects == nil || processorID == uuid.Nil {
		return nil, ErrInvalidConfiguration
	}
	return &Service{
		repository: repository, identity: identity, objects: objects,
		processorID: processorID, lease: DefaultLease,
	}, nil
}

// ProcessOnce claims a bounded batch. Each job must pass every gate in order:
// lock and snapshot capabilities, disable identity, erase objects, then run a
// fenced transactional finalization. Individual failures are persisted using
// stable low-cardinality codes; sensitive provider/object errors never escape.
func (s *Service) ProcessOnce(ctx context.Context, limit int) (int, error) {
	if s == nil || s.repository == nil || s.identity == nil || s.objects == nil || s.processorID == uuid.Nil {
		return 0, ErrInvalidConfiguration
	}
	jobs, err := s.repository.ClaimUserPortalAccountDeletions(ctx, s.processorID, s.lease, limit)
	if err != nil {
		if ctx.Err() != nil {
			return 0, ctx.Err()
		}
		return 0, ErrServiceUnavailable
	}
	completed := 0
	failed := false
	for _, job := range jobs {
		if ctx.Err() != nil {
			return completed, ctx.Err()
		}
		failureCode := s.process(ctx, job)
		if failureCode == "" {
			completed++
			continue
		}
		if ctx.Err() != nil {
			return completed, ctx.Err()
		}
		failed = true
		_ = s.repository.FailUserPortalAccountDeletion(
			ctx, s.processorID, job.ID, job.ProcessingFence, failureCode, processorTrace(job.ID),
		)
	}
	if failed {
		return completed, ErrProcessingFailed
	}
	return completed, nil
}

func (s *Service) process(ctx context.Context, job store.UserPortalAccountDeletionJob) string {
	if len(job.IdentityHandle) != IdentityHandleCiphertextSize || job.IdentityHandleKeyVersion < 1 {
		return "IDENTITY_HANDLE_UNAVAILABLE"
	}
	objects, err := s.repository.PrepareUserPortalAccountDeletion(ctx, s.processorID, job.ID, job.ProcessingFence)
	if err != nil {
		return "DELETION_PREPARE_FAILED"
	}
	if err := s.identity.DisableAccountIdentity(
		ctx, job.AccountID, job.ID, job.IdentityHandleKeyVersion, append([]byte(nil), job.IdentityHandle...),
	); err != nil {
		return "IDENTITY_DISABLE_FAILED"
	}
	for _, object := range objects {
		switch object.Kind {
		case store.UserPortalAccountDeletionStoredObject:
			err = s.objects.Delete(ctx, object.ObjectKey)
		case store.UserPortalAccountDeletionPrivacyExport:
			err = s.objects.DeletePrivacyExport(ctx, object.ObjectKey)
		default:
			return "OBJECT_CAPABILITY_INVALID"
		}
		if err != nil {
			return "OBJECT_DELETE_FAILED"
		}
	}
	if err := s.repository.FinalizeUserPortalAccountDeletion(
		ctx, s.processorID, job.ID, job.ProcessingFence, objects, processorTrace(job.ID),
	); err != nil {
		return "DELETION_FINALIZE_FAILED"
	}
	return ""
}

func processorTrace(requestID uuid.UUID) string {
	return "account-deletion-processor:" + requestID.String()
}

var _ Repository = (*store.Postgres)(nil)
