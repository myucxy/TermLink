package auth

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

const (
	defaultKeycloakAdminTimeout = 10 * time.Second
	maxKeycloakAdminBody        = 256 << 10
)

var (
	ErrKeycloakAdminUnavailable = errors.New("identity administration is unavailable")
	ErrKeycloakAdminRejected    = errors.New("identity administration rejected the request")
)

type KeycloakAdminConfig struct {
	Issuer               string
	BackchannelIssuer    string
	AdminBaseURL         string
	ClientID             string
	ClientSecret         string
	RegistrationProofKey []byte
	RequestTimeout       time.Duration
	HTTPClient           *http.Client
}

type RegistrationIdentityLookupStatus string

const (
	RegistrationIdentityAbsent    RegistrationIdentityLookupStatus = "ABSENT"
	RegistrationIdentityExact     RegistrationIdentityLookupStatus = "EXACT"
	RegistrationIdentityAmbiguous RegistrationIdentityLookupStatus = "AMBIGUOUS"
)

type RegistrationIdentityLookup struct {
	Status RegistrationIdentityLookupStatus
	UserID string
}

type RealmSMTPConfig struct {
	Host        string
	Port        int
	Security    string
	FromName    string
	FromAddress string
	ReplyTo     string
	Username    string
	Password    string
}

type KeycloakAdminClient struct {
	tokenURL       string
	usersURL       string
	realmURL       string
	clientID       string
	clientSecret   string
	proofKey       []byte
	requestTimeout time.Duration
	http           *http.Client

	tokenMu     sync.Mutex
	accessToken string
	tokenExpiry time.Time
	clock       func() time.Time
}

func NewKeycloakAdminClient(config KeycloakAdminConfig) (*KeycloakAdminClient, error) {
	issuer, realm, err := parseKeycloakIssuer(config.Issuer)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(config.ClientID) == "" || strings.TrimSpace(config.ClientSecret) == "" {
		return nil, errors.New("Keycloak admin client credentials are required")
	}
	if len(config.RegistrationProofKey) < 32 {
		return nil, errors.New("Keycloak registration proof key must contain at least 32 bytes")
	}
	timeout := config.RequestTimeout
	if timeout <= 0 {
		timeout = defaultKeycloakAdminTimeout
	}
	if timeout < 100*time.Millisecond || timeout > 30*time.Second {
		return nil, errors.New("Keycloak admin request timeout is outside the supported range")
	}
	client := config.HTTPClient
	if client == nil {
		client = &http.Client{Timeout: timeout}
	}
	// Never forward the client credential or bearer token across a redirect.
	// Keep the caller's transport (for custom CA/proxy policy) but own redirect
	// behavior at this security boundary.
	isolatedClient := *client
	isolatedClient.CheckRedirect = func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }
	tokenIssuer := issuer
	if strings.TrimSpace(config.BackchannelIssuer) != "" {
		tokenIssuer, err = parseKeycloakBackchannelIssuer(config.BackchannelIssuer, realm)
		if err != nil {
			return nil, err
		}
	}
	token := *tokenIssuer
	token.Path = strings.TrimRight(token.Path, "/") + "/protocol/openid-connect/token"
	adminBase, err := keycloakAdminBaseURL(config.AdminBaseURL, tokenIssuer)
	if err != nil {
		return nil, err
	}
	admin := *adminBase
	admin.Path = strings.TrimRight(admin.Path, "/") + "/admin/realms/" + url.PathEscape(realm)
	admin.RawQuery = ""
	admin.Fragment = ""
	realmURL := admin.String()
	admin.Path += "/users"
	return &KeycloakAdminClient{
		tokenURL: token.String(), usersURL: admin.String(), realmURL: realmURL, clientID: strings.TrimSpace(config.ClientID),
		clientSecret: config.ClientSecret, proofKey: append([]byte(nil), config.RegistrationProofKey...),
		requestTimeout: timeout, http: &isolatedClient, clock: time.Now,
	}, nil
}

