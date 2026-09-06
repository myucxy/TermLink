package identity

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/mail"
	"sort"
	"strings"
	"unicode"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/auth"
)

const maximumKeycloakRealmExport = 64 << 20

type KeycloakImportResult struct {
	Imported               int
	SkippedServiceAccounts int
	DryRun                 bool
}

type keycloakRealmExport struct {
	Realm string          `json:"realm"`
	Users json.RawMessage `json:"users"`
}

type keycloakExportUser struct {
	ID                     string                     `json:"id"`
	Username               string                     `json:"username"`
	Email                  string                     `json:"email"`
	EmailVerified          bool                       `json:"emailVerified"`
	Enabled                *bool                      `json:"enabled"`
	FirstName              string                     `json:"firstName"`
	LastName               string                     `json:"lastName"`
	Attributes             map[string]json.RawMessage `json:"attributes"`
	RealmRoles             []string                   `json:"realmRoles"`
	ServiceAccountClientID string                     `json:"serviceAccountClientId"`
}

// ImportKeycloakRealm imports only non-credential identity data. Unknown JSON
// properties (including credentials, federated identities, and TOTP state)
// are intentionally never represented in the destination model. Every user
// is validated before the transaction commits, and existing password fields
// are never updated.
func ImportKeycloakRealm(ctx context.Context, pool *pgxpool.Pool, source io.Reader, dryRun bool) (KeycloakImportResult, error) {
	result := KeycloakImportResult{DryRun: dryRun}
	if pool == nil || source == nil {
		return result, ErrIdentityUnavailable
	}
	contents, err := io.ReadAll(io.LimitReader(source, maximumKeycloakRealmExport+1))
	if err != nil {
		return result, fmt.Errorf("read Keycloak realm export: %w", err)
	}
	defer clear(contents)
	if len(contents) == 0 || len(contents) > maximumKeycloakRealmExport {
		return result, errors.New("Keycloak realm export is empty or exceeds 64 MiB")
	}
	decoder := json.NewDecoder(bytes.NewReader(contents))
	decoder.UseNumber()
	var exported keycloakRealmExport
	if err = decoder.Decode(&exported); err != nil {
		return result, fmt.Errorf("decode Keycloak realm export: %w", err)
	}
	if err = ensureJSONEnd(decoder); err != nil {
		return result, err
	}
	if exported.Realm != "termlink" {
		return result, fmt.Errorf("Keycloak realm is %q, expected %q", exported.Realm, "termlink")
	}
	rawUsers := bytes.TrimSpace(exported.Users)
	if len(rawUsers) == 0 || bytes.Equal(rawUsers, []byte("null")) {
		return result, errors.New("Keycloak realm export does not contain embedded users; export with --users realm_file")
	}
	var users []keycloakExportUser
	if err = json.Unmarshal(rawUsers, &users); err != nil {
		return result, fmt.Errorf("decode Keycloak realm users: %w", err)
	}

	tx, err := pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return result, ErrIdentityUnavailable
	}
	defer func() { _ = tx.Rollback(ctx) }()
	seenSubjects := make(map[uuid.UUID]struct{}, len(users))
	seenUsernames := make(map[string]uuid.UUID, len(users))
	seenEmails := make(map[string]uuid.UUID, len(users))
	for index, exportedUser := range users {
		if strings.TrimSpace(exportedUser.ServiceAccountClientID) != "" {
			result.SkippedServiceAccounts++
			continue
		}
		account, normalizeErr := normalizeKeycloakUser(exportedUser)
		if normalizeErr != nil {
			return result, fmt.Errorf("validate Keycloak user %d: %w", index+1, normalizeErr)
		}
		if _, duplicate := seenSubjects[account.Subject]; duplicate {
			return result, fmt.Errorf("Keycloak export contains duplicate subject %s", account.Subject)
		}
		usernameKey := strings.ToLower(account.Username)
		if previous, duplicate := seenUsernames[usernameKey]; duplicate && previous != account.Subject {
			return result, fmt.Errorf("Keycloak export contains duplicate username for subjects %s and %s", previous, account.Subject)
		}
		emailKey := strings.ToLower(account.Email)
		if previous, duplicate := seenEmails[emailKey]; duplicate && previous != account.Subject {
			return result, fmt.Errorf("Keycloak export contains duplicate email for subjects %s and %s", previous, account.Subject)
		}
		seenSubjects[account.Subject] = struct{}{}
		seenUsernames[usernameKey] = account.Subject
		seenEmails[emailKey] = account.Subject
		if err = importKeycloakAccount(ctx, tx, account); err != nil {
			return result, fmt.Errorf("import Keycloak subject %s: %w", account.Subject, err)
		}
		result.Imported++
	}
	if dryRun {
		if err = tx.Rollback(ctx); err != nil {
			return result, ErrIdentityUnavailable
		}
		return result, nil
	}
	if err = tx.Commit(ctx); err != nil {
		return result, ErrIdentityUnavailable
	}
	return result, nil
}

