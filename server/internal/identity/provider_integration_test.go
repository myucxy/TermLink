package identity

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/pquerna/otp/totp"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/migrations"
)

func TestEmbeddedProviderBootstrapsInitialAdministratorOnce(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for embedded identity integration tests")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	pool, cleanup := newIdentityTestPool(t, ctx, databaseURL)
	defer cleanup()

	newProvider := func(password string) *Provider {
		provider, err := New(ctx, Config{
			Issuer: "https://identity.example.test/realms/termlink", Pool: pool,
			HMACSecret: make([]byte, 64), KeyEncryptionKey: bytesOf(0x41, 32), InitialAdminPassword: password,
			AndroidClientID: "termlink-android", AndroidRedirectURI: "dev.termlink:/oauth2redirect",
			WindowsClientID: "termlink-windows", WindowsRedirectURI: "http://127.0.0.1/oauth2/callback",
			WebClientID: "termlink-web", WebRedirectURI: "https://identity.example.test/auth/callback",
			StepUpClientID: "termlink-web-step-up", AllowedParties: []string{"termlink-android", "termlink-windows"},
		})
		if err != nil {
			t.Fatal(err)
		}
		return provider
	}

	provider := newProvider("Initial_Admin_Secret_42")
	subject, err := provider.Accounts().Authenticate(ctx, "admin", "Initial_Admin_Secret_42")
	if err != nil {
		t.Fatal(err)
	}
	account, err := provider.Accounts().LoadAccount(ctx, subject)
	if err != nil {
		t.Fatal(err)
	}
	for _, required := range initialAdministratorRoles {
		found := false
		for _, role := range account.RealmRoles {
			found = found || role == required
		}
		if !found {
			t.Fatalf("initial administrator roles %v omit %q", account.RealmRoles, required)
		}
	}
	provider = newProvider("Replacement_Admin_Secret_99")
	if _, err = provider.Accounts().Authenticate(ctx, "admin", "Initial_Admin_Secret_42"); err != nil {
		t.Fatalf("idempotent startup replaced the initial credential: %v", err)
	}
	if _, err = provider.Accounts().Authenticate(ctx, "admin", "Replacement_Admin_Secret_99"); err == nil {
		t.Fatal("idempotent startup accepted the replacement deployment secret")
	}
	if err = provider.Accounts().ResetInitialAdministratorPassword(ctx, "Rotated_Admin_Secret_73"); err != nil {
		t.Fatal(err)
	}
	if _, err = provider.Accounts().Authenticate(ctx, "admin", "Initial_Admin_Secret_42"); err == nil {
		t.Fatal("explicit administrator reset retained the old credential")
	}
	if _, err = provider.Accounts().Authenticate(ctx, "admin", "Rotated_Admin_Secret_73"); err != nil {
		t.Fatalf("explicit administrator reset did not install the new credential: %v", err)
	}
	var accountCount int
	if err = pool.QueryRow(ctx, `SELECT count(*) FROM identity_accounts`).Scan(&accountCount); err != nil || accountCount != 1 {
		t.Fatalf("identity account count = %d, %v", accountCount, err)
	}
}

