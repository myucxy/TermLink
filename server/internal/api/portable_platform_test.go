package api

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/agentupdate"
	"github.com/termlink/termlink/server/internal/store"
)

func TestPortableMetadataAllowsOnlyImplementedControlFeatures(t *testing.T) {
	for _, platform := range []string{"macos", "linux"} {
		for _, caps := range [][]string{{}, {store.PCCapabilityAIAgentV1}, {store.PCCapabilityRemoteAgentUpdateV1}, {store.PCCapabilityAIAgentV1, store.PCCapabilityRemoteAgentUpdateV1}} {
			_, _, _, got, err := normalizePCAgentMetadata(store.DeviceTypePCAgent, platform, "arm64", "test", caps,
				false, store.HasPCCapability(caps, store.PCCapabilityRemoteAgentUpdateV1), nil, nil)
			if err != nil || !reflect.DeepEqual(got, caps) {
				t.Fatalf("%s caps=%v got=%v err=%v", platform, caps, got, err)
			}
		}
		for _, caps := range [][]string{{"ai_agent_v1", "file_operations_v1"}, {"ai_agent_v1", "terminal_snapshot_v1"}, {"ai_agent_v1", "future_unknown_feature"}} {
			if _, _, _, _, err := normalizePCAgentMetadata(store.DeviceTypePCAgent, platform, "arm64", "test", caps, false, false, nil, nil); err == nil {
				t.Fatalf("unsupported non-terminal data plane accepted: %v", caps)
			}
		}
	}
}

func TestPortableShellDeclarationsRespectPCPlatform(t *testing.T) {
	for _, platform := range []string{"macos", "linux", "windows"} {
		for _, shell := range store.ManagedShellTypes() {
			commands := []store.TerminalCommandCapability{{ID: shell, ShellType: shell, DisplayName: shell, Source: "detected"}}
			if _, err := normalizeTerminalCommands(commands); err != nil {
				t.Fatal(err)
			}
			_, _, _, _, err := normalizePCAgentMetadata(store.DeviceTypePCAgent, platform, "x64", "test",
				[]string{"realtime_terminal_v1", "terminal_commands_v1"}, false, false, []string{shell}, commands)
			if (err == nil) != store.ShellAllowedOnPlatform(platform, shell) {
				t.Fatalf("platform=%s shell=%s err=%v", platform, shell, err)
			}
		}
	}
	for _, shell := range []string{"bash", "zsh", "fish", "pwsh", "sh"} {
		if err := validateFileOperationRequest(store.FileOperationOpenTerminal, "/Users/test/项目", nil, &shell); err != nil {
			t.Fatal(err)
		}
		body, _ := json.Marshal(map[string]any{"session_id": uuid.NewString(), "terminal_command_id": shell, "shell_type": shell, "cwd": "/Users/test/项目"})
		if err := validateOpenTerminalResult(body, &shell, "/Users/test/项目"); err != nil {
			t.Fatalf("portable result rejected: %v", err)
		}
	}
}

type platformCatalogFixture struct {
	offer       agentupdate.Offer
	plat, arch  string
	build       int64
	legacyCalls int
}

func (f *platformCatalogFixture) LatestWindowsStable(context.Context, int64) (agentupdate.Offer, error) {
	f.legacyCalls++
	return f.offer, nil
}
func (f *platformCatalogFixture) LatestStable(_ context.Context, platform, architecture string, build int64) (agentupdate.Offer, error) {
	f.plat, f.arch, f.build = platform, architecture, build
	return f.offer, nil
}

type updatePCFixture struct{ pcs []store.PCRecord }

func (f *updatePCFixture) SessionCleanupDevice(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) (uuid.UUID, error) {
	return uuid.New(), nil
}
func (f *updatePCFixture) ListPCs(context.Context, uuid.UUID, uuid.UUID) ([]store.PCRecord, error) {
	return f.pcs, nil
}
func (f *updatePCFixture) ListSessionsIncludingEnded(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) ([]store.SessionRecord, error) {
	return nil, nil
}

type capturedUpdateStore struct {
	fakeAgentUpdateStore
	input   store.AgentUpdateCreateInput
	created int
}

func (f *capturedUpdateStore) CreateAgentUpdate(_ context.Context, _, requester, pc uuid.UUID, input store.AgentUpdateCreateInput) (store.AgentUpdateRecord, error) {
	f.created++
	f.input = input
	return store.AgentUpdateRecord{ID: uuid.New(), PCID: pc, RequesterDeviceID: requester, ArtifactPlatform: input.ArtifactPlatform,
		ArtifactArchitecture: input.ArtifactArchitecture, ArtifactType: input.ArtifactType, ArtifactAppleTeamID: input.ArtifactAppleTeamID, Status: store.AgentUpdatePending}, nil
}

