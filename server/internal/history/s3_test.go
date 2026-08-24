package history

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/service/s3"
)

func TestHistoryObjectMetadataIsBounded(t *testing.T) {
	digest := sha256.Sum256([]byte("ciphertext"))
	validKey := "accounts/11111111-1111-4111-8111-111111111111/sessions/22222222-2222-4222-8222-222222222222/chunk/33333333-3333-4333-8333-333333333333.bin"
	if err := validateObject(validKey, 10, digest[:]); err != nil {
		t.Fatalf("valid metadata rejected: %v", err)
	}
	for _, key := range []string{
		"sessions/s/c.bin",
		"accounts/../secret",
		`accounts/a\\secret`,
		"accounts/11111111-1111-4111-8111-111111111111/sessions/22222222-2222-4222-8222-222222222222/chunks/33333333-3333-4333-8333-333333333333.bin",
		"accounts/11111111-1111-4111-8111-111111111111/sessions/22222222-2222-4222-8222-222222222222/chunk/not-a-uuid.bin",
		"accounts/11111111-1111-4111-8111-111111111111/sessions/22222222-2222-4222-8222-222222222222/chunk/33333333-3333-4333-8333-333333333333.bin/extra",
	} {
		if err := validateObject(key, 10, digest[:]); err == nil {
			t.Fatalf("unsafe object key accepted: %q", key)
		}
	}
	if err := validateObject(validKey, MaxObjectBytes+1, digest[:]); err == nil {
		t.Fatal("oversized object accepted")
	}
	transcriptKey := "accounts/11111111-1111-4111-8111-111111111111/sessions/22222222-2222-4222-8222-222222222222/transcript/33333333-3333-4333-8333-333333333333.bin"
	if err := validateObject(transcriptKey, MaxTranscriptObjectBytes, digest[:]); err != nil {
		t.Fatalf("maximum transcript rejected: %v", err)
	}
	if err := validateObject(transcriptKey, MaxTranscriptObjectBytes+1, digest[:]); err == nil {
		t.Fatal("oversized transcript accepted")
	}
	transferKey := "accounts/11111111-1111-4111-8111-111111111111/file-transfers/22222222-2222-4222-8222-222222222222/33333333-3333-4333-8333-333333333333.bin"
	emptyDigest := sha256.Sum256(nil)
	if err := validateObject(transferKey, 0, emptyDigest[:]); err != nil {
		t.Fatalf("zero-byte file transfer rejected: %v", err)
	}
	if err := validateObject(transferKey, MaxFileTransferObjectBytes, digest[:]); err != nil {
		t.Fatalf("maximum file transfer rejected: %v", err)
	}
	if err := validateObject(transferKey, MaxFileTransferObjectBytes+1, digest[:]); err == nil {
		t.Fatal("oversized file transfer accepted")
	}
	if err := validateObject(strings.Replace(transferKey, "/file-transfers/", "/sessions/", 1), 10, digest[:]); err == nil {
		t.Fatal("malformed file transfer key accepted")
	}
}

