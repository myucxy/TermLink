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
	maxHistoryBytes            = 64 << 20
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
	root          string
	issuer        string
	historyOrigin *url.URL
	tokens        map[uuid.UUID]string
	http          *http.Client
	dialer        websocket.Dialer
}

type presignedRequest struct {
	URL       string              `json:"url"`
	Method    string              `json:"method"`
	Headers   map[string][]string `json:"headers"`
	ExpiresAt time.Time           `json:"expires_at"`
}

func main() {
	if err := run(); err != nil {
		writeProbeFailure(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println("Production OIDC/TLS/API/Realtime acceptance probe passed.")
}

func writeProbeFailure(writer io.Writer, err error) {
	// Probe errors can wrap callback URLs, presigned query credentials, OIDC
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
	historyOriginRaw := strings.TrimRight(os.Getenv("TERMLINK_ACCEPTANCE_HISTORY_ORIGIN"), "/")
	if historyOriginRaw == "" {
		historyOriginRaw = "https://history.127.0.0.1.sslip.io:9443"
	}
	identityPath := os.Getenv("TERMLINK_ACCEPTANCE_PROBE_IDENTITY_FILE")
	username := strings.TrimSpace(os.Getenv("TERMLINK_ACCEPTANCE_USER_NAME"))
	if username == "" {
		username = "acceptance-probe"
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
	historyOrigin, err := url.Parse(historyOriginRaw)
	if err != nil || !strings.EqualFold(historyOrigin.Scheme, "https") || historyOrigin.Host == "" ||
		historyOrigin.User != nil || historyOrigin.Path != "" || historyOrigin.RawQuery != "" || historyOrigin.Fragment != "" {
		return errors.New("acceptance history origin must be an HTTPS origin")
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
		root: root, issuer: issuer, historyOrigin: historyOrigin, tokens: make(map[uuid.UUID]string), http: client,
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
	pcToken, err := p.acquireToken(username, string(bytes.TrimSpace(password)), "termlink-acceptance-cli")
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
	keyEpoch, ok := positiveInt64Field(recipients, "key_epoch")
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
	input, err := opaqueEnvelope(android.id, pc.id, sessionID, realtimewire.InputEvent, realtimewire.AndroidToPC, 2)
	if err != nil {
		return err
	}
	if err := routeAndMatch(androidSocket, pcSocket, input); err != nil {
		return fmt.Errorf("route leased input: %w", err)
	}
	output, err := opaqueEnvelope(pc.id, android.id, sessionID, realtimewire.OutputChunk, realtimewire.PCToAndroid, 1)
	if err != nil {
		return err
	}
	if err := routeAndMatch(pcSocket, androidSocket, output); err != nil {
		return fmt.Errorf("route subscribed output: %w", err)
	}
	if _, err := p.signedJSON(pc, http.MethodDelete, "/v1/sessions/"+sessionID.String()+"/lease", nil, uuid.NewString(), http.StatusOK); err != nil {
		return fmt.Errorf("force release lease: %w", err)
	}
	historyDownload, err := p.verifyHistory(pc, android.id, keyEpoch, sessionID)
	if err != nil {
		return fmt.Errorf("verify production history object data plane: %w", err)
	}
	if _, err := p.signedJSON(pc, http.MethodPut, "/v1/sessions/"+sessionID.String(), map[string]any{
		"pc_id": pcID, "shell_type": "powershell", "title_cipher": "", "state": "EXITED",
		"columns": 80, "rows": 24, "last_output_seq": 3, "ended_at": time.Now().UTC(),
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
		return fmt.Errorf("delete history session: %w", err)
	}
	// Session deletion commits the catalog tombstone and a durable object-delete
	// outbox event atomically. The object store side is intentionally asynchronous
	// and at-least-once, so allow the dispatcher a bounded interval to converge.
	if err := p.waitForPresignedStatus(historyDownload, http.StatusNotFound, 15*time.Second); err != nil {
		return fmt.Errorf("wait for deleted history object: %w", err)
	}
	return nil
}

func (p *probe) verifyDirectTransport(source, target *identity) error {
	config, err := p.signedJSON(source, http.MethodGet, "/v1/transport/config", nil, "", http.StatusOK)
	if err != nil {
		return fmt.Errorf("verify Direct transport config: %w", err)
	}
	protocol, ok := positiveInt64Field(config, "protocol_version")
	if !ok || protocol != 1 || config["direct_routing_permitted"] != false {
		return errors.New("Direct transport config is not fail-closed protocol v1")
	}
	capabilities, ok := config["capabilities"].([]any)
	if !ok || !containsString(capabilities, "direct_transport_shadow_multipair_v2") {
		return errors.New("Direct transport multipair capability is unavailable")
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
	hello, err := json.Marshal(map[string]any{
		"type":              "CLIENT_HELLO",
		"role":              role,
		"protocol_versions": []int{1},
		"capabilities": []string{
			"direct_transport_v1",
			"direct_transport_shadow_multipair_v2",
		},
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
	if err := validateDirectSignalWelcome(welcomeBytes); err != nil {
		closeWebSocket(connection)
		return nil, err
	}
	_ = connection.SetReadDeadline(time.Time{})
	_ = connection.SetWriteDeadline(time.Time{})
	return connection, nil
}

func validateDirectSignalWelcome(welcomeBytes []byte) error {
	var welcome struct {
		Type                string   `json:"type"`
		ProtocolVersion     int      `json:"protocol_version"`
		Capabilities        []string `json:"capabilities"`
		HeartbeatIntervalMS int64    `json:"heartbeat_interval_ms"`
		MaxSignalFrameBytes int64    `json:"max_signal_frame_bytes"`
	}
	if err := json.Unmarshal(welcomeBytes, &welcome); err != nil ||
		welcome.Type != "SERVER_WELCOME" ||
		welcome.ProtocolVersion != 1 ||
		welcome.HeartbeatIntervalMS < 1_000 ||
		welcome.HeartbeatIntervalMS > 45_000 ||
		welcome.MaxSignalFrameBytes != 65536 ||
		len(welcome.Capabilities) != 2 ||
		welcome.Capabilities[0] != "direct_transport_shadow_v1" ||
		welcome.Capabilities[1] != "direct_transport_shadow_multipair_v2" {
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

func (p *probe) waitForPresignedStatus(request presignedRequest, expected int, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	var lastStatus int
	for {
		status, err := p.presignedStatus(request, nil)
		if err != nil {
			return err
		}
		lastStatus = status
		if status == expected {
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("object returned HTTP %d, expected %d before timeout", lastStatus, expected)
		}
		time.Sleep(250 * time.Millisecond)
	}
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

func (p *probe) verifyHistory(pc *identity, recipientDeviceID uuid.UUID, keyEpoch int64, sessionID uuid.UUID) (presignedRequest, error) {
	ciphertext := []byte("production-history-ciphertext-v1")
	digest := sha256.Sum256(ciphertext)
	created, err := p.signedJSON(pc, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/history/uploads", map[string]any{
		"kind":              "CHUNK",
		"key_id":            historyRecipientKeyID(recipientDeviceID, keyEpoch),
		"ciphertext_sha256": base64.RawURLEncoding.EncodeToString(digest[:]),
		"size_bytes":        len(ciphertext),
		"start_seq":         1,
		"end_seq":           3,
	}, uuid.NewString(), http.StatusCreated)
	if err != nil {
		return presignedRequest{}, fmt.Errorf("create upload: %w", err)
	}
	uploadID, ok := stringField(created, "upload_id")
	if !ok {
		return presignedRequest{}, errors.New("create upload response has no upload id")
	}
	upload, err := decodePresigned(created["upload"])
	if err != nil {
		return presignedRequest{}, fmt.Errorf("decode upload URL: %w", err)
	}
	if err := p.validateHistoryURL(upload, http.MethodPut); err != nil {
		return presignedRequest{}, err
	}
	if err := p.putPresigned(upload, ciphertext); err != nil {
		return presignedRequest{}, fmt.Errorf("presigned upload: %w", err)
	}
	commitPath := "/v1/sessions/" + sessionID.String() + "/history/uploads/" + uploadID + "/commit"
	if _, err := p.signedJSON(pc, http.MethodPost, commitPath, nil, uuid.NewString(), http.StatusCreated); err != nil {
		return presignedRequest{}, fmt.Errorf("commit upload: %w", err)
	}
	listed, err := p.signedJSON(pc, http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history", nil, "", http.StatusOK)
	if err != nil {
		return presignedRequest{}, fmt.Errorf("list history: %w", err)
	}
	items, ok := listed["items"].([]any)
	if !ok || len(items) != 1 {
		return presignedRequest{}, fmt.Errorf("list history returned %d items", len(items))
	}
	item, ok := items[0].(map[string]any)
	if !ok {
		return presignedRequest{}, errors.New("history item has invalid shape")
	}
	download, err := decodePresigned(item["download"])
	if err != nil {
		return presignedRequest{}, fmt.Errorf("decode download URL: %w", err)
	}
	if err := p.validateHistoryURL(download, http.MethodGet); err != nil {
		return presignedRequest{}, err
	}
	downloaded, err := p.getPresigned(download)
	if err != nil {
		return presignedRequest{}, fmt.Errorf("presigned download: %w", err)
	}
	if !bytes.Equal(downloaded, ciphertext) {
		return presignedRequest{}, errors.New("downloaded ciphertext differs from uploaded bytes")
	}

	if err := p.verifyHistoryRejects(upload, ciphertext); err != nil {
		return presignedRequest{}, err
	}
	if err := p.verifyHistorySizeBoundary(pc, recipientDeviceID, keyEpoch, sessionID); err != nil {
		return presignedRequest{}, err
	}
	return download, nil
}

func (p *probe) verifyHistoryRejects(upload presignedRequest, body []byte) error {
	parsed, err := url.Parse(upload.URL)
	if err != nil {
		return fmt.Errorf("parse upload URL: %w", err)
	}

	unsigned := *parsed
	unsigned.RawQuery = ""
	status, err := p.presignedStatusURL(http.MethodPut, unsigned.String(), nil, nil, 0)
	if err != nil {
		return fmt.Errorf("unsigned history request: %w", err)
	}
	if status >= 200 && status < 300 {
		return errors.New("unsigned history request was accepted")
	}

	tampered := *parsed
	query := tampered.Query()
	signature := query.Get("X-Amz-Signature")
	if len(signature) < 2 {
		return errors.New("presigned upload has no signature to tamper")
	}
	last := signature[len(signature)-1]
	if last == '0' {
		last = '1'
	} else {
		last = '0'
	}
	query.Set("X-Amz-Signature", signature[:len(signature)-1]+string(last))
	tampered.RawQuery = query.Encode()
	status, err = p.presignedStatusURL(http.MethodPut, tampered.String(), upload.Headers, bytes.NewReader(body), int64(len(body)))
	if err != nil {
		return fmt.Errorf("tampered history request: %w", err)
	}
	if status >= 200 && status < 300 {
		return errors.New("tampered history signature was accepted")
	}

	status, err = p.presignedStatusURL(http.MethodPost, parsed.String(), upload.Headers, bytes.NewReader(body), int64(len(body)))
	if err != nil {
		return fmt.Errorf("unsupported history method request: %w", err)
	}
	if status >= 200 && status < 300 {
		return errors.New("POST history request was accepted")
	}

	wrongBucket := *parsed
	wrongBucket.Path = "/other-bucket/accounts/acceptance-test.bin"
	status, err = p.presignedStatusURL(http.MethodGet, wrongBucket.String(), nil, nil, 0)
	if err != nil {
		return fmt.Errorf("wrong-bucket history request: %w", err)
	}
	if status >= 200 && status < 300 {
		return errors.New("wrong-bucket history request was accepted")
	}

	return nil
}

func (p *probe) verifyHistorySizeBoundary(pc *identity, recipientDeviceID uuid.UUID, keyEpoch int64, sessionID uuid.UUID) error {
	digest := sha256.New()
	if _, err := io.CopyN(digest, &zeroReader{}, maxHistoryBytes); err != nil {
		return fmt.Errorf("hash maximum-size history fixture: %w", err)
	}
	created, err := p.signedJSON(pc, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/history/uploads", map[string]any{
		"kind":              "CHUNK",
		"key_id":            historyRecipientKeyID(recipientDeviceID, keyEpoch),
		"ciphertext_sha256": base64.RawURLEncoding.EncodeToString(digest.Sum(nil)),
		"size_bytes":        maxHistoryBytes,
		"start_seq":         4,
		"end_seq":           4,
	}, uuid.NewString(), http.StatusCreated)
	if err != nil {
		return fmt.Errorf("create maximum-size history upload: %w", err)
	}
	upload, err := decodePresigned(created["upload"])
	if err != nil {
		return fmt.Errorf("decode maximum-size upload URL: %w", err)
	}
	if err := p.validateHistoryURL(upload, http.MethodPut); err != nil {
		return err
	}
	response, err := p.presignedRequest(upload.Method, upload.URL, upload.Headers, io.LimitReader(&zeroReader{}, maxHistoryBytes), maxHistoryBytes)
	if err != nil {
		return fmt.Errorf("maximum-size history upload: %w", err)
	}
	_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 1<<20))
	response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return fmt.Errorf("maximum-size history upload returned HTTP %d", response.StatusCode)
	}

	// Send only the oversized request headers until the public gateway returns
	// its bounded 413. Without Expect: 100-continue, an outer reverse proxy can
	// buffer the full 64 MiB+ fixture and translate the intended rejection into
	// a timeout/502 before the TermLink upload gate sees the Content-Length.
	oversizedHeaders := make(map[string][]string, len(upload.Headers)+1)
	for name, values := range upload.Headers {
		oversizedHeaders[name] = append([]string(nil), values...)
	}
	oversizedHeaders["Expect"] = []string{"100-continue"}
	status, err := p.presignedStatusURL(upload.Method, upload.URL, oversizedHeaders,
		io.LimitReader(&zeroReader{}, maxHistoryBytes+1), maxHistoryBytes+1)
	if err != nil {
		return fmt.Errorf("over-limit history request: %w", err)
	}
	if status != http.StatusRequestEntityTooLarge {
		return fmt.Errorf("over-limit history request returned HTTP %d, want 413", status)
	}
	return nil
}

func historyRecipientKeyID(recipientDeviceID uuid.UUID, keyEpoch int64) string {
	return fmt.Sprintf("history-v1:%s:%08d", strings.ReplaceAll(recipientDeviceID.String(), "-", ""), keyEpoch)
}

func (p *probe) putPresigned(request presignedRequest, body []byte) error {
	response, err := p.presignedRequest(request.Method, request.URL, request.Headers, bytes.NewReader(body), int64(len(body)))
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return fmt.Errorf("upload returned HTTP %d (%s; %s)", response.StatusCode, sanitizedS3Code(response.Body), presignedDiagnostics(request))
	}
	_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 1<<20))
	return nil
}

func (p *probe) getPresigned(request presignedRequest) ([]byte, error) {
	response, err := p.presignedRequest(request.Method, request.URL, request.Headers, nil, 0)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return nil, fmt.Errorf("download returned HTTP %d (%s)", response.StatusCode, sanitizedS3Code(response.Body))
	}
	value, err := io.ReadAll(io.LimitReader(response.Body, maxHistoryBytes+1))
	if err != nil {
		return nil, err
	}
	if len(value) > maxHistoryBytes {
		return nil, errors.New("download exceeded history size limit")
	}
	return value, nil
}

func sanitizedS3Code(body io.Reader) string {
	value, err := io.ReadAll(io.LimitReader(body, 1<<16))
	if err != nil {
		return "unavailable"
	}
	start := bytes.Index(value, []byte("<Code>"))
	if start < 0 {
		return "unavailable"
	}
	start += len("<Code>")
	end := bytes.Index(value[start:], []byte("</Code>"))
	if end < 0 || end > 128 {
		return "unavailable"
	}
	code := strings.TrimSpace(string(value[start : start+end]))
	if code == "" || strings.ContainsAny(code, "\r\n<>?&= ") {
		return "unavailable"
	}
	return code
}

func presignedDiagnostics(request presignedRequest) string {
	parsed, err := url.Parse(request.URL)
	if err != nil {
		return "url-invalid"
	}
	names := make([]string, 0, len(request.Headers))
	for name := range request.Headers {
		names = append(names, strings.ToLower(name))
	}
	return fmt.Sprintf("host=%s path=%s method=%s header_names=%s signed_headers=%s", parsed.Host, parsed.EscapedPath(), request.Method, strings.Join(names, ","), parsed.Query().Get("X-Amz-SignedHeaders"))
}

func (p *probe) presignedStatus(request presignedRequest, body io.Reader) (int, error) {
	length := int64(0)
	if body != nil {
		if seeker, ok := body.(io.Seeker); ok {
			position, err := seeker.Seek(0, io.SeekCurrent)
			if err != nil {
				return 0, err
			}
			end, err := seeker.Seek(0, io.SeekEnd)
			if err != nil {
				return 0, err
			}
			_, _ = seeker.Seek(position, io.SeekStart)
			length = end - position
		}
	}
	return p.presignedStatusURL(request.Method, request.URL, request.Headers, body, length)
}

func (p *probe) presignedStatusURL(method, rawURL string, headers map[string][]string, body io.Reader, length int64) (int, error) {
	response, err := p.presignedRequest(method, rawURL, headers, body, length)
	if err != nil {
		return 0, err
	}
	defer response.Body.Close()
	_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 1<<20))
	return response.StatusCode, nil
}

func (p *probe) presignedRequest(method, rawURL string, headers map[string][]string, body io.Reader, length int64) (*http.Response, error) {
	request, err := http.NewRequest(method, rawURL, body)
	if err != nil {
		return nil, err
	}
	if length > 0 {
		request.ContentLength = length
	}
	for name, values := range headers {
		if strings.EqualFold(name, "host") || strings.EqualFold(name, "content-length") {
			continue
		}
		for _, value := range values {
			request.Header.Add(name, value)
		}
	}
	// SigV4 signs the content headers returned by the server. Host is supplied
	// by the URL and must not be duplicated as a normal header.
	if parsed, err := url.Parse(rawURL); err == nil {
		query := parsed.Query()
		if signedHeaders := query.Get("X-Amz-SignedHeaders"); signedHeaders != "" {
			for _, name := range strings.Split(signedHeaders, ";") {
				if strings.EqualFold(name, "host") || strings.EqualFold(name, "content-length") {
					continue
				}
				if name == "content-type" {
					request.Header.Set("Content-Type", "application/octet-stream")
				}
			}
		}
	}
	return p.http.Do(request)
}

func (p *probe) validateHistoryURL(request presignedRequest, expectedMethod string) error {
	if request.Method != expectedMethod {
		return fmt.Errorf("history presigned method=%q, want %q", request.Method, expectedMethod)
	}
	parsed, err := url.Parse(request.URL)
	if err != nil || !strings.EqualFold(parsed.Scheme, p.historyOrigin.Scheme) || !strings.EqualFold(parsed.Host, p.historyOrigin.Host) ||
		!strings.HasPrefix(parsed.EscapedPath(), "/termlink-history/accounts/") {
		return errors.New("history presigned URL did not target the TLS object host and bucket path")
	}
	query := parsed.Query()
	for _, name := range []string{"X-Amz-Algorithm", "X-Amz-Credential", "X-Amz-Date", "X-Amz-Expires", "X-Amz-SignedHeaders", "X-Amz-Signature"} {
		if strings.TrimSpace(query.Get(name)) == "" {
			return fmt.Errorf("history presigned URL is missing %s", name)
		}
	}
	return nil
}

func decodePresigned(value any) (presignedRequest, error) {
	raw, err := json.Marshal(value)
	if err != nil {
		return presignedRequest{}, err
	}
	var request presignedRequest
	if err := json.Unmarshal(raw, &request); err != nil {
		return presignedRequest{}, err
	}
	if request.URL == "" || request.Method == "" {
		return presignedRequest{}, errors.New("presigned response is incomplete")
	}
	return request, nil
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

type zeroReader struct{}

func (*zeroReader) Read(value []byte) (int, error) {
	clear(value)
	return len(value), nil
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
	if !bytes.Contains(identityPage, []byte("identity-form-shell")) {
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
	hello, _ := json.Marshal(map[string]any{
		"role": role, "protocol_versions": []int{1},
		"capabilities": []string{"cumulative_ack", "e2ee_x25519_chacha20poly1305", "flow_control_v1", "lease_v1", "bounded_event_replay_v1"},
	})
	if err := connection.WriteMessage(websocket.TextMessage, hello); err != nil {
		connection.Close()
		return nil, err
	}
	_ = connection.SetReadDeadline(time.Now().Add(15 * time.Second))
	messageType, welcome, err := connection.ReadMessage()
	if err != nil || messageType != websocket.TextMessage || !bytes.Contains(welcome, []byte(`"type":"welcome"`)) {
		connection.Close()
		return nil, errors.New("Realtime welcome is invalid")
	}
	_ = connection.SetReadDeadline(time.Time{})
	_ = connection.SetWriteDeadline(time.Time{})
	return connection, nil
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
