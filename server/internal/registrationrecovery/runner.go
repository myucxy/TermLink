// Package registrationrecovery reconciles invitation reservations with
// HMAC-proven identities created by the configured Keycloak realm.
package registrationrecovery

import (
	"context"
	"errors"
	"log/slog"
	"net/url"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/localnet"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	DefaultInterval    = 5 * time.Minute
	DefaultStaleAfter  = 15 * time.Minute
	DefaultItemTimeout = 15 * time.Second
	DefaultBatchSize   = 50
	MaxBatchSize       = 200
	MaxItemTimeout     = time.Minute
)

var (
	ErrInvalidConfiguration = errors.New("registration recovery configuration is invalid")
	ErrCandidateOverflow    = errors.New("registration recovery candidate feed exceeded its bound")
	ErrCandidateInvalid     = errors.New("registration recovery candidate is invalid")
	ErrIdentityLookup       = errors.New("registration recovery identity lookup failed")
	ErrIdentityDelete       = errors.New("registration recovery identity deletion failed")
	ErrStoreOperation       = errors.New("registration recovery store operation failed")
)

// Store keeps every authoritative transition in PostgreSQL. The runner never
// changes invitation inventory based on a timeout or an ambiguous provider
// response.
type Store interface {
	ListRegistrationRecoveryCandidates(context.Context, time.Time, int) ([]store.RegistrationRecoveryCandidate, error)
	ConfirmRecoveredRegistration(context.Context, uuid.UUID) (store.RegistrationRecoveryResolution, error)
	ReleaseAbsentRegistration(context.Context, uuid.UUID) (store.RegistrationRecoveryResolution, error)
	RecordRegistrationOrphanDeleted(context.Context, uuid.UUID) error
	RegistrationSubjectExists(context.Context, [32]byte) (bool, error)
}

// IdentityAdmin exposes only the two Keycloak operations recovery needs. A
// lookup implementation must return EXACT only after validating the attempt
// UUID and its server-generated HMAC proof.
type IdentityAdmin interface {
	LookupRegistrationAttempt(context.Context, uuid.UUID) (auth.RegistrationIdentityLookup, error)
	DeleteRegistrationIdentity(context.Context, string) error
}

var _ Store = (*store.Postgres)(nil)
var _ IdentityAdmin = (*auth.KeycloakAdminClient)(nil)

type Options struct {
	Issuer      string
	SubjectKey  []byte
	Interval    time.Duration
	StaleAfter  time.Duration
	ItemTimeout time.Duration
	BatchSize   int
	Clock       func() time.Time
	Logger      *slog.Logger
}

type Result struct {
	CandidatesListed        int
	CandidatesInvalid       int
	ProviderConfirmed       int
	AlreadyConfirmed        int
	ReservationsReleased    int
	ReleasedAbsent          int
	OrphanIdentitiesDeleted int
	ProtectedIdentities     int
	AmbiguousIdentities     int
	LookupFailures          int
	DeleteFailures          int
	StoreFailures           int
	BatchFull               bool
}

type Runner struct {
	store       Store
	admin       IdentityAdmin
	issuer      string
	subjectKey  []byte
	interval    time.Duration
	staleAfter  time.Duration
	itemTimeout time.Duration
	batchSize   int
	clock       func() time.Time
	logger      *slog.Logger
	newTicker   func(time.Duration) ticker
}

type ticker interface {
	Chan() <-chan time.Time
	Stop()
}

type realTicker struct{ *time.Ticker }

func (t realTicker) Chan() <-chan time.Time { return t.C }

func New(repository Store, admin IdentityAdmin, options Options) (*Runner, error) {
	if repository == nil || admin == nil || !validIssuer(options.Issuer) || len(options.SubjectKey) < 32 {
		return nil, ErrInvalidConfiguration
	}
	if options.Interval <= 0 {
		options.Interval = DefaultInterval
	}
	if options.StaleAfter <= 0 {
		options.StaleAfter = DefaultStaleAfter
	}
	if options.ItemTimeout <= 0 || options.ItemTimeout > MaxItemTimeout {
		options.ItemTimeout = DefaultItemTimeout
	}
	if options.BatchSize < 1 || options.BatchSize > MaxBatchSize {
		options.BatchSize = DefaultBatchSize
	}
	if options.Clock == nil {
		options.Clock = time.Now
	}
	if options.Logger == nil {
		options.Logger = slog.Default()
	}
	return &Runner{
		store: repository, admin: admin, issuer: options.Issuer,
		subjectKey: append([]byte(nil), options.SubjectKey...),
		interval:   options.Interval, staleAfter: options.StaleAfter,
		itemTimeout: options.ItemTimeout, batchSize: options.BatchSize,
		clock: options.Clock, logger: options.Logger,
		newTicker: func(interval time.Duration) ticker { return realTicker{time.NewTicker(interval)} },
	}, nil
}