func TestEmbeddedProviderPasswordRefreshAndAuthorizationCodeCompatibility(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for embedded identity integration tests")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	pool, cleanup := newIdentityTestPool(t, ctx, databaseURL)
	defer cleanup()

	provider, err := New(ctx, Config{
		Issuer: "https://identity.example.test/realms/termlink", Pool: pool,
		HMACSecret: make([]byte, 64), KeyEncryptionKey: bytesOf(0x42, 32),
		AndroidClientID: "termlink-android", AndroidRedirectURI: "dev.termlink:/oauth2redirect",
		WindowsClientID: "termlink-windows", WindowsRedirectURI: "http://127.0.0.1/oauth2/callback",
		WebClientID: "termlink-web", WebRedirectURI: "https://identity.example.test/auth/callback",
		StepUpClientID: "termlink-web-step-up", AllowedParties: []string{"termlink-android", "termlink-windows"},
	})
	if err != nil {
		t.Fatal(err)
	}
	unverified := auth.DirectRegistrationInput{
		Username: "unverified-reset-user", Nickname: "Unverified Reset User", Email: "unverified-reset@example.test",
		Password: "Unverified password 42!", Confirm: "Unverified password 42!",
	}
	if err = provider.Accounts().Register(ctx, unverified); err != nil {
		t.Fatal(err)
	}
	if _, _, deliver, resetErr := provider.Accounts().BeginPasswordReset(ctx, unverified.Username); resetErr != nil || deliver {
		t.Fatalf("ordinary unverified account reset = deliver:%t error:%v", deliver, resetErr)
	}
	input := auth.DirectRegistrationInput{
		Username: "embedded-user", Nickname: "Embedded User", Email: "embedded@example.test",
		Password: "Correct horse battery staple 42!", Confirm: "Correct horse battery staple 42!",
	}
	if err = provider.Accounts().RegisterVerifiedEmail(ctx, input); err != nil {
		t.Fatal(err)
	}

	password := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-android"},
		"scope": {"openid profile offline_access"}, "username": {input.Username}, "password": {input.Password},
	})
	identity, err := provider.TokenVerifier().Verify(ctx, password.AccessToken)
	if err != nil || identity.AuthorizedParty != "termlink-android" || identity.Subject == "" {
		t.Fatalf("password access token identity = %#v, %v", identity, err)
	}
	if password.RefreshToken == "" {
		t.Fatal("password grant omitted refresh token")
	}
	rotated := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"refresh_token"}, "client_id": {"termlink-android"}, "refresh_token": {password.RefreshToken},
	})
	if rotated.RefreshToken == "" || rotated.RefreshToken == password.RefreshToken {
		t.Fatal("refresh token was not rotated")
	}
	assertTokenRejected(t, provider.Handler(), url.Values{
		"grant_type": {"refresh_token"}, "client_id": {"termlink-android"}, "refresh_token": {password.RefreshToken},
	})
	assertTokenRejected(t, provider.Handler(), url.Values{
		"grant_type": {"refresh_token"}, "client_id": {"termlink-android"}, "refresh_token": {rotated.RefreshToken},
	})

	windowsQuery := url.Values{
		"client_id": {"termlink-windows"}, "response_type": {"code"},
		"redirect_uri": {"http://127.0.0.1:54321/oauth2/callback"}, "scope": {"openid"},
		"state": {strings.Repeat("w", 32)}, "nonce": {strings.Repeat("x", 32)},
		"code_challenge": {strings.Repeat("a", 43)}, "code_challenge_method": {"S256"},
	}
	windowsStart := httptest.NewRecorder()
	provider.Handler().ServeHTTP(windowsStart, httptest.NewRequest(http.MethodGet,
		"https://identity.example.test/realms/termlink/protocol/openid-connect/auth?"+windowsQuery.Encode(), nil))
	if windowsStart.Code != http.StatusOK {
		t.Fatalf("Windows RFC 8252 random-port authorization status/body = %d/%s", windowsStart.Code, windowsStart.Body.String())
	}

	verifier := base64.RawURLEncoding.EncodeToString(bytesOf(0x71, 32))
	challengeDigest := sha256.Sum256([]byte(verifier))
	query := url.Values{
		"client_id": {"termlink-web"}, "response_type": {"code"},
		"redirect_uri": {"https://identity.example.test/auth/callback"},
		"scope":        {"openid profile email offline_access"}, "state": {strings.Repeat("s", 32)},
		"nonce": {strings.Repeat("n", 32)}, "code_challenge": {base64.RawURLEncoding.EncodeToString(challengeDigest[:])},
		"code_challenge_method": {"S256"}, "prompt": {"login"},
	}
	authPath := "/realms/termlink/protocol/openid-connect/auth?" + query.Encode()
	start := httptest.NewRecorder()
	provider.Handler().ServeHTTP(start, httptest.NewRequest(http.MethodGet, "https://identity.example.test"+authPath, nil))
	if start.Code != http.StatusOK {
		t.Fatalf("authorization login status/body = %d/%s", start.Code, start.Body.String())
	}
	csrfMatch := regexp.MustCompile(`name="termlink_csrf" value="([^"]+)"`).FindStringSubmatch(start.Body.String())
	if len(csrfMatch) != 2 {
		t.Fatal("authorization login omitted CSRF token")
	}
	cookies := start.Result().Cookies()
	if len(cookies) == 0 {
		t.Fatal("authorization login omitted CSRF cookie")
	}
	form := url.Values{"termlink_csrf": {csrfMatch[1]}, "username": {input.Username}, "password": {input.Password}}
	finishRequest := httptest.NewRequest(http.MethodPost, "https://identity.example.test"+authPath, strings.NewReader(form.Encode()))
	finishRequest.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	finishRequest.Header.Set("Origin", "https://identity.example.test")
	for _, cookie := range cookies {
		finishRequest.AddCookie(cookie)
	}
	finish := httptest.NewRecorder()
	provider.Handler().ServeHTTP(finish, finishRequest)
	if finish.Code != http.StatusSeeOther {
		t.Fatalf("authorization completion status/body = %d/%s", finish.Code, finish.Body.String())
	}
	callback, err := url.Parse(finish.Header().Get("Location"))
	if err != nil || callback.Query().Get("code") == "" || callback.Query().Get("state") != strings.Repeat("s", 32) {
		t.Fatalf("authorization callback = %q, %v", finish.Header().Get("Location"), err)
	}
	codeToken := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"authorization_code"}, "client_id": {"termlink-web"},
		"redirect_uri": {"https://identity.example.test/auth/callback"}, "code": {callback.Query().Get("code")},
		"code_verifier": {verifier},
	})
	if codeToken.IDToken == "" {
		t.Fatal("OIDC authorization-code exchange omitted ID token")
	}
	idToken, err := provider.Discovery().Verifier("termlink-web").Verify(ctx, codeToken.IDToken)
	if err != nil {
		t.Fatal(err)
	}
	var claims struct{ Nonce, Username, Nickname, Email string }
	var raw map[string]any
	if err = idToken.Claims(&raw); err != nil {
		t.Fatal(err)
	}
	claims.Nonce, _ = raw["nonce"].(string)
	claims.Username, _ = raw["preferred_username"].(string)
	claims.Nickname, _ = raw["nickname"].(string)
	claims.Email, _ = raw["email"].(string)
	if claims.Nonce != strings.Repeat("n", 32) || claims.Username != input.Username || claims.Nickname != input.Nickname || claims.Email != input.Email {
		t.Fatalf("OIDC profile claims = %#v", claims)
	}
	if codeToken.RefreshToken == "" {
		t.Fatal("authorization-code exchange omitted refresh token")
	}
	if _, err = provider.TokenVerifier().Verify(ctx, codeToken.AccessToken); !errors.Is(err, auth.ErrUnauthenticated) {
		t.Fatalf("web access token was accepted by the native API verifier: %v", err)
	}
	apiToken := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-android"},
		"username": {input.Username}, "password": {input.Password},
	})
	if _, err = pool.Exec(ctx, `UPDATE identity_accounts SET auth_epoch=auth_epoch+1 WHERE subject=$1`, identity.Subject); err != nil {
		t.Fatal(err)
	}
	if _, err = provider.TokenVerifier().Verify(ctx, apiToken.AccessToken); !errors.Is(err, auth.ErrUnauthenticated) {
		t.Fatalf("access token survived auth epoch change: %v", err)
	}
	assertTokenRejected(t, provider.Handler(), url.Values{
		"grant_type": {"refresh_token"}, "client_id": {"termlink-web"}, "refresh_token": {codeToken.RefreshToken},
	})
}

