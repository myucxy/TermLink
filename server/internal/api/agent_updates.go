package api

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"net/http"
	"regexp"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/agentupdate"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	agentUpdateLeaseDuration = 30 * time.Minute
	agentUpdateLifetime      = 2 * time.Hour
)

var agentUpdateErrorCodePattern = regexp.MustCompile(`^[A-Z0-9_]{1,64}$`)

type agentUpdateStore interface {
	CreateAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, store.AgentUpdateCreateInput) (store.AgentUpdateRecord, error)
	GetAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.AgentUpdateRecord, error)
	GetActiveAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) (*store.AgentUpdateRecord, error)
	CancelAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.AgentUpdateRecord, error)
	ClaimAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, time.Duration) (*store.AgentUpdateRecord, string, error)
	RenewAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, time.Duration) (store.AgentUpdateRecord, error)
	ProgressAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, store.AgentUpdateProgressInput) (store.AgentUpdateRecord, error)
	CompleteAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, store.AgentUpdateCompleteInput) (store.AgentUpdateRecord, error)
}

func (a *API) agentUpdateRepository() agentUpdateStore {
	if a.agentUpdates != nil {
		return a.agentUpdates
	}
	if a.Store != nil {
		return a.Store
	}
	return nil
}

func (a *API) getAgentUpdateEligibility(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	pcID, ok := parseAgentUpdatePCID(w, r)
	if !ok {
		return
	}
	pc, sessions, ok := a.agentUpdatePCContext(w, r, pcID)
	if !ok {
		return
	}
	currentBuild, err := agentupdate.WindowsAgentBuild(pc.AgentVersion)
	if err != nil {
		writeJSON(w, http.StatusOK, map[string]any{
			"eligible": false, "reason_code": "AGENT_VERSION_UNSUPPORTED",
			"current_version": pc.AgentVersion, "active_session_count": sessions,
		})
		return
	}
	if !pc.Online || !pc.RemoteRoutingEnabled || pc.Platform != store.PCPlatformWindows ||
		!store.HasPCCapability(pc.Capabilities, store.PCCapabilityRemoteAgentUpdateV1) {
		reason := "REMOTE_UPDATE_UNSUPPORTED"
		if !pc.Online {
			reason = "PC_OFFLINE"
		} else if !pc.RemoteRoutingEnabled {
			reason = "REMOTE_UPDATE_DISABLED"
		}
		writeJSON(w, http.StatusOK, map[string]any{
			"eligible": false, "reason_code": reason, "current_version": pc.AgentVersion,
			"current_build": currentBuild, "active_session_count": sessions,
		})
		return
	}
	if a.AgentUpdateCatalog == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATE_CATALOG_UNAVAILABLE", agentupdate.ErrUnavailable)
		return
	}
	offer, err := a.AgentUpdateCatalog.LatestWindowsStable(r.Context(), currentBuild)
	if err != nil {
		code := "AGENT_UPDATE_CATALOG_UNAVAILABLE"
		if errors.Is(err, agentupdate.ErrInvalidArtifact) {
			code = "AGENT_UPDATE_ARTIFACT_INVALID"
		}
		writeError(w, r, http.StatusServiceUnavailable, code, err)
		return
	}
	if !offer.Available {
		writeJSON(w, http.StatusOK, map[string]any{
			"eligible": false, "reason_code": "UPDATE_NOT_AVAILABLE",
			"current_version": pc.AgentVersion, "current_build": currentBuild,
			"active_session_count": sessions,
		})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"eligible": true, "current_version": pc.AgentVersion, "current_build": currentBuild,
		"target_version": offer.Version, "target_build": offer.Build, "required": offer.Required,
		"release_notes": offer.ReleaseNotes, "active_session_count": sessions,
	})
}

