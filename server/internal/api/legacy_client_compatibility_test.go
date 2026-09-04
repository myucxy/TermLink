package api

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestLegacyClientCatalogCompatibilityReturnsNoPlans(t *testing.T) {
	response := httptest.NewRecorder()
	(&API{}).listPlans(response, httptest.NewRequest(http.MethodGet, "/v1/billing/plans", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("status = %d, want %d", response.Code, http.StatusOK)
	}
	var payload struct {
		CatalogVersion int   `json:"catalog_version"`
		Plans          []any `json:"plans"`
	}
	if err := json.Unmarshal(response.Body.Bytes(), &payload); err != nil {
		t.Fatal(err)
	}
	if payload.CatalogVersion != 0 || payload.Plans == nil || len(payload.Plans) != 0 {
		t.Fatalf("legacy catalog payload = %#v, want version 0 with an empty plan list", payload)
	}
}

func TestLegacyClientAccountCapabilityCompatibilityIsUnlimited(t *testing.T) {
	response := httptest.NewRecorder()
	(&API{}).getEntitlement(response, httptest.NewRequest(http.MethodGet, "/v1/account/entitlements", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("status = %d, want %d", response.Code, http.StatusOK)
	}
	var payload struct {
		PlanKey         string         `json:"plan_key"`
		MaxRemotePCs    int            `json:"max_remote_pcs"`
		MaxLiveSessions int            `json:"max_live_sessions"`
		FeatureFlags    map[string]any `json:"feature_flags"`
	}
	if err := json.Unmarshal(response.Body.Bytes(), &payload); err != nil {
		t.Fatal(err)
	}
	if payload.PlanKey != "community" || payload.MaxRemotePCs != -1 || payload.MaxLiveSessions != -1 || payload.FeatureFlags == nil {
		t.Fatalf("legacy account capability payload = %#v, want unlimited community access", payload)
	}
}
