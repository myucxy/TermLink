package registrationrecovery

import (
	"bytes"
	"context"
	"errors"
	"log/slog"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

var (
	testAttemptID  = uuid.MustParse("10000000-0000-4000-8000-000000000001")
	testIdentityID = "20000000-0000-4000-8000-000000000002"
	testNow        = time.Date(2026, 8, 27, 12, 0, 0, 0, time.UTC)
)

type recoveryStoreStub struct {
	candidates []store.RegistrationRecoveryCandidate
	listErr    error

	confirmResults []store.RegistrationRecoveryResolution
	confirmErrs    []error
	releaseResults []store.RegistrationRecoveryResolution
	releaseErrs    []error
	subjectExists  bool
	subjectErr     error
	markerErrs     []error

	listCalls    int
	confirmCalls int
	releaseCalls int
	subjectCalls int
	markerCalls  int
	lastSubject  [32]byte
}

func (s *recoveryStoreStub) ListRegistrationRecoveryCandidates(_ context.Context, _ time.Time, _ int) ([]store.RegistrationRecoveryCandidate, error) {
	s.listCalls++
	return append([]store.RegistrationRecoveryCandidate(nil), s.candidates...), s.listErr
}

func (s *recoveryStoreStub) ConfirmRecoveredRegistration(_ context.Context, _ uuid.UUID) (store.RegistrationRecoveryResolution, error) {
	index := s.confirmCalls
	s.confirmCalls++
	if err := sequenceValue(s.confirmErrs, index); err != nil {
		return "", err
	}
	return sequenceValue(s.confirmResults, index), nil
}

func (s *recoveryStoreStub) ReleaseAbsentRegistration(_ context.Context, _ uuid.UUID) (store.RegistrationRecoveryResolution, error) {
	index := s.releaseCalls
	s.releaseCalls++
	if err := sequenceValue(s.releaseErrs, index); err != nil {
		return "", err
	}
	return sequenceValue(s.releaseResults, index), nil
}

func (s *recoveryStoreStub) RecordRegistrationOrphanDeleted(_ context.Context, _ uuid.UUID) error {
	index := s.markerCalls
	s.markerCalls++
	return sequenceValue(s.markerErrs, index)
}

func (s *recoveryStoreStub) RegistrationSubjectExists(_ context.Context, hash [32]byte) (bool, error) {
	s.subjectCalls++
	s.lastSubject = hash
	return s.subjectExists, s.subjectErr
}

type recoveryAdminStub struct {
	lookups    []auth.RegistrationIdentityLookup
	lookupErrs []error
	deleteErrs []error

	lookupCalls       int
	deleteCalls       int
	afterDeleteAbsent bool
}

func (a *recoveryAdminStub) LookupRegistrationAttempt(_ context.Context, _ uuid.UUID) (auth.RegistrationIdentityLookup, error) {
	index := a.lookupCalls
	a.lookupCalls++
	if err := sequenceValue(a.lookupErrs, index); err != nil {
		return auth.RegistrationIdentityLookup{}, err
	}
	if a.afterDeleteAbsent && a.deleteCalls > 0 {
		return auth.RegistrationIdentityLookup{Status: auth.RegistrationIdentityAbsent}, nil
	}
	return sequenceValue(a.lookups, index), nil
}

func (a *recoveryAdminStub) DeleteRegistrationIdentity(_ context.Context, _ string) error {
	index := a.deleteCalls
	a.deleteCalls++
	return sequenceValue(a.deleteErrs, index)
}

func sequenceValue[T any](values []T, index int) T {
	var zero T
	if len(values) == 0 {
		return zero
	}
	if index >= len(values) {
		return values[len(values)-1]
	}
	return values[index]
}

func reservedCandidate() store.RegistrationRecoveryCandidate {
	return store.RegistrationRecoveryCandidate{
		AttemptID: testAttemptID, State: "RESERVED",
		ReservedUntil: testNow.Add(-31 * time.Minute), UpdatedAt: testNow.Add(-31 * time.Minute),
	}
}

func releasedCandidate() store.RegistrationRecoveryCandidate {
	releasedAt := testNow.Add(-31 * time.Minute)
	return store.RegistrationRecoveryCandidate{
		AttemptID: testAttemptID, State: "RELEASED",
		ReservedUntil: releasedAt.Add(-time.Minute), ReleasedAt: &releasedAt, UpdatedAt: releasedAt,
	}
}

func newTestRunner(t *testing.T, repository Store, admin IdentityAdmin, logger *slog.Logger) *Runner {
	t.Helper()
	if logger == nil {
		logger = slog.New(slog.NewTextHandler(&bytes.Buffer{}, nil))
	}
	runner, err := New(repository, admin, Options{
		Issuer: "https://identity.example.test/realms/termlink", SubjectKey: bytes.Repeat([]byte{0x41}, 32),
		StaleAfter: 15 * time.Minute, ItemTimeout: time.Second, BatchSize: 50,
		Clock: func() time.Time { return testNow }, Logger: logger,
	})
	if err != nil {
		t.Fatalf("New() error = %v", err)
	}
	return runner
}

func exactLookup() auth.RegistrationIdentityLookup {
	return auth.RegistrationIdentityLookup{Status: auth.RegistrationIdentityExact, UserID: testIdentityID}
}

func TestRunnerRecoversIdentityCreatedBeforeDatabaseConfirmation(t *testing.T) {
	repository := &recoveryStoreStub{
		candidates:     []store.RegistrationRecoveryCandidate{reservedCandidate()},
		confirmResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryConfirmed, store.RegistrationRecoveryAlreadyConfirmed},
	}
	admin := &recoveryAdminStub{lookups: []auth.RegistrationIdentityLookup{exactLookup()}}
	runner := newTestRunner(t, repository, admin, nil)

	first, err := runner.RunOnce(context.Background())
	if err != nil || first.ProviderConfirmed != 1 {
		t.Fatalf("first RunOnce() = %+v, %v", first, err)
	}
	second, err := runner.RunOnce(context.Background())
	if err != nil || second.AlreadyConfirmed != 1 {
		t.Fatalf("second RunOnce() = %+v, %v", second, err)
	}
	if admin.deleteCalls != 0 || repository.releaseCalls != 0 {
		t.Fatalf("confirmed identity mutated destructively: delete=%d release=%d", admin.deleteCalls, repository.releaseCalls)
	}
}