func parseKeycloakBackchannelIssuer(raw, realm string) (*url.URL, error) {
	issuer, err := url.Parse(strings.TrimRight(strings.TrimSpace(raw), "/"))
	if err != nil || issuer == nil || issuer.Opaque != "" || issuer.Host == "" || issuer.User != nil ||
		issuer.RawQuery != "" || issuer.Fragment != "" || issuer.RawPath != "" ||
		(issuer.Scheme != "https" && issuer.Scheme != "http") {
		return nil, errors.New("Keycloak backchannel issuer is invalid")
	}
	parts := strings.Split(strings.Trim(issuer.Path, "/"), "/")
	if len(parts) < 2 || parts[len(parts)-2] != "realms" || parts[len(parts)-1] != realm {
		return nil, errors.New("Keycloak backchannel issuer must identify the public realm")
	}
	if issuer.Scheme == "http" && (issuer.Port() == "" || !isLoopbackOrSingleLabelHost(issuer.Hostname())) {
		return nil, errors.New("Keycloak backchannel HTTP issuer must target a loopback or single-label private service")
	}
	return issuer, nil
}

func keycloakAdminBaseURL(raw string, issuer *url.URL) (*url.URL, error) {
	if strings.TrimSpace(raw) == "" {
		derived := *issuer
		parts := strings.Split(strings.Trim(issuer.Path, "/"), "/")
		prefix := strings.Join(parts[:len(parts)-2], "/")
		derived.Path = ""
		if prefix != "" {
			derived.Path = "/" + prefix
		}
		derived.RawPath = ""
		derived.RawQuery = ""
		derived.Fragment = ""
		return &derived, nil
	}
	admin, err := url.Parse(strings.TrimSpace(raw))
	if err != nil || admin == nil || admin.Opaque != "" || admin.Host == "" || admin.User != nil ||
		admin.RawQuery != "" || admin.Fragment != "" || admin.RawPath != "" ||
		(admin.Scheme != "https" && admin.Scheme != "http") {
		return nil, errors.New("Keycloak admin base URL is invalid")
	}
	hostname := strings.TrimSpace(admin.Hostname())
	if hostname == "" || strings.Contains(admin.Path, "\\") || strings.Contains(admin.Path, "//") {
		return nil, errors.New("Keycloak admin base URL is invalid")
	}
	for _, segment := range strings.Split(strings.Trim(admin.Path, "/"), "/") {
		if segment == "." || segment == ".." {
			return nil, errors.New("Keycloak admin base URL is invalid")
		}
	}
	if admin.Scheme == "http" {
		// Cleartext is accepted only for an explicit Docker/loopback hop with
		// an explicit port. This supports the existing private Compose network
		// without allowing a bearer token to be sent to a routed HTTP host.
		if admin.Port() == "" || !isLoopbackOrSingleLabelHost(hostname) {
			return nil, errors.New("Keycloak admin HTTP base URL must target a loopback or single-label private service")
		}
	} else if ip := net.ParseIP(hostname); ip != nil &&
		(ip.IsUnspecified() || ip.IsMulticast() || ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast()) {
		return nil, errors.New("Keycloak admin base URL targets a reserved address")
	}
	admin.Path = strings.TrimRight(admin.Path, "/")
	return admin, nil
}

func isLoopbackOrSingleLabelHost(hostname string) bool {
	if strings.EqualFold(hostname, "localhost") {
		return true
	}
	if ip := net.ParseIP(hostname); ip != nil {
		return ip.IsLoopback()
	}
	if len(hostname) == 0 || len(hostname) > 63 || strings.Contains(hostname, ".") ||
		hostname[0] == '-' || hostname[len(hostname)-1] == '-' {
		return false
	}
	for _, char := range hostname {
		if (char < 'a' || char > 'z') && (char < 'A' || char > 'Z') &&
			(char < '0' || char > '9') && char != '-' {
			return false
		}
	}
	return true
}

