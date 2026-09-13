package main

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"html"
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/realtimewire"
	transportwire "github.com/termlink/termlink/server/internal/transport"
	"google.golang.org/protobuf/encoding/protowire"
)

const (
	deviceProofDomain          = "TERMLINK-DEVICE-PROOF-V1"
	registerDomain             = "TERMLINK-DEVICE-REGISTER-V1"
	transportSignalProofDomain = "TERMLINK-TRANSPORT-SIGNAL-PROOF-V1"
	transportSignalSubprotocol = "termlink.transport.v1"
	initialKeyEpoch            = int64(1)
	maxHTMLBytes               = 2 << 20
)

var anchorHrefPattern = regexp.MustCompile(`(?is)<a\b[^>]*\bhref\s*=\s*(?:"([^"]*)"|'([^']*)')[^>]*>`)

type identity struct {
	id       uuid.UUID
	public   ed25519.PublicKey
	private  ed25519.PrivateKey
	exchange []byte
}

type storedPCIdentity struct {
	Scope      string `json:"scope"`
	DeviceID   string `json:"device_id"`
	PCID       string `json:"pc_id"`
	PrivateKey string `json:"private_key"`
	Exchange   string `json:"exchange"`
}

type probe struct {
	root   string
	issuer string
	tokens map[uuid.UUID]string
	http   *http.Client
	dialer websocket.Dialer
}