func validIssuer(raw string) bool {
	if raw == "" || raw != strings.TrimSpace(raw) {
		return false
	}
	issuer, err := url.Parse(raw)
	if err != nil || issuer.Host == "" || issuer.User != nil ||
		issuer.RawQuery != "" || issuer.Fragment != "" {
		return false
	}
	if issuer.Scheme == "https" {
		return true
	}
	// Local-development relaxation shared with the config/auth packages: the
	// operator opt-in permits a plain-HTTP loopback/private issuer.
	return localnet.AllowHTTP() && localnet.IsLoopbackOrPrivateHost(issuer.Hostname())
}

// RunOnce reconciles one bounded batch. Errors on an individual candidate do
// not block independent candidates, but cancellation immediately stops new
// provider and store operations.
func (r *Runner) RunOnce(ctx context.Context) (Result, error) {
	if r == nil || r.store == nil || r.admin == nil {
		return Result{}, ErrInvalidConfiguration
	}
	cutoff := r.clock().UTC().Add(-r.staleAfter)
	candidates, err := r.store.ListRegistrationRecoveryCandidates(ctx, cutoff, r.batchSize)
	if err != nil {
		if ctx.Err() != nil {
			return Result{}, ctx.Err()
		}
		return Result{StoreFailures: 1}, ErrStoreOperation
	}
	result := Result{CandidatesListed: len(candidates)}
	var passErrors []error
	if len(candidates) > r.batchSize {
		candidates = candidates[:r.batchSize]
		result.CandidatesListed = r.batchSize
		passErrors = append(passErrors, ErrCandidateOverflow)
	}
	result.BatchFull = len(candidates) == r.batchSize
	for _, candidate := range candidates {
		if err := ctx.Err(); err != nil {
			return result, errors.Join(append(passErrors, err)...)
		}
		if !validCandidate(candidate, cutoff) {
			result.CandidatesInvalid++
			passErrors = append(passErrors, ErrCandidateInvalid)
			r.warn(candidate.AttemptID, "INVALID_CANDIDATE")
			continue
		}
		itemContext, cancel := context.WithTimeout(ctx, r.itemTimeout)
		lookup, lookupErr := r.admin.LookupRegistrationAttempt(itemContext, candidate.AttemptID)
		cancel()
		if lookupErr != nil {
			if ctx.Err() != nil {
				return result, errors.Join(append(passErrors, ctx.Err())...)
			}
			result.LookupFailures++
			passErrors = append(passErrors, ErrIdentityLookup)
			r.warn(candidate.AttemptID, "IDENTITY_LOOKUP_FAILED")
			continue
		}
		switch lookup.Status {
		case auth.RegistrationIdentityAbsent:
			if err := r.processAbsent(ctx, candidate.AttemptID, &result); err != nil {
				passErrors = append(passErrors, err)
			}
		case auth.RegistrationIdentityExact:
			if !validIdentityID(lookup.UserID) {
				result.AmbiguousIdentities++
				r.warn(candidate.AttemptID, "IDENTITY_RESULT_AMBIGUOUS")
				continue
			}
			if err := r.processExact(ctx, candidate.AttemptID, lookup.UserID, &result); err != nil {
				passErrors = append(passErrors, err)
			}
		case auth.RegistrationIdentityAmbiguous:
			result.AmbiguousIdentities++
			r.warn(candidate.AttemptID, "IDENTITY_RESULT_AMBIGUOUS")
		default:
			result.AmbiguousIdentities++
			r.warn(candidate.AttemptID, "IDENTITY_RESULT_INVALID")
		}
	}
	return result, errors.Join(passErrors...)
}

func validCandidate(candidate store.RegistrationRecoveryCandidate, cutoff time.Time) bool {
	if candidate.AttemptID == uuid.Nil {
		return false
	}
	switch candidate.State {
	case "RESERVED":
		return !candidate.ReservedUntil.IsZero() && !candidate.ReservedUntil.After(cutoff)
	case "RELEASED":
		return candidate.ReleasedAt != nil && !candidate.ReleasedAt.IsZero() && !candidate.ReleasedAt.After(cutoff)
	default:
		return false
	}
}

func validIdentityID(value string) bool {
	trimmed := strings.TrimSpace(value)
	parsed, err := uuid.Parse(trimmed)
	return err == nil && parsed != uuid.Nil && parsed.String() == strings.ToLower(trimmed)
}

