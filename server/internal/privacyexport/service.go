// Package privacyexport builds account-scoped metadata exports without ever
// reading terminal content or provider secrets.
package privacyexport

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"strconv"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/history"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	artifactVersion = "TLPE1"
	DefaultLease    = 2 * time.Minute
	DefaultTTL      = 7 * 24 * time.Hour
)

var (
	ErrInvalidConfiguration     = errors.New("privacy export configuration invalid")
	ErrExportUnavailable        = errors.New("privacy export unavailable")
	ErrExportBackendUnavailable = errors.New("privacy export backend unavailable")
	ErrExportIntegrity          = errors.New("privacy export integrity check failed")
)

type Repository interface {
	ClaimUserPortalDataExports(context.Context, uuid.UUID, time.Duration, int) ([]store.UserPortalDataExportJob, error)
	UserPortalDataExportSnapshot(context.Context, uuid.UUID, []string) (store.UserPortalExportSnapshot, error)
	CompleteUserPortalDataExport(context.Context, uuid.UUID, uuid.UUID, int64, store.UserPortalDataExportCompletion, string) error
	FailUserPortalDataExport(context.Context, uuid.UUID, uuid.UUID, int64, string, string) error
	UserPortalDataExportArtifact(context.Context, uuid.UUID, uuid.UUID) (store.UserPortalExportArtifact, error)
	ExpiredUserPortalDataExportArtifacts(context.Context, int) ([]store.UserPortalExportArtifact, error)
	MarkUserPortalDataExportExpired(context.Context, store.UserPortalExportArtifact) error
}

type ObjectStore interface {
	PutPrivacyExport(context.Context, string, []byte) ([sha256.Size]byte, error)
	GetPrivacyExport(context.Context, string, int64, []byte) ([]byte, error)
	DeletePrivacyExport(context.Context, string) error
}

type Service struct {
	repository  Repository
	objects     ObjectStore
	processorID uuid.UUID
	aead        cipher.AEAD
	lease       time.Duration
	ttl         time.Duration
}

func New(repository Repository, objects ObjectStore, processorID uuid.UUID, encryptionKey []byte) (*Service, error) {
	if repository == nil || objects == nil || processorID == uuid.Nil || len(encryptionKey) != 32 {
		return nil, ErrInvalidConfiguration
	}
	block, err := aes.NewCipher(append([]byte(nil), encryptionKey...))
	if err != nil {
		return nil, ErrInvalidConfiguration
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, ErrInvalidConfiguration
	}
	return &Service{repository: repository, objects: objects, processorID: processorID, aead: aead, lease: DefaultLease, ttl: DefaultTTL}, nil
}

// ProcessOnce claims and completes a bounded group. Individual failures are
// classified in the request row; raw database, object-store, and crypto errors
// are never returned to a user-facing caller.
func (s *Service) ProcessOnce(ctx context.Context, limit int) (int, error) {
	jobs, err := s.repository.ClaimUserPortalDataExports(ctx, s.processorID, s.lease, limit)
	if err != nil {
		return 0, ErrExportUnavailable
	}
	completed := 0
	for _, job := range jobs {
		if err := s.process(ctx, job); err != nil {
			_ = s.repository.FailUserPortalDataExport(ctx, s.processorID, job.ID, job.ProcessingFence, classify(err), processorTrace(job.ID))
			continue
		}
		completed++
	}
	return completed, nil
}

func (s *Service) process(ctx context.Context, job store.UserPortalDataExportJob) error {
	snapshot, err := s.repository.UserPortalDataExportSnapshot(ctx, job.AccountID, job.Scope)
	if err != nil {
		return errors.New("snapshot")
	}
	plaintext, err := json.Marshal(snapshot)
	if err != nil || len(plaintext) == 0 {
		return errors.New("encode")
	}
	ciphertext, err := s.encrypt(job.AccountID, job.ID, job.ProcessingFence, plaintext)
	if err != nil || int64(len(ciphertext)) > history.MaxPrivacyExportBytes {
		return errors.New("size")
	}
	key := history.PrivacyExportObjectKey(job.AccountID, job.ID, job.ProcessingFence)
	digest, err := s.objects.PutPrivacyExport(ctx, key, ciphertext)
	if err != nil {
		return errors.New("object")
	}
	completion := store.UserPortalDataExportCompletion{
		ArtifactObjectKey: key, ArtifactSHA256: digest[:], ArtifactSizeBytes: int64(len(ciphertext)),
		ExpiresAt: time.Now().UTC().Add(s.ttl),
	}
	if err := s.repository.CompleteUserPortalDataExport(ctx, s.processorID, job.ID, job.ProcessingFence, completion, processorTrace(job.ID)); err != nil {
		_ = s.objects.DeletePrivacyExport(ctx, key)
		return errors.New("fence")
	}
	return nil
}