func main() {
	if err := run(); err != nil {
		writeProbeFailure(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println("Production OIDC/TLS/API/Realtime acceptance probe passed.")
}

func writeProbeFailure(writer io.Writer, err error) {
	// Probe errors can wrap callback URLs, OIDC
	// responses, and local secret paths. Keep process stderr stable and
	// non-sensitive; detailed diagnosis belongs in the bounded test assertions.
	for _, diagnostic := range []struct {
		match string
		stage string
	}{
		{"Direct signalling WebSocket upgrade failed", "direct-signal-upgrade"},
		{"Direct signalling subprotocol negotiation failed", "direct-signal-subprotocol"},
		{"Direct signalling hello failed", "direct-signal-hello"},
		{"Direct signalling welcome closed with code 1008", "direct-signal-welcome-policy"},
		{"Direct signalling welcome closed with code 1013", "direct-signal-welcome-unavailable"},
		{"Direct signalling welcome closed with code", "direct-signal-welcome-close"},
		{"Direct signalling welcome read failed", "direct-signal-welcome-read"},
		{"Direct signalling welcome is invalid", "direct-signal-welcome-read"},
		{"Direct signalling welcome contract is invalid", "direct-signal-welcome-contract"},
		{"Direct transport config", "direct-config"},
		{"Direct transport multipair", "direct-config"},
		{"Direct transport shared state", "direct-ticket"},
		{"Direct transport ticket", "direct-ticket"},
		{"verify PC to Android Direct signalling", "direct-signal-pc-to-android"},
		{"verify Android to PC Direct signalling", "direct-signal-android-to-pc"},
		{"verify PC Direct signalling", "direct-signal-pc"},
		{"verify Android Direct signalling", "direct-signal-android"},
		{"route Host History request", "host-history-request-route"},
		{"route Host History response", "host-history-response-route"},
		{"route Host History control", "host-history-control-route"},
		{"route Session terminate request", "session-terminate-request-route"},
		{"route Session terminate response", "session-terminate-response-route"},
		{"retired history list request", "retired-history-list-request"},
		{"retired history list response", "retired-history-list-response"},
		{"retired history upload request", "retired-history-upload-request"},
		{"retired history upload response", "retired-history-upload-response"},
		{"retired history commit request", "retired-history-commit-request"},
		{"retired history commit response", "retired-history-commit-response"},
		{"verify retired server History API", "retired-history-api"},
		{"exit history session", "session-exit"},
		{"delete retired-history session catalog entry", "session-delete"},
	} {
		if strings.Contains(err.Error(), diagnostic.match) {
			_, _ = fmt.Fprintf(writer, "production acceptance probe failed (stage=%s; details redacted)\n", diagnostic.stage)
			return
		}
	}
	_, _ = fmt.Fprintln(writer, "production acceptance probe failed (details redacted)")
}

func run() error {
	root := strings.TrimRight(os.Getenv("TERMLINK_ACCEPTANCE_ROOT_URL"), "/")
	issuer := strings.TrimRight(os.Getenv("TERMLINK_ACCEPTANCE_OIDC_ISSUER"), "/")
	passwordPath := os.Getenv("TERMLINK_ACCEPTANCE_USER_PASSWORD_FILE")
	caPath := os.Getenv("TERMLINK_ACCEPTANCE_CA_FILE")
	identityPath := os.Getenv("TERMLINK_ACCEPTANCE_PROBE_IDENTITY_FILE")
	username := strings.TrimSpace(os.Getenv("TERMLINK_ACCEPTANCE_USER_NAME"))
	if username == "" {
		username = "acceptance-probe"
	}
	pcClientID, err := acceptancePCClientID(os.Getenv("TERMLINK_ACCEPTANCE_PC_OIDC_CLIENT_ID"))
	if err != nil {
		return err
	}
	if root == "" || issuer == "" || passwordPath == "" || identityPath == "" {
		return errors.New("acceptance root, issuer, password file, and probe identity file are required")
	}
	if len(username) > 128 || strings.ContainsAny(username, "\r\n\x00") {
		return errors.New("acceptance username is invalid")
	}
	if !strings.HasPrefix(root, "https://") || !strings.HasPrefix(issuer, "https://") {
		return errors.New("acceptance endpoints must use HTTPS")
	}
	roots, err := loadRoots(caPath)
	if err != nil {
		return err
	}
	tlsConfig := &tls.Config{MinVersion: tls.VersionTLS12, RootCAs: roots}
	transport := &http.Transport{
		TLSClientConfig:       tlsConfig.Clone(),
		ExpectContinueTimeout: 15 * time.Second,
	}
	client := &http.Client{
		Transport: transport,
		Timeout:   60 * time.Second,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return errors.New("redirects are disabled for the production probe")
		},
	}
	p := &probe{
		root: root, issuer: issuer, tokens: make(map[uuid.UUID]string), http: client,
		dialer: websocket.Dialer{TLSClientConfig: tlsConfig.Clone(), Subprotocols: []string{"termlink.realtime.v1"}, HandshakeTimeout: 15 * time.Second},
	}
	defer transport.CloseIdleConnections()
	if err := p.verifyPublicEndpoints(); err != nil {
		return err
	}
	if err := p.verifyAnonymousRegistration(); err != nil {
		return err
	}

	password, err := os.ReadFile(passwordPath)
	if err != nil || len(bytes.TrimSpace(password)) < 16 {
		return errors.New("acceptance user password file is unavailable")
	}
	defer clear(password)
	pcToken, err := p.acquireToken(username, string(bytes.TrimSpace(password)), pcClientID)
	if err != nil {
		return err
	}
	androidToken, err := p.acquireToken(username, string(bytes.TrimSpace(password)), "termlink-android")
	if err != nil {
		return err
	}
	defer func() {
		pcToken = ""
		androidToken = ""
		for deviceID := range p.tokens {
			p.tokens[deviceID] = ""
			delete(p.tokens, deviceID)
		}
	}()

	pc, pcID, err := loadOrCreatePCIdentity(identityPath, issuer)
	if err != nil {
		return err
	}
	defer pc.destroy()
	android, err := newIdentity()
	if err != nil {
		return err
	}
	defer android.destroy()
	p.tokens[pc.id] = pcToken
	p.tokens[android.id] = androidToken

	if err := p.register(pc, "WINDOWS_PC", "Acceptance PC"); err != nil {
		return err
	}
	sessionID := uuid.New()
	supportedShells := []string{"command-prompt", "windows-powershell"}
	if _, err := p.signedJSON(pc, http.MethodPut, "/v1/pcs/"+pcID.String(), map[string]any{
		"name": "Acceptance PC", "agent_version": "acceptance", "key_epoch": initialKeyEpoch,
		"supported_shells": supportedShells,
	}, newProbeRunIdempotencyKey("pc-presence"), http.StatusOK); err != nil {
		return fmt.Errorf("upsert PC: %w", err)
	}
	if err := p.register(android, "ANDROID", "Acceptance Android"); err != nil {
		return err
	}
	if os.Getenv("TERMLINK_ACCEPTANCE_REQUIRE_DIRECT_TRANSPORT") == "1" {
		if err := p.verifyDirectTransport(pc, android); err != nil {
			return err
		}
	}
	pcsDirect, err := p.signedJSON(android, http.MethodGet, "/v1/pcs", nil, "", http.StatusOK)
	if err != nil {
		return fmt.Errorf("list same-account Android PC access: %w", err)
	}
	if err := validateDiscoveredListItem(pcsDirect, pcID, "device_id", pc.id); err != nil {
		return fmt.Errorf("same-account Android did not discover its PC: %w", err)
	}
	if err := validatePCSupportedShells(pcsDirect, pcID, supportedShells); err != nil {
		return fmt.Errorf("same-account Android did not receive PC shell capabilities: %w", err)
	}
	recipients, err := p.signedJSON(pc, http.MethodGet, "/v1/pcs/"+pcID.String()+"/grants", nil, "", http.StatusOK)
	if err != nil {
		return fmt.Errorf("list same-account PC recipients: %w", err)
	}
	_, ok := positiveInt64Field(recipients, "key_epoch")
	if !ok {
		return errors.New("same-account PC recipients response has an invalid key epoch")
	}
	currentRecipient := false
	if items, ok := recipients["items"].([]any); ok {
		for _, raw := range items {
			item, ok := raw.(map[string]any)
			if !ok || fmt.Sprint(item["device_id"]) != android.id.String() {
				continue
			}
			currentRecipient = item["permission"] == "MANAGE" &&
				strings.TrimSpace(fmt.Sprint(item["exchange_public_key"])) != ""
			break
		}
	}
	if !currentRecipient {
		return errors.New("same-account Android was not exposed as an implicit MANAGE encryption recipient")
	}
	if _, err := p.signedJSON(pc, http.MethodPut, "/v1/sessions/"+sessionID.String(), map[string]any{
		"pc_id": pcID, "shell_type": "powershell", "title_cipher": "", "state": "RUNNING",
		"columns": 80, "rows": 24, "last_output_seq": 1,
	}, uuid.NewString(), http.StatusOK); err != nil {
		return fmt.Errorf("upsert session: %w", err)
	}
	sessionsDirect, err := p.signedJSON(android, http.MethodGet, "/v1/pcs/"+pcID.String()+"/sessions", nil, "", http.StatusOK)
	if err != nil {
		return fmt.Errorf("list same-account Android sessions: %w", err)
	}
	if err := validateDiscoveredListItem(sessionsDirect, sessionID, "pc_id", pcID); err != nil {
		return fmt.Errorf("same-account Android did not discover its managed session: %w", err)
	}
	directSubscription, err := p.signedJSON(android, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/subscribe", map[string]any{}, uuid.NewString(), http.StatusCreated)
	if err != nil {
		return fmt.Errorf("create same-account Android subscription: %w", err)
	}
	if directSubscription["permission"] != "MANAGE" || strings.TrimSpace(fmt.Sprint(directSubscription["token"])) == "" {
		return errors.New("same-account Android subscription did not receive direct MANAGE authority")
	}
	pcSocket, err := p.connectRealtime(pc, "pc")
	if err != nil {
		return fmt.Errorf("connect PC realtime: %w", err)
	}
	defer pcSocket.Close()
	androidSocket, err := p.connectRealtime(android, "android")
	if err != nil {
		return fmt.Errorf("connect Android realtime: %w", err)
	}
	defer androidSocket.Close()
	if os.Getenv("TERMLINK_ACCEPTANCE_REQUIRE_DIRECT_TRANSPORT") == "1" {
		if err := p.verifyDirectFailureIsolation(android); err != nil {
			return err
		}
	}

	subscribe, err := opaqueEnvelope(android.id, pc.id, sessionID, realtimewire.Subscribe, realtimewire.AndroidToPC, 1)
	if err != nil {
		return err
	}
	if err := routeAndMatch(androidSocket, pcSocket, subscribe); err != nil {
		return fmt.Errorf("route Subscribe: %w", err)
	}
	if _, err := p.signedJSON(android, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/lease", map[string]any{}, uuid.NewString(), http.StatusCreated); err != nil {
		return fmt.Errorf("acquire lease: %w", err)
	}
	androidSequence := uint64(2)
	pcSequence := uint64(1)
	input, err := opaqueEnvelope(android.id, pc.id, sessionID, realtimewire.InputEvent, realtimewire.AndroidToPC, androidSequence)
	if err != nil {
		return err
	}
	if err := routeAndMatch(androidSocket, pcSocket, input); err != nil {
		return fmt.Errorf("route leased input: %w", err)
	}
	androidSequence++
	output, err := opaqueEnvelope(pc.id, android.id, sessionID, realtimewire.OutputChunk, realtimewire.PCToAndroid, pcSequence)
	if err != nil {
		return err
	}
	if err := routeAndMatch(pcSocket, androidSocket, output); err != nil {
		return fmt.Errorf("route subscribed output: %w", err)
	}
	pcSequence++
	if err := p.verifyRealtimeStability(pcSocket, androidSocket, pc, android, sessionID, &pcSequence, &androidSequence); err != nil {
		return err
	}
	hostHistoryRequest, err := opaqueEnvelope(android.id, pc.id, sessionID, realtimewire.HostHistoryRequest, realtimewire.AndroidToPC, androidSequence)
	if err != nil {
		return err
	}
	if err := routeAndMatch(androidSocket, pcSocket, hostHistoryRequest); err != nil {
		return fmt.Errorf("route Host History request: %w", err)
	}
	androidSequence++
	hostHistoryResponse, err := opaqueEnvelope(pc.id, android.id, sessionID, realtimewire.HostHistoryResponse, realtimewire.PCToAndroid, pcSequence)
	if err != nil {
		return err
	}
	if err := routeAndMatch(pcSocket, androidSocket, hostHistoryResponse); err != nil {
		return fmt.Errorf("route Host History response: %w", err)
	}
	pcSequence++
	hostHistoryControl, err := opaqueEnvelope(android.id, pc.id, sessionID, realtimewire.HostHistoryControl, realtimewire.AndroidToPC, androidSequence)
	if err != nil {
		return err
	}
	if err := routeAndMatch(androidSocket, pcSocket, hostHistoryControl); err != nil {
		return fmt.Errorf("route Host History control: %w", err)
	}
	androidSequence++
	terminateRequest, err := opaqueEnvelope(android.id, pc.id, sessionID, realtimewire.SessionTerminateRequest, realtimewire.AndroidToPC, androidSequence)
	if err != nil {
		return err
	}
	if err := routeAndMatch(androidSocket, pcSocket, terminateRequest); err != nil {
		return fmt.Errorf("route Session terminate request: %w", err)
	}
	terminateResponse, err := opaqueEnvelope(pc.id, android.id, sessionID, realtimewire.SessionTerminateResponse, realtimewire.PCToAndroid, pcSequence)
	if err != nil {
		return err
	}
	if err := routeAndMatch(pcSocket, androidSocket, terminateResponse); err != nil {
		return fmt.Errorf("route Session terminate response: %w", err)
	}
	if _, err := p.signedJSON(pc, http.MethodDelete, "/v1/sessions/"+sessionID.String()+"/lease", nil, uuid.NewString(), http.StatusOK); err != nil {
		return fmt.Errorf("force release lease: %w", err)
	}
	if err := p.verifyRetiredHistory(pc, sessionID); err != nil {
		return fmt.Errorf("verify retired server History API: %w", err)
	}
	if _, err := p.signedJSON(pc, http.MethodPut, "/v1/sessions/"+sessionID.String(), map[string]any{
		"pc_id": pcID, "shell_type": "powershell", "title_cipher": "", "state": "EXITED",
		"columns": 80, "rows": 24, "last_output_seq": pcSequence, "ended_at": time.Now().UTC(),
	}, uuid.NewString(), http.StatusOK); err != nil {
		return fmt.Errorf("exit history session: %w", err)
	}
	if _, err := p.signedJSON(pc, http.MethodPost, "/v1/devices/"+android.id.String()+"/revoke", map[string]any{}, uuid.NewString(), http.StatusOK); err != nil {
		return fmt.Errorf("revoke Android device: %w", err)
	}
	_ = androidSocket.SetReadDeadline(time.Now().Add(5 * time.Second))
	if _, _, err := androidSocket.ReadMessage(); err == nil {
		return errors.New("revoked Android realtime connection remained open")
	}
	if _, err := p.signedJSON(pc, http.MethodDelete, "/v1/sessions/"+sessionID.String(), nil, uuid.NewString(), http.StatusNoContent); err != nil {
		return fmt.Errorf("delete retired-history session catalog entry: %w", err)
	}
	return nil
}

func acceptancePCClientID(value string) (string, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return "termlink-acceptance-cli", nil
	}
	if value != "termlink-acceptance-cli" && value != "termlink-windows" {
		return "", errors.New("acceptance PC OIDC client is invalid")
	}
	return value, nil
}

func (p *probe) verifyDirectFailureIsolation(device *identity) error {
	connection, err := p.connectDirectSignals(device, "android")
	if err != nil {
		return fmt.Errorf("open Direct failure-isolation connection: %w", err)
	}
	defer connection.Close()
	_ = connection.SetWriteDeadline(time.Now().Add(5 * time.Second))
	if err := connection.WriteMessage(websocket.BinaryMessage, []byte{0}); err != nil {
		return errors.New("Direct failure-isolation frame write failed")
	}
	_ = connection.SetReadDeadline(time.Now().Add(5 * time.Second))
	_, _, err = connection.ReadMessage()
	var closeError *websocket.CloseError
	if !errors.As(err, &closeError) || closeError.Code != websocket.ClosePolicyViolation {
		return errors.New("Direct protocol failure was not isolated to its signalling connection")
	}
	return nil
}

func (p *probe) verifyRealtimeStability(
	pcSocket, androidSocket *websocket.Conn,
	pc, android *identity,
	sessionID uuid.UUID,
	pcSequence *uint64,
	androidSequence *uint64,
) error {
	raw := strings.TrimSpace(os.Getenv("TERMLINK_ACCEPTANCE_REALTIME_SOAK_SECONDS"))
	if raw == "" || raw == "0" {
		return nil
	}
	seconds, err := strconv.Atoi(raw)
	if err != nil || seconds < 1 || seconds > 20 {
		return errors.New("Realtime stability soak must be between 1 and 20 seconds")
	}
	fmt.Println("Production realtime stability soak started.")
	deadline := time.NewTimer(time.Duration(seconds) * time.Second)
	ticker := time.NewTicker(time.Second)
	defer deadline.Stop()
	defer ticker.Stop()
	for {
		select {
		case <-deadline.C:
			return nil
		case <-ticker.C:
			if err := verifyRealtimeLiveness(pcSocket); err != nil {
				return fmt.Errorf("PC realtime liveness during stability soak: %w", err)
			}
			if err := verifyRealtimeLiveness(androidSocket); err != nil {
				return fmt.Errorf("Android realtime liveness during stability soak: %w", err)
			}
			input, err := opaqueEnvelope(android.id, pc.id, sessionID, realtimewire.InputEvent, realtimewire.AndroidToPC, *androidSequence)
			if err != nil {
				return err
			}
			if err := routeAndMatch(androidSocket, pcSocket, input); err != nil {
				return fmt.Errorf("route input during realtime stability soak: %w", err)
			}
			*androidSequence++
			output, err := opaqueEnvelope(pc.id, android.id, sessionID, realtimewire.OutputChunk, realtimewire.PCToAndroid, *pcSequence)
			if err != nil {
				return err
			}
			if err := routeAndMatch(pcSocket, androidSocket, output); err != nil {
				return fmt.Errorf("route output during realtime stability soak: %w", err)
			}
			*pcSequence++
		}
	}
}

func verifyRealtimeLiveness(connection *websocket.Conn) error {
	probeID := uuid.NewString()
	request, err := json.Marshal(map[string]string{"type": "ping", "probe_id": probeID})
	if err != nil {
		return err
	}
	defer clear(request)
	_ = connection.SetWriteDeadline(time.Now().Add(5 * time.Second))
	if err := connection.WriteMessage(websocket.TextMessage, request); err != nil {
		return errors.New("liveness request write failed")
	}
	_ = connection.SetReadDeadline(time.Now().Add(5 * time.Second))
	messageType, response, err := connection.ReadMessage()
	if err != nil {
		return errors.New("liveness response read failed")
	}
	defer clear(response)
	var pong map[string]string
	if messageType != websocket.TextMessage || json.Unmarshal(response, &pong) != nil ||
		len(pong) != 2 || pong["type"] != "pong" || pong["probe_id"] != probeID {
		return errors.New("liveness response is invalid")
	}
	_ = connection.SetReadDeadline(time.Time{})
	_ = connection.SetWriteDeadline(time.Time{})
	return nil
}

func (p *probe) verifyRetiredHistory(pc *identity, sessionID uuid.UUID) error {
	tests := []struct {
		name        string
		method      string
		path        string
		body        any
		idempotency string
	}{
		{name: "list", method: http.MethodGet, path: "/v1/sessions/" + sessionID.String() + "/history"},
		{name: "upload", method: http.MethodPost, path: "/v1/sessions/" + sessionID.String() + "/history/uploads", body: map[string]any{}, idempotency: uuid.NewString()},
		{name: "commit", method: http.MethodPost, path: "/v1/sessions/" + sessionID.String() + "/history/uploads/" + uuid.NewString() + "/commit", idempotency: uuid.NewString()},
	}
	for _, test := range tests {
		response, err := p.signedJSON(pc, test.method, test.path, test.body, test.idempotency, http.StatusUpgradeRequired)
		if err != nil {
			return fmt.Errorf("retired history %s request: %w", test.name, err)
		}
		errorBody, ok := response["error"].(map[string]any)
		if !ok || errorBody["code"] != "CLIENT_UPGRADE_REQUIRED" {
			return fmt.Errorf("retired history %s response did not return the client-upgrade contract", test.name)
		}
	}
	return nil
}

func (p *probe) verifyDirectTransport(source, target *identity) error {
	config, err := p.signedJSON(source, http.MethodGet, "/v1/transport/config", nil, "", http.StatusOK)
	if err != nil {
		return fmt.Errorf("verify Direct transport config: %w", err)
	}
	expectDataPlane := os.Getenv("TERMLINK_ACCEPTANCE_EXPECT_DIRECT_ROUTING") == "1"
	protocol, ok := positiveInt64Field(config, "protocol_version")
	if !ok || protocol != 1 || config["direct_routing_permitted"] != expectDataPlane {
		return errors.New("Direct transport config does not match the explicit acceptance routing gate")
	}
	capabilities, ok := config["capabilities"].([]any)
	if !ok || !containsString(capabilities, "direct_transport_shadow_multipair_v2") {
		return errors.New("Direct transport multipair capability is unavailable")
	}
	if containsString(capabilities, "direct_transport_data_v1") != expectDataPlane {
		return errors.New("Direct transport data capability does not match the explicit acceptance routing gate")
	}
	if containsString(capabilities, "direct_session_cutover_v1") != expectDataPlane {
		return errors.New("Direct session cutover capability does not match the explicit acceptance routing gate")
	}
	connectionID := uuid.New()
	ticket, err := p.signedJSON(source, http.MethodPost, "/v1/transport/tickets", map[string]any{
		"target_device_id":   target.id.String(),
		"connection_id":      connectionID.String(),
		"requested_carriers": []string{"UDP_QUIC"},
	}, "", http.StatusCreated)
	if err != nil {
		return fmt.Errorf("verify Direct transport shared state: %w", err)
	}
	ticketProtocol, ok := positiveInt64Field(ticket, "protocol_version")
	encodedTicket, ticketOK := ticket["ticket_protobuf"].(string)
	pathEpoch, epochErr := directTicketPathEpoch(encodedTicket)
	if !ok || ticketProtocol != 1 || !ticketOK || epochErr != nil {
		return errors.New("Direct transport ticket response is invalid")
	}
	pcSignals, err := p.connectDirectSignals(source, "pc")
	if err != nil {
		return fmt.Errorf("verify PC Direct signalling: %w", err)
	}
	defer closeWebSocket(pcSignals)
	androidSignals, err := p.connectDirectSignals(target, "android")
	if err != nil {
		return fmt.Errorf("verify Android Direct signalling: %w", err)
	}
	defer closeWebSocket(androidSignals)
	pcOffer, err := directAcceptanceSignal(source.id, target.id, connectionID, pathEpoch, 1, transportwire.SignalPathOffer)
	if err != nil {
		return errors.New("Direct PC signal fixture is invalid")
	}
	defer clear(pcOffer)
	if err := routeDirectAndMatch(pcSignals, androidSignals, pcOffer); err != nil {
		return fmt.Errorf("verify PC to Android Direct signalling: %w", err)
	}
	androidAnswer, err := directAcceptanceSignal(target.id, source.id, connectionID, pathEpoch, 1, transportwire.SignalPathAnswer)
	if err != nil {
		return errors.New("Direct Android signal fixture is invalid")
	}
	defer clear(androidAnswer)
	if err := routeDirectAndMatch(androidSignals, pcSignals, androidAnswer); err != nil {
		return fmt.Errorf("verify Android to PC Direct signalling: %w", err)
	}
	return nil
}

func directTicketPathEpoch(encoded string) (uint64, error) {
	if len(encoded) < 32 || len(encoded) > 4096 {
		return 0, errors.New("Direct ticket encoding is out of bounds")
	}
	wire, err := base64.RawURLEncoding.Strict().DecodeString(encoded)
	if err != nil || len(wire) < 128 || len(wire) > 2048 || base64.RawURLEncoding.EncodeToString(wire) != encoded {
		clear(wire)
		return 0, errors.New("Direct ticket encoding is invalid")
	}
	defer clear(wire)
	var epoch uint64
	seenEpoch := false
	for len(wire) > 0 {
		number, kind, consumed := protowire.ConsumeTag(wire)
		if consumed < 0 || number < 1 || number > 13 {
			return 0, errors.New("Direct ticket protobuf is invalid")
		}
		wire = wire[consumed:]
		switch kind {
		case protowire.VarintType:
			value, size := protowire.ConsumeVarint(wire)
			if size < 0 {
				return 0, errors.New("Direct ticket varint is invalid")
			}
			if number == 7 {
				if seenEpoch || value == 0 || value > uint64(^uint64(0)>>1) {
					return 0, errors.New("Direct ticket epoch is invalid")
				}
				epoch, seenEpoch = value, true
			}
			wire = wire[size:]
		case protowire.BytesType:
			_, size := protowire.ConsumeBytes(wire)
			if size < 0 {
				return 0, errors.New("Direct ticket bytes are invalid")
			}
			wire = wire[size:]
		default:
			return 0, errors.New("Direct ticket wire type is invalid")
		}
	}
	if !seenEpoch {
		return 0, errors.New("Direct ticket epoch is missing")
	}
	return epoch, nil
}

func directAcceptanceSignal(sender, recipient, connection uuid.UUID, pathEpoch, sequence uint64, signalType transportwire.SignalType) ([]byte, error) {
	nonce := make([]byte, 12)
	ciphertext := make([]byte, 32)
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	if _, err := rand.Read(ciphertext); err != nil {
		clear(nonce)
		return nil, err
	}
	defer clear(nonce)
	defer clear(ciphertext)
	frame := make([]byte, 0, 160)
	frame = appendProbeVarint(frame, 1, 1)
	frame = appendProbeVarint(frame, 2, uint64(signalType))
	frame = appendProbeBytes(frame, 3, connection[:])
	frame = appendProbeBytes(frame, 4, sender[:])
	frame = appendProbeBytes(frame, 5, recipient[:])
	frame = appendProbeVarint(frame, 6, pathEpoch)
	frame = appendProbeVarint(frame, 7, sequence)
	frame = appendProbeVarint(frame, 9, uint64(time.Now().UnixMilli()))
	frame = appendProbeBytes(frame, 10, []byte("acceptance-signal-key"))
	frame = appendProbeBytes(frame, 11, nonce)
	frame = appendProbeBytes(frame, 12, ciphertext)
	if _, err := transportwire.UnmarshalSignal(frame); err != nil {
		clear(frame)
		return nil, err
	}
	return frame, nil
}

func appendProbeVarint(dst []byte, number protowire.Number, value uint64) []byte {
	dst = protowire.AppendTag(dst, number, protowire.VarintType)
	return protowire.AppendVarint(dst, value)
}

func appendProbeBytes(dst []byte, number protowire.Number, value []byte) []byte {
	dst = protowire.AppendTag(dst, number, protowire.BytesType)
	return protowire.AppendBytes(dst, value)
}

func routeDirectAndMatch(sender, recipient *websocket.Conn, frame []byte) error {
	_ = sender.SetWriteDeadline(time.Now().Add(5 * time.Second))
	if err := sender.WriteMessage(websocket.BinaryMessage, frame); err != nil {
		return errors.New("Direct signal write failed")
	}
	_ = recipient.SetReadDeadline(time.Now().Add(5 * time.Second))
	messageType, received, err := recipient.ReadMessage()
	if err != nil {
		return errors.New("Direct signal read failed")
	}
	defer clear(received)
	if messageType != websocket.BinaryMessage || !bytes.Equal(received, frame) {
		return errors.New("Direct signalling changed or misrouted an opaque frame")
	}
	_ = recipient.SetReadDeadline(time.Time{})
	_ = sender.SetWriteDeadline(time.Time{})
	return nil
}

func (p *probe) connectDirectSignals(device *identity, role string) (*websocket.Conn, error) {
	const target = "/v1/transport/signals"
	headers := http.Header{}
	token, err := p.tokenFor(device)
	if err != nil {
		return nil, err
	}
	headers.Set("Authorization", "Bearer "+token)
	if err := addTransportSignalProof(headers, device); err != nil {
		return nil, err
	}
	dialer := p.dialer
	dialer.Subprotocols = []string{transportSignalSubprotocol}
	websocketURL := "wss" + strings.TrimPrefix(p.root, "https") + target
	connection, response, err := dialer.Dial(websocketURL, headers)
	if response != nil && response.Body != nil {
		_ = response.Body.Close()
	}
	if err != nil {
		return nil, errors.New("Direct signalling WebSocket upgrade failed")
	}
	if connection.Subprotocol() != transportSignalSubprotocol {
		closeWebSocket(connection)
		return nil, errors.New("Direct signalling subprotocol negotiation failed")
	}
	_ = connection.SetWriteDeadline(time.Now().Add(15 * time.Second))
	capabilities := []string{
		"direct_transport_v1",
		"direct_transport_shadow_multipair_v2",
	}
	if os.Getenv("TERMLINK_ACCEPTANCE_EXPECT_DIRECT_ROUTING") == "1" {
		capabilities = append(
			capabilities,
			"direct_transport_data_v1",
			"direct_session_cutover_v1",
		)
	}
	hello, err := json.Marshal(map[string]any{
		"type":              "CLIENT_HELLO",
		"role":              role,
		"protocol_versions": []int{1},
		"capabilities":      capabilities,
	})
	if err != nil {
		closeWebSocket(connection)
		return nil, err
	}
	defer clear(hello)
	if err := connection.WriteMessage(websocket.TextMessage, hello); err != nil {
		closeWebSocket(connection)
		return nil, errors.New("Direct signalling hello failed")
	}
	_ = connection.SetReadDeadline(time.Now().Add(15 * time.Second))
	messageType, welcomeBytes, err := connection.ReadMessage()
	if err != nil {
		closeWebSocket(connection)
		var closeError *websocket.CloseError
		if errors.As(err, &closeError) {
			return nil, fmt.Errorf("Direct signalling welcome closed with code %d", closeError.Code)
		}
		return nil, errors.New("Direct signalling welcome read failed")
	}
	if messageType != websocket.TextMessage || len(welcomeBytes) > 4<<10 {
		closeWebSocket(connection)
		return nil, errors.New("Direct signalling welcome is invalid")
	}
	defer clear(welcomeBytes)
	if err := validateDirectSignalWelcome(
		welcomeBytes,
		os.Getenv("TERMLINK_ACCEPTANCE_EXPECT_DIRECT_ROUTING") == "1",
	); err != nil {
		closeWebSocket(connection)
		return nil, err
	}
	_ = connection.SetReadDeadline(time.Time{})
	_ = connection.SetWriteDeadline(time.Time{})
	return connection, nil
}

func validateDirectSignalWelcome(welcomeBytes []byte, expectDataPlane bool) error {
	var welcome struct {
		Type                string   `json:"type"`
		ProtocolVersion     int      `json:"protocol_version"`
		Capabilities        []string `json:"capabilities"`
		HeartbeatIntervalMS int64    `json:"heartbeat_interval_ms"`
		MaxSignalFrameBytes int64    `json:"max_signal_frame_bytes"`
	}
	expectedCapabilities := []string{
		"direct_transport_shadow_v1",
		"direct_transport_shadow_multipair_v2",
	}
	if expectDataPlane {
		expectedCapabilities = append(
			expectedCapabilities,
			"direct_transport_data_v1",
			"direct_session_cutover_v1",
		)
	}
	if err := json.Unmarshal(welcomeBytes, &welcome); err != nil ||
		welcome.Type != "SERVER_WELCOME" ||
		welcome.ProtocolVersion != 1 ||
		welcome.HeartbeatIntervalMS < 1_000 ||
		welcome.HeartbeatIntervalMS > 45_000 ||
		welcome.MaxSignalFrameBytes != 65536 ||
		!slices.Equal(welcome.Capabilities, expectedCapabilities) {
		return errors.New("Direct signalling welcome contract is invalid")
	}
	return nil
}

func addTransportSignalProof(headers http.Header, device *identity) error {
	timestamp := strconv.FormatInt(time.Now().UnixMilli(), 10)
	nonce, err := randomBase64(16)
	if err != nil {
		return err
	}
	bodyHash := sha256.Sum256(nil)
	canonical := strings.Join([]string{
		transportSignalProofDomain,
		http.MethodGet,
		"/v1/transport/signals",
		timestamp,
		nonce,
		hex.EncodeToString(bodyHash[:]),
	}, "\n")
	headers.Set("X-TermLink-Device", device.id.String())
	headers.Set("X-TermLink-Timestamp", timestamp)
	headers.Set("X-TermLink-Nonce", nonce)
	headers.Set("X-TermLink-Signature", base64.RawURLEncoding.EncodeToString(ed25519.Sign(device.private, []byte(canonical))))
	return nil
}

func closeWebSocket(connection *websocket.Conn) {
	if connection == nil {
		return
	}
	deadline := time.Now().Add(time.Second)
	_ = connection.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(websocket.CloseNormalClosure, ""), deadline)
	_ = connection.Close()
}