func parseKeycloakIssuer(raw string) (*url.URL, string, error) {
	issuer, err := url.Parse(strings.TrimRight(strings.TrimSpace(raw), "/"))
	if err != nil || issuer.Scheme != "https" || issuer.Host == "" || issuer.User != nil || issuer.RawQuery != "" || issuer.Fragment != "" {
		return nil, "", errors.New("Keycloak issuer must be an HTTPS realm URL")
	}
	parts := strings.Split(strings.Trim(issuer.Path, "/"), "/")
	if len(parts) < 2 || parts[len(parts)-2] != "realms" || strings.TrimSpace(parts[len(parts)-1]) == "" {
		return nil, "", errors.New("Keycloak issuer must identify a realm")
	}
	return issuer, parts[len(parts)-1], nil
}

type keycloakUserRepresentation struct {
	ID         string              `json:"id"`
	TOTP       bool                `json:"totp"`
	Attributes map[string][]string `json:"attributes"`
}

// HasConfiguredOTP reports the current Keycloak TOTP binding for an exact OIDC
// subject. It intentionally returns no credential identifiers or secret data.
func (c *KeycloakAdminClient) HasConfiguredOTP(ctx context.Context, userID string) (bool, error) {
	if c == nil || !validKeycloakUserID(userID) {
		return false, ErrKeycloakAdminRejected
	}
	response, err := c.authorizedRequest(ctx, http.MethodGet, c.usersURL+"/"+url.PathEscape(userID))
	if err != nil {
		return false, err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return false, statusOnlyKeycloakError(response.StatusCode)
	}
	var user keycloakUserRepresentation
	if err := decodeBoundedJSON(response.Body, maxKeycloakAdminBody, &user); err != nil || user.ID != userID {
		return false, ErrKeycloakAdminUnavailable
	}
	return user.TOTP, nil
}

// LookupRegistrationAttempt returns EXACT only for one user carrying both the
// exact attempt UUID and its valid server HMAC. Invalid, duplicated, truncated,
// or conflicting results are AMBIGUOUS and never become mutation authority.
func (c *KeycloakAdminClient) LookupRegistrationAttempt(ctx context.Context, attemptID uuid.UUID) (RegistrationIdentityLookup, error) {
	if c == nil || attemptID == uuid.Nil {
		return RegistrationIdentityLookup{}, ErrKeycloakAdminUnavailable
	}
	endpoint, err := url.Parse(c.usersURL)
	if err != nil {
		return RegistrationIdentityLookup{}, ErrKeycloakAdminUnavailable
	}
	query := endpoint.Query()
	query.Set("q", RegistrationAttemptIDAttribute+":"+attemptID.String())
	query.Set("first", "0")
	query.Set("max", "3")
	query.Set("briefRepresentation", "false")
	endpoint.RawQuery = query.Encode()
	response, err := c.authorizedRequest(ctx, http.MethodGet, endpoint.String())
	if err != nil {
		return RegistrationIdentityLookup{}, err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return RegistrationIdentityLookup{}, statusOnlyKeycloakError(response.StatusCode)
	}
	var users []keycloakUserRepresentation
	if err := decodeBoundedJSON(response.Body, maxKeycloakAdminBody, &users); err != nil {
		return RegistrationIdentityLookup{}, ErrKeycloakAdminUnavailable
	}
	if len(users) == 0 {
		return RegistrationIdentityLookup{Status: RegistrationIdentityAbsent}, nil
	}
	if len(users) >= 3 {
		return RegistrationIdentityLookup{Status: RegistrationIdentityAmbiguous}, nil
	}
	matchedID := ""
	for _, user := range users {
		attempts := user.Attributes[RegistrationAttemptIDAttribute]
		proofs := user.Attributes[RegistrationAttemptProofAttribute]
		if len(attempts) != 1 || attempts[0] != attemptID.String() || len(proofs) != 1 ||
			!validRegistrationAttemptProof(c.proofKey, attemptID, proofs[0]) || !validKeycloakUserID(user.ID) {
			return RegistrationIdentityLookup{Status: RegistrationIdentityAmbiguous}, nil
		}
		if matchedID != "" {
			return RegistrationIdentityLookup{Status: RegistrationIdentityAmbiguous}, nil
		}
		matchedID = user.ID
	}
	return RegistrationIdentityLookup{Status: RegistrationIdentityExact, UserID: matchedID}, nil
}

