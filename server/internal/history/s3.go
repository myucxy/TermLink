// Package history stores only client-encrypted terminal history objects.
package history

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/aws/aws-sdk-go-v2/aws"
	awsconfig "github.com/aws/aws-sdk-go-v2/config"
	"github.com/aws/aws-sdk-go-v2/credentials"
	"github.com/aws/aws-sdk-go-v2/service/s3"
	"github.com/aws/aws-sdk-go-v2/service/s3/types"
	"github.com/google/uuid"
)

const (
	MaxObjectBytes           int64 = 64 << 20
	MaxTranscriptObjectBytes int64 = 512 << 10
	// MaxFileTransferObjectBytes is Amazon S3's exact single-PUT ceiling. The
	// broker does not implement multipart upload, so it must never advertise a
	// larger transfer. History limits remain independently much smaller.
	MaxFileTransferObjectBytes int64 = 5 << 30
	fileTransferSHA256Metadata       = "termlink-sha256"
)

type Store struct {
	client  *s3.Client
	presign *s3.PresignClient
	bucket  string
}

type PresignedRequest struct {
	URL       string              `json:"url"`
	Method    string              `json:"method"`
	Headers   map[string][]string `json:"headers,omitempty"`
	ExpiresAt time.Time           `json:"expires_at"`
}

func Open(ctx context.Context, endpoint, publicEndpoint, region, bucket, accessKey, secretKey string) (*Store, error) {
	endpoint = strings.TrimRight(strings.TrimSpace(endpoint), "/")
	publicEndpoint = strings.TrimRight(strings.TrimSpace(publicEndpoint), "/")
	if publicEndpoint == "" {
		publicEndpoint = endpoint
	}
	if endpoint == "" || publicEndpoint == "" || region == "" || bucket == "" || accessKey == "" || secretKey == "" {
		return nil, errors.New("history object store configuration is incomplete")
	}
	cfg, err := awsconfig.LoadDefaultConfig(ctx,
		awsconfig.WithRegion(region),
		awsconfig.WithCredentialsProvider(credentials.NewStaticCredentialsProvider(accessKey, secretKey, "")),
	)
	if err != nil {
		return nil, fmt.Errorf("load S3 configuration: %w", err)
	}
	client := newClient(cfg, endpoint)
	presignClient := newClient(cfg, publicEndpoint)
	store := &Store{client: client, presign: s3.NewPresignClient(presignClient), bucket: bucket}
	if err := store.ensureBucket(ctx); err != nil {
		return nil, err
	}
	return store, nil
}

func newClient(cfg aws.Config, endpoint string) *s3.Client {
	return s3.NewFromConfig(cfg, func(options *s3.Options) {
		options.BaseEndpoint = aws.String(endpoint)
		options.UsePathStyle = true
	})
}

func (s *Store) ensureBucket(ctx context.Context) error {
	if _, err := s.client.HeadBucket(ctx, &s3.HeadBucketInput{Bucket: aws.String(s.bucket)}); err == nil {
		return nil
	}
	if _, err := s.client.CreateBucket(ctx, &s3.CreateBucketInput{Bucket: aws.String(s.bucket)}); err != nil {
		return fmt.Errorf("create history bucket: %w", err)
	}
	if _, err := s.client.HeadBucket(ctx, &s3.HeadBucketInput{Bucket: aws.String(s.bucket)}); err != nil {
		return fmt.Errorf("verify history bucket: %w", err)
	}
	return nil
}

func (s *Store) PresignPut(ctx context.Context, objectKey string, size int64, digest []byte, lifetime time.Duration) (PresignedRequest, error) {
	if err := validateObject(objectKey, size, digest); err != nil {
		return PresignedRequest{}, err
	}
	encodedDigest := base64.StdEncoding.EncodeToString(digest)
	input := &s3.PutObjectInput{
		Bucket:         aws.String(s.bucket),
		Key:            aws.String(objectKey),
		ContentLength:  aws.Int64(size),
		ChecksumSHA256: aws.String(encodedDigest),
		ContentType:    aws.String("application/octet-stream"),
	}
	if objectKeyKind(objectKey) == "file-transfer" {
		// S3-compatible backends differ in whether HeadObject exposes the
		// checksum family. Bind the expected digest into signed user metadata as
		// the portable completion proof. The client cannot omit or alter this
		// header without invalidating SigV4.
		input.Metadata = map[string]string{fileTransferSHA256Metadata: encodedDigest}
	}
	result, err := s.presign.PresignPutObject(ctx, input, s3.WithPresignExpires(lifetime))
	if err != nil {
		return PresignedRequest{}, fmt.Errorf("presign history upload: %w", err)
	}
	return PresignedRequest{URL: result.URL, Method: result.Method, Headers: cloneHeaders(result.SignedHeader), ExpiresAt: time.Now().UTC().Add(lifetime)}, nil
}

func (s *Store) PresignGet(ctx context.Context, objectKey string, lifetime time.Duration) (PresignedRequest, error) {
	if !validObjectKey(objectKey) {
		return PresignedRequest{}, errors.New("invalid history object key")
	}
	result, err := s.presign.PresignGetObject(ctx, &s3.GetObjectInput{Bucket: aws.String(s.bucket), Key: aws.String(objectKey)}, s3.WithPresignExpires(lifetime))
	if err != nil {
		return PresignedRequest{}, fmt.Errorf("presign history download: %w", err)
	}
	return PresignedRequest{URL: result.URL, Method: result.Method, Headers: cloneHeaders(result.SignedHeader), ExpiresAt: time.Now().UTC().Add(lifetime)}, nil
}