func TestEmbeddedAccountSecurityLifecycle(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for embedded identity integration tests")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	pool, cleanup := newIdentityTestPool(t, ctx, databaseURL)
	defer cleanup()

	now := time.Now().UTC().Truncate(time.Second)
	provider, err := New(ctx, Config{
		Issuer: "https://identity.example.test/realms/termlink", Pool: pool,
		HMACSecret: make([]byte, 64), KeyEncryptionKey: bytesOf(0x51, 32), Clock: func() time.Time { return now },
		AndroidClientID: "termlink-android", AndroidRedirectURI: "dev.termlink:/oauth2redirect",
		WindowsClientID: "termlink-windows", WindowsRedirectURI: "http://127.0.0.1/oauth2/callback",
		WebClientID: "termlink-web", WebRedirectURI: "https://identity.example.test/auth/callback",
		StepUpClientID: "termlink-web-step-up", AllowedParties: []string{"termlink-android", "termlink-windows"},
	})
	if err != nil {
		t.Fatal(err)
	}
	input := auth.DirectRegistrationInput{
		Username: "security-user", Nickname: "Security User", Email: "security@example.test",
		Password: "Initial password 42!", Confirm: "Initial password 42!",
	}
	if err = provider.Accounts().RegisterVerifiedEmail(ctx, input); err != nil {
		t.Fatal(err)
	}
	subject, err := provider.Accounts().Authenticate(ctx, input.Username, input.Password)
	if err != nil {
		t.Fatal(err)
	}
	initialToken := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-android"}, "username": {input.Username}, "password": {input.Password},
	})

	secret, provisioningURI, err := provider.Accounts().BeginTOTPEnrollment(ctx, subject, input.Password)
	if err != nil || secret == "" || !strings.HasPrefix(provisioningURI, "otpauth://totp/") {
		t.Fatalf("begin TOTP enrollment = %q, %q, %v", secret, provisioningURI, err)
	}
	confirmationCode, err := totp.GenerateCode(secret, now)
	if err != nil {
		t.Fatal(err)
	}
	recoveryCodes, err := provider.Accounts().ConfirmTOTPEnrollment(ctx, subject, confirmationCode)
	if err != nil || len(recoveryCodes) != recoveryCodeCount {
		t.Fatalf("confirm TOTP enrollment = %#v, %v", recoveryCodes, err)
	}
	if _, err = provider.TokenVerifier().Verify(ctx, initialToken.AccessToken); !errors.Is(err, auth.ErrUnauthenticated) {
		t.Fatalf("pre-MFA token remained valid: %v", err)
	}
	if err = provider.Accounts().VerifyTOTP(ctx, subject, confirmationCode); !errors.Is(err, ErrInvalidTOTP) {
		t.Fatalf("confirmation TOTP replay = %v", err)
	}
	nativePasswordToken := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-android"}, "username": {input.Username}, "password": {input.Password},
	})
	if nativeIdentity, nativeErr := provider.TokenVerifier().Verify(ctx, nativePasswordToken.AccessToken); nativeErr != nil || nativeIdentity.MFA {
		t.Fatalf("native password token identity = %#v, %v", nativeIdentity, nativeErr)
	}

	now = now.Add(30 * time.Second)
	loginCode, err := totp.GenerateCode(secret, now)
	if err != nil {
		t.Fatal(err)
	}
	if err = provider.Accounts().VerifyTOTP(ctx, subject, loginCode); err != nil {
		t.Fatalf("verify TOTP after enrollment = %v", err)
	}

	changedPassword := "Changed password 84!"
	if err = provider.Accounts().ChangePassword(ctx, subject, input.Password, changedPassword, changedPassword, recoveryCodes[0]); err != nil {
		t.Fatal(err)
	}
	if _, err = provider.TokenVerifier().Verify(ctx, nativePasswordToken.AccessToken); !errors.Is(err, auth.ErrUnauthenticated) {
		t.Fatalf("pre-password-change token remained valid: %v", err)
	}
	if err = provider.Accounts().VerifySecondFactor(ctx, subject, recoveryCodes[0]); !errors.Is(err, ErrInvalidTOTP) {
		t.Fatalf("consumed recovery code replay = %v", err)
	}
	if _, err = provider.Accounts().Authenticate(ctx, input.Username, changedPassword); err != nil {
		t.Fatal(err)
	}

	now = now.Add(30 * time.Second)
	regenerationCode, err := totp.GenerateCode(secret, now)
	if err != nil {
		t.Fatal(err)
	}
	newRecoveryCodes, err := provider.Accounts().RegenerateRecoveryCodes(ctx, subject, changedPassword, regenerationCode)
	if err != nil || len(newRecoveryCodes) != recoveryCodeCount {
		t.Fatalf("regenerate recovery codes = %#v, %v", newRecoveryCodes, err)
	}
	if err = provider.Accounts().VerifySecondFactor(ctx, subject, recoveryCodes[1]); !errors.Is(err, ErrInvalidTOTP) {
		t.Fatalf("superseded recovery code = %v", err)
	}
	if err = provider.Accounts().DisableTOTP(ctx, subject, changedPassword, newRecoveryCodes[0]); err != nil {
		t.Fatal(err)
	}
	account, err := provider.Accounts().LoadAccount(ctx, subject)
	if err != nil || account.TOTPConfigured {
		t.Fatalf("account after disabling TOTP = %#v, %v", account, err)
	}
	passwordToken := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-android"}, "username": {input.Username}, "password": {changedPassword},
	})

	email, resetToken, deliver, err := provider.Accounts().BeginPasswordReset(ctx, input.Username)
	if err != nil || !deliver || email != input.Email || resetToken == "" {
		t.Fatalf("begin password reset = %q, token=%t, deliver=%t, %v", email, resetToken != "", deliver, err)
	}
	if _, _, repeatedDelivery, repeatErr := provider.Accounts().BeginPasswordReset(ctx, input.Username); repeatErr != nil || repeatedDelivery {
		t.Fatalf("password reset resend limit = deliver=%t, %v", repeatedDelivery, repeatErr)
	}
	if _, err = pool.Exec(ctx, `UPDATE identity_accounts SET auth_epoch=auth_epoch+1 WHERE subject=$1`, subject); err != nil {
		t.Fatal(err)
	}
	resetPassword := "Reset password 126!"
	if err = provider.Accounts().CompletePasswordReset(ctx, resetToken, resetPassword, resetPassword); !errors.Is(err, ErrPasswordResetInvalid) {
		t.Fatalf("password reset token survived auth epoch change: %v", err)
	}
	email, resetToken, deliver, err = provider.Accounts().BeginPasswordReset(ctx, input.Username)
	if err != nil || !deliver || email != input.Email || resetToken == "" {
		t.Fatalf("begin password reset after auth epoch change = %q, token=%t, deliver=%t, %v", email, resetToken != "", deliver, err)
	}
	if err = provider.Accounts().CompletePasswordReset(ctx, resetToken, resetPassword, resetPassword); err != nil {
		t.Fatal(err)
	}
	if err = provider.Accounts().CompletePasswordReset(ctx, resetToken, resetPassword, resetPassword); !errors.Is(err, ErrPasswordResetInvalid) {
		t.Fatalf("password reset token replay = %v", err)
	}
	if _, err = provider.TokenVerifier().Verify(ctx, passwordToken.AccessToken); !errors.Is(err, auth.ErrUnauthenticated) {
		t.Fatalf("pre-reset token remained valid: %v", err)
	}
	if _, err = provider.Accounts().Authenticate(ctx, input.Username, resetPassword); err != nil {
		t.Fatal(err)
	}
}

