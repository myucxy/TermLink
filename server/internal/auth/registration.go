package auth

// DirectRegistrationService submits the public OIDC registration form from
// the server-side boundary.  Mobile and desktop clients therefore never need
// to open an identity-service browser page or receive an OIDC administrator
// credential.  The service deliberately keeps response bodies bounded and
// never includes submitted credentials in errors or logs.

import (
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"fmt"
	"html"
	"io"
	"mime"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"regexp"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/google/uuid"
)

var (
	errRegistrationRejected       = errors.New("identity registration was rejected")
	ErrRegistrationOutcomeUnknown = errors.New("identity registration outcome is unknown")
	formActionPattern             = regexp.MustCompile(`(?is)<form[^>]*id=["']kc-register-form["'][^>]*action=["']([^"']+)["']`)
	formActionPatternAlt          = regexp.MustCompile(`(?is)<form[^>]*action=["']([^"']+)["'][^>]*id=["']kc-register-form["']`)
	registrationLinkPattern       = regexp.MustCompile(`(?is)href=["']([^"']*(?:login-actions/)?registration[^"']*)["']`)
	htmlClassPattern              = regexp.MustCompile(`(?is)(?:<|\s)class\s*=\s*["']([^"']*)["']`)
	htmlIDPattern                 = regexp.MustCompile(`(?is)(?:<|\s)id\s*=\s*["']([^"']*)["']`)
	ariaInvalidPattern            = regexp.MustCompile(`(?is)(?:<|\s)aria-invalid\s*=\s*["']true["']`)
)

type DirectRegistrationConfig struct {
	Issuer               string
	ClientID             string
	RedirectURI          string
	RegistrationProofKey []byte
	HTTPClient           *http.Client
}

type DirectRegistrationInput struct {
	Username string
	Nickname string
	Email    string
	Password string
	Confirm  string
	// Deprecated compatibility-only fields. They are never submitted to the
	// identity provider and can be removed after the legacy API window closes.
	FirstName string
	LastName  string
}

type DirectRegistrationService struct {
	issuer      *url.URL
	clientID    string
	redirectURI string
	proofKey    []byte
	http        *http.Client
}

const (
	RegistrationAttemptIDAttribute    = "termlink_registration_attempt_id"
	RegistrationAttemptProofAttribute = "termlink_registration_attempt_proof"
	RegistrationAttemptScope          = "termlink-registration-attempt"
	registrationAttemptProofDomain    = "termlink:keycloak-registration-attempt:v1"
)

func NewDirectRegistrationService(config DirectRegistrationConfig) (*DirectRegistrationService, error) {
	issuer, err := url.Parse(strings.TrimRight(strings.TrimSpace(config.Issuer), "/"))
	if err != nil || issuer.Scheme != "https" || issuer.Host == "" || issuer.User != nil || issuer.RawQuery != "" || issuer.Fragment != "" {
		return nil, errors.New("direct registration issuer must be an HTTPS origin")
	}
	if strings.TrimSpace(config.ClientID) == "" {
		return nil, errors.New("direct registration client ID is required")
	}
	redirect, err := url.Parse(strings.TrimSpace(config.RedirectURI))
	if err != nil || !validDirectRegistrationRedirect(redirect) {
		return nil, errors.New("direct registration redirect URI is invalid")
	}
	client := config.HTTPClient
	if client == nil {
		jar, jarErr := cookiejar.New(nil)
		if jarErr != nil {
			return nil, jarErr
		}
		client = &http.Client{Jar: jar, Timeout: 20 * time.Second, CheckRedirect: func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }}
	}
	return &DirectRegistrationService{
		issuer: issuer, clientID: strings.TrimSpace(config.ClientID), redirectURI: redirect.String(),
		proofKey: append([]byte(nil), config.RegistrationProofKey...), http: client,
	}, nil
}

func validDirectRegistrationRedirect(redirect *url.URL) bool {
	if redirect == nil || redirect.Scheme == "" || redirect.User != nil || redirect.RawQuery != "" || redirect.Fragment != "" {
		return false
	}
	if redirect.Scheme == "http" || redirect.Scheme == "https" {
		return redirect.Host != "" && redirect.Path != ""
	}
	// Android's native OIDC client uses this exact private-use scheme. It is
	// never dereferenced by the server and must not be broadened to arbitrary
	// schemes supplied through deployment configuration.
	return redirect.Scheme == "dev.termlink" && redirect.Host == "" && redirect.Path != ""
}

