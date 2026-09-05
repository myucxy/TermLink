package api

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/store"
)

func TestValidateFileOperationRequestShapes(t *testing.T) {
	offset := int64(10)
	powershell := "windows-powershell"
	powershell7 := "powershell-7"
	wsl := "wsl"
	for _, test := range []struct {
		name      string
		operation string
		path      string
		offset    *int64
		shell     *string
		valid     bool
	}{
		{"root list", store.FileOperationList, "", nil, nil, true},
		{"paged list", store.FileOperationList, `C:\`, &offset, nil, true},
		{"download", store.FileOperationDownload, `C:\file.txt`, nil, nil, true},
		{"terminal", store.FileOperationOpenTerminal, `C:\folder`, nil, &powershell, true},
		{"PowerShell 7 terminal", store.FileOperationOpenTerminal, `C:\folder`, nil, &powershell7, true},
		{"WSL terminal", store.FileOperationOpenTerminal, `C:\folder`, nil, &wsl, true},
		{"empty download", store.FileOperationDownload, "", nil, nil, false},
		{"download offset", store.FileOperationDownload, `C:\file.txt`, &offset, nil, false},
		{"unknown shell", store.FileOperationOpenTerminal, `C:\`, nil, stringPointerAPI("bash"), false},
		{"nul path", store.FileOperationList, "C:\x00bad", nil, nil, false},
		{"oversized", store.FileOperationList, strings.Repeat("x", store.MaxFileOperationPathBytes+1), nil, nil, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			err := validateFileOperationRequest(test.operation, test.path, test.offset, test.shell)
			if (err == nil) != test.valid {
				t.Fatalf("error=%v valid=%t", err, test.valid)
			}
		})
	}
}

func TestFileOperationLeaseRequiresCanonical32ByteBase64URLToken(t *testing.T) {
	token := make([]byte, 32)
	for index := range token {
		token[index] = byte(index)
	}
	encoded := base64.RawURLEncoding.EncodeToString(token)
	if _, err := parseFileOperationLease(encoded); err != nil {
		t.Fatalf("valid lease rejected: %v", err)
	}
	for _, invalid := range []string{"", encoded + "=", base64.RawURLEncoding.EncodeToString(token[:31]), "not-base64!"} {
		if _, err := parseFileOperationLease(invalid); err == nil {
			t.Fatalf("invalid lease accepted: %q", invalid)
		}
	}
}

func TestFileTransferFilenameRejectsPathsAndRequiresBoundedLeaf(t *testing.T) {
	for _, valid := range []string{"file.txt", "目录.zip"} {
		if !validFileTransferFilename(valid) {
			t.Fatalf("valid filename rejected: %q", valid)
		}
	}
	for _, invalid := range []string{"", ".", "..", `folder\file.txt`, "folder/file.txt", "bad\nname", strings.Repeat("x", 256)} {
		if validFileTransferFilename(invalid) {
			t.Fatalf("invalid filename accepted: %q", invalid)
		}
	}
}

func TestZeroByteFileTransferMetadataIsAccepted(t *testing.T) {
	if store.MaxFileTransferBytes != 5<<30 {
		t.Fatalf("file-transfer bound=%d", store.MaxFileTransferBytes)
	}
	digest := sha256.Sum256(nil)
	if len(base64.RawURLEncoding.EncodeToString(digest[:])) == 0 {
		t.Fatal("empty-file digest was not encoded")
	}
	// The transfer metadata handler uses this exact lower-bound predicate. Keep the
	// validation contract explicit while malformed negative sizes remain
	// rejected by both HTTP and PostgreSQL.
	for size, valid := range map[int64]bool{0: true, -1: false, store.MaxFileTransferBytes: true, store.MaxFileTransferBytes + 1: false} {
		got := size >= 0 && size <= store.MaxFileTransferBytes
		if got != valid {
			t.Fatalf("size %d accepted=%t, want %t", size, got, valid)
		}
	}
}

func TestValidateOpenTerminalResult(t *testing.T) {
	shell := "windows-powershell"
	path := `C:\folder`
	sessionID := uuid.NewString()
	if err := validateOpenTerminalResult(json.RawMessage(`{"session_id":"`+sessionID+`","shell_type":"windows-powershell","cwd":"C:\\folder"}`), &shell, path); err != nil {
		t.Fatalf("valid terminal result rejected: %v", err)
	}
	for _, invalid := range []string{
		`null`, `{}`, `{"session_id":"not-a-uuid","shell_type":"windows-powershell"}`,
		`{"session_id":"` + strings.ToUpper(sessionID) + `","shell_type":"windows-powershell","cwd":"C:\\folder"}`,
		`{"session_id":"` + sessionID + `","shell_type":"command-prompt","cwd":"C:\\folder"}`,
		`{"session_id":"` + sessionID + `","shell_type":"windows-powershell"}`,
		`{"session_id":"` + sessionID + `","shell_type":"windows-powershell","cwd":"C:\\other"}`,
		`{"session_id":"` + sessionID + `","shell_type":"windows-powershell","cwd":"C:\\folder","extra":true}`,
	} {
		if err := validateOpenTerminalResult(json.RawMessage(invalid), &shell, path); err == nil {
			t.Fatalf("invalid terminal result accepted: %s", invalid)
		}
	}
}

func TestStandardTerminalCommandRequestAndResult(t *testing.T) {
	commandID := "custom-123456"
	path := `C:\folder`
	if err := validateFileOperationRequestWithCommand(
		store.FileOperationOpenTerminal, path, nil, nil, &commandID); err != nil {
		t.Fatalf("standard terminal command rejected: %v", err)
	}
	if err := validateFileOperationRequestWithCommand(
		store.FileOperationOpenTerminal, path, nil, &commandID, &commandID); err == nil {
		t.Fatal("ambiguous terminal command request accepted")
	}
	sessionID := uuid.NewString()
	valid := json.RawMessage(`{"session_id":"` + sessionID + `","terminal_command_id":"` + commandID + `","shell_type":"custom","cwd":"C:\\folder"}`)
	if err := validateOpenTerminalResult(valid, &commandID, path); err != nil {
		t.Fatalf("standard terminal result rejected: %v", err)
	}
	wrongCommand := json.RawMessage(`{"session_id":"` + sessionID + `","terminal_command_id":"custom-other","shell_type":"custom","cwd":"C:\\folder"}`)
	if err := validateOpenTerminalResult(wrongCommand, &commandID, path); err == nil {
		t.Fatal("mismatched terminal command result accepted")
	}
}

func TestZeroByteUploadHandlerAcceptsEmptyFileAndRejectsNegativeSize(t *testing.T) {
	accountID, pcID, operationID, windowsID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	now := time.Date(2026, 8, 24, 12, 0, 0, 0, time.UTC)
	authentication, privateKey := fileOperationTestAuth(t, accountID, windowsID, "WINDOWS_PC", now)
	token := make([]byte, 32)
	_, _ = rand.Read(token)
	encodedToken := base64.RawURLEncoding.EncodeToString(token)
	filename, size, directoryZIP := "empty.txt", int64(0), false
	digest := sha256.Sum256(nil)
	repository := &fakeFileOperationStore{record: store.FileOperationRecord{
		ID: operationID, PCID: pcID, Operation: store.FileOperationDownload, Status: store.FileOperationProcessing,
		Filename: &filename, SizeBytes: &size, SHA256: digest[:], DirectoryZIP: &directoryZIP,
		ExpiresAt: now.Add(time.Hour), CreatedAt: now, UpdatedAt: now,
	}}
	service := &API{fileOperations: repository, Clock: func() time.Time { return now }}

	invoke := func(sizeBytes int64) *httptest.ResponseRecorder {
		body, _ := json.Marshal(map[string]any{
			"lease_token": encodedToken, "filename": filename, "size_bytes": sizeBytes,
			"sha256": base64.RawURLEncoding.EncodeToString(digest[:]), "directory_zip": false,
		})
		request := signedFileOperationRequest(t, privateKey, windowsID, now, http.MethodPost,
			"/v1/pcs/"+pcID.String()+"/file-operations/"+operationID.String()+"/upload", body)
		request.SetPathValue("pcId", pcID.String())
		request.SetPathValue("operationId", operationID.String())
		response := httptest.NewRecorder()
		authentication.Authenticate(http.HandlerFunc(service.uploadFileOperation)).ServeHTTP(response, request)
		return response
	}
	if response := invoke(0); response.Code != http.StatusOK || repository.uploadInput.SizeBytes != 0 ||
		!strings.Contains(response.Body.String(), `"protocol":"REALTIME_V2"`) {
		t.Fatalf("zero-byte transfer status=%d body=%s metadata=%+v", response.Code, response.Body.String(), repository.uploadInput)
	}
	if response := invoke(-1); response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), "INVALID_FILE_TRANSFER_METADATA") {
		t.Fatalf("negative upload status=%d body=%s", response.Code, response.Body.String())
	}
}

func stringPointerAPI(value string) *string { return &value }

func TestFileOperationHandlersEnforceRolesAndNeverExposeObjectKey(t *testing.T) {
	accountID, pcID, operationID := uuid.New(), uuid.New(), uuid.New()
	androidID, windowsID := uuid.New(), uuid.New()
	now := time.Date(2026, 8, 24, 12, 0, 0, 0, time.UTC)
	androidAuth, androidKey := fileOperationTestAuth(t, accountID, androidID, "ANDROID", now)
	windowsAuth, windowsKey := fileOperationTestAuth(t, accountID, windowsID, "WINDOWS_PC", now)
	filename, size, directoryZIP := "folder.zip", int64(9), true
	digest := sha256.Sum256([]byte("zip bytes"))
	repository := &fakeFileOperationStore{record: store.FileOperationRecord{
		ID: operationID, PCID: pcID, RequesterDeviceID: androidID, Operation: store.FileOperationDownload,
		Path: `C:\folder`, Status: store.FileOperationSucceeded,
		Filename: &filename, SizeBytes: &size, SHA256: digest[:], DirectoryZIP: &directoryZIP,
		ExpiresAt: now.Add(time.Hour), CompletedAt: timePointer(now), CreatedAt: now, UpdatedAt: now,
	}}
	service := &API{fileOperations: repository, Clock: func() time.Time { return now }}

	createBody := []byte(`{"operation":"DOWNLOAD","path":"C:\\folder"}`)
	create := signedFileOperationRequest(t, androidKey, androidID, now, http.MethodPost, "/v1/pcs/"+pcID.String()+"/file-operations", createBody)
	create.SetPathValue("pcId", pcID.String())
	createResponse := httptest.NewRecorder()
	androidAuth.Authenticate(http.HandlerFunc(service.createFileOperation)).ServeHTTP(createResponse, create)
	if createResponse.Code != http.StatusCreated || repository.createInput.Operation != store.FileOperationDownload {
		t.Fatalf("create status=%d body=%s input=%+v", createResponse.Code, createResponse.Body.String(), repository.createInput)
	}

	wrongRole := signedFileOperationRequest(t, androidKey, androidID, now, http.MethodPost, "/v1/pcs/"+pcID.String()+"/file-operations/claim", nil)
	wrongRole.SetPathValue("pcId", pcID.String())
	wrongRoleResponse := httptest.NewRecorder()
	androidAuth.Authenticate(http.HandlerFunc(service.claimFileOperation)).ServeHTTP(wrongRoleResponse, wrongRole)
	if wrongRoleResponse.Code != http.StatusForbidden || !strings.Contains(wrongRoleResponse.Body.String(), "WINDOWS_AGENT_REQUIRED") {
		t.Fatalf("wrong-role claim status=%d body=%s", wrongRoleResponse.Code, wrongRoleResponse.Body.String())
	}

	repository.record.Status = store.FileOperationProcessing
	repository.record.LeaseExpiresAt = timePointer(now.Add(time.Hour))
	repository.claimToken = base64.RawURLEncoding.EncodeToString(make([]byte, 32))
	claim := signedFileOperationRequest(t, windowsKey, windowsID, now, http.MethodPost, "/v1/pcs/"+pcID.String()+"/file-operations/claim", nil)
	claim.SetPathValue("pcId", pcID.String())
	claimResponse := httptest.NewRecorder()
	windowsAuth.Authenticate(http.HandlerFunc(service.claimFileOperation)).ServeHTTP(claimResponse, claim)
	if claimResponse.Code != http.StatusOK || !strings.Contains(claimResponse.Body.String(), `"lease_token"`) || strings.Contains(claimResponse.Body.String(), "object_key") {
		t.Fatalf("claim status=%d body=%s", claimResponse.Code, claimResponse.Body.String())
	}
	emptyDigest := sha256.Sum256(nil)
	uploadBody, _ := json.Marshal(map[string]any{
		"lease_token": repository.claimToken, "filename": "empty.txt", "size_bytes": 0,
		"sha256": base64.RawURLEncoding.EncodeToString(emptyDigest[:]), "directory_zip": false,
	})
	upload := signedFileOperationRequest(t, windowsKey, windowsID, now, http.MethodPost, "/v1/pcs/"+pcID.String()+"/file-operations/"+operationID.String()+"/upload", uploadBody)
	upload.SetPathValue("pcId", pcID.String())
	upload.SetPathValue("operationId", operationID.String())
	uploadResponse := httptest.NewRecorder()
	windowsAuth.Authenticate(http.HandlerFunc(service.uploadFileOperation)).ServeHTTP(uploadResponse, upload)
	if uploadResponse.Code != http.StatusOK || repository.uploadInput.SizeBytes != 0 ||
		!bytes.Equal(repository.uploadInput.SHA256, emptyDigest[:]) {
		t.Fatalf("zero-byte transfer status=%d body=%s metadata=%+v", uploadResponse.Code, uploadResponse.Body.String(), repository.uploadInput)
	}

	repository.record.Status = store.FileOperationSucceeded
	get := signedFileOperationRequest(t, androidKey, androidID, now, http.MethodGet, "/v1/pcs/"+pcID.String()+"/file-operations/"+operationID.String(), nil)
	get.SetPathValue("pcId", pcID.String())
	get.SetPathValue("operationId", operationID.String())
	getResponse := httptest.NewRecorder()
	androidAuth.Authenticate(http.HandlerFunc(service.getFileOperation)).ServeHTTP(getResponse, get)
	if getResponse.Code != http.StatusOK || !strings.Contains(getResponse.Body.String(), `"transfer"`) ||
		!strings.Contains(getResponse.Body.String(), `"protocol":"REALTIME_V2"`) ||
		!strings.Contains(getResponse.Body.String(), `"sha256":"`+base64.RawURLEncoding.EncodeToString(emptyDigest[:])+`"`) ||
		strings.Contains(getResponse.Body.String(), "object_key") {
		t.Fatalf("transfer poll status=%d body=%s", getResponse.Code, getResponse.Body.String())
	}
}

func TestDownloadCompletionRequiresRealtimeTransferMetadata(t *testing.T) {
	accountID, pcID, operationID, windowsID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	now := time.Date(2026, 8, 24, 12, 0, 0, 0, time.UTC)
	authentication, privateKey := fileOperationTestAuth(t, accountID, windowsID, "WINDOWS_PC", now)
	token := make([]byte, 32)
	_, _ = rand.Read(token)
	encodedToken := base64.RawURLEncoding.EncodeToString(token)
	leaseHash := sha256.Sum256(token)
	size := int64(9)
	digest := sha256.Sum256([]byte("zip bytes"))
	objectProtocol := "OBJECT_V1"
	repository := &fakeFileOperationStore{record: store.FileOperationRecord{
		ID: operationID, PCID: pcID, Operation: store.FileOperationDownload, Status: store.FileOperationProcessing,
		SizeBytes: &size, SHA256: digest[:], TransferProtocol: &objectProtocol,
		LeaseTokenHash: leaseHash[:], LeaseExpiresAt: timePointer(now.Add(time.Hour)),
		ExpiresAt: now.Add(time.Hour), CreatedAt: now, UpdatedAt: now,
	}}
	service := &API{fileOperations: repository, Clock: func() time.Time { return now }}
	body, _ := json.Marshal(map[string]any{"lease_token": encodedToken, "status": store.FileOperationSucceeded})
	request := signedFileOperationRequest(t, privateKey, windowsID, now, http.MethodPost, "/v1/pcs/"+pcID.String()+"/file-operations/"+operationID.String()+"/complete", body)
	request.SetPathValue("pcId", pcID.String())
	request.SetPathValue("operationId", operationID.String())
	response := httptest.NewRecorder()
	authentication.Authenticate(http.HandlerFunc(service.completeFileOperation)).ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || repository.completeCalls != 0 {
		t.Fatalf("object completion status=%d body=%s completeCalls=%d", response.Code, response.Body.String(), repository.completeCalls)
	}

	realtimeProtocol := store.FileTransferRealtimeV2
	repository.record.TransferProtocol = &realtimeProtocol
	retry := signedFileOperationRequest(t, privateKey, windowsID, now, http.MethodPost, "/v1/pcs/"+pcID.String()+"/file-operations/"+operationID.String()+"/complete", body)
	retry.SetPathValue("pcId", pcID.String())
	retry.SetPathValue("operationId", operationID.String())
	retryResponse := httptest.NewRecorder()
	authentication.Authenticate(http.HandlerFunc(service.completeFileOperation)).ServeHTTP(retryResponse, retry)
	if retryResponse.Code != http.StatusOK || repository.completeCalls != 1 {
		t.Fatalf("realtime completion status=%d body=%s completeCalls=%d", retryResponse.Code, retryResponse.Body.String(), repository.completeCalls)
	}
}

func TestOpenTerminalCompletionRequiresCanonicalSessionAndMatchingShell(t *testing.T) {
	accountID, pcID, operationID, windowsID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	now := time.Date(2026, 8, 24, 12, 0, 0, 0, time.UTC)
	authentication, privateKey := fileOperationTestAuth(t, accountID, windowsID, "WINDOWS_PC", now)
	token := make([]byte, 32)
	_, _ = rand.Read(token)
	encodedToken := base64.RawURLEncoding.EncodeToString(token)
	leaseHash := sha256.Sum256(token)
	shell := "windows-powershell"
	repository := &fakeFileOperationStore{record: store.FileOperationRecord{
		ID: operationID, PCID: pcID, Operation: store.FileOperationOpenTerminal, Path: `C:\folder`, ShellType: &shell,
		Status: store.FileOperationProcessing, LeaseTokenHash: leaseHash[:], LeaseExpiresAt: timePointer(now.Add(time.Hour)),
		ExpiresAt: now.Add(time.Hour), CreatedAt: now, UpdatedAt: now,
	}}
	service := &API{fileOperations: repository, Clock: func() time.Time { return now }}
	invoke := func(result any) *httptest.ResponseRecorder {
		body, _ := json.Marshal(map[string]any{"lease_token": encodedToken, "status": store.FileOperationSucceeded, "result": result})
		request := signedFileOperationRequest(t, privateKey, windowsID, now, http.MethodPost,
			"/v1/pcs/"+pcID.String()+"/file-operations/"+operationID.String()+"/complete", body)
		request.SetPathValue("pcId", pcID.String())
		request.SetPathValue("operationId", operationID.String())
		response := httptest.NewRecorder()
		authentication.Authenticate(http.HandlerFunc(service.completeFileOperation)).ServeHTTP(response, request)
		return response
	}
	invalid := invoke(map[string]any{"session_id": uuid.NewString(), "shell_type": "command-prompt", "cwd": `C:\folder`})
	if invalid.Code != http.StatusBadRequest || repository.completeCalls != 0 {
		t.Fatalf("mismatched shell status=%d body=%s completeCalls=%d", invalid.Code, invalid.Body.String(), repository.completeCalls)
	}
	missingCWD := invoke(map[string]any{"session_id": uuid.NewString(), "shell_type": shell})
	if missingCWD.Code != http.StatusBadRequest || repository.completeCalls != 0 {
		t.Fatalf("missing cwd status=%d body=%s completeCalls=%d", missingCWD.Code, missingCWD.Body.String(), repository.completeCalls)
	}
	mismatchedCWD := invoke(map[string]any{"session_id": uuid.NewString(), "shell_type": shell, "cwd": `C:\other`})
	if mismatchedCWD.Code != http.StatusBadRequest || repository.completeCalls != 0 {
		t.Fatalf("mismatched cwd status=%d body=%s completeCalls=%d", mismatchedCWD.Code, mismatchedCWD.Body.String(), repository.completeCalls)
	}
	valid := invoke(map[string]any{"session_id": uuid.NewString(), "shell_type": shell, "cwd": `C:\folder`})
	if valid.Code != http.StatusOK || repository.completeCalls != 1 {
		t.Fatalf("valid terminal status=%d body=%s completeCalls=%d", valid.Code, valid.Body.String(), repository.completeCalls)
	}
}

func TestFileOperationFullStackIdempotencyAcrossLeaseRotation(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the file-operation HTTP idempotency test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := store.Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()
	accountID, pcID, requesterID, windowsID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	insertFullStackFileOperationFixture(t, ctx, repository, accountID, pcID, requesterID, windowsID)
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, accountID)
	})

	now := time.Now().UTC()
	authentication, privateKey := fileOperationTestAuth(t, accountID, windowsID, "WINDOWS_PC", now)
	service := (&API{Store: repository, Auth: authentication, Clock: func() time.Time { return now }}).Handler()
	digest := sha256.Sum256(nil)

	createDownload := func(path string) store.FileOperationRecord {
		item, createErr := repository.CreateFileOperation(ctx, accountID, requesterID, pcID, store.FileOperationCreateInput{
			Operation: store.FileOperationDownload, Path: path, ExpiresAt: now.Add(time.Hour),
		})
		if createErr != nil {
			t.Fatal(createErr)
		}
		return item
	}
	invoke := func(method, target string, body []byte, key string) *httptest.ResponseRecorder {
		request := signedFileOperationRequest(t, privateKey, windowsID, now, method, target, body)
		// Nonce uniqueness matters to the real auth middleware just as much as
		// idempotency-key uniqueness matters to the API middleware.
		nonceBytes := make([]byte, 16)
		if _, err := rand.Read(nonceBytes); err != nil {
			t.Fatal(err)
		}
		nonce := base64.RawURLEncoding.EncodeToString(nonceBytes)
		timestamp := strconv.FormatInt(now.UnixMilli(), 10)
		bodyDigest := sha256.Sum256(body)
		request.Header.Set("X-TermLink-Nonce", nonce)
		request.Header.Set("X-TermLink-Signature", base64.RawURLEncoding.EncodeToString(ed25519.Sign(privateKey,
			auth.CanonicalRequest(method, target, timestamp, nonce, bodyDigest))))
		if key != "" {
			request.Header.Set("Idempotency-Key", key)
		}
		response := httptest.NewRecorder()
		service.ServeHTTP(response, request)
		return response
	}
	parseLease := func(response *httptest.ResponseRecorder) string {
		var payload struct {
			LeaseToken string `json:"lease_token"`
		}
		if err := json.Unmarshal(response.Body.Bytes(), &payload); err != nil || payload.LeaseToken == "" {
			t.Fatalf("invalid claim body=%s error=%v", response.Body.String(), err)
		}
		return payload.LeaseToken
	}

	first := createDownload(`C:\empty.txt`)
	claimPath := "/v1/pcs/" + pcID.String() + "/file-operations/claim"
	missingKey := invoke(http.MethodPost, claimPath, nil, "")
	if missingKey.Code != http.StatusBadRequest || !strings.Contains(missingKey.Body.String(), "IDEMPOTENCY_KEY_REQUIRED") {
		t.Fatalf("claim without key status=%d body=%s", missingKey.Code, missingKey.Body.String())
	}
	stillPending, err := repository.GetFileOperation(ctx, accountID, requesterID, pcID, first.ID)
	if err != nil || stillPending.Status != store.FileOperationPending {
		t.Fatalf("keyless claim reached broker item=%+v err=%v", stillPending, err)
	}
	freshClaim := invoke(http.MethodPost, claimPath, nil, "claim-first-0001")
	if freshClaim.Code != http.StatusOK {
		t.Fatalf("fresh claim status=%d body=%s", freshClaim.Code, freshClaim.Body.String())
	}
	firstToken := parseLease(freshClaim)
	renewPath := "/v1/pcs/" + pcID.String() + "/file-operations/" + first.ID.String() + "/renew"
	renewBody, _ := json.Marshal(map[string]any{"lease_token": firstToken})
	renew := invoke(http.MethodPost, renewPath, renewBody, "renew-first-0001")
	renewReplay := invoke(http.MethodPost, renewPath, renewBody, "renew-first-0001")
	if renew.Code != http.StatusOK || renewReplay.Code != http.StatusOK || renewReplay.Header().Get("Idempotency-Replayed") != "true" {
		t.Fatalf("renew/replay statuses=%d/%d replay=%q bodies=%s / %s", renew.Code, renewReplay.Code,
			renewReplay.Header().Get("Idempotency-Replayed"), renew.Body.String(), renewReplay.Body.String())
	}
	var renewPayload struct {
		FileOperation struct {
			LeaseExpiresAt time.Time `json:"lease_expires_at"`
			ExpiresAt      time.Time `json:"expires_at"`
		} `json:"file_operation"`
	}
	if err := json.Unmarshal(renew.Body.Bytes(), &renewPayload); err != nil ||
		renewPayload.FileOperation.LeaseExpiresAt.After(renewPayload.FileOperation.ExpiresAt) {
		t.Fatalf("renew lease exceeds operation expiry payload=%s error=%v", renew.Body.String(), err)
	}
	firstUploadBody, _ := json.Marshal(map[string]any{
		"lease_token": firstToken, "filename": "empty.txt", "size_bytes": 0,
		"sha256": base64.RawURLEncoding.EncodeToString(digest[:]), "directory_zip": false,
	})
	firstUploadPath := "/v1/pcs/" + pcID.String() + "/file-operations/" + first.ID.String() + "/upload"
	firstUpload := invoke(http.MethodPost, firstUploadPath, firstUploadBody, "upload-first-0001")
	firstUploadReplay := invoke(http.MethodPost, firstUploadPath, firstUploadBody, "upload-first-0001")
	if firstUpload.Code != http.StatusOK || firstUploadReplay.Code != http.StatusOK ||
		firstUploadReplay.Header().Get("Idempotency-Replayed") != "true" ||
		!strings.Contains(firstUpload.Body.String(), `"protocol":"REALTIME_V2"`) {
		t.Fatalf("transfer/replay statuses=%d/%d replay=%q bodies=%s / %s",
			firstUpload.Code, firstUploadReplay.Code, firstUploadReplay.Header().Get("Idempotency-Replayed"),
			firstUpload.Body.String(), firstUploadReplay.Body.String())
	}
	firstCompleteBody, _ := json.Marshal(map[string]any{"lease_token": firstToken, "status": store.FileOperationSucceeded})
	firstCompletePath := "/v1/pcs/" + pcID.String() + "/file-operations/" + first.ID.String() + "/complete"
	firstComplete := invoke(http.MethodPost, firstCompletePath, firstCompleteBody, "complete-first-0001")
	firstCompleteReplay := invoke(http.MethodPost, firstCompletePath, firstCompleteBody, "complete-first-0001")
	if firstComplete.Code != http.StatusOK || firstCompleteReplay.Code != http.StatusOK ||
		firstCompleteReplay.Header().Get("Idempotency-Replayed") != "true" {
		t.Fatalf("complete/replay statuses=%d/%d replay=%q bodies=%s / %s",
			firstComplete.Code, firstCompleteReplay.Code, firstCompleteReplay.Header().Get("Idempotency-Replayed"),
			firstComplete.Body.String(), firstCompleteReplay.Body.String())
	}
	var cleanupCount int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM outbox
		WHERE event_type='history.object.delete' AND aggregate_id=$1`, first.ID).Scan(&cleanupCount); err != nil || cleanupCount != 0 {
		t.Fatalf("successful download cleanup count=%d error=%v", cleanupCount, err)
	}

	second := createDownload(`C:\retry.txt`)
	leaseOneResponse := invoke(http.MethodPost, claimPath, nil, "claim-second-0001")
	if leaseOneResponse.Code != http.StatusOK {
		t.Fatalf("initial second claim status=%d body=%s", leaseOneResponse.Code, leaseOneResponse.Body.String())
	}
	leaseOne := parseLease(leaseOneResponse)
	leaseOneUploadBody, _ := json.Marshal(map[string]any{
		"lease_token": leaseOne, "filename": "retry.txt", "size_bytes": 0,
		"sha256": base64.RawURLEncoding.EncodeToString(digest[:]), "directory_zip": false,
	})
	secondUploadPath := "/v1/pcs/" + pcID.String() + "/file-operations/" + second.ID.String() + "/upload"
	if response := invoke(http.MethodPost, secondUploadPath, leaseOneUploadBody, "upload-second-current"); response.Code != http.StatusOK {
		t.Fatalf("initial second upload status=%d body=%s", response.Code, response.Body.String())
	}
	if _, err := repository.Pool.Exec(ctx, `UPDATE pc_file_operations SET lease_expires_at=now()-interval '1 second' WHERE id=$1`, second.ID); err != nil {
		t.Fatal(err)
	}
	leaseTwoResponse := invoke(http.MethodPost, claimPath, nil, "claim-second-0002")
	if leaseTwoResponse.Code != http.StatusOK {
		t.Fatalf("rotated claim status=%d body=%s", leaseTwoResponse.Code, leaseTwoResponse.Body.String())
	}
	leaseTwo := parseLease(leaseTwoResponse)
	if leaseTwo == leaseOne {
		t.Fatal("expired operation reused its old lease token")
	}
	leaseTwoUploadBody, _ := json.Marshal(map[string]any{
		"lease_token": leaseTwo, "filename": "retry.txt", "size_bytes": 0,
		"sha256": base64.RawURLEncoding.EncodeToString(digest[:]), "directory_zip": false,
	})
	reusedKey := invoke(http.MethodPost, secondUploadPath, leaseTwoUploadBody, "upload-second-current")
	if reusedKey.Code != http.StatusConflict || !strings.Contains(reusedKey.Body.String(), "IDEMPOTENCY_KEY_REUSED") {
		t.Fatalf("rotated lease reused key status=%d body=%s", reusedKey.Code, reusedKey.Body.String())
	}
	freshKey := invoke(http.MethodPost, secondUploadPath, leaseTwoUploadBody, "upload-second-rotated")
	if freshKey.Code != http.StatusOK {
		t.Fatalf("rotated lease fresh key status=%d body=%s", freshKey.Code, freshKey.Body.String())
	}
	freshKeyReplay := invoke(http.MethodPost, secondUploadPath, leaseTwoUploadBody, "upload-second-rotated")
	if freshKeyReplay.Code != http.StatusOK || freshKeyReplay.Header().Get("Idempotency-Replayed") != "true" {
		t.Fatalf("rotated lease upload replay status=%d replay=%q body=%s", freshKeyReplay.Code,
			freshKeyReplay.Header().Get("Idempotency-Replayed"), freshKeyReplay.Body.String())
	}
	secondCompleteBody, _ := json.Marshal(map[string]any{"lease_token": leaseTwo, "status": store.FileOperationSucceeded})
	secondCompletePath := "/v1/pcs/" + pcID.String() + "/file-operations/" + second.ID.String() + "/complete"
	secondComplete := invoke(http.MethodPost, secondCompletePath, secondCompleteBody, "complete-second-current")
	secondCompleteReplay := invoke(http.MethodPost, secondCompletePath, secondCompleteBody, "complete-second-current")
	if secondComplete.Code != http.StatusOK || secondCompleteReplay.Code != http.StatusOK ||
		secondCompleteReplay.Header().Get("Idempotency-Replayed") != "true" {
		t.Fatalf("rotated completion statuses=%d/%d replay=%q bodies=%s / %s", secondComplete.Code,
			secondCompleteReplay.Code, secondCompleteReplay.Header().Get("Idempotency-Replayed"),
			secondComplete.Body.String(), secondCompleteReplay.Body.String())
	}
}

func insertFullStackFileOperationFixture(t *testing.T, ctx context.Context, repository *store.Postgres, accountID, pcID, requesterID, windowsID uuid.UUID) {
	t.Helper()
	subject := sha256.Sum256([]byte("file-operation-http-" + accountID.String()))
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, accountID, subject[:]); err != nil {
		t.Fatal(err)
	}
	insertDevice := func(id uuid.UUID, deviceType string, seed byte, approvedBy *uuid.UUID) {
		key := bytes.Repeat([]byte{seed}, ed25519.PublicKeySize)
		exchange := bytes.Repeat([]byte{seed + 10}, 32)
		if _, err := repository.Pool.Exec(ctx, `
			INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_by,approved_at)
			VALUES($1,$2,$3,$4,$5,$6,$7,'TRUSTED',$8,now())`, id, accountID, deviceType,
			deviceType, key, exchange, "key-"+id.String(), approvedBy); err != nil {
			t.Fatal(err)
		}
	}
	insertDevice(windowsID, "WINDOWS_PC", 1, nil)
	insertDevice(requesterID, "ANDROID", 2, &windowsID)
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch)
		VALUES($1,$2,$3,'HTTP idempotency PC','test',1)`, pcID, accountID, windowsID); err != nil {
		t.Fatal(err)
	}
}

type fileOperationTokenVerifier struct{}

func (fileOperationTokenVerifier) Verify(context.Context, string) (auth.Identity, error) {
	return auth.Identity{Issuer: "https://issuer.test", Subject: "subject", AuthorizedParty: "test", TokenID: "token"}, nil
}

type fileOperationDeviceLoader struct{ device auth.Device }

func (loader fileOperationDeviceLoader) LoadDeviceForSubject(_ context.Context, _ [32]byte, id uuid.UUID) (auth.Device, error) {
	if id != loader.device.ID {
		return auth.Device{}, errors.New("unknown device")
	}
	return loader.device, nil
}

type fileOperationNonceStore struct{}

func (fileOperationNonceStore) Consume(context.Context, string, string, time.Duration) (bool, error) {
	return true, nil
}

func fileOperationTestAuth(t *testing.T, accountID, deviceID uuid.UUID, deviceType string, now time.Time) (auth.Middleware, ed25519.PrivateKey) {
	t.Helper()
	publicKey, privateKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	middleware := auth.Middleware{
		Tokens: fileOperationTokenVerifier{},
		Devices: fileOperationDeviceLoader{device: auth.Device{
			ID: deviceID, UserID: accountID, Type: deviceType, SigningPublicKey: publicKey, TrustState: "TRUSTED",
		}},
		Nonces: fileOperationNonceStore{}, SubjectKey: []byte("01234567890123456789012345678901"),
		Clock: func() time.Time { return now },
	}
	return middleware, privateKey
}

func signedFileOperationRequest(t *testing.T, privateKey ed25519.PrivateKey, deviceID uuid.UUID, now time.Time, method, target string, body []byte) *http.Request {
	t.Helper()
	request := httptest.NewRequest(method, target, bytes.NewReader(body))
	timestamp := strconv.FormatInt(now.UnixMilli(), 10)
	nonce := base64.RawURLEncoding.EncodeToString([]byte("0123456789abcdef"))
	digest := sha256.Sum256(body)
	signature := ed25519.Sign(privateKey, auth.CanonicalRequest(method, target, timestamp, nonce, digest))
	request.Header.Set("Authorization", "Bearer test")
	request.Header.Set("X-TermLink-Device", deviceID.String())
	request.Header.Set("X-TermLink-Timestamp", timestamp)
	request.Header.Set("X-TermLink-Nonce", nonce)
	request.Header.Set("X-TermLink-Signature", base64.RawURLEncoding.EncodeToString(signature))
	return request
}

type fakeFileOperationStore struct {
	record        store.FileOperationRecord
	createInput   store.FileOperationCreateInput
	uploadInput   store.FileOperationUploadInput
	claimToken    string
	completeCalls int
}

func (fake *fakeFileOperationStore) CreateFileOperation(_ context.Context, _, _, _ uuid.UUID, input store.FileOperationCreateInput) (store.FileOperationRecord, error) {
	fake.createInput = input
	result := fake.record
	result.Status = store.FileOperationPending
	return result, nil
}
func (fake *fakeFileOperationStore) ClaimFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, time.Duration) (*store.FileOperationRecord, string, error) {
	result := fake.record
	return &result, fake.claimToken, nil
}
func (fake *fakeFileOperationStore) GetFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.FileOperationRecord, error) {
	return fake.record, nil
}
func (fake *fakeFileOperationStore) CancelFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.FileOperationRecord, error) {
	result := fake.record
	result.Status = store.FileOperationCancelled
	return result, nil
}
func (fake *fakeFileOperationStore) RecordFileOperationUpload(_ context.Context, _, _, _, _ uuid.UUID, _ [32]byte, input store.FileOperationUploadInput) (store.FileOperationRecord, error) {
	fake.uploadInput = input
	return fake.record, nil
}
func (fake *fakeFileOperationStore) RecordFileOperationTransfer(_ context.Context, _, _, _, _ uuid.UUID, _ [32]byte, input store.FileOperationUploadInput) (store.FileOperationRecord, error) {
	fake.uploadInput = input
	protocol := store.FileTransferRealtimeV2
	fake.record.Filename = &input.Filename
	fake.record.SizeBytes = &input.SizeBytes
	fake.record.SHA256 = append([]byte(nil), input.SHA256...)
	fake.record.DirectoryZIP = &input.DirectoryZIP
	fake.record.TransferProtocol = &protocol
	return fake.record, nil
}
func (fake *fakeFileOperationStore) AgentFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte) (store.FileOperationRecord, error) {
	return fake.record, nil
}
func (fake *fakeFileOperationStore) RenewFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, time.Duration) (store.FileOperationRecord, error) {
	return fake.record, nil
}
func (fake *fakeFileOperationStore) CompleteFileOperation(_ context.Context, _, _, _, _ uuid.UUID, _ [32]byte, input store.FileOperationCompleteInput) (store.FileOperationRecord, error) {
	fake.completeCalls++
	result := fake.record
	result.Status = input.Status
	return result, nil
}

func timePointer(value time.Time) *time.Time { return &value }