func TestRunnerProviderTimeoutButExactIdentityConfirmsWithoutRelease(t *testing.T) {
	repository := &recoveryStoreStub{
		candidates:     []store.RegistrationRecoveryCandidate{reservedCandidate()},
		confirmResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryConfirmed},
	}
	admin := &recoveryAdminStub{lookups: []auth.RegistrationIdentityLookup{exactLookup()}}

	result, err := newTestRunner(t, repository, admin, nil).RunOnce(context.Background())
	if err != nil || result.ProviderConfirmed != 1 {
		t.Fatalf("RunOnce() = %+v, %v", result, err)
	}
	if repository.releaseCalls != 0 {
		t.Fatal("an exact identity was treated as an absent provider result")
	}
}

func TestRunnerDatabaseAlreadyConfirmedNeverDeletesIdentity(t *testing.T) {
	repository := &recoveryStoreStub{
		candidates:     []store.RegistrationRecoveryCandidate{reservedCandidate()},
		confirmResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryAlreadyConfirmed},
	}
	admin := &recoveryAdminStub{lookups: []auth.RegistrationIdentityLookup{exactLookup()}}

	result, err := newTestRunner(t, repository, admin, nil).RunOnce(context.Background())
	if err != nil || result.AlreadyConfirmed != 1 || admin.deleteCalls != 0 {
		t.Fatalf("RunOnce() = %+v, %v; delete=%d", result, err, admin.deleteCalls)
	}
}

func TestRunnerAuthoritativeAbsentReleasesIdempotently(t *testing.T) {
	repository := &recoveryStoreStub{
		candidates:     []store.RegistrationRecoveryCandidate{reservedCandidate()},
		releaseResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryReleased, store.RegistrationRecoveryAlreadyReleased},
	}
	admin := &recoveryAdminStub{lookups: []auth.RegistrationIdentityLookup{{Status: auth.RegistrationIdentityAbsent}}}
	runner := newTestRunner(t, repository, admin, nil)

	first, err := runner.RunOnce(context.Background())
	if err != nil || first.ReservationsReleased != 1 {
		t.Fatalf("first RunOnce() = %+v, %v", first, err)
	}
	second, err := runner.RunOnce(context.Background())
	if err != nil || second.ReleasedAbsent != 1 {
		t.Fatalf("second RunOnce() = %+v, %v", second, err)
	}
	if repository.releaseCalls != 2 || repository.confirmCalls != 0 || admin.deleteCalls != 0 {
		t.Fatalf("unexpected calls: release=%d confirm=%d delete=%d", repository.releaseCalls, repository.confirmCalls, admin.deleteCalls)
	}
}