func TestKeycloakImportPreservesSubjectAndNeverImportsCredentials(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for embedded identity integration tests")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	pool, cleanup := newIdentityTestPool(t, ctx, databaseURL)
	defer cleanup()

	if _, err := ImportKeycloakRealm(ctx, pool, strings.NewReader(`{"realm":"termlink"}`), true); err == nil || !strings.Contains(err.Error(), "--users realm_file") {
		t.Fatalf("realm export without embedded users = %v", err)
	}
	subject := uuid.New()
	export := keycloakExportJSON(t, subject, "Imported User")
	result, err := ImportKeycloakRealm(ctx, pool, strings.NewReader(export), true)
	if err != nil || !result.DryRun || result.Imported != 1 || result.SkippedServiceAccounts != 1 {
		t.Fatalf("dry-run import = %#v, %v", result, err)
	}
	var count int
	if err = pool.QueryRow(ctx, `SELECT count(*) FROM identity_accounts`).Scan(&count); err != nil || count != 0 {
		t.Fatalf("dry-run persisted %d accounts: %v", count, err)
	}
	result, err = ImportKeycloakRealm(ctx, pool, strings.NewReader(export), false)
	if err != nil || result.Imported != 1 {
		t.Fatalf("committed import = %#v, %v", result, err)
	}
	store, err := NewAccountStore(pool)
	if err != nil {
		t.Fatal(err)
	}
	account, err := store.LoadAccount(ctx, subject.String())
	if err != nil || account.Subject != subject || account.Nickname != "Imported User" ||
		!account.PasswordResetRequired || len(account.RealmRoles) != 2 || account.RealmRoles[0] != "ADMIN" {
		t.Fatalf("imported account = %#v, %v", account, err)
	}
	provider, err := New(ctx, Config{
		Issuer: "https://identity.example.test/realms/termlink", Pool: pool,
		HMACSecret: make([]byte, 64), KeyEncryptionKey: bytesOf(0x62, 32),
		AndroidClientID: "termlink-android", AndroidRedirectURI: "dev.termlink:/oauth2redirect",
		WindowsClientID: "termlink-windows", WindowsRedirectURI: "http://127.0.0.1/oauth2/callback",
		WebClientID: "termlink-web", WebRedirectURI: "https://identity.example.test/auth/callback",
		StepUpClientID: "termlink-web-step-up", AllowedParties: []string{"termlink-android", "termlink-windows"},
	})
	if err != nil {
		t.Fatal(err)
	}
	assertTokenRejected(t, provider.Handler(), url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-android"},
		"username": {"imported-user"}, "password": {"must-not-be-imported"},
	})
	if _, err = pool.Exec(ctx, `UPDATE identity_accounts SET email_verified=false WHERE subject=$1`, subject); err != nil {
		t.Fatal(err)
	}
	resetEmail, resetToken, deliver, err := store.BeginPasswordReset(ctx, "imported-user")
	if err != nil || !deliver || resetEmail != "Imported@example.test" || resetToken == "" {
		t.Fatalf("migrated unverified account reset = %q, token=%t, deliver=%t, %v", resetEmail, resetToken != "", deliver, err)
	}
	newPassword := "New embedded password 42!"
	if err = store.CompletePasswordReset(ctx, resetToken, newPassword, newPassword); err != nil {
		t.Fatal(err)
	}
	account, err = store.LoadAccount(ctx, subject.String())
	if err != nil || !account.EmailVerified || account.PasswordResetRequired {
		t.Fatalf("migrated account after password reset = %#v, %v", account, err)
	}
	beforeProfileUpdate := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-android"},
		"username": {"imported-user"}, "password": {newPassword},
	})
	updatedExport := keycloakExportJSON(t, subject, "Updated Imported User")
	if _, err = ImportKeycloakRealm(ctx, pool, strings.NewReader(updatedExport), false); err != nil {
		t.Fatal(err)
	}
	account, err = store.LoadAccount(ctx, subject.String())
	if err != nil || account.Nickname != "Updated Imported User" || account.PasswordResetRequired {
		t.Fatalf("re-imported account = %#v, %v", account, err)
	}
	if authenticated, authenticateErr := store.Authenticate(ctx, "imported-user", newPassword); authenticateErr != nil || authenticated != subject.String() {
		t.Fatalf("preserved embedded password authentication = %q, %v", authenticated, authenticateErr)
	}
	if _, err = provider.TokenVerifier().Verify(ctx, beforeProfileUpdate.AccessToken); !errors.Is(err, auth.ErrUnauthenticated) {
		t.Fatalf("access token survived imported profile update: %v", err)
	}
	assertTokenRejected(t, provider.Handler(), url.Values{
		"grant_type": {"refresh_token"}, "client_id": {"termlink-android"},
		"refresh_token": {beforeProfileUpdate.RefreshToken},
	})
}

