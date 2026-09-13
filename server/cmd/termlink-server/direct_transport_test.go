package main

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestReadOptionalDirectSecretFileIsExplicitAndRedactedByCaller(t *testing.T) {
	const name = "TERMLINK_DIRECT_REDIS_URL_FILE"
	t.Setenv(name, "")
	if _, err := readOptionalSecretFile(name); err == nil {
		t.Fatal("missing direct secret was accepted")
	}
	path := filepath.Join(t.TempDir(), "direct-redis-url")
	if err := os.WriteFile(path, []byte("redis://:secret@direct-valkey:6379/0\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	t.Setenv(name, path)
	value, err := readOptionalSecretFile(name)
	if err != nil {
		t.Fatal(err)
	}
	if value != "redis://:secret@direct-valkey:6379/0" {
		t.Fatalf("unexpected direct secret value length=%d", len(value))
	}
}

func TestDirectTransportFeatureFlagDefaultsDisabledAndRejectsInvalidValues(t *testing.T) {
	const name = "TERMLINK_DIRECT_TRANSPORT_ENABLED"
	original, existed := os.LookupEnv(name)
	_ = os.Unsetenv(name)
	t.Cleanup(func() {
		if existed {
			_ = os.Setenv(name, original)
		} else {
			_ = os.Unsetenv(name)
		}
	})
	enabled, err := optionalFeatureFlag(name)
	if err != nil || enabled {
		t.Fatalf("default enabled=%t err=%v, want disabled", enabled, err)
	}
	if err := os.Setenv(name, "true"); err != nil {
		t.Fatal(err)
	}
	if enabled, err = optionalFeatureFlag(name); err != nil || !enabled {
		t.Fatalf("true enabled=%t err=%v", enabled, err)
	}
	if err := os.Setenv(name, "sometimes"); err != nil {
		t.Fatal(err)
	}
	if _, err = optionalFeatureFlag(name); err == nil {
		t.Fatal("invalid feature flag accepted")
	}
}

func TestDirectTransportEmergencySwitchIgnoresRoutingValue(t *testing.T) {
	const transportName = "TERMLINK_DIRECT_TRANSPORT_ENABLED"
	const routingName = "TERMLINK_DIRECT_ROUTING_PERMITTED"
	t.Setenv(transportName, "false")
	t.Setenv(routingName, "malformed-stale-value")
	enabled, routing, err := directTransportFeatureFlags()
	if err != nil || enabled || routing {
		t.Fatalf("emergency-off flags=(%t,%t) err=%v, want false,false,nil", enabled, routing, err)
	}

	t.Setenv(transportName, "true")
	t.Setenv(routingName, "false")
	enabled, routing, err = directTransportFeatureFlags()
	if err != nil || !enabled || routing {
		t.Fatalf("shadow-only flags=(%t,%t) err=%v, want true,false,nil", enabled, routing, err)
	}

	t.Setenv(routingName, "true")
	enabled, routing, err = directTransportFeatureFlags()
	if err != nil || !enabled || !routing {
		t.Fatalf("production-direct flags=(%t,%t) err=%v, want true,true,nil", enabled, routing, err)
	}

	t.Setenv(routingName, "malformed")
	if _, _, err = directTransportFeatureFlags(); err == nil {
		t.Fatal("enabled transport accepted malformed routing permission")
	}
}

func TestDirectRealtimeCapabilityRequiresReadyTransportAndRoutingPermission(t *testing.T) {
	for _, test := range []struct {
		ready, permitted, want bool
	}{
		{ready: false, permitted: false, want: false},
		{ready: false, permitted: true, want: false},
		{ready: true, permitted: false, want: false},
		{ready: true, permitted: true, want: true},
	} {
		options := directRealtimeHubOptions(test.ready, test.permitted)
		if options.DirectTransportDataV1 != test.want {
			t.Fatalf("ready=%t permitted=%t capability=%t, want %t", test.ready, test.permitted, options.DirectTransportDataV1, test.want)
		}
	}
}

func TestDirectTransportRoutesAreAbsentUntilExplicitlyRegistered(t *testing.T) {
	mux := http.NewServeMux()
	realtimeCalls := 0
	mux.HandleFunc("/v1/realtime", func(w http.ResponseWriter, _ *http.Request) {
		realtimeCalls++
		w.WriteHeader(http.StatusNoContent)
	})

	disabled := httptest.NewRecorder()
	mux.ServeHTTP(disabled, httptest.NewRequest(http.MethodGet, "/v1/transport/config", nil))
	if disabled.Code != http.StatusNotFound {
		t.Fatalf("disabled direct config status=%d, want 404", disabled.Code)
	}

	failingSignal := http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		http.Error(w, "signal failure", http.StatusServiceUnavailable)
	})
	registerDirectTransportRoutes(mux,
		http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusOK) }),
		http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusCreated) }),
		failingSignal,
	)
	signal := httptest.NewRecorder()
	mux.ServeHTTP(signal, httptest.NewRequest(http.MethodGet, "/v1/transport/signals", nil))
	if signal.Code != http.StatusServiceUnavailable {
		t.Fatalf("signal failure status=%d", signal.Code)
	}
	realtime := httptest.NewRecorder()
	mux.ServeHTTP(realtime, httptest.NewRequest(http.MethodGet, "/v1/realtime", nil))
	if realtime.Code != http.StatusNoContent || realtimeCalls != 1 {
		t.Fatalf("realtime after signal failure status/calls=%d/%d", realtime.Code, realtimeCalls)
	}
}

func TestParseDirectSTUNServers(t *testing.T) {
	for name, test := range map[string]struct {
		input string
		want  []string
		err   bool
	}{
		"empty":        {input: ""},
		"host":         {input: "stun:stun.example.test:3478", want: []string{"stun:stun.example.test:3478"}},
		"two":          {input: "stun:ONE.example.test:3478, stun:[2001:db8::1]:5349", want: []string{"stun:one.example.test:3478", "stun:[2001:db8::1]:5349"}},
		"wrong scheme": {input: "stuns:example.test:3478", err: true},
		"missing port": {input: "stun:example.test", err: true},
		"path":         {input: "stun:example.test:3478/path", err: true},
		"userinfo":     {input: "stun:user@example.test:3478", err: true},
		"zone":         {input: "stun:[fe80::1%eth0]:3478", err: true},
		"bad port":     {input: "stun:example.test:0", err: true},
		"leading zero": {input: "stun:example.test:03478", err: true},
		"duplicate":    {input: "stun:example.test:3478,stun:EXAMPLE.test:3478", err: true},
		"too many":     {input: "stun:a.test:1,stun:b.test:2,stun:c.test:3", err: true},
	} {
		t.Run(name, func(t *testing.T) {
			got, err := parseDirectSTUNServers(test.input)
			if (err != nil) != test.err {
				t.Fatalf("error=%v, want error=%t", err, test.err)
			}
			if strings.Join(got, ",") != strings.Join(test.want, ",") {
				t.Fatalf("servers=%v, want %v", got, test.want)
			}
		})
	}
}
