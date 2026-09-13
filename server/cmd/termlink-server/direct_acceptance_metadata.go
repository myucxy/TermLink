package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"strconv"
	"strings"
)

const (
	directAcceptanceMetadataPath        = "/v1/acceptance/direct-metadata"
	directAcceptanceMetadataEnableEnv   = "TERMLINK_DIRECT_PRODUCT_E2E_METADATA_ENABLED"
	directAcceptanceMetadataBuildEnv    = "TERMLINK_DIRECT_PRODUCT_E2E_BUILD_SHA256"
	directAcceptanceMetadataMaxBytes    = 4 << 10
	directAcceptanceMetadataSchema      = 1
	directAcceptanceMetadataKind        = "termlink.server-direct-acceptance-metadata"
	directAcceptanceMetadataEnvironment = "acceptance"
)

type directAcceptanceMetadataConfig struct {
	origin      string
	buildSHA256 string
}

type directAcceptanceMetadata struct {
	SchemaVersion          int      `json:"schema_version"`
	Kind                   string   `json:"kind"`
	Environment            string   `json:"environment"`
	Origin                 string   `json:"origin"`
	BuildSHA256            string   `json:"build_sha256"`
	DirectTransportEnabled bool     `json:"direct_transport_enabled"`
	DirectRoutingPermitted bool     `json:"direct_routing_permitted"`
	Capabilities           []string `json:"capabilities"`
}

// loadDirectAcceptanceMetadataConfig keeps the endpoint absent unless the
// dedicated acceptance gate is explicitly enabled. Invalid enabled metadata
// fails startup instead of publishing a plausible but unverified identity.
func loadDirectAcceptanceMetadataConfig(publicOrigin string) (*directAcceptanceMetadataConfig, error) {
	enabled, err := optionalFeatureFlag(directAcceptanceMetadataEnableEnv)
	if err != nil {
		return nil, err
	}
	if !enabled {
		return nil, nil
	}
	origin, err := exactHTTPSOrigin(publicOrigin)
	if err != nil {
		return nil, fmt.Errorf("invalid %s origin", directAcceptanceMetadataEnvironment)
	}
	buildSHA256 := os.Getenv(directAcceptanceMetadataBuildEnv)
	if !isLowerHexSHA256(buildSHA256) {
		return nil, fmt.Errorf("invalid %s", directAcceptanceMetadataBuildEnv)
	}
	return &directAcceptanceMetadataConfig{origin: origin, buildSHA256: buildSHA256}, nil
}

func exactHTTPSOrigin(value string) (string, error) {
	if value == "" || value != strings.TrimSpace(value) || strings.Contains(value, "#") {
		return "", errors.New("origin is empty or padded")
	}
	parsed, err := url.Parse(value)
	if err != nil || parsed == nil || parsed.Scheme != "https" || parsed.Opaque != "" ||
		parsed.Host == "" || parsed.Hostname() == "" || parsed.User != nil || parsed.Path != "" ||
		parsed.RawPath != "" || parsed.RawQuery != "" || parsed.ForceQuery || parsed.Fragment != "" {
		return "", errors.New("origin must be an exact HTTPS origin")
	}
	return value, nil
}

func isLowerHexSHA256(value string) bool {
	if len(value) != 64 {
		return false
	}
	for _, character := range value {
		if (character < '0' || character > '9') && (character < 'a' || character > 'f') {
			return false
		}
	}
	return true
}

func registerDirectAcceptanceMetadataRoute(
	mux *http.ServeMux,
	config *directAcceptanceMetadataConfig,
	directTransportReady bool,
	directRoutingPermitted bool,
) error {
	if config == nil {
		return nil
	}
	routingReady := directTransportReady && directRoutingPermitted
	capabilities := make([]string, 0, 4)
	if directTransportReady {
		capabilities = append(capabilities,
			"direct_transport_shadow_v1",
			"direct_transport_shadow_multipair_v2",
		)
	}
	if routingReady {
		capabilities = append(capabilities,
			"direct_transport_data_v1",
			"direct_session_cutover_v1",
		)
	}
	body, err := json.Marshal(directAcceptanceMetadata{
		SchemaVersion:          directAcceptanceMetadataSchema,
		Kind:                   directAcceptanceMetadataKind,
		Environment:            directAcceptanceMetadataEnvironment,
		Origin:                 config.origin,
		BuildSHA256:            config.buildSHA256,
		DirectTransportEnabled: directTransportReady,
		DirectRoutingPermitted: routingReady,
		Capabilities:           capabilities,
	})
	if err != nil || len(body) >= directAcceptanceMetadataMaxBytes {
		return errors.New("direct acceptance metadata is unavailable")
	}
	mux.HandleFunc("GET "+directAcceptanceMetadataPath, func(w http.ResponseWriter, request *http.Request) {
		if request.URL.EscapedPath() != directAcceptanceMetadataPath || request.URL.RawQuery != "" || request.URL.ForceQuery {
			http.NotFound(w, request)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Content-Length", strconv.Itoa(len(body)))
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write(body)
	})
	return nil
}

// hideDirectAcceptanceMetadataWhenDisabled prevents the generic authenticated
// /v1 fallback from turning an intentionally absent endpoint into a 401. All
// methods and query variants remain indistinguishable from an unknown route.
func hideDirectAcceptanceMetadataWhenDisabled(config *directAcceptanceMetadataConfig, next http.Handler) http.Handler {
	if config != nil {
		return next
	}
	return http.HandlerFunc(func(w http.ResponseWriter, request *http.Request) {
		if request.URL.Path == directAcceptanceMetadataPath {
			http.NotFound(w, request)
			return
		}
		next.ServeHTTP(w, request)
	})
}
