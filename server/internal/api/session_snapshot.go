package api

import (
	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
	"net/http"
)

func (a *API) applyHostSessionDeletions(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	pc, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_PC_ID", err)
		return
	}
	if !store.IsPCAgentDeviceType(p.Device.Type) {
		writeError(w, r, 403, "PC_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	var input struct {
		SessionIDs []uuid.UUID `json:"session_ids"`
	}
	if err = decodeJSON(r, &input); err == nil {
		err = store.ValidateSessionCleanup(input.SessionIDs)
	}
	if err != nil {
		writeError(w, r, 400, "INVALID_SESSION_CLEANUP", err)
		return
	}
	if err = a.Store.ApplyHostSessionDeletions(r.Context(), p.UserID, p.Device.ID, pc, input.SessionIDs, traceID(r.Context())); err != nil {
		writeStoreError(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) replaceSessionSnapshot(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	pcID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_PC_ID", err)
		return
	}
	if !store.IsPCAgentDeviceType(p.Device.Type) {
		writeError(w, r, 403, "PC_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	var input struct {
		Sessions []store.SessionSnapshotEntry `json:"sessions"`
	}
	if err = decodeJSON(r, &input); err == nil {
		err = store.ValidateSessionSnapshot(input.Sessions)
	}
	if err != nil {
		writeError(w, r, 400, "INVALID_SESSION_SNAPSHOT", err)
		return
	}
	if err = a.Store.ReplaceSessionSnapshot(r.Context(), p.UserID, p.Device.ID, pcID, input.Sessions); err != nil {
		writeStoreError(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}