func (a *API) createAgentUpdate(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	pcID, ok := parseAgentUpdatePCID(w, r)
	if !ok {
		return
	}
	var input struct {
		ExpectedTargetBuild int64 `json:"expected_target_build"`
	}
	if err := decodeJSON(r, &input); err != nil || input.ExpectedTargetBuild < 1 {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	pc, sessions, ok := a.agentUpdatePCContext(w, r, pcID)
	if !ok {
		return
	}
	currentBuild, err := agentupdate.WindowsAgentBuild(pc.AgentVersion)
	if err != nil || !pc.Online || !pc.RemoteRoutingEnabled || pc.Platform != store.PCPlatformWindows ||
		!store.HasPCCapability(pc.Capabilities, store.PCCapabilityRemoteAgentUpdateV1) {
		writeError(w, r, http.StatusConflict, "REMOTE_UPDATE_UNAVAILABLE", store.ErrConflict)
		return
	}
	if a.AgentUpdateCatalog == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATE_CATALOG_UNAVAILABLE", agentupdate.ErrUnavailable)
		return
	}
	offer, err := a.AgentUpdateCatalog.LatestWindowsStable(r.Context(), currentBuild)
	if err != nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATE_CATALOG_UNAVAILABLE", err)
		return
	}
	if !offer.Available {
		writeError(w, r, http.StatusConflict, "UPDATE_NOT_AVAILABLE", store.ErrConflict)
		return
	}
	if offer.Build != input.ExpectedTargetBuild {
		writeError(w, r, http.StatusConflict, "UPDATE_TARGET_CHANGED", store.ErrConflict)
		return
	}
	repository := a.agentUpdateRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATES_UNAVAILABLE", errors.New("Agent update store unavailable"))
		return
	}
	item, err := repository.CreateAgentUpdate(r.Context(), p.UserID, p.Device.ID, pcID, store.AgentUpdateCreateInput{
		CurrentVersion: pc.AgentVersion, CurrentBuild: currentBuild,
		TargetVersion: offer.Version, TargetBuild: offer.Build,
		ArtifactURL: offer.DownloadURL, ArtifactFilename: offer.ArtifactFilename,
		ArtifactSizeBytes: offer.SizeBytes, ArtifactSHA256: offer.SHA256,
		ArtifactSignerFingerprint: offer.SignerFingerprint,
		ExpiresAt:                 a.now().Add(agentUpdateLifetime),
	})
	if errors.Is(err, store.ErrConflict) {
		writeError(w, r, http.StatusConflict, "AGENT_UPDATE_ALREADY_ACTIVE", err)
		return
	}
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	view := agentUpdateView(item, false)
	view["active_session_count"] = sessions
	writeJSON(w, http.StatusCreated, map[string]any{"agent_update": view})
}

func (a *API) claimAgentUpdate(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if !store.IsPCAgentDeviceType(p.Device.Type) {
		writeError(w, r, http.StatusForbidden, "WINDOWS_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	pcID, ok := parseAgentUpdatePCID(w, r)
	if !ok {
		return
	}
	repository := a.agentUpdateRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATES_UNAVAILABLE", errors.New("Agent update store unavailable"))
		return
	}
	item, lease, err := repository.ClaimAgentUpdate(r.Context(), p.UserID, p.Device.ID, pcID, agentUpdateLeaseDuration)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if item == nil {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"agent_update": agentUpdateView(*item, true), "lease_token": lease,
	})
}

func (a *API) getAgentUpdate(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	pcID, updateID, ok := parseAgentUpdateIDs(w, r)
	if !ok {
		return
	}
	repository := a.agentUpdateRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATES_UNAVAILABLE", errors.New("Agent update store unavailable"))
		return
	}
	item, err := repository.GetAgentUpdate(r.Context(), p.UserID, p.Device.ID, pcID, updateID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"agent_update": agentUpdateView(item, false)})
}