func (r *Runner) processAbsent(ctx context.Context, attemptID uuid.UUID, result *Result) error {
	resolution, err := r.store.ReleaseAbsentRegistration(ctx, attemptID)
	if err != nil {
		result.StoreFailures++
		r.warn(attemptID, "STORE_RELEASE_FAILED")
		return ErrStoreOperation
	}
	switch resolution {
	case store.RegistrationRecoveryReleased:
		result.ReservationsReleased++
	case store.RegistrationRecoveryAlreadyReleased:
		result.ReleasedAbsent++
	case store.RegistrationRecoveryAlreadyConfirmed:
		result.AlreadyConfirmed++
	default:
		result.StoreFailures++
		r.warn(attemptID, "STORE_RELEASE_RESULT_INVALID")
		return ErrStoreOperation
	}
	return nil
}

func (r *Runner) processExact(ctx context.Context, attemptID uuid.UUID, identityID string, result *Result) error {
	resolution, err := r.store.ConfirmRecoveredRegistration(ctx, attemptID)
	if err != nil {
		result.StoreFailures++
		r.warn(attemptID, "STORE_CONFIRM_FAILED")
		return ErrStoreOperation
	}
	switch resolution {
	case store.RegistrationRecoveryConfirmed:
		result.ProviderConfirmed++
		return nil
	case store.RegistrationRecoveryAlreadyConfirmed:
		result.AlreadyConfirmed++
		return nil
	case store.RegistrationRecoveryOrphan:
		// Continue below. Only an exact HMAC-proven identity and an absent
		// internal subject are sufficient authority for deletion.
	default:
		result.StoreFailures++
		r.warn(attemptID, "STORE_CONFIRM_RESULT_INVALID")
		return ErrStoreOperation
	}

	subjectHash := auth.SubjectHash(r.subjectKey, auth.Identity{Issuer: r.issuer, Subject: identityID})
	exists, err := r.store.RegistrationSubjectExists(ctx, subjectHash)
	if err != nil {
		result.StoreFailures++
		r.warn(attemptID, "STORE_SUBJECT_CHECK_FAILED")
		return ErrStoreOperation
	}
	if exists {
		result.ProtectedIdentities++
		r.warn(attemptID, "IDENTITY_DELETE_PROTECTED")
		return nil
	}
	itemContext, cancel := context.WithTimeout(ctx, r.itemTimeout)
	err = r.admin.DeleteRegistrationIdentity(itemContext, identityID)
	cancel()
	if err != nil {
		result.DeleteFailures++
		r.warn(attemptID, "IDENTITY_DELETE_FAILED")
		return ErrIdentityDelete
	}
	result.OrphanIdentitiesDeleted++
	if err := r.store.RecordRegistrationOrphanDeleted(ctx, attemptID); err != nil {
		result.StoreFailures++
		r.warn(attemptID, "STORE_DELETE_MARKER_FAILED")
		return ErrStoreOperation
	}
	return nil
}

// Run performs one initial pass and then retries unresolved work on a fixed
// interval. All mutations are idempotent, so overlapping replicas are safe.
func (r *Runner) Run(ctx context.Context) {
	r.runAndLog(ctx)
	ticker := r.newTicker(r.interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.Chan():
			r.runAndLog(ctx)
		}
	}
}

func (r *Runner) runAndLog(ctx context.Context) {
	result, err := r.RunOnce(ctx)
	if ctx.Err() != nil {
		return
	}
	if err != nil || result.AmbiguousIdentities > 0 || result.ProtectedIdentities > 0 {
		r.logger.Warn("registration recovery completed with unresolved candidates",
			"error_class", "REGISTRATION_RECOVERY_PARTIAL",
			"candidates", result.CandidatesListed,
			"ambiguous", result.AmbiguousIdentities,
			"protected", result.ProtectedIdentities,
			"lookup_failures", result.LookupFailures,
			"delete_failures", result.DeleteFailures,
			"store_failures", result.StoreFailures)
		return
	}
	if result.ProviderConfirmed > 0 || result.ReservationsReleased > 0 || result.OrphanIdentitiesDeleted > 0 {
		r.logger.Info("registration recovery completed",
			"provider_confirmed", result.ProviderConfirmed,
			"reservations_released", result.ReservationsReleased,
			"orphan_identities_deleted", result.OrphanIdentitiesDeleted)
	}
}

func (r *Runner) warn(attemptID uuid.UUID, reason string) {
	r.logger.Warn("registration recovery candidate requires attention",
		"registration_attempt_id", attemptID,
		"reason", reason)
}