func (c *KeycloakAdminClient) DeleteRegistrationIdentity(ctx context.Context, userID string) error {
	if c == nil || !validKeycloakUserID(userID) {
		return ErrKeycloakAdminRejected
	}
	response, err := c.authorizedRequest(ctx, http.MethodDelete, c.usersURL+"/"+url.PathEscape(userID))
	if err != nil {
		return err
	}
	defer response.Body.Close()
	switch response.StatusCode {
	case http.StatusNoContent, http.StatusNotFound:
		return nil
	default:
		return statusOnlyKeycloakError(response.StatusCode)
	}
}

// ConfigureRealmEmail updates the Keycloak SMTP transport and password-reset
// switch without changing realm-wide OTP or native-client authentication
// flows. The service account needs manage-realm in addition to its bounded
// user-management roles when this optional Web capability is enabled.
func (c *KeycloakAdminClient) ConfigureRealmEmail(ctx context.Context, config RealmSMTPConfig, passwordResetEnabled bool) error {
	if c == nil || strings.TrimSpace(c.realmURL) == "" {
		return ErrKeycloakAdminUnavailable
	}
	response, err := c.authorizedRequest(ctx, http.MethodGet, c.realmURL)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return statusOnlyKeycloakError(response.StatusCode)
	}
	var realm map[string]any
	if err = decodeBoundedJSON(response.Body, maxKeycloakAdminBody, &realm); err != nil {
		return err
	}
	realm["smtpServer"] = map[string]string{
		"host": config.Host, "port": strconv.Itoa(config.Port), "from": config.FromAddress,
		"fromDisplayName": config.FromName, "replyTo": config.ReplyTo,
		"auth": strconv.FormatBool(strings.TrimSpace(config.Username) != ""), "user": config.Username,
		"password": config.Password, "starttls": strconv.FormatBool(config.Security == "STARTTLS"),
		"ssl": strconv.FormatBool(config.Security == "TLS"),
	}
	realm["resetPasswordAllowed"] = passwordResetEnabled
	realm["editUsernameAllowed"] = false
	payload, err := json.Marshal(realm)
	if err != nil || len(payload) > maxKeycloakAdminBody {
		return ErrKeycloakAdminUnavailable
	}
	update, err := c.authorizedJSONRequest(ctx, http.MethodPut, c.realmURL, payload)
	if err != nil {
		return err
	}
	defer update.Body.Close()
	if update.StatusCode != http.StatusNoContent {
		return statusOnlyKeycloakError(update.StatusCode)
	}
	return nil
}