func TestEmbeddedSigningKeyRotationKeepsOverlapVerifiable(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for embedded identity integration tests")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	pool, cleanup := newIdentityTestPool(t, ctx, databaseURL)
	defer cleanup()

	now := time.Now().UTC().Truncate(time.Second)
	provider, err := New(ctx, Config{
		Issuer: "https://identity.example.test/realms/termlink", Pool: pool,
		HMACSecret: make([]byte, 64), KeyEncryptionKey: bytesOf(0x61, 32), Clock: func() time.Time { return now },
		AndroidClientID: "termlink-android", AndroidRedirectURI: "dev.termlink:/oauth2redirect",
		WindowsClientID: "termlink-windows", WindowsRedirectURI: "http://127.0.0.1/oauth2/callback",
		WebClientID: "termlink-web", WebRedirectURI: "https://identity.example.test/auth/callback",
		StepUpClientID: "termlink-web-step-up", AllowedParties: []string{"termlink-android", "termlink-windows"},
	})
	if err != nil {
		t.Fatal(err)
	}
	input := auth.DirectRegistrationInput{
		Username: "rotation-user", Nickname: "Rotation User", Email: "rotation@example.test",
		Password: "Rotation password 42!", Confirm: "Rotation password 42!",
	}
	if err = provider.Accounts().RegisterVerifiedEmail(ctx, input); err != nil {
		t.Fatal(err)
	}
	oldToken := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-android"}, "username": {input.Username}, "password": {input.Password},
	})
	oldKeyID := accessTokenKeyID(t, oldToken.AccessToken)
	if _, err = pool.Exec(ctx, `UPDATE identity_signing_keys SET sign_until=$2 WHERE key_id=$1`, oldKeyID, now.Add(identityRotationLead-time.Hour)); err != nil {
		t.Fatal(err)
	}
	if err = provider.signingKeys.Refresh(ctx); err != nil {
		t.Fatal(err)
	}
	newToken := requestToken(t, provider.Handler(), url.Values{
		"grant_type": {"password"}, "client_id": {"termlink-android"}, "username": {input.Username}, "password": {input.Password},
	})
	newKeyID := accessTokenKeyID(t, newToken.AccessToken)
	if newKeyID == oldKeyID {
		t.Fatalf("signing key did not rotate: %q", oldKeyID)
	}
	if _, err = provider.TokenVerifier().Verify(ctx, oldToken.AccessToken); err != nil {
		t.Fatalf("old overlap token verification = %v", err)
	}
	if _, err = provider.TokenVerifier().Verify(ctx, newToken.AccessToken); err != nil {
		t.Fatalf("new rotated token verification = %v", err)
	}
	response := httptest.NewRecorder()
	provider.Handler().ServeHTTP(response, httptest.NewRequest(http.MethodGet, "https://identity.example.test/realms/termlink/protocol/openid-connect/certs", nil))
	var keySet struct {
		Keys []json.RawMessage `json:"keys"`
	}
	if response.Code != http.StatusOK || json.Unmarshal(response.Body.Bytes(), &keySet) != nil || len(keySet.Keys) != 2 {
		t.Fatalf("rotated JWKS status/body = %d/%s", response.Code, response.Body.String())
	}
}