func (a *API) getActiveAgentUpdate(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	pcID, ok := parseAgentUpdatePCID(w, r)
	if !ok {
		return
	}
	repository := a.agentUpdateRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATES_UNAVAILABLE", errors.New("Agent update store unavailable"))
		return
	}
	item, err := repository.GetActiveAgentUpdate(r.Context(), p.UserID, p.Device.ID, pcID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if item == nil {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"agent_update": agentUpdateView(*item, false)})
}

func (a *API) cancelAgentUpdate(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	pcID, updateID, ok := parseAgentUpdateIDs(w, r)
	if !ok {
		return
	}
	repository := a.agentUpdateRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATES_UNAVAILABLE", errors.New("Agent update store unavailable"))
		return
	}
	item, err := repository.CancelAgentUpdate(r.Context(), p.UserID, p.Device.ID, pcID, updateID)
	if errors.Is(err, store.ErrConflict) {
		writeError(w, r, http.StatusConflict, "AGENT_UPDATE_CANNOT_CANCEL", err)
		return
	}
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"agent_update": agentUpdateView(item, false)})
}

func (a *API) renewAgentUpdate(w http.ResponseWriter, r *http.Request) {
	p, pcID, updateID, leaseHash, ok := a.agentUpdateLeaseRequest(w, r)
	if !ok {
		return
	}
	item, err := a.agentUpdateRepository().RenewAgentUpdate(
		r.Context(), p.UserID, p.Device.ID, pcID, updateID, leaseHash, agentUpdateLeaseDuration)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"agent_update": agentUpdateView(item, true)})
}

func (a *API) progressAgentUpdate(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if !store.IsPCAgentDeviceType(p.Device.Type) {
		writeError(w, r, http.StatusForbidden, "WINDOWS_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	pcID, updateID, ok := parseAgentUpdateIDs(w, r)
	if !ok {
		return
	}
	var input struct {
		LeaseToken         string `json:"lease_token"`
		Phase              string `json:"phase"`
		ProgressPercent    int    `json:"progress_percent"`
		ActiveSessionCount *int   `json:"active_session_count"`
	}
	if err := decodeJSON(r, &input); err != nil || !knownAgentUpdatePhase(input.Phase) ||
		input.ProgressPercent < 0 || input.ProgressPercent > 100 ||
		(input.ActiveSessionCount != nil && *input.ActiveSessionCount < 0) {
		writeError(w, r, http.StatusBadRequest, "INVALID_AGENT_UPDATE_PROGRESS", err)
		return
	}
	leaseHash, err := parseAgentUpdateLease(input.LeaseToken)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LEASE_TOKEN", err)
		return
	}
	repository := a.agentUpdateRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATES_UNAVAILABLE", errors.New("Agent update store unavailable"))
		return
	}
	item, err := repository.ProgressAgentUpdate(r.Context(), p.UserID, p.Device.ID, pcID, updateID, leaseHash,
		store.AgentUpdateProgressInput{Phase: input.Phase, ProgressPercent: input.ProgressPercent, ActiveSessionCount: input.ActiveSessionCount})
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"agent_update": agentUpdateView(item, true)})
}