func (s *DirectRegistrationService) Register(ctx context.Context, input DirectRegistrationInput) error {
	return s.register(ctx, uuid.Nil, input)
}

func (s *DirectRegistrationService) register(ctx context.Context, attemptID uuid.UUID, input DirectRegistrationInput) error {
	if s == nil || s.http == nil || s.issuer == nil {
		return errors.New("direct registration is unavailable")
	}
	if strings.TrimSpace(input.Nickname) == "" {
		input.Nickname = strings.TrimSpace(input.Username)
	}
	if err := validateRegistrationInput(input); err != nil {
		return err
	}
	attemptProof := ""
	if attemptID != uuid.Nil {
		var err error
		attemptProof, err = registrationAttemptProof(s.proofKey, attemptID)
		if err != nil {
			return errRegistrationRejected
		}
	}
	session, err := s.newSession()
	if err != nil {
		return errRegistrationRejected
	}
	state, err := randomRegistrationText(32)
	if err != nil {
		return err
	}
	verifier, err := randomRegistrationText(32)
	if err != nil {
		return err
	}
	challengeBytes := sha256.Sum256([]byte(verifier))
	challenge := base64.RawURLEncoding.EncodeToString(challengeBytes[:])
	scopes := []string{"openid", "profile", "offline_access"}
	if attemptID != uuid.Nil {
		// The two recovery-only User Profile fields are selected by this
		// optional Keycloak client scope. Ordinary login and Account Console
		// sessions never request it, so the attributes are not user-visible.
		scopes = append(scopes, RegistrationAttemptScope)
	}
	query := url.Values{
		"client_id":             {s.clientID},
		"redirect_uri":          {s.redirectURI},
		"response_type":         {"code"},
		"scope":                 {strings.Join(scopes, " ")},
		"state":                 {state},
		"code_challenge":        {challenge},
		"code_challenge_method": {"S256"},
		"prompt":                {"create"},
	}
	authURL := *s.issuer
	authURL.Path = strings.TrimRight(authURL.Path, "/") + "/protocol/openid-connect/auth"
	authURL.RawQuery = query.Encode()
	page, err := session.get(ctx, authURL.String())
	if err != nil {
		return errRegistrationRejected
	}
	// Keycloak 26 honors prompt=create by returning the registration form from
	// the authorization endpoint itself. Older versions first return the login
	// page and require following its registration link. Accept both shapes, but
	// never follow an unrelated link when a valid provider form is already
	// present: doing so can move the browser session back to the login flow.
	registrationPage := page
	action, hidden, ok := registrationForm(registrationPage, s.issuer)
	if !ok {
		registrationURL, found := registrationURLFromPage(page, s.issuer)
		if !found {
			return errRegistrationRejected
		}
		nextPage, getErr := session.get(ctx, registrationURL)
		if getErr != nil {
			return errRegistrationRejected
		}
		registrationPage = nextPage
		action, hidden, ok = registrationForm(registrationPage, s.issuer)
	}
	if !ok {
		return errRegistrationRejected
	}
	for name, value := range hidden {
		if name == "password" || name == "password-confirm" || name == "username" || name == "email" {
			continue
		}
		if value != "" {
			hidden[name] = value
		}
	}
	form := url.Values{}
	for name, value := range hidden {
		form.Set(name, value)
	}
	form.Set("username", input.Username)
	form.Set("email", input.Email)
	form.Set("password", input.Password)
	form.Set("password-confirm", input.Confirm)
	nicknameField, nicknameFieldPresent := registrationAttributeField(registrationPage, "nickname")
	if nicknameFieldPresent {
		form.Set(nicknameField, input.Nickname)
	} else {
		// Declarative User Profile renders custom attributes by their exact
		// name. Keep the default for bounded test providers while the realm
		// contract independently requires the nickname profile attribute.
		form.Set("nickname", input.Nickname)
	}
	if attemptID != uuid.Nil {
		// These are not client-provided profile fields. The server writes both
		// values after copying the provider's hidden inputs, so a caller cannot
		// replace the correlation proof through the public registration JSON.
		attemptField, attemptOK := registrationAttributeField(registrationPage, RegistrationAttemptIDAttribute)
		proofField, proofOK := registrationAttributeField(registrationPage, RegistrationAttemptProofAttribute)
		if !attemptOK || !proofOK {
			// No provider POST has occurred, so it is authoritative that this
			// attempt did not create an uncorrelated identity.
			return errRegistrationRejected
		}
		form.Set(attemptField, attemptID.String())
		form.Set(proofField, attemptProof)
	}
	response, err := session.post(ctx, action, form)
	if err != nil {
		// A timeout or connection loss after the POST began cannot prove that
		// Keycloak did not commit the identity. Callers must leave invitation
		// inventory reserved for HMAC-backed reconciliation.
		return ErrRegistrationOutcomeUnknown
	}
	defer response.Body.Close()
	if response.StatusCode >= 300 && response.StatusCode < 400 {
		// Keycloak redirects a successful registration back into the auth flow.
		// The client performs the actual password grant immediately afterwards.
		location := response.Header.Get("Location")
		if isRegistrationCallback(location, s.redirectURI, state) {
			return nil
		}
		return ErrRegistrationOutcomeUnknown
	}
	if response.StatusCode == http.StatusOK {
		mediaType, _, contentTypeErr := mime.ParseMediaType(response.Header.Get("Content-Type"))
		if contentTypeErr != nil || !strings.EqualFold(mediaType, "text/html") {
			return ErrRegistrationOutcomeUnknown
		}
		body, readErr := readBounded(response.Body, 256*1024)
		if readErr != nil {
			return ErrRegistrationOutcomeUnknown
		}
		if isExplicitRegistrationSuccessPage(body) {
			return nil
		}
		if isExplicitRegistrationRejectionPage(body) {
			return errRegistrationRejected
		}
		return ErrRegistrationOutcomeUnknown
	}
	if response.StatusCode == http.StatusBadRequest || response.StatusCode == http.StatusUnauthorized ||
		response.StatusCode == http.StatusForbidden || response.StatusCode == http.StatusConflict ||
		response.StatusCode == http.StatusUnprocessableEntity || response.StatusCode == http.StatusTooManyRequests {
		return errRegistrationRejected
	}
	return ErrRegistrationOutcomeUnknown
}