func TestEmbeddedIdentityCleanupRemovesOnlyExpiredState(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for embedded identity integration tests")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	pool, cleanup := newIdentityTestPool(t, ctx, databaseURL)
	defer cleanup()

	now := time.Now().UTC().Truncate(time.Second)
	subject := uuid.New()
	if _, err := pool.Exec(ctx, `
		INSERT INTO identity_accounts(subject,username,nickname,email,email_verified)
		VALUES($1,'cleanup-user','Cleanup User','cleanup@example.test',true)`, subject); err != nil {
		t.Fatal(err)
	}
	for _, session := range []struct {
		signature string
		expiresAt time.Time
	}{
		{signature: "expired-session-signature", expiresAt: now.Add(-time.Minute)},
		{signature: "active-session-signature", expiresAt: now.Add(time.Minute)},
	} {
		if _, err := pool.Exec(ctx, `
			INSERT INTO identity_oauth_sessions(session_type,signature,request_id,request_payload,expires_at)
			VALUES('ACCESS',$1,$2,'{}'::jsonb,$3)`, session.signature, uuid.New(), session.expiresAt); err != nil {
			t.Fatal(err)
		}
	}
	for _, assertion := range []struct {
		digest    byte
		expiresAt time.Time
	}{
		{digest: 0x11, expiresAt: now.Add(-time.Minute)},
		{digest: 0x22, expiresAt: now.Add(time.Minute)},
	} {
		if _, err := pool.Exec(ctx, `
			INSERT INTO identity_client_assertion_jtis(jti_hash,expires_at)
			VALUES($1,$2)`, bytesOf(assertion.digest, 32), assertion.expiresAt); err != nil {
			t.Fatal(err)
		}
	}
	for _, reset := range []struct {
		digest     byte
		expiresAt  time.Time
		consumedAt *time.Time
	}{
		{digest: 0x31, expiresAt: now.Add(-time.Minute)},
		{digest: 0x32, expiresAt: now.Add(time.Minute), consumedAt: &now},
		{digest: 0x33, expiresAt: now.Add(time.Minute)},
	} {
		if _, err := pool.Exec(ctx, `
			INSERT INTO identity_password_reset_tokens(token_hash,subject,auth_epoch,expires_at,consumed_at,created_at)
			VALUES($1,$2,1,$3,$4,$5)`, bytesOf(reset.digest, 32), subject, reset.expiresAt,
			reset.consumedAt, reset.expiresAt.Add(-time.Hour)); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO identity_signing_keys(
			key_id,algorithm,public_jwk,private_key_ciphertext,not_before,sign_until,verify_until)
		VALUES('expired-test-key','RS256','{}'::jsonb,$1,$2,$3,$4)`,
		bytesOf(0x41, 256), now.Add(-3*time.Hour), now.Add(-2*time.Hour), now.Add(-time.Hour)); err != nil {
		t.Fatal(err)
	}

	if err := cleanupExpiredIdentityState(ctx, pool, now, true); err != nil {
		t.Fatal(err)
	}
	assertIdentityRowCount(t, ctx, pool, "identity_oauth_sessions", 1)
	assertIdentityRowCount(t, ctx, pool, "identity_client_assertion_jtis", 1)
	assertIdentityRowCount(t, ctx, pool, "identity_password_reset_tokens", 1)
	assertIdentityRowCount(t, ctx, pool, "identity_signing_keys", 0)
}

func accessTokenKeyID(t *testing.T, rawToken string) string {
	t.Helper()
	parts := strings.Split(rawToken, ".")
	if len(parts) != 3 {
		t.Fatalf("access token has %d segments", len(parts))
	}
	header, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		t.Fatal(err)
	}
	var value struct {
		KeyID string `json:"kid"`
	}
	if err = json.Unmarshal(header, &value); err != nil || value.KeyID == "" {
		t.Fatalf("access token header = %s, %v", header, err)
	}
	return value.KeyID
}

func keycloakExportJSON(t *testing.T, subject uuid.UUID, nickname string) string {
	t.Helper()
	encoded, err := json.Marshal(map[string]any{
		"realm": "termlink",
		"users": []any{
			map[string]any{
				"id": subject.String(), "username": "imported-user", "email": "Imported@Example.Test",
				"emailVerified": true, "enabled": true, "attributes": map[string]any{"nickname": []string{nickname}},
				"realmRoles":  []string{"offline_access", "ADMIN", "ADMIN"},
				"credentials": []any{map[string]any{"type": "password", "secretData": "must-not-be-imported"}}, "totp": true,
			},
			map[string]any{"id": uuid.NewString(), "serviceAccountClientId": "termlink-server"},
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	return string(encoded)
}

type identityTokenResponse struct {
	AccessToken  string `json:"access_token"`
	RefreshToken string `json:"refresh_token"`
	IDToken      string `json:"id_token"`
}

func requestToken(t *testing.T, handler http.Handler, form url.Values) identityTokenResponse {
	t.Helper()
	request := httptest.NewRequest(http.MethodPost, "https://identity.example.test/realms/termlink/protocol/openid-connect/token", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("token status/body = %d/%s", response.Code, response.Body.String())
	}
	var result identityTokenResponse
	if err := json.Unmarshal(response.Body.Bytes(), &result); err != nil || result.AccessToken == "" {
		t.Fatalf("token response = %#v, %v", result, err)
	}
	return result
}

func assertTokenRejected(t *testing.T, handler http.Handler, form url.Values) {
	t.Helper()
	request := httptest.NewRequest(http.MethodPost, "https://identity.example.test/realms/termlink/protocol/openid-connect/token", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), `"error":"invalid_grant"`) {
		t.Fatalf("rejected token request status/body = %d/%s", response.Code, response.Body.String())
	}
}

func newIdentityTestPool(t *testing.T, ctx context.Context, databaseURL string) (*pgxpool.Pool, func()) {
	t.Helper()
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	schema := "embedded_identity_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quoted := pgx.Identifier{schema}.Sanitize()
	if _, err = admin.Exec(ctx, "CREATE SCHEMA "+quoted); err != nil {
		admin.Close()
		t.Fatal(err)
	}
	configuration, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		admin.Close()
		t.Fatal(err)
	}
	configuration.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, configuration)
	if err != nil {
		admin.Close()
		t.Fatal(err)
	}
	migration, err := migrations.Files.ReadFile("000043_embedded_identity_provider.up.sql")
	if err != nil {
		pool.Close()
		admin.Close()
		t.Fatal(err)
	}
	if _, err = pool.Exec(ctx, string(migration)); err != nil {
		pool.Close()
		admin.Close()
		t.Fatal(err)
	}
	return pool, func() {
		pool.Close()
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cleanupCancel()
		if _, cleanupErr := admin.Exec(cleanupContext, "DROP SCHEMA "+quoted+" CASCADE"); cleanupErr != nil {
			t.Error(cleanupErr)
		}
		admin.Close()
	}
}

func assertIdentityRowCount(t *testing.T, ctx context.Context, pool *pgxpool.Pool, table string, expected int) {
	t.Helper()
	allowed := map[string]bool{
		"identity_oauth_sessions":        true,
		"identity_client_assertion_jtis": true,
		"identity_password_reset_tokens": true,
		"identity_signing_keys":          true,
	}
	if !allowed[table] {
		t.Fatalf("unsupported identity table %q", table)
	}
	var count int
	if err := pool.QueryRow(ctx, "SELECT count(*) FROM "+table).Scan(&count); err != nil {
		t.Fatal(err)
	}
	if count != expected {
		t.Fatalf("%s row count = %d, want %d", table, count, expected)
	}
}

func bytesOf(value byte, count int) []byte {
	result := make([]byte, count)
	for index := range result {
		result[index] = value
	}
	return result
}