func ensureJSONEnd(decoder *json.Decoder) error {
	var trailing any
	if err := decoder.Decode(&trailing); errors.Is(err, io.EOF) {
		return nil
	} else if err != nil {
		return fmt.Errorf("decode trailing Keycloak export data: %w", err)
	}
	return errors.New("Keycloak realm export contains multiple JSON values")
}

func normalizeKeycloakUser(exported keycloakExportUser) (Account, error) {
	subject, err := uuid.Parse(strings.TrimSpace(exported.ID))
	if err != nil || subject == uuid.Nil {
		return Account{}, errors.New("subject is not a UUID")
	}
	username := strings.TrimSpace(exported.Username)
	if !validImportedUsername(username) {
		return Account{}, errors.New("username is invalid")
	}
	email, err := normalizeImportedEmail(exported.Email)
	if err != nil {
		return Account{}, err
	}
	nickname := firstKeycloakAttribute(exported.Attributes["nickname"])
	if nickname == "" {
		nickname = strings.TrimSpace(strings.TrimSpace(exported.FirstName) + " " + strings.TrimSpace(exported.LastName))
	}
	if nickname == "" {
		nickname = username
	}
	if !auth.ValidateNickname(nickname) {
		return Account{}, errors.New("nickname is invalid")
	}
	enabled := true
	if exported.Enabled != nil {
		enabled = *exported.Enabled
	}
	roles, err := normalizeImportedRoles(exported.RealmRoles)
	if err != nil {
		return Account{}, err
	}
	return Account{
		Subject: subject, Username: username, Nickname: nickname, Email: email,
		EmailVerified: exported.EmailVerified, Enabled: enabled, PasswordResetRequired: true, RealmRoles: roles,
	}, nil
}

func validImportedUsername(value string) bool {
	if !utf8.ValidString(value) || utf8.RuneCountInString(value) < 3 || utf8.RuneCountInString(value) > 128 {
		return false
	}
	for _, char := range value {
		if unicode.IsControl(char) || unicode.In(char, unicode.Cf) || char == '\u2028' || char == '\u2029' {
			return false
		}
	}
	return true
}

func normalizeImportedEmail(value string) (string, error) {
	value = strings.TrimSpace(value)
	if value == "" || len(value) > 320 || strings.ContainsAny(value, "\r\n\t ") {
		return "", errors.New("email is invalid")
	}
	parsed, err := mail.ParseAddress(value)
	if err != nil || parsed.Address != value {
		return "", errors.New("email is invalid")
	}
	local, domain, ok := strings.Cut(value, "@")
	if !ok || local == "" || domain == "" {
		return "", errors.New("email is invalid")
	}
	return local + "@" + strings.ToLower(domain), nil
}

func firstKeycloakAttribute(raw json.RawMessage) string {
	if len(raw) == 0 {
		return ""
	}
	var values []string
	if json.Unmarshal(raw, &values) == nil && len(values) > 0 {
		return strings.TrimSpace(values[0])
	}
	var value string
	if json.Unmarshal(raw, &value) == nil {
		return strings.TrimSpace(value)
	}
	return ""
}

func normalizeImportedRoles(values []string) ([]string, error) {
	if len(values) > 128 {
		return nil, errors.New("too many realm roles")
	}
	seen := make(map[string]struct{}, len(values))
	result := make([]string, 0, len(values))
	for _, value := range values {
		value = strings.TrimSpace(value)
		if value == "" || len(value) > 128 || !utf8.ValidString(value) {
			return nil, errors.New("realm role is invalid")
		}
		for _, char := range value {
			if unicode.IsControl(char) || unicode.In(char, unicode.Cf) {
				return nil, errors.New("realm role is invalid")
			}
		}
		if _, duplicate := seen[value]; duplicate {
			continue
		}
		seen[value] = struct{}{}
		result = append(result, value)
	}
	sort.Strings(result)
	return result, nil
}

func importKeycloakAccount(ctx context.Context, tx pgx.Tx, account Account) error {
	_, err := tx.Exec(ctx, `
		INSERT INTO identity_accounts(
			subject,username,nickname,email,email_verified,enabled,password_reset_required,realm_roles)
		VALUES($1,$2,$3,$4,$5,$6,true,$7)
		ON CONFLICT(subject) DO UPDATE SET
			username=EXCLUDED.username,nickname=EXCLUDED.nickname,email=EXCLUDED.email,
			email_verified=EXCLUDED.email_verified,enabled=EXCLUDED.enabled,realm_roles=EXCLUDED.realm_roles,
			auth_epoch=identity_accounts.auth_epoch+1,updated_at=now()
		WHERE ROW(identity_accounts.username,identity_accounts.nickname,identity_accounts.email,
		          identity_accounts.email_verified,identity_accounts.enabled,identity_accounts.realm_roles)
		      IS DISTINCT FROM
		      ROW(EXCLUDED.username,EXCLUDED.nickname,EXCLUDED.email,
		          EXCLUDED.email_verified,EXCLUDED.enabled,EXCLUDED.realm_roles)`,
		account.Subject, account.Username, account.Nickname, account.Email, account.EmailVerified, account.Enabled, account.RealmRoles)
	return err
}