func containsString(values []any, expected string) bool {
	for _, value := range values {
		if text, ok := value.(string); ok && text == expected {
			return true
		}
	}
	return false
}

// A persisted probe identity deliberately reuses its PC UUID across runs, but
// its presence update must execute on every run. Reusing one idempotency key
// would replay the first cached 200 response forever and leave last_seen_at
// stale, causing the active-session directory to hide the fresh test session.
func newProbeRunIdempotencyKey(operation string) string {
	return "acceptance-" + operation + "-" + uuid.NewString()
}

func loadRoots(path string) (*x509.CertPool, error) {
	pool, err := x509.SystemCertPool()
	if err != nil || pool == nil {
		pool = x509.NewCertPool()
	}
	// Public production acceptance is terminated by the real public
	// certificate and therefore relies on the host trust store. LAN acceptance
	// supplies the private Caddy CA explicitly and appends it below.
	if strings.TrimSpace(path) == "" {
		return pool, nil
	}
	pem, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read acceptance CA: %w", err)
	}
	if !pool.AppendCertsFromPEM(pem) {
		return nil, errors.New("acceptance CA is invalid")
	}
	return pool, nil
}

func stringField(value map[string]any, name string) (string, bool) {
	field, ok := value[name].(string)
	return field, ok && strings.TrimSpace(field) != ""
}

