package api

import (
	"context"
	"net/http"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type agentRoutingStore interface {
	GetAgentPCRemoteRouting(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) (store.UserPortalPCControlPolicy, error)
	SetAgentPCRemoteRouting(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, bool, int64, string) (store.UserPortalPCControlPolicy, error)
}

func (a *API) agentRoutingRepository() agentRoutingStore {
	if a.agentRouting != nil {
		return a.agentRouting
	}
	if a.Store != nil {
		return a.Store
	}
	return nil
}

func (a *API) agentPCRemoteRouting(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if !store.IsPCAgentDeviceType(p.Device.Type) {
		writeError(w, r, http.StatusForbidden, "PC_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	pcID, ok := parseAgentUpdatePCID(w, r)
	if !ok {
		return
	}
	repository := a.agentRoutingRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "PC_ROUTING_UNAVAILABLE", store.ErrDenied)
		return
	}
	var policy store.UserPortalPCControlPolicy
	var err error
	if r.Method == http.MethodGet {
		policy, err = repository.GetAgentPCRemoteRouting(r.Context(), p.UserID, p.Device.ID, pcID)
	} else {
		var input struct {
			Enabled         *bool `json:"enabled"`
			ExpectedVersion int64 `json:"expected_version"`
		}
		if err := decodeJSON(r, &input); err != nil || input.Enabled == nil || input.ExpectedVersion < 1 {
			writeError(w, r, http.StatusBadRequest, "INVALID_PC_ROUTING_POLICY", err)
			return
		}
		policy, err = repository.SetAgentPCRemoteRouting(r.Context(), p.UserID, p.Device.ID, pcID,
			*input.Enabled, input.ExpectedVersion, traceID(r.Context()))
	}
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"policy": userPortalPCControlPolicyView(policy)})
}