func TestMobileCanRequestOnlyPlatformMatchedMacUpdate(t *testing.T) {
	for _, role := range []string{"ANDROID", "IOS"} {
		for _, arch := range []string{"arm64", "x64"} {
			t.Run(role+"/"+arch, func(t *testing.T) {
				now := time.Now().UTC()
				account, device, pcID := uuid.New(), uuid.New(), uuid.New()
				authentication, key := fileOperationTestAuth(t, account, device, role, now)
				catalog := &platformCatalogFixture{offer: agentupdate.Offer{Available: true, Version: "0.3.17", Build: 147,
					Platform: "macos", Architecture: arch, ArtifactType: "pkg", AppleTeamID: "ABCDE12345",
					DownloadURL: "https://updates.example/TermLink.pkg", ArtifactFilename: "TermLink.pkg", SizeBytes: 123}}
				repository := &capturedUpdateStore{}
				service := &API{AgentUpdateCatalog: catalog, agentUpdates: repository, agentUpdatePCs: &updatePCFixture{pcs: []store.PCRecord{{
					ID: pcID, AgentVersion: "0.3.16.146", Platform: "macos", Architecture: arch, Online: true, RemoteRoutingEnabled: true,
					Capabilities: []string{store.PCCapabilityRemoteAgentUpdateV1}}}}}
				path := "/v1/pcs/" + pcID.String() + "/agent-updates"
				req := signedFileOperationRequest(t, key, device, now, http.MethodPost, path, []byte(`{"expected_target_build":147}`))
				req.SetPathValue("pcId", pcID.String())
				response := httptest.NewRecorder()
				authentication.Authenticate(http.HandlerFunc(service.createAgentUpdate)).ServeHTTP(response, req)
				if response.Code != http.StatusCreated || repository.created != 1 || catalog.legacyCalls != 0 || catalog.plat != "macos" || catalog.arch != arch ||
					repository.input.ArtifactAppleTeamID == nil || *repository.input.ArtifactAppleTeamID != "ABCDE12345" {
					t.Fatalf("wrong platform selection status=%d input=%+v body=%s", response.Code, repository.input, response.Body.String())
				}
				if strings.Contains(response.Body.String(), "artifact_url") || strings.Contains(response.Body.String(), "artifact_apple_team_id") {
					t.Fatal("mobile view leaked installer metadata")
				}
			})
		}
	}
}

func TestPlatformUpdateRejectsWrongArtifactAndUnsupportedAgent(t *testing.T) {
	pc := store.PCRecord{Platform: "macos", Architecture: "arm64", AgentVersion: "0.3.16.146", Online: true, RemoteRoutingEnabled: true, Capabilities: []string{"remote_agent_update_v1"}}
	valid := agentupdate.Offer{Available: true, Build: 147, Platform: "macos", Architecture: "arm64", ArtifactType: "pkg", AppleTeamID: "ABCDE12345"}
	for _, change := range []func(*agentupdate.Offer){
		func(v *agentupdate.Offer) { v.Platform = "windows" }, func(v *agentupdate.Offer) { v.Architecture = "x64" },
		func(v *agentupdate.Offer) { v.ArtifactType = "exe" }, func(v *agentupdate.Offer) { v.AppleTeamID = "" }, func(v *agentupdate.Offer) { v.Build = 146 },
	} {
		v := valid
		change(&v)
		a := &API{AgentUpdateCatalog: &platformCatalogFixture{offer: v}}
		if _, err := a.latestAgentUpdate(context.Background(), pc, 146); !errors.Is(err, agentupdate.ErrInvalidArtifact) {
			t.Fatalf("invalid offer accepted %+v %v", v, err)
		}
	}
	for _, change := range []func(*store.PCRecord){func(p *store.PCRecord) { p.Capabilities = nil }, func(p *store.PCRecord) { p.Online = false }, func(p *store.PCRecord) { p.RemoteRoutingEnabled = false }, func(p *store.PCRecord) { p.Architecture = "unknown" }, func(p *store.PCRecord) { p.Platform = "linux" }} {
		v := pc
		change(&v)
		if _, reason := agentUpdateEligibility(v); reason == "" {
			t.Fatalf("unsupported PC eligible %+v", v)
		}
	}
	pc.AgentVersion = "0.1.0.20"
	if _, reason := agentUpdateEligibility(pc); reason != "" {
		t.Fatalf("Windows downloader boundary applied to Mac: %s", reason)
	}
	pc.Platform = "windows"
	pc.Architecture = "x64"
	if _, reason := agentUpdateEligibility(pc); reason != "AGENT_VERSION_UNSUPPORTED" {
		t.Fatal(reason)
	}
}