func (a *API) completeAgentUpdate(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if !store.IsPCAgentDeviceType(p.Device.Type) {
		writeError(w, r, http.StatusForbidden, "WINDOWS_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	pcID, updateID, ok := parseAgentUpdateIDs(w, r)
	if !ok {
		return
	}
	var input struct {
		LeaseToken           string  `json:"lease_token"`
		Status               string  `json:"status"`
		ErrorCode            *string `json:"error_code"`
		ErrorDetail          *string `json:"error_detail"`
		ObservedAgentVersion *string `json:"observed_agent_version"`
		ObservedAgentBuild   *int64  `json:"observed_agent_build"`
	}
	if err := decodeJSON(r, &input); err != nil || !knownAgentUpdateCompletion(input.Status) ||
		(input.ErrorCode != nil && !agentUpdateErrorCodePattern.MatchString(*input.ErrorCode)) ||
		(input.ErrorDetail != nil && (len(*input.ErrorDetail) < 1 || len(*input.ErrorDetail) > 1024)) ||
		!validObservedAgentIdentity(input.ObservedAgentVersion, input.ObservedAgentBuild) {
		writeError(w, r, http.StatusBadRequest, "INVALID_AGENT_UPDATE_RESULT", err)
		return
	}
	leaseHash, err := parseAgentUpdateLease(input.LeaseToken)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LEASE_TOKEN", err)
		return
	}
	repository := a.agentUpdateRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATES_UNAVAILABLE", errors.New("Agent update store unavailable"))
		return
	}
	item, err := repository.CompleteAgentUpdate(r.Context(), p.UserID, p.Device.ID, pcID, updateID, leaseHash,
		store.AgentUpdateCompleteInput{
			Status: input.Status, ErrorCode: input.ErrorCode, ErrorDetail: input.ErrorDetail,
			ObservedAgentVersion: input.ObservedAgentVersion, ObservedAgentBuild: input.ObservedAgentBuild,
		})
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"agent_update": agentUpdateView(item, true)})
}

func validObservedAgentIdentity(version *string, build *int64) bool {
	if version == nil || build == nil {
		return version == nil && build == nil
	}
	if len(*version) < 1 || len(*version) > 64 || *build < 0 {
		return false
	}
	parsed, err := agentupdate.WindowsAgentBuild(*version)
	return err == nil && parsed == *build
}

