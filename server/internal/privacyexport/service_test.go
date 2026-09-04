package privacyexport

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

func TestEncryptionBindsAccountAndRequestAAD(t *testing.T) {
	service := testService(t, &fakeRepository{}, newFakeObjects())
	accountID, requestID := uuid.New(), uuid.New()
	plaintext := []byte(`{"schema_version":1}`)
	ciphertext, err := service.encrypt(accountID, requestID, 7, plaintext)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(ciphertext, plaintext) {
		t.Fatal("ciphertext contains plaintext export")
	}
	decoded, err := service.decrypt(accountID, requestID, 7, ciphertext)
	if err != nil || !bytes.Equal(decoded, plaintext) {
		t.Fatalf("decrypt = %q error=%v", decoded, err)
	}
	for _, identity := range [][2]uuid.UUID{{uuid.New(), requestID}, {accountID, uuid.New()}} {
		if _, err := service.decrypt(identity[0], identity[1], 7, ciphertext); !errors.Is(err, ErrExportIntegrity) {
			t.Fatalf("wrong AAD decrypt error = %v", err)
		}
	}
	if _, err := service.decrypt(accountID, requestID, 8, ciphertext); !errors.Is(err, ErrExportIntegrity) {
		t.Fatalf("wrong fence AAD decrypt error = %v", err)
	}
}

func TestProcessDownloadAndExpireLifecycle(t *testing.T) {
	accountID, requestID := uuid.New(), uuid.New()
	repository := &fakeRepository{jobs: []store.UserPortalDataExportJob{{ID: requestID, AccountID: accountID, Scope: []string{"ACCOUNT_PROFILE"}, ProcessingFence: 3}}}
	repository.snapshot = stableSnapshot()
	objects := newFakeObjects()
	service := testService(t, repository, objects)
	if completed, err := service.ProcessOnce(context.Background(), 1); err != nil || completed != 1 {
		t.Fatalf("ProcessOnce completed=%d err=%v", completed, err)
	}
	if repository.artifact.ObjectKey == "" || bytes.Contains(objects.data[repository.artifact.ObjectKey], []byte(`"account"`)) {
		t.Fatal("artifact was not stored as opaque ciphertext")
	}
	download, err := service.Download(context.Background(), accountID, requestID)
	if err != nil || !json.Valid(download) {
		t.Fatalf("Download error=%v body=%q", err, download)
	}
	first := append([]byte(nil), download...)
	second, err := service.Download(context.Background(), accountID, requestID)
	if err != nil || !bytes.Equal(first, second) {
		t.Fatal("stable export JSON changed between downloads")
	}
	if _, err := service.Download(context.Background(), uuid.New(), requestID); !errors.Is(err, ErrExportUnavailable) {
		t.Fatalf("cross-account download error = %v", err)
	}
	repository.expired = []store.UserPortalExportArtifact{repository.artifact}
	if cleaned, err := service.CleanupExpired(context.Background(), 1); err != nil || cleaned != 1 {
		t.Fatalf("cleanup count=%d error=%v", cleaned, err)
	}
	if _, exists := objects.data[repository.artifact.ObjectKey]; exists {
		t.Fatal("expired export object was retained")
	}
}

func TestStaleFenceArtifactIsDeletedAndFailsClosed(t *testing.T) {
	accountID, requestID := uuid.New(), uuid.New()
	repository := &fakeRepository{jobs: []store.UserPortalDataExportJob{{ID: requestID, AccountID: accountID, Scope: []string{"ACCOUNT_PROFILE"}, ProcessingFence: 7}}, completeErr: store.ErrConflict, snapshot: stableSnapshot()}
	objects := newFakeObjects()
	service := testService(t, repository, objects)
	if completed, err := service.ProcessOnce(context.Background(), 1); err != nil || completed != 0 {
		t.Fatalf("stale process completed=%d err=%v", completed, err)
	}
	if len(objects.data) != 0 {
		t.Fatal("stale fenced worker left an export object")
	}
	if repository.failedCode != "EXPORT_FENCE_LOST" {
		t.Fatalf("failure code = %q", repository.failedCode)
	}
}

func TestDownloadRejectsDigestAndCiphertextTampering(t *testing.T) {
	accountID, requestID := uuid.New(), uuid.New()
	repository := &fakeRepository{jobs: []store.UserPortalDataExportJob{{ID: requestID, AccountID: accountID, Scope: []string{"ACCOUNT_PROFILE"}, ProcessingFence: 1}}, snapshot: stableSnapshot()}
	objects := newFakeObjects()
	service := testService(t, repository, objects)
	_, _ = service.ProcessOnce(context.Background(), 1)
	objects.data[repository.artifact.ObjectKey][0] ^= 0xff
	if _, err := service.Download(context.Background(), accountID, requestID); !errors.Is(err, ErrExportIntegrity) {
		t.Fatalf("tampered download error = %v", err)
	}
}