func processorTrace(requestID uuid.UUID) string {
	return "privacy-export-processor:" + requestID.String()
}

func classify(err error) string {
	switch err.Error() {
	case "snapshot":
		return "EXPORT_SNAPSHOT_FAILED"
	case "encode":
		return "EXPORT_ENCODING_FAILED"
	case "size":
		return "EXPORT_TOO_LARGE"
	case "object":
		return "EXPORT_STORAGE_FAILED"
	case "fence":
		return "EXPORT_FENCE_LOST"
	default:
		return "EXPORT_FAILED"
	}
}

// Download verifies account scope in PostgreSQL before touching object
// storage, then verifies SHA-256 and AES-GCM AAD before returning stable JSON.
// The internal object key is never returned.
func (s *Service) Download(ctx context.Context, accountID, requestID uuid.UUID) ([]byte, error) {
	artifact, err := s.repository.UserPortalDataExportArtifact(ctx, accountID, requestID)
	if err != nil {
		if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) {
			return nil, ErrExportUnavailable
		}
		// Never wrap the repository error: it can carry SQL, connection, or
		// storage metadata that must not escape into an API response or log.
		return nil, ErrExportBackendUnavailable
	}
	ciphertext, err := s.objects.GetPrivacyExport(ctx, artifact.ObjectKey, artifact.SizeBytes, artifact.SHA256[:])
	if err != nil {
		return nil, ErrExportIntegrity
	}
	plaintext, err := s.decrypt(accountID, requestID, artifact.ProcessingFence, ciphertext)
	if err != nil || !json.Valid(plaintext) {
		return nil, ErrExportIntegrity
	}
	return plaintext, nil
}

func (s *Service) CleanupExpired(ctx context.Context, limit int) (int, error) {
	items, err := s.repository.ExpiredUserPortalDataExportArtifacts(ctx, limit)
	if err != nil {
		return 0, ErrExportUnavailable
	}
	cleaned := 0
	for _, item := range items {
		if err := s.objects.DeletePrivacyExport(ctx, item.ObjectKey); err != nil {
			continue
		}
		if err := s.repository.MarkUserPortalDataExportExpired(ctx, item); err != nil {
			continue
		}
		cleaned++
	}
	return cleaned, nil
}

func (s *Service) encrypt(accountID, requestID uuid.UUID, processingFence int64, plaintext []byte) ([]byte, error) {
	nonce := make([]byte, s.aead.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	result := make([]byte, 0, len(artifactVersion)+len(nonce)+len(plaintext)+s.aead.Overhead())
	result = append(result, artifactVersion...)
	result = append(result, nonce...)
	result = s.aead.Seal(result, nonce, plaintext, exportAAD(accountID, requestID, processingFence))
	return result, nil
}

func (s *Service) decrypt(accountID, requestID uuid.UUID, processingFence int64, ciphertext []byte) ([]byte, error) {
	header := len(artifactVersion) + s.aead.NonceSize()
	if len(ciphertext) < header+s.aead.Overhead() || string(ciphertext[:len(artifactVersion)]) != artifactVersion {
		return nil, ErrExportIntegrity
	}
	nonce := ciphertext[len(artifactVersion):header]
	plaintext, err := s.aead.Open(nil, nonce, ciphertext[header:], exportAAD(accountID, requestID, processingFence))
	if err != nil {
		return nil, ErrExportIntegrity
	}
	return plaintext, nil
}

func exportAAD(accountID, requestID uuid.UUID, processingFence int64) []byte {
	return []byte("termlink/privacy-export/v1|" + accountID.String() + "|" + requestID.String() + "|" + strconv.FormatInt(processingFence, 10))
}