func (a *API) agentUpdateLeaseRequest(
	w http.ResponseWriter,
	r *http.Request,
) (auth.Principal, uuid.UUID, uuid.UUID, [32]byte, bool) {
	p := principal(r)
	if !store.IsPCAgentDeviceType(p.Device.Type) {
		writeError(w, r, http.StatusForbidden, "WINDOWS_AGENT_REQUIRED", store.ErrDenied)
		return auth.Principal{}, uuid.Nil, uuid.Nil, [32]byte{}, false
	}
	pcID, updateID, ok := parseAgentUpdateIDs(w, r)
	if !ok {
		return auth.Principal{}, uuid.Nil, uuid.Nil, [32]byte{}, false
	}
	var input struct {
		LeaseToken string `json:"lease_token"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return auth.Principal{}, uuid.Nil, uuid.Nil, [32]byte{}, false
	}
	leaseHash, err := parseAgentUpdateLease(input.LeaseToken)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LEASE_TOKEN", err)
		return auth.Principal{}, uuid.Nil, uuid.Nil, [32]byte{}, false
	}
	if a.agentUpdateRepository() == nil {
		writeError(w, r, http.StatusServiceUnavailable, "AGENT_UPDATES_UNAVAILABLE", errors.New("Agent update store unavailable"))
		return auth.Principal{}, uuid.Nil, uuid.Nil, [32]byte{}, false
	}
	return p, pcID, updateID, leaseHash, true
}

func (a *API) agentUpdatePCContext(
	w http.ResponseWriter,
	r *http.Request,
	pcID uuid.UUID,
) (store.PCRecord, int, bool) {
	p := principal(r)
	pcs, err := a.Store.ListPCs(r.Context(), p.UserID, p.Device.ID)
	if err != nil {
		writeStoreError(w, r, err)
		return store.PCRecord{}, 0, false
	}
	var pc *store.PCRecord
	for index := range pcs {
		if pcs[index].ID == pcID {
			pc = &pcs[index]
			break
		}
	}
	if pc == nil {
		writeError(w, r, http.StatusForbidden, "FORBIDDEN", store.ErrDenied)
		return store.PCRecord{}, 0, false
	}
	sessions, err := a.Store.ListSessionsIncludingEnded(r.Context(), p.UserID, p.Device.ID, pcID)
	if err != nil {
		writeStoreError(w, r, err)
		return store.PCRecord{}, 0, false
	}
	active := 0
	for _, session := range sessions {
		if session.EndedAt == nil && session.State != "EXITED" {
			active++
		}
	}
	return *pc, active, true
}

func parseAgentUpdatePCID(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	value, err := uuid.Parse(r.PathValue("pcId"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PC_ID", err)
		return uuid.Nil, false
	}
	return value, true
}

func parseAgentUpdateIDs(w http.ResponseWriter, r *http.Request) (uuid.UUID, uuid.UUID, bool) {
	pcID, ok := parseAgentUpdatePCID(w, r)
	if !ok {
		return uuid.Nil, uuid.Nil, false
	}
	updateID, err := uuid.Parse(r.PathValue("updateId"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_AGENT_UPDATE_ID", err)
		return uuid.Nil, uuid.Nil, false
	}
	return pcID, updateID, true
}

func parseAgentUpdateLease(value string) ([32]byte, error) {
	decoded, err := base64.RawURLEncoding.DecodeString(strings.TrimSpace(value))
	if err != nil || len(decoded) != 32 {
		return [32]byte{}, errors.New("invalid Agent update lease")
	}
	return sha256.Sum256(decoded), nil
}

func agentUpdateView(item store.AgentUpdateRecord, includeArtifact bool) map[string]any {
	result := map[string]any{
		"id": item.ID, "pc_id": item.PCID, "requester_device_id": item.RequesterDeviceID,
		"current_version": item.CurrentVersion, "current_build": item.CurrentBuild,
		"target_version": item.TargetVersion, "target_build": item.TargetBuild,
		"status": item.Status, "phase": item.Phase, "progress_percent": item.ProgressPercent,
		"attempt_count": item.AttemptCount, "expires_at": item.ExpiresAt,
		"created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
	if includeArtifact {
		result["artifact_url"] = item.ArtifactURL
		result["artifact_filename"] = item.ArtifactFilename
		result["artifact_size_bytes"] = item.ArtifactSizeBytes
		result["artifact_sha256"] = hex.EncodeToString(item.ArtifactSHA256)
		if item.ArtifactSignerFingerprint != nil {
			result["artifact_signer_fingerprint"] = *item.ArtifactSignerFingerprint
		}
	}
	if item.ActiveSessionCount != nil {
		result["active_session_count"] = *item.ActiveSessionCount
	}
	if item.LeaseExpiresAt != nil && item.Status == store.AgentUpdateProcessing {
		result["lease_expires_at"] = *item.LeaseExpiresAt
	}
	if item.ErrorCode != nil {
		result["error_code"] = *item.ErrorCode
	}
	if item.ErrorDetail != nil {
		result["error_detail"] = *item.ErrorDetail
	}
	if item.ObservedAgentVersion != nil {
		result["observed_agent_version"] = *item.ObservedAgentVersion
	}
	if item.ObservedAgentBuild != nil {
		result["observed_agent_build"] = *item.ObservedAgentBuild
	}
	if item.StartedAt != nil {
		result["started_at"] = *item.StartedAt
	}
	if item.CompletedAt != nil {
		result["completed_at"] = *item.CompletedAt
	}
	if item.CancelledAt != nil {
		result["cancelled_at"] = *item.CancelledAt
	}
	return result
}

func knownAgentUpdatePhase(value string) bool {
	switch value {
	case store.AgentUpdateRequested, store.AgentUpdatePrechecking, store.AgentUpdateDownloading,
		store.AgentUpdateVerified, store.AgentUpdateDrainingSessions, store.AgentUpdateInstalling,
		store.AgentUpdateStartingNewAgent, store.AgentUpdateVerifyingConnection, store.AgentUpdateRollingBack:
		return true
	default:
		return false
	}
}

func knownAgentUpdateCompletion(value string) bool {
	switch value {
	case store.AgentUpdateSucceeded, store.AgentUpdateFailed, store.AgentUpdateRolledBack, store.AgentUpdateRollbackFailed:
		return true
	default:
		return false
	}
}