func TestDownloadClassifiesRepositoryFailuresWithoutLeakingDetails(t *testing.T) {
	accountID, requestID := uuid.New(), uuid.New()
	backendDetail := errors.New("postgres dsn password and internal object key")
	tests := []struct {
		name string
		err  error
		want error
	}{
		{name: "denied", err: store.ErrDenied, want: ErrExportUnavailable},
		{name: "wrapped denied", err: errors.Join(errors.New("resource detail"), store.ErrDenied), want: ErrExportUnavailable},
		{name: "not found", err: store.ErrNotFound, want: ErrExportUnavailable},
		{name: "backend", err: backendDetail, want: ErrExportBackendUnavailable},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			objects := newFakeObjects()
			service := testService(t, &fakeRepository{artifactErr: test.err}, objects)
			body, err := service.Download(context.Background(), accountID, requestID)
			if body != nil || !errors.Is(err, test.want) {
				t.Fatalf("Download body/error = %q/%v, want nil/%v", body, err, test.want)
			}
			if err == test.err || strings.Contains(err.Error(), "postgres") || strings.Contains(err.Error(), "object key") ||
				strings.Contains(err.Error(), "resource detail") {
				t.Fatalf("classified error leaked repository detail: %v", err)
			}
			if objects.getCalls != 0 {
				t.Fatalf("repository rejection touched object storage %d times", objects.getCalls)
			}
		})
	}
}

func stableSnapshot() store.UserPortalExportSnapshot {
	return store.UserPortalExportSnapshot{
		SchemaVersion: 1, Account: json.RawMessage(`{"id":"account"}`), Devices: json.RawMessage(`[]`),
		PCs: json.RawMessage(`[]`), Sessions: json.RawMessage(`[]`), Security: json.RawMessage(`[]`),
	}
}

func testService(t *testing.T, repository Repository, objects ObjectStore) *Service {
	t.Helper()
	service, err := New(repository, objects, uuid.New(), bytes.Repeat([]byte{0x42}, 32))
	if err != nil {
		t.Fatal(err)
	}
	return service
}

type fakeRepository struct {
	mu          sync.Mutex
	jobs        []store.UserPortalDataExportJob
	snapshot    store.UserPortalExportSnapshot
	artifact    store.UserPortalExportArtifact
	expired     []store.UserPortalExportArtifact
	completeErr error
	artifactErr error
	failedCode  string
}

func (r *fakeRepository) ClaimUserPortalDataExports(context.Context, uuid.UUID, time.Duration, int) ([]store.UserPortalDataExportJob, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	jobs := append([]store.UserPortalDataExportJob(nil), r.jobs...)
	r.jobs = nil
	return jobs, nil
}
func (r *fakeRepository) UserPortalDataExportSnapshot(context.Context, uuid.UUID, []string) (store.UserPortalExportSnapshot, error) {
	return r.snapshot, nil
}
func (r *fakeRepository) CompleteUserPortalDataExport(_ context.Context, _ uuid.UUID, requestID uuid.UUID, _ int64, completion store.UserPortalDataExportCompletion, _ string) error {
	if r.completeErr != nil {
		return r.completeErr
	}
	parts := strings.Split(completion.ArtifactObjectKey, "/")
	accountID, _ := uuid.Parse(parts[1])
	var digest [32]byte
	copy(digest[:], completion.ArtifactSHA256)
	segments := strings.Split(strings.TrimSuffix(completion.ArtifactObjectKey, ".bin"), "/")
	fence, _ := strconv.ParseInt(segments[len(segments)-1], 10, 64)
	r.artifact = store.UserPortalExportArtifact{ID: requestID, AccountID: accountID, ProcessingFence: fence, ObjectKey: completion.ArtifactObjectKey, SHA256: digest, SizeBytes: completion.ArtifactSizeBytes, ExpiresAt: completion.ExpiresAt}
	return nil
}
func (r *fakeRepository) FailUserPortalDataExport(_ context.Context, _ uuid.UUID, _ uuid.UUID, _ int64, code, _ string) error {
	r.failedCode = code
	return nil
}
func (r *fakeRepository) UserPortalDataExportArtifact(_ context.Context, accountID, requestID uuid.UUID) (store.UserPortalExportArtifact, error) {
	if r.artifactErr != nil {
		return store.UserPortalExportArtifact{}, r.artifactErr
	}
	if r.artifact.AccountID != accountID || r.artifact.ID != requestID {
		return store.UserPortalExportArtifact{}, store.ErrDenied
	}
	return r.artifact, nil
}
func (r *fakeRepository) ExpiredUserPortalDataExportArtifacts(context.Context, int) ([]store.UserPortalExportArtifact, error) {
	return append([]store.UserPortalExportArtifact(nil), r.expired...), nil
}
func (r *fakeRepository) MarkUserPortalDataExportExpired(context.Context, store.UserPortalExportArtifact) error {
	return nil
}

type fakeObjects struct {
	mu       sync.Mutex
	data     map[string][]byte
	getCalls int
}

func newFakeObjects() *fakeObjects { return &fakeObjects{data: map[string][]byte{}} }
func (s *fakeObjects) PutPrivacyExport(_ context.Context, key string, body []byte) ([32]byte, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.data[key] = append([]byte(nil), body...)
	return sha256.Sum256(body), nil
}
func (s *fakeObjects) GetPrivacyExport(_ context.Context, key string, size int64, digest []byte) ([]byte, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.getCalls++
	body, ok := s.data[key]
	actual := sha256.Sum256(body)
	if !ok || int64(len(body)) != size || !bytes.Equal(actual[:], digest) {
		return nil, errors.New("integrity")
	}
	return append([]byte(nil), body...), nil
}
func (s *fakeObjects) DeletePrivacyExport(_ context.Context, key string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.data, key)
	return nil
}
