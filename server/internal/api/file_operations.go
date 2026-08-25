package api

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/history"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	fileOperationLifetime      = 24 * time.Hour
	fileOperationLeaseDuration = time.Hour
	fileTransferURLLifetime    = time.Hour
	fileDownloadURLLifetime    = 10 * time.Minute
)

type fileOperationStore interface {
	CreateFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, store.FileOperationCreateInput) (store.FileOperationRecord, error)
	ClaimFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, time.Duration) (*store.FileOperationRecord, string, error)
	GetFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.FileOperationRecord, error)
	CancelFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.FileOperationRecord, error)
	RecordFileOperationUpload(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, store.FileOperationUploadInput) (store.FileOperationRecord, error)
	AgentFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte) (store.FileOperationRecord, error)
	RenewFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, time.Duration) (store.FileOperationRecord, error)
	CompleteFileOperation(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, store.FileOperationCompleteInput) (store.FileOperationRecord, error)
}

func (a *API) fileOperationRepository() fileOperationStore {
	if a.fileOperations != nil {
		return a.fileOperations
	}
	return a.Store
}

func (a *API) createFileOperation(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	pcID, ok := parseFileOperationPCID(w, r)
	if !ok {
		return
	}
	var input struct {
		Operation string  `json:"operation"`
		Path      string  `json:"path"`
		Offset    *int64  `json:"offset"`
		ShellType *string `json:"shell_type"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	if err := validateFileOperationRequest(input.Operation, input.Path, input.Offset, input.ShellType); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_FILE_OPERATION", err)
		return
	}
	repository := a.fileOperationRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "FILE_OPERATIONS_UNAVAILABLE", errors.New("file-operation store unavailable"))
		return
	}
	item, err := repository.CreateFileOperation(r.Context(), p.UserID, p.Device.ID, pcID, store.FileOperationCreateInput{
		Operation: input.Operation, Path: input.Path, Offset: input.Offset, ShellType: input.ShellType,
		ExpiresAt: a.now().Add(fileOperationLifetime),
	})
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"file_operation": fileOperationView(item)})
}

func (a *API) claimFileOperation(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "WINDOWS_PC" {
		writeError(w, r, http.StatusForbidden, "WINDOWS_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	pcID, ok := parseFileOperationPCID(w, r)
	if !ok {
		return
	}
	repository := a.fileOperationRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "FILE_OPERATIONS_UNAVAILABLE", errors.New("file-operation store unavailable"))
		return
	}
	item, leaseToken, err := repository.ClaimFileOperation(r.Context(), p.UserID, p.Device.ID, pcID, fileOperationLeaseDuration)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if item == nil {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"file_operation": fileOperationView(*item), "lease_token": leaseToken})
}

func (a *API) getFileOperation(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	pcID, operationID, ok := parseFileOperationIDs(w, r)
	if !ok {
		return
	}
	repository := a.fileOperationRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "FILE_OPERATIONS_UNAVAILABLE", errors.New("file-operation store unavailable"))
		return
	}
	item, err := repository.GetFileOperation(r.Context(), p.UserID, p.Device.ID, pcID, operationID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	response := map[string]any{"file_operation": fileOperationView(item)}
	if item.Status == store.FileOperationSucceeded && item.Operation == store.FileOperationDownload {
		if a.History == nil || item.ObjectKey == "" || item.Filename == nil || item.SizeBytes == nil || len(item.SHA256) != sha256.Size ||
			item.DirectoryZIP == nil || item.CompletedAt == nil {
			writeError(w, r, http.StatusServiceUnavailable, "FILE_TRANSFER_UNAVAILABLE", errors.New("download object unavailable"))
			return
		}
		if a.now().Before(item.CompletedAt.Add(store.FileOperationDownloadGracePeriod)) {
			download, err := a.History.PresignGet(r.Context(), item.ObjectKey, fileDownloadURLLifetime)
			if err != nil {
				writeError(w, r, http.StatusServiceUnavailable, "FILE_TRANSFER_UNAVAILABLE", err)
				return
			}
			response["download"] = map[string]any{
				"filename": *item.Filename, "size_bytes": *item.SizeBytes,
				"sha256":        base64.RawURLEncoding.EncodeToString(item.SHA256),
				"directory_zip": *item.DirectoryZIP, "request": download,
			}
		}
	}
	writeJSON(w, http.StatusOK, response)
}

func (a *API) cancelFileOperation(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	pcID, operationID, ok := parseFileOperationIDs(w, r)
	if !ok {
		return
	}
	repository := a.fileOperationRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "FILE_OPERATIONS_UNAVAILABLE", errors.New("file-operation store unavailable"))
		return
	}
	item, err := repository.CancelFileOperation(r.Context(), p.UserID, p.Device.ID, pcID, operationID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"file_operation": fileOperationView(item)})
}

func (a *API) uploadFileOperation(w http.ResponseWriter, r *http.Request) {
	if a.History == nil {
		writeError(w, r, http.StatusServiceUnavailable, "FILE_TRANSFER_UNAVAILABLE", errors.New("object store unavailable"))
		return
	}
	p := principal(r)
	if p.Device.Type != "WINDOWS_PC" {
		writeError(w, r, http.StatusForbidden, "WINDOWS_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	pcID, operationID, ok := parseFileOperationIDs(w, r)
	if !ok {
		return
	}
	var input struct {
		LeaseToken   string `json:"lease_token"`
		Filename     string `json:"filename"`
		SizeBytes    int64  `json:"size_bytes"`
		SHA256       string `json:"sha256"`
		DirectoryZIP bool   `json:"directory_zip"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	leaseHash, err := parseFileOperationLease(input.LeaseToken)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LEASE_TOKEN", err)
		return
	}
	digest, err := base64.RawURLEncoding.DecodeString(input.SHA256)
	if err != nil || len(digest) != sha256.Size || input.SizeBytes < 0 || input.SizeBytes > history.MaxFileTransferObjectBytes ||
		!validFileTransferFilename(input.Filename) || (input.DirectoryZIP && !strings.HasSuffix(strings.ToLower(input.Filename), ".zip")) {
		writeError(w, r, http.StatusBadRequest, "INVALID_FILE_TRANSFER_METADATA", errors.New("invalid file transfer metadata"))
		return
	}
	repository := a.fileOperationRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "FILE_OPERATIONS_UNAVAILABLE", errors.New("file-operation store unavailable"))
		return
	}
	item, err := repository.RecordFileOperationUpload(r.Context(), p.UserID, p.Device.ID, pcID, operationID, leaseHash, store.FileOperationUploadInput{
		Filename: input.Filename, SizeBytes: input.SizeBytes, SHA256: digest, DirectoryZIP: input.DirectoryZIP,
	})
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	upload, err := a.History.PresignPut(r.Context(), item.ObjectKey, input.SizeBytes, digest, fileTransferURLLifetime)
	if err != nil {
		writeError(w, r, http.StatusServiceUnavailable, "FILE_TRANSFER_UNAVAILABLE", err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"upload": upload})
}

