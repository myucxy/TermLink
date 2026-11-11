package api

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type ownPCRoutingFixture struct {
	account, device, pc uuid.UUID
	calls               int
	enabled             bool
	version             int64
}

func (f *ownPCRoutingFixture) GetAgentPCRemoteRouting(_ context.Context, account, device, pc uuid.UUID) (store.UserPortalPCControlPolicy, error) {
	f.calls++
	if account != f.account || device != f.device || pc != f.pc {
		return store.UserPortalPCControlPolicy{}, store.ErrDenied
	}
	return store.UserPortalPCControlPolicy{PCID: pc, RemoteRoutingEnabled: f.enabled, PolicyVersion: f.version}, nil
}
func (f *ownPCRoutingFixture) SetAgentPCRemoteRouting(ctx context.Context, account, device, pc uuid.UUID, enabled bool, version int64, _ string) (store.UserPortalPCControlPolicy, error) {
	p, err := f.GetAgentPCRemoteRouting(ctx, account, device, pc)
	if err != nil {
		return p, err
	}
	if version != f.version {
		return p, store.ErrConflict
	}
	if f.enabled != enabled {
		f.version++
		f.enabled = enabled
	}
	p.PolicyVersion = f.version
	p.RemoteRoutingEnabled = f.enabled
	return p, nil
}

func TestSignedAgentRoutingRejectsMobileForeignPCAndStalePolicy(t *testing.T) {
	account, device, pc := uuid.New(), uuid.New(), uuid.New()
	now := time.Now().UTC()
	for _, scenario := range []struct {
		name, role, body string
		foreign          bool
		status           int
	}{
		{"enable", "PC_AGENT", `{"enabled":true,"expected_version":4}`, false, 200},
		{"legacy disable", "WINDOWS_PC", `{"enabled":false,"expected_version":4}`, false, 200},
		{"stale enable", "PC_AGENT", `{"enabled":true,"expected_version":3}`, false, 409},
		{"version required", "PC_AGENT", `{"enabled":true}`, false, 400},
		{"enabled required", "PC_AGENT", `{"expected_version":4}`, false, 400},
		{"foreign PC", "PC_AGENT", `{"enabled":true,"expected_version":4}`, true, 403},
		{"android denied", "ANDROID", `{"enabled":true,"expected_version":4}`, false, 403},
		{"ios denied", "IOS", `{"enabled":true,"expected_version":4}`, false, 403},
	} {
		t.Run(scenario.name, func(t *testing.T) {
			authentication, key := fileOperationTestAuth(t, account, device, scenario.role, now)
			repo := &ownPCRoutingFixture{account: account, device: device, pc: pc, version: 4}
			a := &API{agentRouting: repo}
			target := pc
			if scenario.foreign {
				target = uuid.New()
			}
			path := "/v1/pcs/" + target.String() + "/remote-routing"
			req := signedFileOperationRequest(t, key, device, now, http.MethodPut, path, []byte(scenario.body))
			req.SetPathValue("pcId", target.String())
			response := httptest.NewRecorder()
			authentication.Authenticate(http.HandlerFunc(a.agentPCRemoteRouting)).ServeHTTP(response, req)
			if response.Code != scenario.status {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
			if scenario.status == 200 && !strings.Contains(response.Body.String(), `"policy_version"`) {
				t.Fatal("current policy missing")
			}
			if (scenario.role == "ANDROID" || scenario.role == "IOS") && repo.calls != 0 {
				t.Fatal("mobile reached own-PC mutation")
			}
		})
	}
}
