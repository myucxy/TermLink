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
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/realtimewire"
)

const (
	deviceProofDomain = "TERMLINK-DEVICE-PROOF-V1"
	registerDomain    = "TERMLINK-DEVICE-REGISTER-V1"
	maxHistoryBytes   = 64 << 20
	maxHTMLBytes      = 2 << 20
)

var anchorHrefPattern = regexp.MustCompile(`(?is)<a\b[^>]*\bhref\s*=\s*(?:"([^"]*)"|'([^']*)')[^>]*>`)

type identity struct {
	id       uuid.UUID
	public   ed25519.PublicKey
	private  ed25519.PrivateKey
	exchange []byte
}

type storedPCIdentity struct {
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

type presignedRequest struct {
	URL       string              `json:"url"`
	Method    string              `json:"method"`
	Headers   map[string][]string `json:"headers"`
	ExpiresAt time.Time           `json:"expires_at"`
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
	identityPath := os.Getenv("TERMLINK_ACCEPTANCE_PROBE_IDENTITY_FILE")
	username := strings.TrimSpace(os.Getenv("TERMLINK_ACCEPTANCE_USER_NAME"))
	if username == "" {
		username = "acceptance-probe"
	}
	if root == "" || issuer == "" || passwordPath == "" || caPath == "" || identityPath == "" {
		return errors.New("acceptance root, issuer, password file, CA file, and probe identity file are required")
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
	transport := &http.Transport{TLSClientConfig: tlsConfig.Clone()}
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

	pc, pcID, err := loadOrCreatePCIdentity(identityPath)
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
	if _, err := p.signedJSON(pc, http.MethodPut, "/v1/pcs/"+pcID.String(), map[string]any{
		"name": "Acceptance PC", "agent_version": "acceptance", "key_epoch": 1,
	}, "acceptance-pc-"+pcID.String(), http.StatusOK); err != nil {
		return fmt.Errorf("upsert PC: %w", err)
	}
	if err := p.register(android, "ANDROID", "Acceptance Android"); err != nil {
		return err
	}
	selfTrust, err := p.signedJSON(android, http.MethodPost, "/v1/devices/self/trust", map[string]any{}, uuid.NewString(), http.StatusOK)
	if err != nil {
		return fmt.Errorf("self-trust Android device: %w", err)
	}
	if selfTrust["status"] != "TRUSTED" || selfTrust["changed"] != true {
		return errors.New("Android self-trust response is invalid")
	}
	pcsBeforeGrant, err := p.signedJSON(android, http.MethodGet, "/v1/pcs", nil, "", http.StatusOK)
	if err != nil {
		return fmt.Errorf("list Android PC access before grant: %w", err)
	}
	if items, ok := pcsBeforeGrant["items"].([]any); !ok || len(items) != 0 {
		return errors.New("Android self-trust unexpectedly granted PC access")
	}
	if _, err := p.signedJSON(pc, http.MethodPost, "/v1/pcs/"+pcID.String()+"/grants", map[string]any{
		"device_id": android.id, "permission": "CONTROL",
	}, uuid.NewString(), http.StatusOK); err != nil {
		return fmt.Errorf("grant Android PC control: %w", err)
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
	if err := p.verifyHistory(pc, sessionID); err != nil {
		return fmt.Errorf("verify production history object data plane: %w", err)
	}
	return nil
}

func loadRoots(path string) (*x509.CertPool, error) {
	pem, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read acceptance CA: %w", err)
	}
	pool, err := x509.SystemCertPool()
	if err != nil || pool == nil {
		pool = x509.NewCertPool()
	}
	if !pool.AppendCertsFromPEM(pem) {
		return nil, errors.New("acceptance CA is invalid")
	}
	return pool, nil
}

func (p *probe) verifyHistory(pc *identity, sessionID uuid.UUID) error {
	ciphertext := []byte("production-history-ciphertext-v1")
	digest := sha256.Sum256(ciphertext)
	created, err := p.signedJSON(pc, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/history/uploads", map[string]any{
		"kind":              "CHUNK",
		"key_id":            "epoch-0001",
		"ciphertext_sha256": base64.RawURLEncoding.EncodeToString(digest[:]),
		"size_bytes":        len(ciphertext),
		"start_seq":         1,
		"end_seq":           3,
	}, uuid.NewString(), http.StatusCreated)
	if err != nil {
		return fmt.Errorf("create upload: %w", err)
	}
	uploadID, ok := stringField(created, "upload_id")
	if !ok {
		return errors.New("create upload response has no upload id")
	}
	upload, err := decodePresigned(created["upload"])
	if err != nil {
		return fmt.Errorf("decode upload URL: %w", err)
	}
	if err := validateHistoryURL(upload, http.MethodPut); err != nil {
		return err
	}
	if err := p.putPresigned(upload, ciphertext); err != nil {
		return fmt.Errorf("presigned upload: %w", err)
	}
	commitPath := "/v1/sessions/" + sessionID.String() + "/history/uploads/" + uploadID + "/commit"
	if _, err := p.signedJSON(pc, http.MethodPost, commitPath, nil, uuid.NewString(), http.StatusCreated); err != nil {
		return fmt.Errorf("commit upload: %w", err)
	}
	listed, err := p.signedJSON(pc, http.MethodGet, "/v1/sessions/"+sessionID.String()+"/history", nil, "", http.StatusOK)
	if err != nil {
		return fmt.Errorf("list history: %w", err)
	}
	items, ok := listed["items"].([]any)
	if !ok || len(items) != 1 {
		return fmt.Errorf("list history returned %d items", len(items))
	}
	item, ok := items[0].(map[string]any)
	if !ok {
		return errors.New("history item has invalid shape")
	}
	download, err := decodePresigned(item["download"])
	if err != nil {
		return fmt.Errorf("decode download URL: %w", err)
	}
	if err := validateHistoryURL(download, http.MethodGet); err != nil {
		return err
	}
	downloaded, err := p.getPresigned(download)
	if err != nil {
		return fmt.Errorf("presigned download: %w", err)
	}
	if !bytes.Equal(downloaded, ciphertext) {
		return errors.New("downloaded ciphertext differs from uploaded bytes")
	}

	if err := p.verifyHistoryRejects(upload, ciphertext); err != nil {
		return err
	}
	if err := p.verifyHistorySizeBoundary(pc, sessionID); err != nil {
		return err
	}
	if _, err := p.signedJSON(pc, http.MethodDelete, "/v1/sessions/"+sessionID.String(), nil, uuid.NewString(), http.StatusNoContent); err != nil {
		return fmt.Errorf("delete history session: %w", err)
	}
	status, err := p.presignedStatus(download, nil)
	if err != nil {
		return fmt.Errorf("request deleted object: %w", err)
	}
	if status != http.StatusNotFound {
		return fmt.Errorf("deleted history object returned HTTP %d", status)
	}
	return nil
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

func (p *probe) verifyHistorySizeBoundary(pc *identity, sessionID uuid.UUID) error {
	digest := sha256.New()
	if _, err := io.CopyN(digest, &zeroReader{}, maxHistoryBytes); err != nil {
		return fmt.Errorf("hash maximum-size history fixture: %w", err)
	}
	created, err := p.signedJSON(pc, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/history/uploads", map[string]any{
		"kind":              "CHUNK",
		"key_id":            "epoch-boundary",
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
	if err := validateHistoryURL(upload, http.MethodPut); err != nil {
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

	status, err := p.presignedStatusURL(upload.Method, upload.URL, upload.Headers,
		io.LimitReader(&zeroReader{}, maxHistoryBytes+1), maxHistoryBytes+1)
	if err != nil {
		return fmt.Errorf("over-limit history request: %w", err)
	}
	if status != http.StatusRequestEntityTooLarge {
		return fmt.Errorf("over-limit history request returned HTTP %d, want 413", status)
	}
	return nil
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

func validateHistoryURL(request presignedRequest, expectedMethod string) error {
	if request.Method != expectedMethod {
		return fmt.Errorf("history presigned method=%q, want %q", request.Method, expectedMethod)
	}
	parsed, err := url.Parse(request.URL)
	if err != nil || !strings.EqualFold(parsed.Scheme, "https") || !strings.EqualFold(parsed.Hostname(), "history.127.0.0.1.sslip.io") || parsed.Port() != "9443" ||
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
// proves the public portal, OIDC login page, registration link, custom theme,
// and real Keycloak registration form are connected without creating an
// account or loading any managed acceptance credential.
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
	registrationURL, ok := registrationURLFromHTML(identityPage, authorizationURL)
	if !ok || validateRegistrationURL(registrationURL, issuer, publicOrigin) != nil {
		return errors.New("anonymous OIDC login page has no usable same-origin registration link")
	}

	registrationResponse, err := browser.Get(registrationURL.String())
	if err != nil {
		return fmt.Errorf("anonymous OIDC registration page: %w", err)
	}
	registrationPage, err := readBoundedHTML(registrationResponse)
	if err != nil {
		return fmt.Errorf("anonymous OIDC registration page: %w", err)
	}
	if registrationResponse.StatusCode != http.StatusOK {
		return fmt.Errorf("anonymous OIDC registration page returned HTTP %d", registrationResponse.StatusCode)
	}
	if !bytes.Contains(registrationPage, []byte("identity-form-shell")) ||
		!hasHTMLAttribute(registrationPage, "form", "id", "kc-register-form") {
		return errors.New("anonymous OIDC registration page is not the themed Keycloak registration form")
	}
	for _, field := range []string{"username", "email", "password", "password-confirm"} {
		if !hasHTMLAttribute(registrationPage, "input", "name", field) {
			return fmt.Errorf("anonymous OIDC registration form is missing the %s field", field)
		}
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

func validateRegistrationURL(candidate, issuer, publicOrigin *url.URL) error {
	if !sameHTTPSOrigin(candidate, publicOrigin) || candidate.User != nil || candidate.Fragment != "" ||
		candidate.Path != strings.TrimRight(issuer.Path, "/")+"/login-actions/registration" {
		return errors.New("OIDC registration URL violates the origin policy")
	}
	return nil
}

func sameHTTPSOrigin(candidate, expected *url.URL) bool {
	return candidate != nil && expected != nil && strings.EqualFold(candidate.Scheme, "https") &&
		strings.EqualFold(candidate.Scheme, expected.Scheme) && strings.EqualFold(candidate.Host, expected.Host)
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

func loadOrCreatePCIdentity(path string) (*identity, uuid.UUID, error) {
	if value, pcID, err := loadPCIdentity(path); err == nil {
		return value, pcID, nil
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil, uuid.Nil, err
	}
	value, err := newIdentity()
	if err != nil {
		return nil, uuid.Nil, err
	}
	pcID := uuid.New()
	stored := storedPCIdentity{
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
			return loadPCIdentity(path)
		}
		return nil, uuid.Nil, err
	}
	return value, pcID, nil
}

func loadPCIdentity(path string) (*identity, uuid.UUID, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, uuid.Nil, err
	}
	if !info.Mode().IsRegular() || (runtime.GOOS != "windows" && info.Mode().Perm()&0o077 != 0) {
		return nil, uuid.Nil, errors.New("acceptance probe identity permissions are unsafe")
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, uuid.Nil, err
	}
	defer clear(raw)
	var stored storedPCIdentity
	if err := json.Unmarshal(raw, &stored); err != nil {
		return nil, uuid.Nil, errors.New("acceptance probe identity is invalid")
	}
	deviceID, err := uuid.Parse(stored.DeviceID)
	if err != nil {
		return nil, uuid.Nil, errors.New("acceptance probe device ID is invalid")
	}
	pcID, err := uuid.Parse(stored.PCID)
	if err != nil {
		return nil, uuid.Nil, errors.New("acceptance probe PC ID is invalid")
	}
	privateKey, privateErr := base64.RawURLEncoding.DecodeString(stored.PrivateKey)
	exchange, exchangeErr := base64.RawURLEncoding.DecodeString(stored.Exchange)
	if privateErr != nil || exchangeErr != nil || len(privateKey) != ed25519.PrivateKeySize || len(exchange) != 32 {
		clear(privateKey)
		clear(exchange)
		return nil, uuid.Nil, errors.New("acceptance probe key material is invalid")
	}
	public := make([]byte, ed25519.PublicKeySize)
	copy(public, privateKey[ed25519.SeedSize:])
	return &identity{id: deviceID, public: public, private: privateKey, exchange: exchange}, pcID, nil
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
	if err := json.Unmarshal(raw, &decoded); err != nil {
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