func (a *API) renewFileOperation(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "WINDOWS_PC" {
		writeError(w, r, http.StatusForbidden, "WINDOWS_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	pcID, operationID, ok := parseFileOperationIDs(w, r)
	if !ok {
		return
	}
	var input struct {
		LeaseToken string `json:"lease_token"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	leaseHash, err := parseFileOperationLease(input.LeaseToken)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LEASE_TOKEN", err)
		return
	}
	repository := a.fileOperationRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "FILE_OPERATIONS_UNAVAILABLE", errors.New("file-operation store unavailable"))
		return
	}
	item, err := repository.RenewFileOperation(r.Context(), p.UserID, p.Device.ID, pcID, operationID, leaseHash, fileOperationLeaseDuration)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"file_operation": fileOperationView(item)})
}

func (a *API) completeFileOperation(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "WINDOWS_PC" {
		writeError(w, r, http.StatusForbidden, "WINDOWS_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	pcID, operationID, ok := parseFileOperationIDs(w, r)
	if !ok {
		return
	}
	var input struct {
		LeaseToken string          `json:"lease_token"`
		Status     string          `json:"status"`
		Result     json.RawMessage `json:"result"`
		Error      *string         `json:"error"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	leaseHash, err := parseFileOperationLease(input.LeaseToken)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LEASE_TOKEN", err)
		return
	}
	if input.Status != store.FileOperationSucceeded && input.Status != store.FileOperationFailed {
		writeError(w, r, http.StatusBadRequest, "INVALID_FILE_OPERATION_RESULT", errors.New("invalid completion status"))
		return
	}
	if input.Status == store.FileOperationFailed {
		if input.Error == nil || strings.TrimSpace(*input.Error) == "" || len(*input.Error) > 1024 || input.Result != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_FILE_OPERATION_RESULT", errors.New("invalid failure result"))
			return
		}
	} else if input.Error != nil || len(input.Result) > store.MaxFileOperationResultBytes || (input.Result != nil && !json.Valid(input.Result)) {
		writeError(w, r, http.StatusBadRequest, "INVALID_FILE_OPERATION_RESULT", errors.New("invalid success result"))
		return
	}
	repository := a.fileOperationRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "FILE_OPERATIONS_UNAVAILABLE", errors.New("file-operation store unavailable"))
		return
	}
	current, err := repository.AgentFileOperation(r.Context(), p.UserID, p.Device.ID, pcID, operationID, leaseHash)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if input.Status == store.FileOperationSucceeded {
		switch current.Operation {
		case store.FileOperationList:
			if len(input.Result) == 0 || string(input.Result) == "null" {
				writeError(w, r, http.StatusBadRequest, "INVALID_FILE_OPERATION_RESULT", errors.New("LIST result required"))
				return
			}
		case store.FileOperationDownload:
			if len(input.Result) != 0 || a.History == nil || current.ObjectKey == "" || current.SizeBytes == nil || len(current.SHA256) != sha256.Size {
				writeError(w, r, http.StatusBadRequest, "INVALID_FILE_OPERATION_RESULT", errors.New("download upload required"))
				return
			}
			if err := a.History.VerifyCiphertext(r.Context(), current.ObjectKey, *current.SizeBytes, current.SHA256); err != nil {
				writeError(w, r, http.StatusUnprocessableEntity, "FILE_TRANSFER_INTEGRITY_FAILED", err)
				return
			}
		case store.FileOperationOpenTerminal:
			if err := validateOpenTerminalResult(input.Result, current.ShellType, current.Path); err != nil {
				writeError(w, r, http.StatusBadRequest, "INVALID_FILE_OPERATION_RESULT", err)
				return
			}
		default:
			writeError(w, r, http.StatusBadRequest, "INVALID_FILE_OPERATION_RESULT", errors.New("unknown operation"))
			return
		}
	}
	item, err := repository.CompleteFileOperation(r.Context(), p.UserID, p.Device.ID, pcID, operationID, leaseHash, store.FileOperationCompleteInput{
		Status: input.Status, Result: input.Result, ErrorMessage: input.Error,
	})
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"file_operation": fileOperationView(item)})
}

