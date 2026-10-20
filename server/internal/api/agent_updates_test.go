package api

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/presence"
	"github.com/termlink/termlink/server/internal/store"
)

func TestGetActiveAgentUpdateRestoresAndroidPollingWithoutArtifactSecrets(t *testing.T) {
	accountID, pcID, androidID := uuid.New(), uuid.New(), uuid.New()
	now := time.Date(2026, 8, 26, 12, 0, 0, 0, time.UTC)
	authentication, privateKey := fileOperationTestAuth(t, accountID, androidID, "ANDROID", now)
	activeSessions := 2
	repository := &fakeAgentUpdateStore{active: &store.AgentUpdateRecord{
		ID: uuid.New(), AccountID: accountID, PCID: pcID, RequesterDeviceID: androidID,
		CurrentVersion: "0.1.31.37", CurrentBuild: 37,
		TargetVersion: "0.1.32.38", TargetBuild: 38,
		ArtifactURL:      "https://updates.example/TermLink-Setup.exe",
		ArtifactFilename: "TermLink-Setup.exe", ArtifactSizeBytes: 123,
		ArtifactSHA256: make([]byte, 32), Status: store.AgentUpdateProcessing,
		Phase: store.AgentUpdateDownloading, ProgressPercent: 35,
		ActiveSessionCount: &activeSessions, ExpiresAt: now.Add(time.Hour),
		CreatedAt: now.Add(-time.Minute), UpdatedAt: now,
	}}
	service := &API{agentUpdates: repository, Clock: func() time.Time { return now }}
	path := "/v1/pcs/" + pcID.String() + "/agent-updates/active"
	request := signedFileOperationRequest(t, privateKey, androidID, now, http.MethodGet, path, nil)
	request.SetPathValue("pcId", pcID.String())
	response := httptest.NewRecorder()
	authentication.Authenticate(http.HandlerFunc(service.getActiveAgentUpdate)).ServeHTTP(response, request)

	if response.Code != http.StatusOK || repository.activeCalls != 1 ||
		repository.activeRequester != androidID || repository.activePC != pcID {
		t.Fatalf("active recovery status=%d calls=%d requester=%s pc=%s body=%s",
			response.Code, repository.activeCalls, repository.activeRequester, repository.activePC, response.Body.String())
	}
	body := response.Body.String()
	if !strings.Contains(body, `"phase":"DOWNLOADING"`) ||
		!strings.Contains(body, `"progress_percent":35`) ||
		strings.Contains(body, "artifact_url") || strings.Contains(body, "artifact_sha256") {
		t.Fatalf("active recovery exposed the wrong view: %s", body)
	}
}

func TestGetActiveAgentUpdateReturnsNoContentAndRequiresAndroid(t *testing.T) {
	accountID, pcID := uuid.New(), uuid.New()
	now := time.Date(2026, 8, 26, 12, 0, 0, 0, time.UTC)
	repository := &fakeAgentUpdateStore{}
	service := &API{agentUpdates: repository, Clock: func() time.Time { return now }}
	path := "/v1/pcs/" + pcID.String() + "/agent-updates/active"

	androidID := uuid.New()
	androidAuth, androidKey := fileOperationTestAuth(t, accountID, androidID, "ANDROID", now)
	androidRequest := signedFileOperationRequest(t, androidKey, androidID, now, http.MethodGet, path, nil)
	androidRequest.SetPathValue("pcId", pcID.String())
	androidResponse := httptest.NewRecorder()
	androidAuth.Authenticate(http.HandlerFunc(service.getActiveAgentUpdate)).ServeHTTP(androidResponse, androidRequest)
	if androidResponse.Code != http.StatusNoContent || androidResponse.Body.Len() != 0 {
		t.Fatalf("empty active recovery status=%d body=%s", androidResponse.Code, androidResponse.Body.String())
	}

	windowsID := uuid.New()
	windowsAuth, windowsKey := fileOperationTestAuth(t, accountID, windowsID, "WINDOWS_PC", now)
	windowsRequest := signedFileOperationRequest(t, windowsKey, windowsID, now, http.MethodGet, path, nil)
	windowsRequest.SetPathValue("pcId", pcID.String())
	windowsResponse := httptest.NewRecorder()
	windowsAuth.Authenticate(http.HandlerFunc(service.getActiveAgentUpdate)).ServeHTTP(windowsResponse, windowsRequest)
	if windowsResponse.Code != http.StatusForbidden ||
		!strings.Contains(windowsResponse.Body.String(), "ANDROID_DEVICE_REQUIRED") || repository.activeCalls != 1 {
		t.Fatalf("Windows active recovery status=%d calls=%d body=%s",
			windowsResponse.Code, repository.activeCalls, windowsResponse.Body.String())
	}
}

func TestObservedAgentIdentityRequiresMatchingBuild(t *testing.T) {
	version := "0.1.32.38"
	matching, wrong := int64(38), int64(37)
	if !validObservedAgentIdentity(&version, &matching) {
		t.Fatal("matching observed Agent version/build was rejected")
	}
	if validObservedAgentIdentity(&version, &wrong) ||
		validObservedAgentIdentity(&version, nil) ||
		validObservedAgentIdentity(nil, &matching) {
		t.Fatal("an incomplete or mismatched observed Agent identity was accepted")
	}
	if !validObservedAgentIdentity(nil, nil) {
		t.Fatal("a pre-install failure without an observed Agent identity was rejected")
	}
}