func positiveInt64Field(value map[string]any, name string) (int64, bool) {
	field, ok := value[name].(json.Number)
	if !ok {
		return 0, false
	}
	parsed, err := strconv.ParseInt(field.String(), 10, 64)
	if err != nil || parsed < 1 {
		return 0, false
	}
	return parsed, true
}

func (p *probe) acquireToken(username, password, clientID string) (string, error) {
	configurationURL := p.issuer + "/.well-known/openid-configuration"
	response, err := p.http.Get(configurationURL)
	if err != nil {
		return "", fmt.Errorf("OIDC discovery: %w", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return "", fmt.Errorf("OIDC discovery returned HTTP %d", response.StatusCode)
	}
	var configuration struct {
		TokenEndpoint string `json:"token_endpoint"`
	}
	if err := json.NewDecoder(io.LimitReader(response.Body, 1<<20)).Decode(&configuration); err != nil || configuration.TokenEndpoint == "" {
		return "", errors.New("OIDC discovery response is invalid")
	}
	issuerURL, issuerErr := url.Parse(p.issuer)
	tokenURL, tokenErr := url.Parse(configuration.TokenEndpoint)
	if issuerErr != nil || tokenErr != nil || tokenURL.Scheme != "https" || tokenURL.User != nil ||
		tokenURL.RawQuery != "" || tokenURL.Fragment != "" || !strings.EqualFold(tokenURL.Host, issuerURL.Host) {
		return "", errors.New("OIDC token endpoint violates the HTTPS origin policy")
	}
	form := url.Values{
		"grant_type": {"password"}, "client_id": {clientID},
		"username": {username}, "password": {password}, "scope": {"openid"},
	}
	request, _ := http.NewRequest(http.MethodPost, configuration.TokenEndpoint, strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response, err = p.http.Do(request)
	if err != nil {
		return "", fmt.Errorf("OIDC token request: %w", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		var oauthError struct {
			Code        string `json:"error"`
			Description string `json:"error_description"`
		}
		_ = json.NewDecoder(io.LimitReader(response.Body, 1<<16)).Decode(&oauthError)
		if oauthError.Code != "" {
			category := ""
			switch strings.ToLower(strings.TrimSpace(oauthError.Description)) {
			case "invalid user credentials":
				category = "/invalid_credentials"
			case "account is not fully set up":
				category = "/account_incomplete"
			}
			return "", fmt.Errorf("OIDC token request returned HTTP %d (%s%s)", response.StatusCode, oauthError.Code, category)
		}
		return "", fmt.Errorf("OIDC token request returned HTTP %d", response.StatusCode)
	}
	var token struct {
		AccessToken string `json:"access_token"`
	}
	if err := json.NewDecoder(io.LimitReader(response.Body, 1<<20)).Decode(&token); err != nil || token.AccessToken == "" {
		return "", errors.New("OIDC token response is invalid")
	}
	if err := p.validateAccessToken(token.AccessToken, clientID); err != nil {
		return "", err
	}
	return token.AccessToken, nil
}

func (p *probe) validateAccessToken(raw, expectedParty string) error {
	parts := strings.Split(raw, ".")
	if len(parts) != 3 {
		return errors.New("OIDC access token is not a JWT")
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return errors.New("OIDC access token payload is invalid")
	}
	defer clear(payload)
	var claims struct {
		Issuer          string          `json:"iss"`
		Audience        json.RawMessage `json:"aud"`
		Subject         string          `json:"sub"`
		AuthorizedParty string          `json:"azp"`
		TokenType       string          `json:"typ"`
		TokenID         string          `json:"jti"`
		ExpiresAt       int64           `json:"exp"`
	}
	if err := json.Unmarshal(payload, &claims); err != nil {
		return errors.New("OIDC access token claims are invalid")
	}
	if claims.Issuer != p.issuer || claims.AuthorizedParty != expectedParty || claims.TokenType != "Bearer" ||
		claims.Subject == "" || claims.TokenID == "" || claims.ExpiresAt <= time.Now().Unix() ||
		!audienceContains(claims.Audience, "termlink-server") {
		return errors.New("OIDC access token contract is invalid")
	}
	return nil
}

func audienceContains(raw json.RawMessage, expected string) bool {
	var values []string
	if json.Unmarshal(raw, &values) == nil {
		for _, value := range values {
			if value == expected {
				return true
			}
		}
		return false
	}
	var value string
	return json.Unmarshal(raw, &value) == nil && value == expected
}

func (p *probe) verifyPublicEndpoints() error {
	for _, path := range []string{"/readyz", "/v1/client-config"} {
		response, err := p.http.Get(p.root + path)
		if err != nil {
			return fmt.Errorf("GET %s: %w", path, err)
		}
		_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 1<<20))
		_ = response.Body.Close()
		if response.StatusCode != http.StatusOK {
			return fmt.Errorf("GET %s returned HTTP %d", path, response.StatusCode)
		}
	}
	return nil
}

// verifyAnonymousRegistration exercises only browser-safe GET requests. It
// proves the public portal, OIDC login page, hosted registration form, and
// localized client-side failures are connected without creating an account or
// loading any managed acceptance credential. Keycloak native self-registration
// must stay absent so it cannot bypass TermLink registration policy.
func (p *probe) verifyAnonymousRegistration() error {
	issuer, err := url.Parse(p.issuer)
	if err != nil || !strings.EqualFold(issuer.Scheme, "https") || issuer.Host == "" || issuer.User != nil ||
		issuer.RawQuery != "" || issuer.Fragment != "" {
		return errors.New("anonymous registration issuer is invalid")
	}
	publicOrigin := &url.URL{Scheme: issuer.Scheme, Host: issuer.Host}
	jar, err := cookiejar.New(nil)
	if err != nil {
		return errors.New("anonymous registration cookie store is unavailable")
	}
	browser := *p.http
	browser.Jar = jar
	browser.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }

	loginResponse, err := browser.Get(publicOrigin.String() + "/login")
	if err != nil {
		return fmt.Errorf("anonymous public login: %w", err)
	}
	_, _ = io.Copy(io.Discard, io.LimitReader(loginResponse.Body, maxHTMLBytes+1))
	_ = loginResponse.Body.Close()
	if loginResponse.StatusCode != http.StatusFound && loginResponse.StatusCode != http.StatusSeeOther {
		return fmt.Errorf("anonymous public login returned HTTP %d", loginResponse.StatusCode)
	}
	authorizationURL, err := loginResponse.Location()
	if err != nil || validateAuthorizationURL(authorizationURL, issuer, publicOrigin) != nil {
		return errors.New("public login did not return a same-origin OIDC PKCE authorization redirect")
	}

	identityResponse, err := browser.Get(authorizationURL.String())
	if err != nil {
		return fmt.Errorf("anonymous OIDC login page: %w", err)
	}
	identityPage, err := readBoundedHTML(identityResponse)
	if err != nil {
		return fmt.Errorf("anonymous OIDC login page: %w", err)
	}
	if identityResponse.StatusCode != http.StatusOK {
		return fmt.Errorf("anonymous OIDC login page returned HTTP %d", identityResponse.StatusCode)
	}
	if !usesTermLinkIdentityTheme(identityPage) {
		return errors.New("anonymous OIDC login page did not use the TermLink theme")
	}
	if _, ok := registrationURLFromHTML(identityPage, authorizationURL); ok {
		return errors.New("anonymous OIDC login page exposes native provider registration")
	}

	registrationResponse, err := browser.Get(publicOrigin.String() + "/register")
	if err != nil {
		return fmt.Errorf("anonymous hosted registration page: %w", err)
	}
	registrationPage, err := readBoundedHTML(registrationResponse)
	if err != nil {
		return fmt.Errorf("anonymous hosted registration page: %w", err)
	}
	if registrationResponse.StatusCode != http.StatusOK {
		return fmt.Errorf("anonymous hosted registration page returned HTTP %d", registrationResponse.StatusCode)
	}
	if !bytes.Contains(registrationPage, []byte("identity-form-shell")) ||
		!hasHTMLAttribute(registrationPage, "form", "id", "registration-form") {
		return errors.New("anonymous hosted registration page is not the TermLink registration form")
	}
	for _, field := range []string{"username", "email", "password", "password_confirm"} {
		if !hasHTMLAttribute(registrationPage, "input", "name", field) {
			return fmt.Errorf("anonymous hosted registration form is missing the %s field", field)
		}
	}
	if !hasHTMLAttribute(registrationPage, "input", "name", "nickname") {
		return errors.New("anonymous hosted registration form is missing the nickname field")
	}

	registrationScriptResponse, err := browser.Get(publicOrigin.String() + "/assets/register.js")
	if err != nil {
		return fmt.Errorf("anonymous hosted registration script: %w", err)
	}
	registrationScript, err := readBoundedHTML(registrationScriptResponse)
	if err != nil {
		return fmt.Errorf("anonymous hosted registration script: %w", err)
	}
	if registrationScriptResponse.StatusCode != http.StatusOK {
		return fmt.Errorf("anonymous hosted registration script returned HTTP %d", registrationScriptResponse.StatusCode)
	}
	if !bytes.Contains(registrationScript, []byte("注册服务暂时不可用")) ||
		!bytes.Contains(registrationScript, []byte("Registration is temporarily unavailable")) {
		return errors.New("anonymous hosted registration failures are not localized in Chinese and English")
	}
	return nil
}