func validateOpenTerminalResult(result json.RawMessage, requestedShell *string, requestedPath string) error {
	if requestedShell == nil || len(result) == 0 || string(result) == "null" {
		return errors.New("terminal result required")
	}
	var value struct {
		SessionID string `json:"session_id"`
		ShellType string `json:"shell_type"`
		CWD       string `json:"cwd"`
	}
	decoder := json.NewDecoder(strings.NewReader(string(result)))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&value); err != nil || decoder.Decode(&struct{}{}) != io.EOF {
		return errors.New("invalid terminal result")
	}
	sessionID, err := uuid.Parse(value.SessionID)
	if err != nil || sessionID == uuid.Nil || sessionID.String() != value.SessionID ||
		value.ShellType != *requestedShell || value.CWD != requestedPath {
		return errors.New("invalid terminal result")
	}
	return nil
}

func validateFileOperationRequest(operation, path string, offset *int64, shellType *string) error {
	if !utf8.ValidString(path) || len(path) > store.MaxFileOperationPathBytes || strings.ContainsRune(path, '\x00') {
		return errors.New("invalid path")
	}
	switch operation {
	case store.FileOperationList:
		if shellType != nil || (offset != nil && *offset < 0) {
			return errors.New("invalid LIST parameters")
		}
	case store.FileOperationDownload:
		if path == "" || offset != nil || shellType != nil {
			return errors.New("invalid DOWNLOAD parameters")
		}
	case store.FileOperationOpenTerminal:
		if path == "" || offset != nil || shellType == nil ||
			(*shellType != "command-prompt" && *shellType != "windows-powershell" &&
				*shellType != "powershell-7" && *shellType != "wsl") {
			return errors.New("invalid OPEN_TERMINAL parameters")
		}
	default:
		return errors.New("unknown operation")
	}
	return nil
}

func parseFileOperationPCID(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	pcID, err := uuid.Parse(r.PathValue("pcId"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PC_ID", err)
		return uuid.Nil, false
	}
	return pcID, true
}

func parseFileOperationIDs(w http.ResponseWriter, r *http.Request) (uuid.UUID, uuid.UUID, bool) {
	pcID, ok := parseFileOperationPCID(w, r)
	if !ok {
		return uuid.Nil, uuid.Nil, false
	}
	operationID, err := uuid.Parse(r.PathValue("operationId"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_FILE_OPERATION_ID", err)
		return uuid.Nil, uuid.Nil, false
	}
	return pcID, operationID, true
}

func parseFileOperationLease(value string) ([32]byte, error) {
	var result [32]byte
	token, err := base64.RawURLEncoding.DecodeString(value)
	if err != nil || len(token) != 32 {
		return result, errors.New("invalid lease token")
	}
	return sha256.Sum256(token), nil
}

func validFileTransferFilename(value string) bool {
	return utf8.ValidString(value) && len(value) >= 1 && len(value) <= 255 &&
		!strings.ContainsAny(value, "/\\\x00\r\n") && value != "." && value != ".."
}

func fileOperationView(item store.FileOperationRecord) map[string]any {
	result := map[string]any{
		"id": item.ID, "pc_id": item.PCID, "operation": item.Operation, "path": item.Path,
		"status": item.Status, "attempt_count": item.AttemptCount, "expires_at": item.ExpiresAt,
		"created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
	if item.Offset != nil {
		result["offset"] = *item.Offset
	}
	if item.ShellType != nil {
		result["shell_type"] = *item.ShellType
	}
	if item.LeaseExpiresAt != nil && item.Status == store.FileOperationProcessing {
		result["lease_expires_at"] = *item.LeaseExpiresAt
	}
	if len(item.Result) != 0 && string(item.Result) != "null" {
		result["result"] = item.Result
	}
	if item.ErrorMessage != nil {
		result["error"] = *item.ErrorMessage
	}
	if item.CompletedAt != nil {
		result["completed_at"] = *item.CompletedAt
	}
	if item.CancelledAt != nil {
		result["cancelled_at"] = *item.CancelledAt
	}
	return result
}