func TestInstalledAgentConfirmationRequiresWindowsIdentityAndLiveConnection(t *testing.T) {
	accountID, pcID, deviceID := uuid.New(), uuid.New(), uuid.New()
	now := time.Now().UTC()
	for _, scenario := range []struct {
		role     string
		online   bool
		build    int
		expected int
	}{
		{"WINDOWS_PC", false, 141, http.StatusConflict},
		{"ANDROID", true, 141, http.StatusForbidden},
		{"WINDOWS_PC", true, 140, http.StatusBadRequest},
		{"WINDOWS_PC", true, 141, http.StatusOK},
	} {
		authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, scenario.role, now)
		repository := &fakeAgentUpdateStore{active: &store.AgentUpdateRecord{ID: uuid.New(), PCID: pcID, Status: store.AgentUpdateSucceeded, TargetBuild: 141, ArtifactURL: "https://private.example/setup.exe"}}
		registry := presence.New(time.Minute)
		if scenario.online {
			registry.Register(accountID, deviceID, uuid.New(), "pc")
		}
		service := &API{agentUpdates: repository, Presence: registry}
		path := "/v1/pcs/" + pcID.String() + "/agent-updates/reconcile"
		body := []byte(fmt.Sprintf(`{"observed_agent_version":"0.3.11.141","observed_agent_build":%d}`, scenario.build))
		request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodPost, path, body)
		request.SetPathValue("pcId", pcID.String())
		response := httptest.NewRecorder()
		authentication.Authenticate(http.HandlerFunc(service.reconcileInstalledAgentUpdates)).ServeHTTP(response, request)
		if response.Code != scenario.expected {
			t.Fatalf("role=%s online=%v build=%d status=%d want=%d", scenario.role, scenario.online, scenario.build, response.Code, scenario.expected)
		}
		if response.Code == http.StatusOK {
			if repository.activeCalls != 1 || !strings.Contains(response.Body.String(), `"SUCCEEDED"`) || strings.Contains(response.Body.String(), "artifact_url") {
				t.Fatal("wrong confirmation view")
			}
		} else if repository.activeCalls != 0 {
			t.Fatal("rejected confirmation reached store")
		}
	}
}

func TestRemoteAgentUpdateBootstrapBuildBoundary(t *testing.T) {
	if minimumRemoteAgentUpdateBuild != 47 {
		t.Fatalf("minimum remote Agent update build=%d, want 47", minimumRemoteAgentUpdateBuild)
	}
	for _, build := range []int64{0, 45, 46} {
		if remoteAgentUpdateBootstrapCapable(build) {
			t.Fatalf("known broken downloader build %d was accepted", build)
		}
	}
	for _, build := range []int64{47, 48, 100} {
		if !remoteAgentUpdateBootstrapCapable(build) {
			t.Fatalf("fixed downloader build %d was rejected", build)
		}
	}
}

type fakeAgentUpdateStore struct {
	active          *store.AgentUpdateRecord
	activeCalls     int
	activeRequester uuid.UUID
	activePC        uuid.UUID
}

func (fake *fakeAgentUpdateStore) CreateAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, store.AgentUpdateCreateInput) (store.AgentUpdateRecord, error) {
	return store.AgentUpdateRecord{}, nil
}

func (fake *fakeAgentUpdateStore) GetAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.AgentUpdateRecord, error) {
	return store.AgentUpdateRecord{}, nil
}

func (fake *fakeAgentUpdateStore) GetActiveAgentUpdate(_ context.Context, _ uuid.UUID, requesterID, pcID uuid.UUID) (*store.AgentUpdateRecord, error) {
	fake.activeCalls++
	fake.activeRequester = requesterID
	fake.activePC = pcID
	return fake.active, nil
}

func (fake *fakeAgentUpdateStore) CancelAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.AgentUpdateRecord, error) {
	return store.AgentUpdateRecord{}, nil
}

func (fake *fakeAgentUpdateStore) ClaimAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, time.Duration) (*store.AgentUpdateRecord, string, error) {
	return nil, "", nil
}

func (fake *fakeAgentUpdateStore) ReconcileInstalledAgentUpdates(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, string, int64, uuid.UUID) ([]store.AgentUpdateRecord, error) {
	fake.activeCalls++
	if fake.active == nil {
		return nil, nil
	}
	return []store.AgentUpdateRecord{*fake.active}, nil
}

func (fake *fakeAgentUpdateStore) RenewAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, time.Duration) (store.AgentUpdateRecord, error) {
	return store.AgentUpdateRecord{}, nil
}

func (fake *fakeAgentUpdateStore) ProgressAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, store.AgentUpdateProgressInput) (store.AgentUpdateRecord, error) {
	return store.AgentUpdateRecord{}, nil
}

func (fake *fakeAgentUpdateStore) CompleteAgentUpdate(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID, [32]byte, store.AgentUpdateCompleteInput) (store.AgentUpdateRecord, error) {
	return store.AgentUpdateRecord{}, nil
}