func usesTermLinkIdentityTheme(page []byte) bool {
	return bytes.Contains(page, []byte("identity-form-shell")) ||
		bytes.Contains(page, []byte("identity-card"))
}

func readBoundedHTML(response *http.Response) ([]byte, error) {
	if response == nil || response.Body == nil {
		return nil, errors.New("HTML response is unavailable")
	}
	defer response.Body.Close()
	page, err := io.ReadAll(io.LimitReader(response.Body, maxHTMLBytes+1))
	if err != nil {
		return nil, errors.New("HTML response could not be read")
	}
	if len(page) > maxHTMLBytes {
		return nil, errors.New("HTML response exceeded the size limit")
	}
	return page, nil
}

func validateAuthorizationURL(candidate, issuer, publicOrigin *url.URL) error {
	if !sameHTTPSOrigin(candidate, publicOrigin) || candidate.User != nil || candidate.Fragment != "" ||
		candidate.Path != strings.TrimRight(issuer.Path, "/")+"/protocol/openid-connect/auth" {
		return errors.New("OIDC authorization URL violates the origin policy")
	}
	query := candidate.Query()
	if query.Get("client_id") != "termlink-web" || query.Get("response_type") != "code" ||
		query.Get("redirect_uri") != publicOrigin.String()+"/auth/callback" ||
		query.Get("code_challenge_method") != "S256" || strings.TrimSpace(query.Get("code_challenge")) == "" ||
		strings.TrimSpace(query.Get("state")) == "" || strings.TrimSpace(query.Get("nonce")) == "" ||
		!spaceSeparatedContains(query.Get("scope"), "openid") {
		return errors.New("OIDC authorization URL violates the PKCE contract")
	}
	return nil
}

