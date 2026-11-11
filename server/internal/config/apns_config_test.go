package config

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/pem"
	"fmt"
	"strings"
	"testing"
)

func apnsConfigFixture(t *testing.T) (Input, string) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	bytes, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	encoded := string(pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: bytes}))
	return Input{PublicURL: "https://termlink.example.test", OIDCIssuer: "https://issuer.example.test", OIDCClientID: "server", OIDCClientSecret: "oidc", DatabaseURL: "postgres", RedisURL: "redis", TokenSigningKey: strings.Repeat("k", 32),
		IOSOIDCClientID: "termlink-ios", APNsEnabled: true, APNsTeamID: "ABCDE12345", APNsKeyID: "12345ABCDE", APNsTopic: "dev.termlink.ios", APNsEnvironment: "sandbox", APNsPrivateKey: encoded}, encoded
}

func TestAPNsConfigurationIsOptInStrictAndRedacted(t *testing.T) {
	valid, key := apnsConfigFixture(t)
	cfg, err := New(valid)
	if err != nil {
		t.Fatal(err)
	}
	if !cfg.APNsEnabled || cfg.APNsPrivateKey.Value() != key {
		t.Fatal("APNs settings lost")
	}
	for _, v := range []string{fmt.Sprint(cfg), fmt.Sprintf("%#v", cfg)} {
		if strings.Contains(v, key) || strings.Contains(v, "PRIVATE KEY") {
			t.Fatal("APNs private key leaked")
		}
	}
	for name, mutate := range map[string]func(*Input){
		"missing key": func(i *Input) { i.APNsPrivateKey = "" }, "invalid key": func(i *Input) { i.APNsPrivateKey = "not a key" },
		"wrong team": func(i *Input) { i.APNsTeamID = "abcde12345" }, "wrong key id": func(i *Input) { i.APNsKeyID = "invalid" },
		"wrong environment": func(i *Input) { i.APNsEnvironment = "custom" }, "url topic": func(i *Input) { i.APNsTopic = "https://untrusted" },
		"missing ios": func(i *Input) { i.IOSOIDCClientID = "" },
	} {
		t.Run(name, func(t *testing.T) {
			i := valid
			mutate(&i)
			if _, err := New(i); err == nil {
				t.Fatal("invalid APNs enabled")
			}
		})
	}
	disabled := valid
	disabled.APNsEnabled = false
	disabled.APNsPrivateKey = ""
	if _, err = New(disabled); err != nil {
		t.Fatal("unconfigured disabled APNs broke startup")
	}
}

func TestAPNsLoadUsesPrivateFileOnlyWhenEnabled(t *testing.T) {
	_, key := apnsConfigFixture(t)
	files := writeSecrets(t, map[string]string{"db": "postgres://fixture/db", "apns": key})
	env := map[string]string{"TERMLINK_PUBLIC_URL": "https://termlink.example.test", "TERMLINK_DATABASE_URL_FILE": files["db"], "TERMLINK_IOS_OIDC_CLIENT_ID": "termlink-ios",
		"TERMLINK_APNS_TEAM_ID": "ABCDE12345", "TERMLINK_APNS_KEY_ID": "12345ABCDE", "TERMLINK_APNS_TOPIC": "dev.termlink.ios", "TERMLINK_APNS_ENVIRONMENT": "production",
		"TERMLINK_APNS_PRIVATE_KEY_FILE": files["apns"]}
	if c, err := LoadFromEnv(env); err != nil || !c.APNsPrivateKey.Empty() {
		t.Fatal("disabled APNs read private key")
	}
	env["TERMLINK_APNS_ENABLED"] = "true"
	c, err := LoadFromEnv(env)
	if err != nil || c.APNsPrivateKey.Empty() {
		t.Fatal(err)
	}
	delete(env, "TERMLINK_APNS_PRIVATE_KEY_FILE")
	if _, err := LoadFromEnv(env); err == nil {
		t.Fatal("enabled APNs accepted missing private file")
	}
}
