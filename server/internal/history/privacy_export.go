package history

import (
	"bytes"
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/service/s3"
	"github.com/google/uuid"
)

const MaxPrivacyExportBytes int64 = 16 << 20

func PrivacyExportObjectKey(accountID, requestID uuid.UUID, processingFence int64) string {
	return "accounts/" + accountID.String() + "/privacy-exports/" + requestID.String() + "/" + strconv.FormatInt(processingFence, 10) + ".bin"
}

func validPrivacyExportObjectKey(key string) bool {
	parts := strings.Split(key, "/")
	if len(parts) != 5 || parts[0] != "accounts" || parts[2] != "privacy-exports" || !strings.HasSuffix(parts[4], ".bin") {
		return false
	}
	accountID, accountErr := uuid.Parse(parts[1])
	requestID, requestErr := uuid.Parse(parts[3])
	fenceText := strings.TrimSuffix(parts[4], ".bin")
	fence, fenceErr := strconv.ParseInt(fenceText, 10, 64)
	return accountErr == nil && requestErr == nil && fenceErr == nil && fence > 0 &&
		accountID.String() == parts[1] && requestID.String() == parts[3] && strconv.FormatInt(fence, 10) == fenceText
}

// PutPrivacyExport stores only an already-encrypted opaque export artifact.
func (s *Store) PutPrivacyExport(ctx context.Context, key string, ciphertext []byte) ([sha256.Size]byte, error) {
	digest := sha256.Sum256(ciphertext)
	if s == nil || s.client == nil || validatePrivacyExportObject(key, int64(len(ciphertext)), digest[:]) != nil {
		return [sha256.Size]byte{}, errors.New("invalid privacy export object")
	}
	_, err := s.client.PutObject(ctx, &s3.PutObjectInput{
		Bucket:        aws.String(s.bucket),
		Key:           aws.String(key),
		Body:          bytes.NewReader(ciphertext),
		ContentLength: aws.Int64(int64(len(ciphertext))),
		ContentType:   aws.String("application/octet-stream"),
	})
	if err != nil {
		return [sha256.Size]byte{}, fmt.Errorf("store privacy export: %w", err)
	}
	return digest, nil
}

func (s *Store) GetPrivacyExport(ctx context.Context, key string, size int64, expectedDigest []byte) ([]byte, error) {
	if s == nil || s.client == nil || validatePrivacyExportObject(key, size, expectedDigest) != nil {
		return nil, errors.New("invalid privacy export metadata")
	}
	object, err := s.client.GetObject(ctx, &s3.GetObjectInput{Bucket: aws.String(s.bucket), Key: aws.String(key)})
	if err != nil {
		return nil, fmt.Errorf("read privacy export: %w", err)
	}
	defer object.Body.Close()
	body, err := io.ReadAll(io.LimitReader(object.Body, size+1))
	if err != nil {
		return nil, fmt.Errorf("read privacy export: %w", err)
	}
	digest := sha256.Sum256(body)
	if int64(len(body)) != size || !equalDigest(digest[:], expectedDigest) {
		return nil, errors.New("privacy export integrity mismatch")
	}
	return body, nil
}

func validatePrivacyExportObject(key string, size int64, digest []byte) error {
	if !validPrivacyExportObjectKey(key) || size <= 0 || size > MaxPrivacyExportBytes || len(digest) != sha256.Size {
		return errors.New("invalid privacy export metadata")
	}
	return nil
}

func (s *Store) DeletePrivacyExport(ctx context.Context, key string) error {
	if s == nil || s.client == nil || !validPrivacyExportObjectKey(key) {
		return errors.New("invalid privacy export object key")
	}
	_, err := s.client.DeleteObject(ctx, &s3.DeleteObjectInput{Bucket: aws.String(s.bucket), Key: aws.String(key)})
	if err != nil {
		return fmt.Errorf("delete privacy export: %w", err)
	}
	return nil
}