func registrationURLFromHTML(page []byte, base *url.URL) (*url.URL, bool) {
	for _, match := range anchorHrefPattern.FindAllSubmatch(page, -1) {
		raw := match[1]
		if len(raw) == 0 {
			raw = match[2]
		}
		parsed, err := url.Parse(html.UnescapeString(string(raw)))
		if err != nil {
			continue
		}
		resolved := base.ResolveReference(parsed)
		if strings.Contains(resolved.Path, "/login-actions/registration") {
			return resolved, true
		}
	}
	return nil, false
}

func sameHTTPSOrigin(candidate, expected *url.URL) bool {
	return candidate != nil && expected != nil && strings.EqualFold(candidate.Scheme, "https") &&
		strings.EqualFold(candidate.Scheme, expected.Scheme) && strings.EqualFold(candidate.Host, expected.Host)
}

func validateDiscoveredListItem(response map[string]any, itemID uuid.UUID, ownerField string, ownerID uuid.UUID) error {
	items, ok := response["items"].([]any)
	if !ok {
		return errors.New("items field is invalid")
	}
	for _, raw := range items {
		item, ok := raw.(map[string]any)
		if !ok || fmt.Sprint(item["id"]) != itemID.String() {
			continue
		}
		if fmt.Sprint(item[ownerField]) != ownerID.String() {
			return fmt.Errorf("item %s has invalid %s", itemID, ownerField)
		}
		return nil
	}
	return fmt.Errorf("item %s is missing", itemID)
}

