package e2eserver

import (
	"encoding/base64"
	"net/http"
	"sort"
	"strings"
	"time"

	"github.com/google/uuid"
)

func (s *Server) getCurrentDevice(w http.ResponseWriter, r *http.Request) {
	device, ok := s.currentDevice(r)
	if !ok {
		writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED")
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"device": device})
}

func (s *Server) listDevices(w http.ResponseWriter, r *http.Request) {
	s.mu.RLock()
	items := make([]Device, 0, len(s.devices))
	for _, device := range s.devices {
		items = append(items, device)
	}
	s.mu.RUnlock()
	sort.Slice(items, func(i, j int) bool {
		if items[i].CreatedAt.Equal(items[j].CreatedAt) {
			return items[i].ID.String() < items[j].ID.String()
		}
		return items[i].CreatedAt.Before(items[j].CreatedAt)
	})
	writeJSON(w, http.StatusOK, map[string]any{"items": items})
}

func (s *Server) upsertPC(w http.ResponseWriter, r *http.Request) {
	device, ok := s.currentDevice(r)
	if !ok || device.Type != "WINDOWS_PC" {
		writeError(w, r, http.StatusForbidden, "WINDOWS_DEVICE_REQUIRED")
		return
	}
	pcID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PC_ID")
		return
	}
	var input struct {
		Name         string `json:"name"`
		AgentVersion string `json:"agent_version"`
		KeyEpoch     int64  `json:"key_epoch"`
	}
	if err := decodeJSON(w, r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST")
		return
	}
	input.Name = strings.TrimSpace(input.Name)
	input.AgentVersion = strings.TrimSpace(input.AgentVersion)
	if input.Name == "" || len(input.Name) > 128 || input.AgentVersion == "" || len(input.AgentVersion) > 128 || input.KeyEpoch < 1 {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST")
		return
	}
	now := s.now().UTC()
	s.mu.Lock()
	existing, exists := s.pcs[pcID]
	if exists && existing.DeviceID != device.ID {
		s.mu.Unlock()
		writeError(w, r, http.StatusForbidden, "PC_NOT_OWNED")
		return
	}
	keyEpoch := input.KeyEpoch
	if existing.KeyEpoch > keyEpoch {
		keyEpoch = existing.KeyEpoch
	}
	pc := PC{
		ID: pcID, DeviceID: device.ID, Name: input.Name, AgentVersion: input.AgentVersion,
		KeyEpoch: keyEpoch, RemoteRoutingEnabled: true, LastSeenAt: &now,
	}
	s.pcs[pcID] = pc
	s.mu.Unlock()
	writeJSON(w, http.StatusOK, map[string]any{"id": pcID})
}

func (s *Server) listPCs(w http.ResponseWriter, _ *http.Request) {
	s.mu.RLock()
	items := make([]PC, 0, len(s.pcs))
	for _, pc := range s.pcs {
		items = append(items, pc)
	}
	s.mu.RUnlock()
	sort.Slice(items, func(i, j int) bool {
		if items[i].Name == items[j].Name {
			return items[i].ID.String() < items[j].ID.String()
		}
		return items[i].Name < items[j].Name
	})
	writeJSON(w, http.StatusOK, map[string]any{"items": items})
}

type implicitPCGrant struct {
	DeviceID          uuid.UUID `json:"device_id"`
	DeviceType        string    `json:"device_type"`
	DeviceName        string    `json:"device_name"`
	ExchangePublicKey []byte    `json:"exchange_public_key"`
	KeyID             string    `json:"key_id"`
	Permission        string    `json:"permission"`
}