func TestRunnerDeletesOnlyReleasedExactOrphan(t *testing.T) {
	repository := &recoveryStoreStub{
		candidates:     []store.RegistrationRecoveryCandidate{releasedCandidate()},
		confirmResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryOrphan},
		releaseResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryAlreadyReleased},
	}
	admin := &recoveryAdminStub{lookups: []auth.RegistrationIdentityLookup{exactLookup()}, afterDeleteAbsent: true}
	runner := newTestRunner(t, repository, admin, nil)

	first, err := runner.RunOnce(context.Background())
	if err != nil || first.OrphanIdentitiesDeleted != 1 || repository.markerCalls != 1 {
		t.Fatalf("first RunOnce() = %+v, %v; marker=%d", first, err, repository.markerCalls)
	}
	second, err := runner.RunOnce(context.Background())
	if err != nil || second.ReleasedAbsent != 1 {
		t.Fatalf("second RunOnce() = %+v, %v", second, err)
	}
	if admin.deleteCalls != 1 {
		t.Fatalf("orphan deleted %d times, want once", admin.deleteCalls)
	}
}

func TestRunnerDeleteSuccessMarkerFailureHasSafeAbsentRetry(t *testing.T) {
	repository := &recoveryStoreStub{
		candidates:     []store.RegistrationRecoveryCandidate{releasedCandidate()},
		confirmResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryOrphan},
		releaseResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryAlreadyReleased},
		markerErrs:     []error{errors.New("database unavailable")},
	}
	admin := &recoveryAdminStub{lookups: []auth.RegistrationIdentityLookup{exactLookup()}, afterDeleteAbsent: true}
	runner := newTestRunner(t, repository, admin, nil)

	first, err := runner.RunOnce(context.Background())
	if !errors.Is(err, ErrStoreOperation) || first.OrphanIdentitiesDeleted != 1 {
		t.Fatalf("first RunOnce() = %+v, %v", first, err)
	}
	second, err := runner.RunOnce(context.Background())
	if err != nil || second.ReleasedAbsent != 1 || admin.deleteCalls != 1 {
		t.Fatalf("second RunOnce() = %+v, %v; delete=%d", second, err, admin.deleteCalls)
	}
}

func TestRunnerAmbiguousOrFailedLookupNeverMutates(t *testing.T) {
	tests := []struct {
		name      string
		lookup    auth.RegistrationIdentityLookup
		lookupErr error
		ambiguous int
		failed    int
	}{
		{name: "multiple or invalid provider matches", lookup: auth.RegistrationIdentityLookup{Status: auth.RegistrationIdentityAmbiguous}, ambiguous: 1},
		{name: "unknown status", lookup: auth.RegistrationIdentityLookup{Status: "UNKNOWN"}, ambiguous: 1},
		{name: "exact with invalid identity id", lookup: auth.RegistrationIdentityLookup{Status: auth.RegistrationIdentityExact, UserID: "not-a-uuid"}, ambiguous: 1},
		{name: "provider timeout", lookupErr: context.DeadlineExceeded, failed: 1},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			repository := &recoveryStoreStub{candidates: []store.RegistrationRecoveryCandidate{reservedCandidate()}}
			admin := &recoveryAdminStub{lookups: []auth.RegistrationIdentityLookup{tt.lookup}, lookupErrs: []error{tt.lookupErr}}
			result, _ := newTestRunner(t, repository, admin, nil).RunOnce(context.Background())
			if result.AmbiguousIdentities != tt.ambiguous || result.LookupFailures != tt.failed {
				t.Fatalf("RunOnce() result = %+v", result)
			}
			if repository.confirmCalls != 0 || repository.releaseCalls != 0 || admin.deleteCalls != 0 {
				t.Fatalf("ambiguous lookup mutated state: confirm=%d release=%d delete=%d", repository.confirmCalls, repository.releaseCalls, admin.deleteCalls)
			}
		})
	}
}