// VerifyCiphertext streams bounded history objects, but verifies potentially
// 5 GiB file transfers using signed immutable object metadata plus HEAD length.
// The server never decrypts or interprets the bytes.
func (s *Store) VerifyCiphertext(ctx context.Context, objectKey string, size int64, expectedDigest []byte) error {
	if err := validateObject(objectKey, size, expectedDigest); err != nil {
		return err
	}
	fileTransfer := objectKeyKind(objectKey) == "file-transfer"
	headInput := &s3.HeadObjectInput{Bucket: aws.String(s.bucket), Key: aws.String(objectKey)}
	if fileTransfer {
		headInput.ChecksumMode = types.ChecksumModeEnabled
	}
	head, err := s.client.HeadObject(ctx, headInput)
	if err != nil {
		return fmt.Errorf("stat history ciphertext: %w", err)
	}
	if head.ContentLength == nil || *head.ContentLength != size {
		return errors.New("history ciphertext size mismatch")
	}
	if fileTransfer {
		encodedDigest, present := head.Metadata[fileTransferSHA256Metadata]
		if !present || encodedDigest == "" {
			return errors.New("file transfer checksum metadata is missing")
		}
		actualDigest, decodeErr := base64.StdEncoding.Strict().DecodeString(encodedDigest)
		if decodeErr != nil || !equalDigest(actualDigest, expectedDigest) {
			return errors.New("file transfer checksum metadata mismatch")
		}
		return nil
	}
	object, err := s.client.GetObject(ctx, &s3.GetObjectInput{Bucket: aws.String(s.bucket), Key: aws.String(objectKey)})
	if err != nil {
		return fmt.Errorf("read history ciphertext: %w", err)
	}
	defer object.Body.Close()
	hash := sha256.New()
	written, err := io.Copy(hash, io.LimitReader(object.Body, size+1))
	if err != nil {
		return fmt.Errorf("hash history ciphertext: %w", err)
	}
	if written != size || !equalDigest(hash.Sum(nil), expectedDigest) {
		return errors.New("history ciphertext integrity mismatch")
	}
	return nil
}

func (s *Store) Delete(ctx context.Context, objectKey string) error {
	if !validObjectKey(objectKey) {
		return errors.New("invalid history object key")
	}
	_, err := s.client.DeleteObject(ctx, &s3.DeleteObjectInput{Bucket: aws.String(s.bucket), Key: aws.String(objectKey)})
	if err != nil {
		return fmt.Errorf("delete history ciphertext: %w", err)
	}
	return nil
}

func validateObject(key string, size int64, digest []byte) error {
	maxSize := MaxObjectBytes
	minSize := int64(1)
	switch objectKeyKind(key) {
	case "transcript":
		maxSize = MaxTranscriptObjectBytes
	case "file-transfer":
		minSize = 0
		maxSize = MaxFileTransferObjectBytes
	}
	if !validObjectKey(key) || size < minSize || size > maxSize || len(digest) != sha256.Size {
		return errors.New("invalid history object metadata")
	}
	return nil
}

func objectKeyKind(value string) string {
	parts := strings.Split(value, "/")
	if len(parts) == 5 && parts[0] == "accounts" && parts[2] == "file-transfers" {
		return "file-transfer"
	}
	if len(parts) != 6 {
		return ""
	}
	return parts[4]
}

func validObjectKey(value string) bool {
	parts := strings.Split(value, "/")
	if len(parts) == 5 && parts[0] == "accounts" && parts[2] == "file-transfers" && strings.HasSuffix(parts[4], ".bin") {
		accountID, accountErr := uuid.Parse(parts[1])
		pcID, pcErr := uuid.Parse(parts[3])
		operationText := strings.TrimSuffix(parts[4], ".bin")
		operationID, operationErr := uuid.Parse(operationText)
		return accountErr == nil && accountID.String() == parts[1] &&
			pcErr == nil && pcID.String() == parts[3] &&
			operationErr == nil && operationID.String() == operationText
	}
	if len(parts) != 6 || parts[0] != "accounts" || parts[2] != "sessions" ||
		(parts[4] != "chunk" && parts[4] != "snapshot" && parts[4] != "transcript") || !strings.HasSuffix(parts[5], ".bin") {
		return false
	}
	accountID, err := uuid.Parse(parts[1])
	if err != nil || accountID.String() != parts[1] {
		return false
	}
	sessionID, err := uuid.Parse(parts[3])
	if err != nil || sessionID.String() != parts[3] {
		return false
	}
	uploadText := strings.TrimSuffix(parts[5], ".bin")
	uploadID, err := uuid.Parse(uploadText)
	return err == nil && uploadID.String() == uploadText
}

func equalDigest(left, right []byte) bool {
	if len(left) != len(right) {
		return false
	}
	var difference byte
	for i := range left {
		difference |= left[i] ^ right[i]
	}
	return difference == 0
}

func cloneHeaders(headers http.Header) map[string][]string {
	result := make(map[string][]string, len(headers))
	for name, values := range headers {
		result[name] = append([]string(nil), values...)
	}
	return result
}