// RegisterAttempt binds an invitation reservation to the identity created by
// Keycloak. Recovery accepts the attribute only with its server-generated HMAC
// proof, so a mutable Keycloak profile attribute is not authority by itself.
func (s *DirectRegistrationService) RegisterAttempt(ctx context.Context, attemptID uuid.UUID, input DirectRegistrationInput) error {
	if attemptID == uuid.Nil {
		return errRegistrationRejected
	}
	return s.register(ctx, attemptID, input)
}

func registrationAttemptProof(key []byte, attemptID uuid.UUID) (string, error) {
	if len(key) < 32 || attemptID == uuid.Nil {
		return "", errors.New("registration attempt proof is unavailable")
	}
	mac := hmac.New(sha256.New, key)
	_, _ = mac.Write([]byte(registrationAttemptProofDomain))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write(attemptID[:])
	return base64.RawURLEncoding.EncodeToString(mac.Sum(nil)), nil
}

func validRegistrationAttemptProof(key []byte, attemptID uuid.UUID, proof string) bool {
	expected, err := registrationAttemptProof(key, attemptID)
	if err != nil {
		return false
	}
	provided, err := base64.RawURLEncoding.DecodeString(strings.TrimSpace(proof))
	if err != nil {
		return false
	}
	want, err := base64.RawURLEncoding.DecodeString(expected)
	return err == nil && hmac.Equal(provided, want)
}

// newSession preserves the configured client's transport and request policies,
// but gives each registration flow its own cookies. Keycloak binds registration
// actions to those cookies, so sharing a jar can make a later request reuse an
// earlier flow's session code.
func (s *DirectRegistrationService) newSession() (*DirectRegistrationService, error) {
	jar, err := cookiejar.New(nil)
	if err != nil {
		return nil, err
	}
	client := *s.http
	client.Jar = jar
	session := *s
	session.http = &client
	return &session, nil
}