// listImplicitPCGrants is a compatibility view required by the Windows Agent
// to obtain Android exchange keys. E2E mode has no grant state: every trusted
// Android device in the single in-memory account is an implicit controller.
func (s *Server) listImplicitPCGrants(w http.ResponseWriter, r *http.Request) {
	device, ok := s.currentDevice(r)
	if !ok || device.Type != "WINDOWS_PC" {
		writeError(w, r, http.StatusForbidden, "WINDOWS_DEVICE_REQUIRED")
		return
	}
	pcID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PC_ID")
		return
	}
	s.mu.RLock()
	pc, exists := s.pcs[pcID]
	if !exists {
		s.mu.RUnlock()
		writeError(w, r, http.StatusNotFound, "PC_NOT_FOUND")
		return
	}
	if pc.DeviceID != device.ID {
		s.mu.RUnlock()
		writeError(w, r, http.StatusForbidden, "PC_NOT_OWNED")
		return
	}
	items := make([]implicitPCGrant, 0)
	for _, candidate := range s.devices {
		if candidate.Type == "ANDROID" {
			items = append(items, implicitPCGrant{
				DeviceID: candidate.ID, DeviceType: candidate.Type, DeviceName: candidate.Name,
				ExchangePublicKey: candidate.ExchangePublicKey, KeyID: candidate.KeyID, Permission: "MANAGE",
			})
		}
	}
	s.mu.RUnlock()
	sort.Slice(items, func(i, j int) bool { return items[i].DeviceID.String() < items[j].DeviceID.String() })
	writeJSON(w, http.StatusOK, map[string]any{"pc_id": pcID, "key_epoch": pc.KeyEpoch, "items": items})
}

func (s *Server) upsertSession(w http.ResponseWriter, r *http.Request) {
	device, ok := s.currentDevice(r)
	if !ok || device.Type != "WINDOWS_PC" {
		writeError(w, r, http.StatusForbidden, "WINDOWS_DEVICE_REQUIRED")
		return
	}
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_SESSION_ID")
		return
	}
	var input struct {
		PCID               string     `json:"pc_id"`
		ShellType          string     `json:"shell_type"`
		TitleCipher        string     `json:"title_cipher"`
		State              string     `json:"state"`
		Columns            int        `json:"columns"`
		Rows               int        `json:"rows"`
		LastOutputSequence uint64     `json:"last_output_seq"`
		EndedAt            *time.Time `json:"ended_at"`
	}
	if err := decodeJSON(w, r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST")
		return
	}
	pcID, err := uuid.Parse(input.PCID)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PC_ID")
		return
	}
	title, err := decodeBase64(input.TitleCipher)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_CIPHERTEXT")
		return
	}
	input.ShellType = strings.TrimSpace(input.ShellType)
	if input.ShellType == "" || len(input.ShellType) > 64 {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST")
		return
	}
	if input.State != "RUNNING" && input.State != "INTERRUPTED" && input.State != "EXITED" {
		writeError(w, r, http.StatusBadRequest, "INVALID_SESSION_STATE")
		return
	}
	if input.Columns < 1 || input.Columns > 65535 || input.Rows < 1 || input.Rows > 65535 {
		writeError(w, r, http.StatusBadRequest, "INVALID_SIZE")
		return
	}
	now := s.now().UTC()
	s.mu.Lock()
	pc, exists := s.pcs[pcID]
	if !exists {
		s.mu.Unlock()
		writeError(w, r, http.StatusNotFound, "PC_NOT_FOUND")
		return
	}
	if pc.DeviceID != device.ID {
		s.mu.Unlock()
		writeError(w, r, http.StatusForbidden, "PC_NOT_OWNED")
		return
	}
	existing, exists := s.sessions[sessionID]
	if exists && existing.PCID != pcID {
		s.mu.Unlock()
		writeError(w, r, http.StatusConflict, "SESSION_ALREADY_EXISTS")
		return
	}
	createdAt := now
	shellType := input.ShellType
	sequence := input.LastOutputSequence
	if exists {
		createdAt = existing.CreatedAt
		shellType = existing.ShellType
		if existing.LastOutputSequence > sequence {
			sequence = existing.LastOutputSequence
		}
	}
	item := Session{
		ID: sessionID, PCID: pcID, ShellType: shellType, TitleCipher: append([]byte(nil), title...),
		State: input.State, Columns: input.Columns, Rows: input.Rows,
		LastOutputSequence: sequence, CreatedAt: createdAt, EndedAt: input.EndedAt,
	}
	s.sessions[sessionID] = item
	s.mu.Unlock()
	writeJSON(w, http.StatusOK, map[string]any{"id": sessionID})
}