func (c *KeycloakAdminClient) MarkRegistrationEmailVerified(ctx context.Context, rawEmail string) error {
	email := strings.ToLower(strings.TrimSpace(rawEmail))
	if email == "" || len(email) > 320 || strings.ContainsAny(email, "\r\n\t ") || !strings.Contains(email, "@") {
		return ErrKeycloakAdminRejected
	}
	endpoint, err := url.Parse(c.usersURL)
	if err != nil {
		return ErrKeycloakAdminUnavailable
	}
	query := endpoint.Query()
	query.Set("email", email)
	query.Set("exact", "true")
	query.Set("first", "0")
	query.Set("max", "2")
	endpoint.RawQuery = query.Encode()
	response, err := c.authorizedRequest(ctx, http.MethodGet, endpoint.String())
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return statusOnlyKeycloakError(response.StatusCode)
	}
	var users []struct {
		ID    string `json:"id"`
		Email string `json:"email"`
	}
	if err = decodeBoundedJSON(response.Body, maxKeycloakAdminBody, &users); err != nil || len(users) != 1 ||
		!validKeycloakUserID(users[0].ID) || !strings.EqualFold(strings.TrimSpace(users[0].Email), email) {
		return ErrKeycloakAdminRejected
	}
	payload, _ := json.Marshal(map[string]any{"email": email, "emailVerified": true})
	update, err := c.authorizedJSONRequest(ctx, http.MethodPut, c.usersURL+"/"+url.PathEscape(users[0].ID), payload)
	if err != nil {
		return err
	}
	defer update.Body.Close()
	if update.StatusCode != http.StatusNoContent {
		return statusOnlyKeycloakError(update.StatusCode)
	}
	return nil
}

func (c *KeycloakAdminClient) UpdateNickname(ctx context.Context, userID, nickname string) error {
	if c == nil || !validKeycloakUserID(userID) || !validNickname(nickname) {
		return ErrKeycloakAdminRejected
	}
	return c.updateUser(ctx, userID, func(user map[string]any) error {
		attributes, _ := user["attributes"].(map[string]any)
		if attributes == nil {
			attributes = make(map[string]any)
		}
		attributes["nickname"] = []string{strings.TrimSpace(nickname)}
		user["attributes"] = attributes
		return nil
	})
}

func (c *KeycloakAdminClient) UpdateVerifiedEmail(ctx context.Context, userID, email string) error {
	email = strings.TrimSpace(email)
	if c == nil || !validKeycloakUserID(userID) || len(email) < 3 || len(email) > 320 ||
		strings.ContainsAny(email, "\r\n\t ") || !strings.Contains(email, "@") {
		return ErrKeycloakAdminRejected
	}
	return c.updateUser(ctx, userID, func(user map[string]any) error {
		user["email"] = email
		user["emailVerified"] = true
		return nil
	})
}

func (c *KeycloakAdminClient) updateUser(ctx context.Context, userID string, mutate func(map[string]any) error) error {
	response, err := c.authorizedRequest(ctx, http.MethodGet, c.usersURL+"/"+url.PathEscape(userID))
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return statusOnlyKeycloakError(response.StatusCode)
	}
	var user map[string]any
	if err = decodeBoundedJSON(response.Body, maxKeycloakAdminBody, &user); err != nil || mutate(user) != nil {
		return ErrKeycloakAdminRejected
	}
	payload, err := json.Marshal(user)
	if err != nil || len(payload) > maxKeycloakAdminBody {
		return ErrKeycloakAdminRejected
	}
	update, err := c.authorizedJSONRequest(ctx, http.MethodPut, c.usersURL+"/"+url.PathEscape(userID), payload)
	if err != nil {
		return err
	}
	defer update.Body.Close()
	if update.StatusCode == http.StatusConflict {
		return ErrKeycloakAdminRejected
	}
	if update.StatusCode != http.StatusNoContent {
		return statusOnlyKeycloakError(update.StatusCode)
	}
	return nil
}

func (c *KeycloakAdminClient) RegisterVerifiedEmail(ctx context.Context, input DirectRegistrationInput) error {
	return c.registerAccount(ctx, uuid.Nil, input, true)
}

func (c *KeycloakAdminClient) RegisterVerifiedEmailAttempt(ctx context.Context, attemptID uuid.UUID, input DirectRegistrationInput) error {
	if attemptID == uuid.Nil {
		return ErrKeycloakAdminRejected
	}
	return c.registerAccount(ctx, attemptID, input, true)
}