func validateRegistrationInput(input DirectRegistrationInput) error {
	if len(strings.TrimSpace(input.Username)) < 3 || len(strings.TrimSpace(input.Username)) > 128 || strings.ContainsAny(input.Username, "\r\n") {
		return errors.New("username is invalid")
	}
	if len(strings.TrimSpace(input.Email)) < 3 || len(strings.TrimSpace(input.Email)) > 320 || strings.ContainsAny(input.Email, "\r\n") {
		return errors.New("email is invalid")
	}
	nickname := input.Nickname
	if strings.TrimSpace(nickname) == "" {
		// Internal callers from the compatibility window did not carry a
		// nickname. Public HTTP registration rejects the omission; using the
		// immutable username here keeps recovery and old service callers safe.
		nickname = input.Username
	}
	if !validNickname(nickname) {
		return errors.New("nickname is invalid")
	}
	if len(input.Password) < 8 || len(input.Password) > 1024 || input.Password != input.Confirm {
		return errors.New("password is invalid")
	}
	return nil
}

func validNickname(value string) bool {
	value = strings.TrimSpace(value)
	if value == "" || !utf8.ValidString(value) || utf8.RuneCountInString(value) > 64 {
		return false
	}
	for _, char := range value {
		if unicode.IsControl(char) || unicode.In(char, unicode.Cf) || char == '\u2028' || char == '\u2029' {
			return false
		}
	}
	return true
}

// ValidateNickname keeps the Web self-service profile boundary aligned with
// registration without exposing any provider-specific representation.
func ValidateNickname(value string) bool { return validNickname(value) }

// ValidateDirectRegistrationInput is exposed to the HTTP boundary so malformed
// requests can be rejected before a provider session is created.
func ValidateDirectRegistrationInput(input DirectRegistrationInput) error {
	return validateRegistrationInput(input)
}

func registrationURLFromPage(page string, issuer *url.URL) (string, bool) {
	base := *issuer
	base.Path = strings.TrimRight(base.Path, "/") + "/protocol/openid-connect/auth"
	for _, match := range registrationLinkPattern.FindAllStringSubmatch(page, 32) {
		if len(match) != 2 {
			continue
		}
		link, err := url.Parse(html.UnescapeString(match[1]))
		if err != nil {
			continue
		}
		resolved := base.ResolveReference(link)
		if isIssuerURL(resolved.String(), issuer) && strings.Contains(resolved.Path, "/login-actions/registration") {
			return resolved.String(), true
		}
	}
	return "", false
}

func registrationForm(page string, issuer *url.URL) (string, map[string]string, bool) {
	match := formActionPattern.FindStringSubmatch(page)
	if len(match) != 2 {
		match = formActionPatternAlt.FindStringSubmatch(page)
	}
	if len(match) != 2 {
		return "", nil, false
	}
	actionURL, err := url.Parse(html.UnescapeString(match[1]))
	if err != nil || !isIssuerURL(actionURL.String(), issuer) || !strings.Contains(actionURL.Path, "/login-actions/registration") {
		return "", nil, false
	}
	hidden := make(map[string]string)
	inputPattern := regexp.MustCompile(`(?is)<input\b[^>]*\bname=["']([^"']+)["'][^>]*>`)
	valuePattern := regexp.MustCompile(`(?is)\bvalue=["']([^"']*)["']`)
	for _, item := range inputPattern.FindAllStringSubmatch(page, 32) {
		if len(item) != 2 {
			continue
		}
		valueMatch := valuePattern.FindStringSubmatch(item[0])
		if len(valueMatch) == 2 {
			hidden[html.UnescapeString(item[1])] = html.UnescapeString(valueMatch[1])
		}
	}
	return actionURL.String(), hidden, true
}

func registrationAttributeField(page, attribute string) (string, bool) {
	legacy := "user.attributes." + attribute
	foundLegacy := false
	inputPattern := regexp.MustCompile(`(?is)<input\b[^>]*\bname=["']([^"']+)["'][^>]*>`)
	for _, item := range inputPattern.FindAllStringSubmatch(page, 64) {
		if len(item) != 2 {
			continue
		}
		switch html.UnescapeString(item[1]) {
		case attribute:
			// Declarative User Profile in Keycloak 26 uses the attribute name
			// directly. Prefer that exact field if a custom theme happens to
			// render both current and legacy spellings.
			return attribute, true
		case legacy:
			foundLegacy = true
		}
	}
	return legacy, foundLegacy
}