func decodeBase64(value string) ([]byte, error) {
	if decoded, err := base64.RawURLEncoding.DecodeString(value); err == nil {
		return decoded, nil
	}
	return base64.StdEncoding.DecodeString(value)
}

func (s *Server) listSessions(w http.ResponseWriter, r *http.Request) {
	pcID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PC_ID")
		return
	}
	s.mu.RLock()
	_, exists := s.pcs[pcID]
	items := make([]Session, 0)
	if exists {
		for _, session := range s.sessions {
			if session.PCID == pcID {
				items = append(items, session)
			}
		}
	}
	s.mu.RUnlock()
	if !exists {
		writeError(w, r, http.StatusNotFound, "PC_NOT_FOUND")
		return
	}
	sort.Slice(items, func(i, j int) bool {
		if items[i].CreatedAt.Equal(items[j].CreatedAt) {
			return items[i].ID.String() < items[j].ID.String()
		}
		return items[i].CreatedAt.After(items[j].CreatedAt)
	})
	writeJSON(w, http.StatusOK, map[string]any{"items": items})
}

func (s *Server) acquireLease(w http.ResponseWriter, r *http.Request) {
	device, ok := s.currentDevice(r)
	if !ok || device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED")
		return
	}
	sessionID, ok := parsePathUUID(w, r, "id", "INVALID_SESSION_ID")
	if !ok {
		return
	}
	now := s.now().UTC()
	s.mu.Lock()
	session, exists := s.sessions[sessionID]
	if !exists {
		s.mu.Unlock()
		writeError(w, r, http.StatusNotFound, "SESSION_NOT_FOUND")
		return
	}
	if session.State == "EXITED" {
		s.mu.Unlock()
		writeError(w, r, http.StatusConflict, "SESSION_ENDED")
		return
	}
	if lease, active := s.activeLeaseLocked(sessionID, now); active {
		s.mu.Unlock()
		_ = lease
		writeError(w, r, http.StatusConflict, "LEASE_HELD")
		return
	}
	s.leaseVersions[sessionID]++
	lease := Lease{
		SessionID: sessionID, HolderDeviceID: device.ID, ID: uuid.New(),
		Version: s.leaseVersions[sessionID], ExpiresAt: now.Add(s.leaseTTL),
	}
	s.leases[sessionID] = lease
	s.mu.Unlock()
	writeJSON(w, http.StatusCreated, lease)
}

func (s *Server) getLease(w http.ResponseWriter, r *http.Request) {
	device, ok := s.currentDevice(r)
	if !ok {
		writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED")
		return
	}
	sessionID, ok := parsePathUUID(w, r, "id", "INVALID_SESSION_ID")
	if !ok {
		return
	}
	now := s.now().UTC()
	s.mu.Lock()
	session, exists := s.sessions[sessionID]
	if !exists {
		s.mu.Unlock()
		writeError(w, r, http.StatusNotFound, "SESSION_NOT_FOUND")
		return
	}
	if device.Type == "WINDOWS_PC" {
		pc := s.pcs[session.PCID]
		if pc.DeviceID != device.ID {
			s.mu.Unlock()
			writeError(w, r, http.StatusForbidden, "PC_NOT_OWNED")
			return
		}
	}
	lease, active := s.activeLeaseLocked(sessionID, now)
	s.mu.Unlock()
	if !active {
		writeError(w, r, http.StatusNotFound, "LEASE_NOT_FOUND")
		return
	}
	writeJSON(w, http.StatusOK, lease)
}