func TestRunnerProtectsIdentityAlreadyLinkedToInternalUser(t *testing.T) {
	repository := &recoveryStoreStub{
		candidates: []store.RegistrationRecoveryCandidate{releasedCandidate()}, subjectExists: true,
		confirmResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryOrphan},
	}
	admin := &recoveryAdminStub{lookups: []auth.RegistrationIdentityLookup{exactLookup()}}
	runner := newTestRunner(t, repository, admin, nil)

	result, err := runner.RunOnce(context.Background())
	if err != nil || result.ProtectedIdentities != 1 || admin.deleteCalls != 0 {
		t.Fatalf("RunOnce() = %+v, %v; delete=%d", result, err, admin.deleteCalls)
	}
	want := auth.SubjectHash(runner.subjectKey, auth.Identity{Issuer: runner.issuer, Subject: testIdentityID})
	if repository.lastSubject != want {
		t.Fatal("internal subject protection used the wrong issuer/subject hash")
	}
}

func TestRunnerDeleteFailureIsRetriedWithoutReleasingInvitation(t *testing.T) {
	repository := &recoveryStoreStub{
		candidates:     []store.RegistrationRecoveryCandidate{releasedCandidate()},
		confirmResults: []store.RegistrationRecoveryResolution{store.RegistrationRecoveryOrphan},
	}
	admin := &recoveryAdminStub{
		lookups:    []auth.RegistrationIdentityLookup{exactLookup()},
		deleteErrs: []error{errors.New("provider secret must not escape"), nil},
	}
	runner := newTestRunner(t, repository, admin, nil)

	first, err := runner.RunOnce(context.Background())
	if !errors.Is(err, ErrIdentityDelete) || first.DeleteFailures != 1 {
		t.Fatalf("first RunOnce() = %+v, %v", first, err)
	}
	second, err := runner.RunOnce(context.Background())
	if err != nil || second.OrphanIdentitiesDeleted != 1 {
		t.Fatalf("second RunOnce() = %+v, %v", second, err)
	}
	if repository.releaseCalls != 0 || admin.deleteCalls != 2 {
		t.Fatalf("unexpected calls: release=%d delete=%d", repository.releaseCalls, admin.deleteCalls)
	}
}

func TestRunnerLogsAndErrorsDoNotExposeProviderSecretsOrIdentity(t *testing.T) {
	var logs bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&logs, nil))
	repository := &recoveryStoreStub{candidates: []store.RegistrationRecoveryCandidate{reservedCandidate()}}
	secretError := errors.New("email=alice@example.test token=top-secret user=20000000-0000-4000-8000-000000000002")
	admin := &recoveryAdminStub{lookupErrs: []error{secretError}}

	_, err := newTestRunner(t, repository, admin, logger).RunOnce(context.Background())
	if !errors.Is(err, ErrIdentityLookup) {
		t.Fatalf("RunOnce() error = %v", err)
	}
	combined := logs.String() + " " + err.Error()
	for _, forbidden := range []string{"alice@example.test", "top-secret", testIdentityID} {
		if strings.Contains(combined, forbidden) {
			t.Fatalf("recovery diagnostics exposed %q: %s", forbidden, combined)
		}
	}
	if !strings.Contains(logs.String(), testAttemptID.String()) || !strings.Contains(logs.String(), "IDENTITY_LOOKUP_FAILED") {
		t.Fatalf("recovery diagnostics lack safe correlation fields: %s", logs.String())
	}
}

func TestRunnerRejectsInvalidConfigurationAndCandidates(t *testing.T) {
	repository := &recoveryStoreStub{}
	admin := &recoveryAdminStub{}
	for _, options := range []Options{
		{},
		{Issuer: "http://identity.example.test/realms/termlink", SubjectKey: bytes.Repeat([]byte{1}, 32)},
		{Issuer: "https://identity.example.test/realms/termlink", SubjectKey: bytes.Repeat([]byte{1}, 31)},
	} {
		if _, err := New(repository, admin, options); !errors.Is(err, ErrInvalidConfiguration) {
			t.Fatalf("New(%+v) error = %v", options, err)
		}
	}

	invalid := reservedCandidate()
	invalid.ReservedUntil = testNow
	repository.candidates = []store.RegistrationRecoveryCandidate{invalid}
	result, err := newTestRunner(t, repository, admin, nil).RunOnce(context.Background())
	if !errors.Is(err, ErrCandidateInvalid) || result.CandidatesInvalid != 1 || admin.lookupCalls != 0 {
		t.Fatalf("RunOnce() = %+v, %v; lookups=%d", result, err, admin.lookupCalls)
	}
}
