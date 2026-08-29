package api

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/privacyexport"
)

// PrivacyExportDownloader is the only privacy-export capability exposed at
// the HTTP boundary. Implementations return decrypted JSON and never expose
// storage object keys or ciphertext.
type PrivacyExportDownloader interface {
	Download(context.Context, uuid.UUID, uuid.UUID) ([]byte, error)
}

func (a *API) downloadUserPortalDataExport(w http.ResponseWriter, r *http.Request) {
	accountID := principal(r).UserID
	if accountID == uuid.Nil {
		writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED", errors.New("authenticated account is required"))
		return
	}

	exportID, err := uuid.Parse(r.PathValue("exportId"))
	if err != nil {
		writeDataExportNotFound(w, r)
		return
	}
	if a.DataExportDownloader == nil {
		writeDataExportUnavailable(w, r, errors.New("data export service is unavailable"), "data_export_downloader_unavailable")
		return
	}

	body, err := a.DataExportDownloader.Download(r.Context(), accountID, exportID)
	if err != nil {
		switch {
		case errors.Is(err, privacyexport.ErrExportUnavailable):
			writeDataExportNotFound(w, r)
		case errors.Is(err, privacyexport.ErrExportIntegrity):
			writeDataExportUnavailable(w, r, err, "data_export_integrity_failed")
		default:
			writeDataExportUnavailable(w, r, err, "data_export_backend_failed")
		}
		return
	}
	if !json.Valid(body) {
		writeDataExportUnavailable(w, r, privacyexport.ErrExportIntegrity, "data_export_integrity_failed")
		return
	}

	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Content-Disposition", `attachment; filename="termlink-data-export.json"`)
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(body)
}

func writeDataExportNotFound(w http.ResponseWriter, r *http.Request) {
	writeError(w, r, http.StatusNotFound, "DATA_EXPORT_NOT_FOUND", privacyexport.ErrExportUnavailable)
}

func writeDataExportUnavailable(w http.ResponseWriter, r *http.Request, err error, errorClass string) {
	logAPIFailure(r.Context(), slog.LevelError, "privacy export download failed", errorClass, err)
	writeError(w, r, http.StatusServiceUnavailable, "DATA_EXPORT_UNAVAILABLE", err)
}