// Register creates an identity only after the TermLink API has enforced the
// current registration, invitation, verification, and rate-limit policies.
func (c *KeycloakAdminClient) Register(ctx context.Context, input DirectRegistrationInput) error {
	return c.registerAccount(ctx, uuid.Nil, input, false)
}

// RegisterAttempt retains the invitation-recovery proof on the created
// identity so a provider-committed but locally interrupted saga can reconcile.
func (c *KeycloakAdminClient) RegisterAttempt(ctx context.Context, attemptID uuid.UUID, input DirectRegistrationInput) error {
	if attemptID == uuid.Nil {
		return ErrKeycloakAdminRejected
	}
	return c.registerAccount(ctx, attemptID, input, false)
}

// registerAccount is the single identity-creation boundary. Verified e-mail
// registration reaches it only after TermLink has consumed a valid challenge,
// allowing the identity to start verified without a second provider mail flow.
func (c *KeycloakAdminClient) registerAccount(ctx context.Context, attemptID uuid.UUID, input DirectRegistrationInput, emailVerified bool) error {
	if c == nil {
		return ErrKeycloakAdminUnavailable
	}
	if strings.TrimSpace(input.Nickname) == "" {
		input.Nickname = strings.TrimSpace(input.Username)
	}
	if err := validateRegistrationInput(input); err != nil {
		return ErrKeycloakAdminRejected
	}
	attributes := map[string][]string{"nickname": {strings.TrimSpace(input.Nickname)}}
	if attemptID != uuid.Nil {
		proof, err := registrationAttemptProof(c.proofKey, attemptID)
		if err != nil {
			return ErrKeycloakAdminRejected
		}
		attributes[RegistrationAttemptIDAttribute] = []string{attemptID.String()}
		attributes[RegistrationAttemptProofAttribute] = []string{proof}
	}
	payload, err := json.Marshal(map[string]any{
		"username": strings.TrimSpace(input.Username), "email": strings.TrimSpace(input.Email),
		"emailVerified": emailVerified, "enabled": true, "attributes": attributes,
		"credentials": []map[string]any{{"type": "password", "value": input.Password, "temporary": false}},
	})
	if err != nil || len(payload) > maxKeycloakAdminBody {
		return ErrKeycloakAdminRejected
	}
	response, err := c.authorizedJSONRequest(ctx, http.MethodPost, c.usersURL, payload)
	if err != nil {
		if errors.Is(err, ErrRegistrationOutcomeUnknown) {
			return err
		}
		return ErrKeycloakAdminUnavailable
	}
	defer response.Body.Close()
	switch response.StatusCode {
	case http.StatusCreated:
		return nil
	case http.StatusBadRequest, http.StatusConflict, http.StatusUnprocessableEntity:
		return errRegistrationRejected
	default:
		return ErrRegistrationOutcomeUnknown
	}
}

func validKeycloakUserID(value string) bool {
	parsed, err := uuid.Parse(strings.TrimSpace(value))
	return err == nil && parsed != uuid.Nil && parsed.String() == strings.ToLower(strings.TrimSpace(value))
}

func (c *KeycloakAdminClient) authorizedRequest(ctx context.Context, method, target string) (*http.Response, error) {
	return c.authorizedJSONRequest(ctx, method, target, nil)
}

