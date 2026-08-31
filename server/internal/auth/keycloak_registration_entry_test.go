package auth

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
)

func TestLoginThemeRegistrationEntryUsesTermLinkRegisterPage(t *testing.T) {
	templateData, err := os.ReadFile("../../acceptance/keycloak-theme/termlink/login/template.ftl")
	if err != nil {
		t.Fatal(err)
	}
	loginData, err := os.ReadFile("../../acceptance/keycloak-theme/termlink/login/login.ftl")
	if err != nil {
		t.Fatal(err)
	}
	templateText, loginText := string(templateData), string(loginData)
	for _, clientID := range []string{"termlink-web", "termlink-windows", "termlink-android"} {
		if !strings.Contains(templateText, clientID) {
			t.Fatalf("login theme does not derive the register URL for %s", clientID)
		}
	}
	if !strings.Contains(loginText, `href="${termlinkRegisterUrl}"`) ||
		strings.Contains(loginText, "aria-disabled") || strings.Contains(loginText, "?continue=") {
		t.Fatal("login create-account entry must be a direct, enabled TermLink /register link")
	}

	realmData, err := os.ReadFile("../../acceptance/keycloak-realm.json")
	if err != nil {
		t.Fatal(err)
	}
	var realm struct {
		Clients []acceptanceRealmClient `json:"clients"`
	}
	if err := json.Unmarshal(realmData, &realm); err != nil {
		t.Fatal(err)
	}
	for _, expected := range []string{"termlink-web", "termlink-windows", "termlink-android"} {
		found := false
		for _, client := range realm.Clients {
			if client.ClientID == expected {
				found = client.BaseURL != ""
				break
			}
		}
		if !found {
			t.Fatalf("%s lacks the public base URL required by the direct registration entry", expected)
		}
	}
}