func isExplicitRegistrationSuccessPage(body string) bool {
	// Keycloak normally completes an app registration with the validated OIDC
	// callback handled above. Some themes instead render a 200 success alert.
	// Accept only those machine-identifiable alerts: arbitrary 200 pages (for
	// example a login or maintenance page) must never become registration
	// success merely because they do not contain a known error string.
	if hasHTMLID(body, "kc-register-form", "kc-form-login") ||
		ariaInvalidPattern.MatchString(body) ||
		hasHTMLClass(body, "tl-alert-error", "tl-alert-warning", "pf-m-danger", "alert-error") {
		return false
	}
	if hasHTMLClass(body, "tl-alert-success") {
		return true
	}
	// These are the success markers emitted by Keycloak's current and legacy
	// base themes. Requiring kc-feedback-text prevents an unrelated framework
	// success banner from being treated as an identity-provider result.
	return hasHTMLClass(body, "kc-feedback-text") &&
		hasHTMLClass(body, "pf-m-success", "alert-success")
}

func isExplicitRegistrationRejectionPage(body string) bool {
	return hasHTMLID(body, "kc-register-form") || ariaInvalidPattern.MatchString(body) ||
		hasHTMLClass(body, "tl-alert-error", "tl-alert-warning", "pf-m-danger", "alert-error")
}

func hasHTMLID(body string, accepted ...string) bool {
	values := make(map[string]struct{}, len(accepted))
	for _, value := range accepted {
		values[strings.ToLower(value)] = struct{}{}
	}
	for _, match := range htmlIDPattern.FindAllStringSubmatch(body, -1) {
		if len(match) != 2 {
			continue
		}
		value := strings.ToLower(strings.TrimSpace(html.UnescapeString(match[1])))
		if _, ok := values[value]; ok {
			return true
		}
	}
	return false
}

func hasHTMLClass(body string, accepted ...string) bool {
	values := make(map[string]struct{}, len(accepted))
	for _, value := range accepted {
		values[strings.ToLower(value)] = struct{}{}
	}
	for _, match := range htmlClassPattern.FindAllStringSubmatch(body, -1) {
		if len(match) != 2 {
			continue
		}
		for _, class := range strings.Fields(strings.ToLower(html.UnescapeString(match[1]))) {
			if _, ok := values[class]; ok {
				return true
			}
		}
	}
	return false
}

func isIssuerURL(value string, issuer *url.URL) bool {
	parsed, err := url.Parse(value)
	return err == nil && parsed.Scheme == issuer.Scheme && strings.EqualFold(parsed.Host, issuer.Host) && strings.HasPrefix(parsed.Path, strings.TrimRight(issuer.Path, "/"))
}

func isRegistrationCallback(value, redirectURI, state string) bool {
	callback, err := url.Parse(value)
	if err != nil || callback.User != nil || callback.Opaque != "" || strings.Contains(value, "#") {
		return false
	}
	expected, err := url.Parse(redirectURI)
	if err != nil || !validDirectRegistrationRedirect(expected) {
		return false
	}
	if !strings.EqualFold(callback.Scheme, expected.Scheme) ||
		!strings.EqualFold(callback.Host, expected.Host) ||
		callback.EscapedPath() != expected.EscapedPath() {
		return false
	}
	query, err := url.ParseQuery(callback.RawQuery)
	if err != nil {
		return false
	}
	if _, hasError := query["error"]; hasError {
		return false
	}
	codes, codeOK := query["code"]
	states, stateOK := query["state"]
	return codeOK && len(codes) == 1 && codes[0] != "" &&
		stateOK && len(states) == 1 && states[0] == state
}

func (s *DirectRegistrationService) get(ctx context.Context, target string) (string, error) {
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
	if err != nil {
		return "", err
	}
	response, err := s.http.Do(request)
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return "", errors.New("identity registration page unavailable")
	}
	return readBounded(response.Body, 256*1024)
}

func (s *DirectRegistrationService) post(ctx context.Context, target string, form url.Values) (*http.Response, error) {
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, target, strings.NewReader(form.Encode()))
	if err != nil {
		return nil, err
	}
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	return s.http.Do(request)
}

func readBounded(reader io.Reader, max int) (string, error) {
	data, err := io.ReadAll(io.LimitReader(reader, int64(max)+1))
	if err != nil || len(data) > max {
		return "", errors.New("identity response is too large")
	}
	return string(data), nil
}

func randomRegistrationText(size int) (string, error) {
	bytes := make([]byte, size)
	if _, err := rand.Read(bytes); err != nil {
		return "", fmt.Errorf("generate registration state: %w", err)
	}
	defer func() {
		for i := range bytes {
			bytes[i] = 0
		}
	}()
	return base64.RawURLEncoding.EncodeToString(bytes), nil
}