func (c *KeycloakAdminClient) authorizedJSONRequest(ctx context.Context, method, target string, body []byte) (*http.Response, error) {
	for attempt := 0; attempt < 2; attempt++ {
		token, err := c.token(ctx)
		if err != nil {
			return nil, err
		}
		requestCtx, cancel := context.WithTimeout(ctx, c.requestTimeout)
		request, err := http.NewRequestWithContext(requestCtx, method, target, bytes.NewReader(body))
		if err != nil {
			cancel()
			return nil, ErrKeycloakAdminUnavailable
		}
		request.Header.Set("Authorization", "Bearer "+token)
		request.Header.Set("Accept", "application/json")
		if len(body) > 0 {
			request.Header.Set("Content-Type", "application/json")
		}
		response, err := c.http.Do(request)
		if err != nil {
			cancel()
			if method == http.MethodPost && target == c.usersURL {
				// Once account creation reaches the transport, a timeout or
				// disconnect cannot prove whether Keycloak committed the user.
				return nil, ErrRegistrationOutcomeUnknown
			}
			return nil, ErrKeycloakAdminUnavailable
		}
		response.Body = &cancelOnCloseReadCloser{ReadCloser: response.Body, cancel: cancel}
		if response.StatusCode != http.StatusUnauthorized || attempt == 1 {
			return response, nil
		}
		response.Body.Close()
		c.invalidateToken(token)
	}
	return nil, ErrKeycloakAdminUnavailable
}

func (c *KeycloakAdminClient) token(ctx context.Context) (string, error) {
	c.tokenMu.Lock()
	defer c.tokenMu.Unlock()
	if c.accessToken != "" && c.clock().Add(30*time.Second).Before(c.tokenExpiry) {
		return c.accessToken, nil
	}
	form := url.Values{
		"grant_type":    {"client_credentials"},
		"client_id":     {c.clientID},
		"client_secret": {c.clientSecret},
	}
	requestCtx, cancel := context.WithTimeout(ctx, c.requestTimeout)
	defer cancel()
	request, err := http.NewRequestWithContext(requestCtx, http.MethodPost, c.tokenURL, strings.NewReader(form.Encode()))
	if err != nil {
		return "", ErrKeycloakAdminUnavailable
	}
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Accept", "application/json")
	response, err := c.http.Do(request)
	if err != nil {
		return "", ErrKeycloakAdminUnavailable
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return "", statusOnlyKeycloakError(response.StatusCode)
	}
	var payload struct {
		AccessToken string `json:"access_token"`
		TokenType   string `json:"token_type"`
		ExpiresIn   int64  `json:"expires_in"`
	}
	if err := decodeBoundedJSON(response.Body, 64<<10, &payload); err != nil ||
		strings.TrimSpace(payload.AccessToken) == "" || !strings.EqualFold(payload.TokenType, "Bearer") ||
		payload.ExpiresIn < 1 || payload.ExpiresIn > int64((24*time.Hour)/time.Second) {
		return "", ErrKeycloakAdminUnavailable
	}
	c.accessToken = payload.AccessToken
	c.tokenExpiry = c.clock().Add(time.Duration(payload.ExpiresIn) * time.Second)
	return c.accessToken, nil
}

func (c *KeycloakAdminClient) invalidateToken(token string) {
	c.tokenMu.Lock()
	defer c.tokenMu.Unlock()
	if c.accessToken == token {
		c.accessToken = ""
		c.tokenExpiry = time.Time{}
	}
}

func statusOnlyKeycloakError(status int) error {
	if status == http.StatusUnauthorized || status == http.StatusForbidden || status == http.StatusBadRequest {
		return ErrKeycloakAdminRejected
	}
	return ErrKeycloakAdminUnavailable
}

func decodeBoundedJSON(reader io.Reader, limit int64, target any) error {
	limited := io.LimitReader(reader, limit+1)
	data, err := io.ReadAll(limited)
	if err != nil || int64(len(data)) > limit {
		return ErrKeycloakAdminUnavailable
	}
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	if err := decoder.Decode(target); err != nil {
		return ErrKeycloakAdminUnavailable
	}
	var trailing any
	if err := decoder.Decode(&trailing); !errors.Is(err, io.EOF) {
		return ErrKeycloakAdminUnavailable
	}
	return nil
}

type cancelOnCloseReadCloser struct {
	io.ReadCloser
	cancel context.CancelFunc
}

func (r *cancelOnCloseReadCloser) Close() error {
	err := r.ReadCloser.Close()
	r.cancel()
	return err
}