func (s *Server) renewLease(w http.ResponseWriter, r *http.Request) {
	device, ok := s.currentDevice(r)
	if !ok || device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED")
		return
	}
	sessionID, ok := parsePathUUID(w, r, "id", "INVALID_SESSION_ID")
	if !ok {
		return
	}
	leaseID, ok := parsePathUUID(w, r, "leaseId", "INVALID_LEASE_ID")
	if !ok {
		return
	}
	now := s.now().UTC()
	s.mu.Lock()
	lease, active := s.activeLeaseLocked(sessionID, now)
	if !active || lease.ID != leaseID || lease.HolderDeviceID != device.ID {
		s.mu.Unlock()
		writeError(w, r, http.StatusConflict, "LEASE_NOT_HELD")
		return
	}
	s.leaseVersions[sessionID]++
	lease.Version = s.leaseVersions[sessionID]
	lease.ExpiresAt = now.Add(s.leaseTTL)
	s.leases[sessionID] = lease
	s.mu.Unlock()
	writeJSON(w, http.StatusOK, lease)
}

func (s *Server) releaseLease(w http.ResponseWriter, r *http.Request) {
	device, ok := s.currentDevice(r)
	if !ok || device.Type != "ANDROID" {
		writeError(w, r, http.StatusForbidden, "ANDROID_DEVICE_REQUIRED")
		return
	}
	sessionID, ok := parsePathUUID(w, r, "id", "INVALID_SESSION_ID")
	if !ok {
		return
	}
	leaseID, ok := parsePathUUID(w, r, "leaseId", "INVALID_LEASE_ID")
	if !ok {
		return
	}
	now := s.now().UTC()
	s.mu.Lock()
	lease, active := s.activeLeaseLocked(sessionID, now)
	if !active || lease.ID != leaseID || lease.HolderDeviceID != device.ID {
		s.mu.Unlock()
		writeError(w, r, http.StatusConflict, "LEASE_NOT_HELD")
		return
	}
	delete(s.leases, sessionID)
	s.mu.Unlock()
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) forceReleaseLease(w http.ResponseWriter, r *http.Request) {
	device, ok := s.currentDevice(r)
	if !ok || device.Type != "WINDOWS_PC" {
		writeError(w, r, http.StatusForbidden, "WINDOWS_DEVICE_REQUIRED")
		return
	}
	sessionID, ok := parsePathUUID(w, r, "id", "INVALID_SESSION_ID")
	if !ok {
		return
	}
	now := s.now().UTC()
	s.mu.Lock()
	session, exists := s.sessions[sessionID]
	if !exists {
		s.mu.Unlock()
		writeError(w, r, http.StatusNotFound, "SESSION_NOT_FOUND")
		return
	}
	pc := s.pcs[session.PCID]
	if pc.DeviceID != device.ID {
		s.mu.Unlock()
		writeError(w, r, http.StatusForbidden, "PC_NOT_OWNED")
		return
	}
	_, released := s.activeLeaseLocked(sessionID, now)
	if released {
		delete(s.leases, sessionID)
	}
	s.mu.Unlock()
	writeJSON(w, http.StatusOK, map[string]bool{"released": released})
}

func (s *Server) activeLeaseLocked(sessionID uuid.UUID, now time.Time) (Lease, bool) {
	lease, exists := s.leases[sessionID]
	if !exists {
		return Lease{}, false
	}
	if !now.Before(lease.ExpiresAt) {
		delete(s.leases, sessionID)
		return Lease{}, false
	}
	return lease, true
}

func parsePathUUID(w http.ResponseWriter, r *http.Request, name, code string) (uuid.UUID, bool) {
	id, err := uuid.Parse(r.PathValue(name))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, code)
		return uuid.Nil, false
	}
	return id, true
}
