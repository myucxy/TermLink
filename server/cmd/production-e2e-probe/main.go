package main

import (
	"bytes"
	"context"
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
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/realtimewire"
)

const (
	deviceProofDomain = "TERMLINK-DEVICE-PROOF-V1"
	registerDomain    = "TERMLINK-DEVICE-REGISTER-V1"
)

type identity struct {
	id       uuid.UUID
	public   ed25519.PublicKey
	private  ed25519.PrivateKey
	exchange []byte
}

type probe struct {
	root   string
	issuer string
	token  string
	http   *http.Client
	dialer websocket.Dialer
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "production acceptance probe failed:", err)
		os.Exit(1)
	}
	fmt.Println("Production OIDC/TLS/API/Realtime acceptance probe passed.")
}

func run() error {
	root := strings.TrimRight(os.Getenv("TERMLINK_ACCEPTANCE_ROOT_URL"), "/")
	issuer := strings.TrimRight(os.Getenv("TERMLINK_ACCEPTANCE_OIDC_ISSUER"), "/")
	passwordPath := os.Getenv("TERMLINK_ACCEPTANCE_USER_PASSWORD_FILE")
	caPath := os.Getenv("TERMLINK_ACCEPTANCE_CA_FILE")
	if root == "" || issuer == "" || passwordPath == "" || caPath == "" {
		return errors.New("acceptance root, issuer, password file, and CA file are required")
	}
	if !strings.HasPrefix(root, "https://") || !strings.HasPrefix(issuer, "https://") {
		return errors.New("acceptance endpoints must use HTTPS")
	}
	roots, err := loadRoots(caPath)
	if err != nil {
		return err
	}
	tlsConfig := &tls.Config{MinVersion: tls.VersionTLS12, RootCAs: roots}
	dialer := &net.Dialer{Timeout: 15 * time.Second}
	dialContext := func(ctx context.Context, network, address string) (net.Conn, error) {
		host, port, err := net.SplitHostPort(address)
		if err != nil {
			return nil, err
		}
		switch host {
		case "api.termlink.localhost", "id.termlink.localhost":
			host = "127.0.0.1"
		}
		return dialer.DialContext(ctx, network, net.JoinHostPort(host, port))
	}
	transport := &http.Transport{TLSClientConfig: tlsConfig.Clone(), DialContext: dialContext}
	client := &http.Client{Transport: transport, Timeout: 15 * time.Second}
	p := &probe{
		root: root, issuer: issuer, http: client,
		dialer: websocket.Dialer{
			TLSClientConfig: tlsConfig.Clone(), NetDialContext: dialContext,
			Subprotocols: []string{"termlink.realtime.v1"}, HandshakeTimeout: 15 * time.Second,
		},
	}
	defer transport.CloseIdleConnections()

	password, err := os.ReadFile(passwordPath)
	if err != nil || len(bytes.TrimSpace(password)) < 16 {
		return errors.New("acceptance user password file is unavailable")
	}
	defer clear(password)
	token, err := p.acquireToken(string(bytes.TrimSpace(password)))
	if err != nil {
		return err
	}
	p.token = token
	defer func() { p.token = "" }()

	if err := p.verifyPublicEndpoints(); err != nil {
		return err
	}
	pc, err := newIdentity()
	if err != nil {
		return err
	}
	defer pc.destroy()
	android, err := newIdentity()
	if err != nil {
		return err
	}
	defer android.destroy()

	if err := p.register(pc, "WINDOWS_PC", "Acceptance PC"); err != nil {
		return err
	}
	pcID, sessionID := uuid.New(), uuid.New()
	if _, err := p.signedJSON(pc, http.MethodPut, "/v1/pcs/"+pcID.String(), map[string]any{
		"name": "Acceptance PC", "agent_version": "acceptance", "key_epoch": 1,
	}, "acceptance-pc-"+pcID.String(), http.StatusOK); err != nil {
		return fmt.Errorf("upsert PC: %w", err)
	}
	if err := p.register(android, "ANDROID", "Acceptance Android"); err != nil {
		return err
	}
	challenge, err := p.signedJSON(pc, http.MethodPost, "/v1/pairing/challenges", map[string]any{
		"target_device_id": android.id, "pc_id": pcID, "permission": "CONTROL",
	}, uuid.NewString(), http.StatusCreated)
	if err != nil {
		return fmt.Errorf("create pairing challenge: %w", err)
	}
	challengeID, _ := challenge["challenge_id"].(string)
	code, _ := challenge["code"].(string)
	challengeNonce, _ := challenge["challenge_nonce"].(string)
	if challengeID == "" || code == "" || challengeNonce == "" {
		return errors.New("pairing response is incomplete")
	}
	if _, err := p.signedJSON(android, http.MethodPost, "/v1/pairing/challenges/"+challengeID+"/complete", map[string]any{
		"code": code, "challenge_nonce": challengeNonce,
	}, uuid.NewString(), http.StatusOK); err != nil {
		return fmt.Errorf("complete pairing challenge: %w", err)
	}
	if _, err := p.signedJSON(pc, http.MethodPut, "/v1/sessions/"+sessionID.String(), map[string]any{
		"pc_id": pcID, "shell_type": "powershell", "title_cipher": "", "state": "RUNNING",
		"columns": 80, "rows": 24, "last_output_seq": 1,
	}, uuid.NewString(), http.StatusOK); err != nil {
		return fmt.Errorf("upsert session: %w", err)
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
	if _, err := p.signedJSON(pc, http.MethodPost, "/v1/devices/"+android.id.String()+"/revoke", map[string]any{}, uuid.NewString(), http.StatusOK); err != nil {
		return fmt.Errorf("revoke Android device: %w", err)
	}
	_ = androidSocket.SetReadDeadline(time.Now().Add(5 * time.Second))
	if _, _, err := androidSocket.ReadMessage(); err == nil {
		return errors.New("revoked Android realtime connection remained open")
	}
	return nil
}

func loadRoots(path string) (*x509.CertPool, error) {
	pem, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read acceptance CA: %w", err)
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(pem) {
		return nil, errors.New("acceptance CA is invalid")
	}
	return pool, nil
}

func (p *probe) acquireToken(password string) (string, error) {
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
	form := url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-acceptance-cli"},
		"username": {"acceptance-user"}, "password": {password}, "scope": {"openid"},
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
	return token.AccessToken, nil
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
	request.Header.Set("Authorization", "Bearer "+p.token)
	request.Header.Set("Content-Type", "application/json")
	response, err := p.http.Do(request)
	clear(body)
	if err != nil {
		return fmt.Errorf("register %s: %w", deviceType, err)
	}
	defer response.Body.Close()
	_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 1<<20))
	if response.StatusCode != http.StatusCreated {
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
	request.Header.Set("Authorization", "Bearer "+p.token)
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
	if err := json.Unmarshal(raw, &decoded); err != nil {
		return nil, err
	}
	return decoded, nil
}

func (p *probe) connectRealtime(device *identity, role string) (*websocket.Conn, error) {
	target := "/v1/realtime"
	headers := http.Header{}
	headers.Set("Authorization", "Bearer "+p.token)
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
		"capabilities": []string{"cumulative_ack", "e2ee_x25519_chacha20poly1305", "lease_v1", "bounded_event_replay_v1"},
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