func validatePCSupportedShells(response map[string]any, pcID uuid.UUID, expected []string) error {
	items, ok := response["items"].([]any)
	if !ok {
		return errors.New("items field is invalid")
	}
	for _, raw := range items {
		item, ok := raw.(map[string]any)
		if !ok || fmt.Sprint(item["id"]) != pcID.String() {
			continue
		}
		shells, ok := item["supported_shells"].([]any)
		if !ok || len(shells) != len(expected) {
			return fmt.Errorf("PC %s has invalid supported_shells", pcID)
		}
		for index := range expected {
			if fmt.Sprint(shells[index]) != expected[index] {
				return fmt.Errorf("PC %s has invalid supported_shells", pcID)
			}
		}
		return nil
	}
	return fmt.Errorf("PC %s is missing", pcID)
}

func spaceSeparatedContains(value, expected string) bool {
	for _, field := range strings.Fields(value) {
		if field == expected {
			return true
		}
	}
	return false
}

func hasHTMLAttribute(page []byte, tag, attribute, value string) bool {
	pattern := regexp.MustCompile(`(?is)<` + regexp.QuoteMeta(tag) + `\b[^>]*\b` + regexp.QuoteMeta(attribute) +
		`\s*=\s*(?:"` + regexp.QuoteMeta(value) + `"|'` + regexp.QuoteMeta(value) + `')`)
	return pattern.Match(page)
}

func newIdentity() (*identity, error) {
	public, private, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return nil, err
	}
	exchange := make([]byte, 32)
	if _, err := rand.Read(exchange); err != nil {
		clear(private)
		return nil, err
	}
	return &identity{id: uuid.New(), public: public, private: private, exchange: exchange}, nil
}

func loadOrCreatePCIdentity(path, scope string) (*identity, uuid.UUID, error) {
	if strings.TrimSpace(scope) == "" {
		return nil, uuid.Nil, errors.New("acceptance probe identity scope is required")
	}
	if value, pcID, storedScope, err := loadPCIdentity(path); err == nil {
		if storedScope == scope {
			return value, pcID, nil
		}
		value.destroy()
		// A device ID cannot move between OIDC issuer scopes. This file is a
		// managed acceptance fixture, so rotate it before registering locally.
		if err := os.Remove(path); err != nil {
			return nil, uuid.Nil, fmt.Errorf("rotate acceptance probe identity: %w", err)
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil, uuid.Nil, err
	}
	value, err := newIdentity()
	if err != nil {
		return nil, uuid.Nil, err
	}
	pcID := uuid.New()
	stored := storedPCIdentity{
		Scope:      scope,
		DeviceID:   value.id.String(),
		PCID:       pcID.String(),
		PrivateKey: base64.RawURLEncoding.EncodeToString(value.private),
		Exchange:   base64.RawURLEncoding.EncodeToString(value.exchange),
	}
	raw, err := json.Marshal(stored)
	if err != nil {
		value.destroy()
		return nil, uuid.Nil, err
	}
	defer clear(raw)
	if err := writePrivateFile(path, raw); err != nil {
		value.destroy()
		if errors.Is(err, os.ErrExist) {
			loaded, loadedPCID, loadedScope, loadErr := loadPCIdentity(path)
			if loadErr != nil {
				return nil, uuid.Nil, loadErr
			}
			if loadedScope != scope {
				loaded.destroy()
				return nil, uuid.Nil, errors.New("acceptance probe identity scope changed concurrently")
			}
			return loaded, loadedPCID, nil
		}
		return nil, uuid.Nil, err
	}
	return value, pcID, nil
}

func loadPCIdentity(path string) (*identity, uuid.UUID, string, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, uuid.Nil, "", err
	}
	if !info.Mode().IsRegular() || (runtime.GOOS != "windows" && info.Mode().Perm()&0o077 != 0) {
		return nil, uuid.Nil, "", errors.New("acceptance probe identity permissions are unsafe")
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, uuid.Nil, "", err
	}
	defer clear(raw)
	var stored storedPCIdentity
	if err := json.Unmarshal(raw, &stored); err != nil {
		return nil, uuid.Nil, "", errors.New("acceptance probe identity is invalid")
	}
	deviceID, err := uuid.Parse(stored.DeviceID)
	if err != nil {
		return nil, uuid.Nil, "", errors.New("acceptance probe device ID is invalid")
	}
	pcID, err := uuid.Parse(stored.PCID)
	if err != nil {
		return nil, uuid.Nil, "", errors.New("acceptance probe PC ID is invalid")
	}
	privateKey, privateErr := base64.RawURLEncoding.DecodeString(stored.PrivateKey)
	exchange, exchangeErr := base64.RawURLEncoding.DecodeString(stored.Exchange)
	if privateErr != nil || exchangeErr != nil || len(privateKey) != ed25519.PrivateKeySize || len(exchange) != 32 {
		clear(privateKey)
		clear(exchange)
		return nil, uuid.Nil, "", errors.New("acceptance probe key material is invalid")
	}
	public := make([]byte, ed25519.PublicKeySize)
	copy(public, privateKey[ed25519.SeedSize:])
	return &identity{id: deviceID, public: public, private: privateKey, exchange: exchange}, pcID, stored.Scope, nil
}

func writePrivateFile(path string, raw []byte) error {
	directory := filepath.Dir(path)
	if err := os.MkdirAll(directory, 0o700); err != nil {
		return err
	}
	file, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return err
	}
	written := false
	defer func() {
		_ = file.Close()
		if !written {
			_ = os.Remove(path)
		}
	}()
	if _, err := file.Write(raw); err != nil {
		return err
	}
	if err := file.Sync(); err != nil {
		return err
	}
	if err := file.Close(); err != nil {
		return err
	}
	written = true
	return nil
}

func (i *identity) destroy() {
	clear(i.private)
	clear(i.exchange)
}

func (p *probe) register(device *identity, deviceType, name string) error {
	now := time.Now().UnixMilli()
	nonce, err := randomBase64(16)
	if err != nil {
		return err
	}
	signing := base64.RawURLEncoding.EncodeToString(device.public)
	exchange := base64.RawURLEncoding.EncodeToString(device.exchange)
	keyID := "acceptance-key-" + device.id.String()
	canonical := strings.Join([]string{
		registerDomain, device.id.String(), deviceType, name, signing, exchange,
		keyID, fmt.Sprint(now), nonce,
	}, "\n")
	body, err := json.Marshal(map[string]any{
		"id": device.id, "type": deviceType, "name": name,
		"signing_public_key": signing, "exchange_public_key": exchange, "key_id": keyID,
		"timestamp_ms": now, "nonce": nonce,
		"signature": base64.RawURLEncoding.EncodeToString(ed25519.Sign(device.private, []byte(canonical))),
	})
	if err != nil {
		return err
	}
	request, _ := http.NewRequest(http.MethodPost, p.root+"/v1/devices/register", bytes.NewReader(body))
	token, err := p.tokenFor(device)
	if err != nil {
		clear(body)
		return err
	}
	request.Header.Set("Authorization", "Bearer "+token)
	request.Header.Set("Content-Type", "application/json")
	response, err := p.http.Do(request)
	clear(body)
	if err != nil {
		return fmt.Errorf("register %s: %w", deviceType, err)
	}
	defer response.Body.Close()
	_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 1<<20))
	if response.StatusCode != http.StatusCreated && response.StatusCode != http.StatusConflict {
		return fmt.Errorf("register %s returned HTTP %d", deviceType, response.StatusCode)
	}
	return nil
}

