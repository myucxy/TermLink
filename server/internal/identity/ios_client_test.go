package identity

import "testing"

func TestIOSClientUsesPKCECodeAndRefreshWithoutPasswordGrant(t *testing.T) {
	config := Config{AndroidClientID: "android", AndroidRedirectURI: "dev.termlink:/oauth2redirect", WindowsClientID: "windows", WindowsRedirectURI: "http://127.0.0.1/oauth2/callback", WebClientID: "web", WebRedirectURI: "https://example.test/account/callback", StepUpClientID: "step-up"}
	clients, err := embeddedClients(config)
	if err != nil {
		t.Fatal(err)
	}
	if len(clients) != 4 {
		t.Fatal("iOS must be opt-in")
	}
	config.IOSClientID, config.IOSRedirectURI = "ios", "dev.termlink.ios:/oauth2redirect"
	clients, err = embeddedClients(config)
	if err != nil {
		t.Fatal(err)
	}
	client := clients["ios"]
	if client == nil || !client.IsPublic() || !client.GetGrantTypes().Has("authorization_code") || !client.GetGrantTypes().Has("refresh_token") || client.GetGrantTypes().Has("password") {
		t.Fatal("invalid iOS public client grant policy")
	}
	config.IOSClientID = "android"
	if _, err := embeddedClients(config); err == nil {
		t.Fatal("duplicate client IDs accepted")
	}
	config.IOSClientID, config.IOSRedirectURI = "ios", "https://evil.test/callback"
	if _, err := embeddedClients(config); err == nil {
		t.Fatal("unregistered redirect accepted")
	}
}