func TestFileTransferPresignedPutAndHeadAgainstMinIO(t *testing.T) {
	endpoint := os.Getenv("TERMLINK_MINIO_TEST_ENDPOINT")
	accessKey := os.Getenv("TERMLINK_MINIO_TEST_ACCESS_KEY")
	secretKey := os.Getenv("TERMLINK_MINIO_TEST_SECRET_KEY")
	if endpoint == "" || accessKey == "" || secretKey == "" {
		t.Skip("TERMLINK_MINIO_TEST_ENDPOINT and credentials are required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	bucket := "termlink-file-transfer-test-" + strings.ReplaceAll(time.Now().UTC().Format("20060102-150405.000000000"), ".", "-")
	objectStore, err := Open(ctx, endpoint, "", "us-east-1", bucket, accessKey, secretKey)
	if err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = objectStore.client.DeleteBucket(cleanupCtx, &s3.DeleteBucketInput{Bucket: aws.String(bucket)})
	}()
	objectKey := "accounts/11111111-1111-4111-8111-111111111111/file-transfers/22222222-2222-4222-8222-222222222222/33333333-3333-4333-8333-333333333333.bin"
	body := []byte{}
	digest := sha256.Sum256(body)
	presigned, err := objectStore.PresignPut(ctx, objectKey, int64(len(body)), digest[:], time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	request, err := http.NewRequestWithContext(ctx, presigned.Method, presigned.URL, strings.NewReader(string(body)))
	if err != nil {
		t.Fatal(err)
	}
	for name, values := range presigned.Headers {
		for _, value := range values {
			request.Header.Add(name, value)
		}
	}
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	_ = response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		t.Fatalf("presigned MinIO PUT status=%d", response.StatusCode)
	}
	if err := objectStore.VerifyCiphertext(ctx, objectKey, int64(len(body)), digest[:]); err != nil {
		t.Fatalf("MinIO HEAD metadata verification failed: %v", err)
	}
	if err := objectStore.Delete(ctx, objectKey); err != nil {
		t.Fatalf("delete exact test object: %v", err)
	}
}

func TestOpenUsesPublicEndpointOnlyForPresignedRequests(t *testing.T) {
	internalRequests := make(chan struct{}, 1)
	internal := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			internalRequests <- struct{}{}
		}
		w.WriteHeader(http.StatusOK)
	}))
	defer internal.Close()
	public := httptest.NewServer(http.NotFoundHandler())
	defer public.Close()

	store, err := Open(context.Background(), internal.URL, public.URL, "us-east-1", "termlink-history", "access-key", "secret-key")
	if err != nil {
		t.Fatalf("Open() error = %v", err)
	}
	presigned, err := store.PresignGet(context.Background(), "accounts/11111111-1111-4111-8111-111111111111/sessions/22222222-2222-4222-8222-222222222222/chunk/33333333-3333-4333-8333-333333333333.bin", time.Minute)
	if err != nil {
		t.Fatalf("PresignGet() error = %v", err)
	}
	if !strings.HasPrefix(presigned.URL, public.URL+"/termlink-history/accounts/") {
		t.Fatalf("presigned URL = %q, want public endpoint %q", presigned.URL, public.URL)
	}
	select {
	case <-internalRequests:
	case <-time.After(time.Second):
		t.Fatal("Open() did not use internal endpoint for bucket verification")
	}
}

func TestOpenDefaultsEmptyPublicEndpointToInternal(t *testing.T) {
	getRequests := 0
	emptyDigest := sha256.Sum256(nil)
	encodedEmptyDigest := base64.StdEncoding.EncodeToString(emptyDigest[:])
	internal := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Length", "0")
		w.Header().Set("X-Amz-Meta-Termlink-Sha256", encodedEmptyDigest)
		if r.Method == http.MethodGet {
			getRequests++
		}
		w.WriteHeader(http.StatusOK)
	}))
	defer internal.Close()

	store, err := Open(context.Background(), internal.URL, "", "us-east-1", "termlink-history", "access-key", "secret-key")
	if err != nil {
		t.Fatalf("Open() error = %v", err)
	}
	presigned, err := store.PresignGet(context.Background(), "accounts/11111111-1111-4111-8111-111111111111/sessions/22222222-2222-4222-8222-222222222222/chunk/33333333-3333-4333-8333-333333333333.bin", time.Minute)
	if err != nil {
		t.Fatalf("PresignGet() error = %v", err)
	}
	if !strings.HasPrefix(presigned.URL, internal.URL+"/termlink-history/accounts/") {
		t.Fatalf("presigned URL = %q, want internal endpoint %q", presigned.URL, internal.URL)
	}
	transferKey := "accounts/11111111-1111-4111-8111-111111111111/file-transfers/22222222-2222-4222-8222-222222222222/33333333-3333-4333-8333-333333333333.bin"
	put, err := store.PresignPut(context.Background(), transferKey, 0, emptyDigest[:], time.Minute)
	if err != nil {
		t.Fatalf("PresignPut(zero-byte transfer) error = %v", err)
	}
	if put.Method != http.MethodPut || !strings.Contains(put.URL, "X-Amz-Checksum-Sha256=") ||
		!strings.Contains(strings.ToLower(put.URL), "x-amz-meta-termlink-sha256") ||
		len(put.Headers["X-Amz-Meta-Termlink-Sha256"]) != 1 || put.Headers["X-Amz-Meta-Termlink-Sha256"][0] != encodedEmptyDigest {
		t.Fatalf("zero-byte presigned PUT = %+v", put)
	}
	if err := store.VerifyCiphertext(context.Background(), transferKey, 0, emptyDigest[:]); err != nil {
		t.Fatalf("VerifyCiphertext(zero-byte transfer) error = %v", err)
	}
	if getRequests != 0 {
		t.Fatalf("file transfer verification issued %d GET requests, want HEAD metadata only", getRequests)
	}
}