func (p *probe) signedJSON(device *identity, method, path string, value any, idempotency string, expected int) (map[string]any, error) {
	var body []byte
	var err error
	if value != nil {
		body, err = json.Marshal(value)
		if err != nil {
			return nil, err
		}
	}
	defer clear(body)
	request, _ := http.NewRequest(method, p.root+path, bytes.NewReader(body))
	token, err := p.tokenFor(device)
	if err != nil {
		return nil, err
	}
	request.Header.Set("Authorization", "Bearer "+token)
	if len(body) > 0 {
		request.Header.Set("Content-Type", "application/json")
	}
	if idempotency != "" {
		request.Header.Set("Idempotency-Key", idempotency)
	}
	if err := addProof(request.Header, device, method, path, body); err != nil {
		return nil, err
	}
	response, err := p.http.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	raw, err := io.ReadAll(io.LimitReader(response.Body, 1<<20))
	if err != nil {
		return nil, err
	}
	defer clear(raw)
	if response.StatusCode != expected {
		return nil, fmt.Errorf("%s %s returned HTTP %d", method, path, response.StatusCode)
	}
	if len(bytes.TrimSpace(raw)) == 0 {
		return map[string]any{}, nil
	}
	var decoded map[string]any
	decoder := json.NewDecoder(bytes.NewReader(raw))
	decoder.UseNumber()
	if err := decoder.Decode(&decoded); err != nil {
		return nil, err
	}
	return decoded, nil
}

func (p *probe) connectRealtime(device *identity, role string) (*websocket.Conn, error) {
	target := "/v1/realtime"
	headers := http.Header{}
	token, err := p.tokenFor(device)
	if err != nil {
		return nil, err
	}
	headers.Set("Authorization", "Bearer "+token)
	if err := addProof(headers, device, http.MethodGet, target, nil); err != nil {
		return nil, err
	}
	websocketURL := "wss" + strings.TrimPrefix(p.root, "https") + target
	connection, response, err := p.dialer.Dial(websocketURL, headers)
	if response != nil && response.Body != nil {
		_ = response.Body.Close()
	}
	if err != nil {
		return nil, err
	}
	_ = connection.SetWriteDeadline(time.Now().Add(15 * time.Second))
	capabilities := []string{
		"cumulative_ack",
		"e2ee_x25519_chacha20poly1305",
		"flow_control_v1",
		"lease_v1",
		"bounded_event_replay_v1",
		"host_history_v1",
		"liveness_probe_v1",
	}
	expectDirectDataPlane := os.Getenv("TERMLINK_ACCEPTANCE_EXPECT_DIRECT_ROUTING") == "1"
	if expectDirectDataPlane {
		capabilities = append(
			capabilities,
			"direct_transport_data_v1",
			"direct_session_cutover_v1",
		)
	}
	hello, _ := json.Marshal(map[string]any{
		"role": role, "protocol_versions": []int{1},
		"capabilities": capabilities,
	})
	if err := connection.WriteMessage(websocket.TextMessage, hello); err != nil {
		connection.Close()
		return nil, err
	}
	_ = connection.SetReadDeadline(time.Now().Add(15 * time.Second))
	messageType, welcome, err := connection.ReadMessage()
	if err != nil || messageType != websocket.TextMessage ||
		validateProductionRealtimeWelcome(welcome, expectDirectDataPlane) != nil {
		connection.Close()
		return nil, errors.New("Realtime welcome is invalid")
	}
	_ = connection.SetReadDeadline(time.Time{})
	_ = connection.SetWriteDeadline(time.Time{})
	return connection, nil
}

func validateProductionRealtimeWelcome(welcomeBytes []byte, expectDirectDataPlane bool) error {
	var welcome struct {
		Type            string   `json:"type"`
		ProtocolVersion int      `json:"protocol_version"`
		ConnectionID    string   `json:"connection_id"`
		Capabilities    []string `json:"capabilities"`
	}
	if err := json.Unmarshal(welcomeBytes, &welcome); err != nil ||
		welcome.Type != "welcome" || welcome.ProtocolVersion != 1 {
		return errors.New("Realtime welcome structure is invalid")
	}
	if connectionID, err := uuid.Parse(welcome.ConnectionID); err != nil || connectionID == uuid.Nil {
		return errors.New("Realtime welcome connection ID is invalid")
	}
	hasData := slices.Contains(welcome.Capabilities, "direct_transport_data_v1")
	hasCutover := slices.Contains(welcome.Capabilities, "direct_session_cutover_v1")
	if hasData != expectDirectDataPlane || hasCutover != expectDirectDataPlane {
		return errors.New("Realtime Direct capability bundle does not match the acceptance routing gate")
	}
	return nil
}

func (p *probe) tokenFor(device *identity) (string, error) {
	if device == nil {
		return "", errors.New("acceptance device identity is missing")
	}
	token := p.tokens[device.id]
	if token == "" {
		return "", errors.New("acceptance device token is missing")
	}
	return token, nil
}

func addProof(headers http.Header, device *identity, method, target string, body []byte) error {
	timestamp := fmt.Sprint(time.Now().UnixMilli())
	nonce, err := randomBase64(16)
	if err != nil {
		return err
	}
	hash := sha256.Sum256(body)
	canonical := strings.Join([]string{
		deviceProofDomain, strings.ToUpper(method), target, timestamp, nonce, hex.EncodeToString(hash[:]),
	}, "\n")
	headers.Set("X-TermLink-Device", device.id.String())
	headers.Set("X-TermLink-Timestamp", timestamp)
	headers.Set("X-TermLink-Nonce", nonce)
	headers.Set("X-TermLink-Signature", base64.RawURLEncoding.EncodeToString(ed25519.Sign(device.private, []byte(canonical))))
	return nil
}

func opaqueEnvelope(sender, recipient, session uuid.UUID, message realtimewire.MessageType, direction realtimewire.Direction, sequence uint64) ([]byte, error) {
	keyID := []byte("acceptance-key-v1")
	nonce := make([]byte, 12)
	ciphertext := make([]byte, 32)
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	if _, err := rand.Read(ciphertext); err != nil {
		return nil, err
	}
	return (realtimewire.Envelope{
		ProtocolVersion: 1, MessageType: message, SessionID: session[:], SenderDeviceID: sender[:],
		Direction: direction, Sequence: sequence, SentAtMS: time.Now().UnixMilli(), KeyID: keyID,
		Nonce: nonce, Ciphertext: ciphertext, RecipientDeviceID: recipient[:],
	}).Marshal()
}

func routeAndMatch(sender, recipient *websocket.Conn, frame []byte) error {
	_ = sender.SetWriteDeadline(time.Now().Add(5 * time.Second))
	if err := sender.WriteMessage(websocket.BinaryMessage, frame); err != nil {
		return err
	}
	_ = recipient.SetReadDeadline(time.Now().Add(5 * time.Second))
	messageType, received, err := recipient.ReadMessage()
	if err != nil {
		return err
	}
	if messageType != websocket.BinaryMessage || !bytes.Equal(received, frame) {
		return errors.New("Relay changed or misrouted the opaque Envelope")
	}
	_ = recipient.SetReadDeadline(time.Time{})
	return nil
}

func randomBase64(length int) (string, error) {
	value := make([]byte, length)
	if _, err := rand.Read(value); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(value), nil
}

func clear(value []byte) {
	for index := range value {
		value[index] = 0
	}
}
