package auth

// DirectRegistrationService submits the public OIDC registration form from
// the server-side boundary.  Mobile and desktop clients therefore never need
// to open an identity-service browser page or receive an OIDC administrator
// credential.  The service deliberately keeps response bodies bounded and
// never includes submitted credentials in errors or logs.

import (
	"context"
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
)

var (
	errRegistrationRejected = errors.New("identity registration was rejected")
	formActionPattern       = regexp.MustCompile(`(?is)<form[^>]*id=["']kc-register-form["'][^>]*action=["']([^"']+)["']`)
	formActionPatternAlt    = regexp.MustCompile(`(?is)<form[^>]*action=["']([^"']+)["'][^>]*id=["']kc-register-form["']`)
	registrationLinkPattern = regexp.MustCompile(`(?is)href=["']([^"']*(?:login-actions/)?registration[^"']*)["']`)
	htmlClassPattern        = regexp.MustCompile(`(?is)(?:<|\s)class\s*=\s*["']([^"']*)["']`)
	htmlIDPattern           = regexp.MustCompile(`(?is)(?:<|\s)id\s*=\s*["']([^"']*)["']`)
	ariaInvalidPattern      = regexp.MustCompile(`(?is)(?:<|\s)aria-invalid\s*=\s*["']true["']`)
)

type DirectRegistrationConfig struct {
	Issuer      string
	ClientID    string
	RedirectURI string
	HTTPClient  *http.Client
}

type DirectRegistrationInput struct {
	Username  string
	Email     string
	Password  string
	Confirm   string
	FirstName string
	LastName  string
}

type DirectRegistrationService struct {
	issuer      *url.URL
	clientID    string
	redirectURI string
	http        *http.Client
}

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
	return &DirectRegistrationService{issuer: issuer, clientID: strings.TrimSpace(config.ClientID), redirectURI: redirect.String(), http: client}, nil
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
	if s == nil || s.http == nil || s.issuer == nil {
		return errors.New("direct registration is unavailable")
	}
	if err := validateRegistrationInput(input); err != nil {
		return err
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
	query := url.Values{
		"client_id":             {s.clientID},
		"redirect_uri":          {s.redirectURI},
		"response_type":         {"code"},
		"scope":                 {"openid profile offline_access"},
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
	registrationURL, ok := registrationURLFromPage(page, s.issuer)
	if !ok {
		return errRegistrationRejected
	}
	registrationPage, err := session.get(ctx, registrationURL)
	if err != nil {
		return errRegistrationRejected
	}
	action, hidden, ok := registrationForm(registrationPage, s.issuer)
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
	form.Set("firstName", input.FirstName)
	form.Set("lastName", input.LastName)
	response, err := session.post(ctx, action, form)
	if err != nil {
		return errRegistrationRejected
	}
	defer response.Body.Close()
	if response.StatusCode >= 300 && response.StatusCode < 400 {
		// Keycloak redirects a successful registration back into the auth flow.
		// The client performs the actual password grant immediately afterwards.
		location := response.Header.Get("Location")
		if isRegistrationCallback(location, s.redirectURI, state) {
			return nil
		}
		return errRegistrationRejected
	}
	if response.StatusCode == http.StatusOK {
		mediaType, _, contentTypeErr := mime.ParseMediaType(response.Header.Get("Content-Type"))
		if contentTypeErr != nil || !strings.EqualFold(mediaType, "text/html") {
			return errRegistrationRejected
		}
		body, readErr := readBounded(response.Body, 256*1024)
		if readErr == nil && isExplicitRegistrationSuccessPage(body) {
			return nil
		}
	}
	return errRegistrationRejected
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
	if len(input.Password) < 8 || len(input.Password) > 1024 || input.Password != input.Confirm {
		return errors.New("password is invalid")
	}
	if len(input.FirstName) > 128 || len(input.LastName) > 128 {
		return errors.New("profile name is invalid")
	}
	return nil
}

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