func TestVerifyCiphertextStreamsHistoryButUsesSignedMetadataForFileTransfers(t *testing.T) {
	historyBody := []byte("encrypted history")
	historyDigest := sha256.Sum256(historyBody)
	transferDigest := sha256.Sum256([]byte("download"))
	var historyGets, transferGets int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		isTransfer := strings.Contains(r.URL.Path, "/file-transfers/")
		if r.Method == http.MethodHead {
			if isTransfer {
				w.Header().Set("Content-Length", "8")
				w.Header().Set("X-Amz-Meta-Termlink-Sha256", base64.StdEncoding.EncodeToString(transferDigest[:]))
			} else {
				w.Header().Set("Content-Length", strconv.Itoa(len(historyBody)))
			}
			w.WriteHeader(http.StatusOK)
			return
		}
		if r.Method == http.MethodGet {
			if isTransfer {
				transferGets++
			} else {
				historyGets++
				_, _ = w.Write(historyBody)
			}
			return
		}
		w.WriteHeader(http.StatusOK)
	}))
	defer server.Close()
	objectStore, err := Open(context.Background(), server.URL, "", "us-east-1", "termlink-history", "access-key", "secret-key")
	if err != nil {
		t.Fatal(err)
	}
	historyKey := "accounts/11111111-1111-4111-8111-111111111111/sessions/22222222-2222-4222-8222-222222222222/chunk/33333333-3333-4333-8333-333333333333.bin"
	transferKey := "accounts/11111111-1111-4111-8111-111111111111/file-transfers/22222222-2222-4222-8222-222222222222/33333333-3333-4333-8333-333333333333.bin"
	if err := objectStore.VerifyCiphertext(context.Background(), historyKey, int64(len(historyBody)), historyDigest[:]); err != nil {
		t.Fatalf("history verification failed: %v", err)
	}
	if err := objectStore.VerifyCiphertext(context.Background(), transferKey, 8, transferDigest[:]); err != nil {
		t.Fatalf("file transfer verification failed: %v", err)
	}
	if historyGets != 1 || transferGets != 0 {
		t.Fatalf("GET requests history/transfer=%d/%d, want 1/0", historyGets, transferGets)
	}
	wrongDigest := sha256.Sum256([]byte("wrong"))
	if err := objectStore.VerifyCiphertext(context.Background(), transferKey, 8, wrongDigest[:]); err == nil {
		t.Fatal("file transfer checksum metadata mismatch accepted")
	}
}

func TestFileTransferMetadataVerificationFailsClosedWithoutReadingObject(t *testing.T) {
	digest := sha256.Sum256([]byte("download"))
	transferKey := "accounts/11111111-1111-4111-8111-111111111111/file-transfers/22222222-2222-4222-8222-222222222222/33333333-3333-4333-8333-333333333333.bin"
	for _, test := range []struct {
		name     string
		metadata string
		size     string
	}{
		{name: "missing", size: "8"},
		{name: "invalid base64", metadata: "not-base64!", size: "8"},
		{name: "wrong digest", metadata: base64.StdEncoding.EncodeToString(make([]byte, sha256.Size)), size: "8"},
		{name: "wrong size", metadata: base64.StdEncoding.EncodeToString(digest[:]), size: "7"},
	} {
		t.Run(test.name, func(t *testing.T) {
			gets := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method == http.MethodHead {
					w.Header().Set("Content-Length", test.size)
					if test.metadata != "" {
						w.Header().Set("X-Amz-Meta-Termlink-Sha256", test.metadata)
					}
					w.WriteHeader(http.StatusOK)
					return
				}
				if r.Method == http.MethodGet {
					gets++
				}
				w.WriteHeader(http.StatusOK)
			}))
			defer server.Close()
			objectStore, err := Open(context.Background(), server.URL, "", "us-east-1", "termlink-history", "access-key", "secret-key")
			if err != nil {
				t.Fatal(err)
			}
			if err := objectStore.VerifyCiphertext(context.Background(), transferKey, 8, digest[:]); err == nil {
				t.Fatal("invalid file-transfer HEAD metadata was accepted")
			}
			if gets != 0 {
				t.Fatalf("file-transfer verification issued %d GET requests", gets)
			}
		})
	}
}
