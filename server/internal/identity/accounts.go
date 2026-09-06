package identity

import (
	"context"
	"errors"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/ory/fosite"
	"github.com/termlink/termlink/server/internal/auth"
)

const maximumFailedLogins = 5

const (
	initialAdministratorUsername = "admin"
	initialAdministratorNickname = "TermLink Administrator"
	initialAdministratorEmail    = "admin@termlink.local"
	initialAdministratorLockID   = int64(0x544c49444d494e31)
)

var initialAdministratorRoles = []string{
	"SUPER_ADMIN",
	"ADMIN",
	"USER",
	"TERMLINK_BOOTSTRAP_MANAGED_V1",
}

var (
	ErrAccountSetupRequired = errors.New("identity account setup is required")
	ErrInvalidCredentials   = errors.New("identity credentials are invalid")
	ErrIdentityUnavailable  = errors.New("embedded identity service is unavailable")
)

type Account struct {
	Subject               uuid.UUID
	Username              string
	Nickname              string
	Email                 string
	EmailVerified         bool
	Enabled               bool
	PasswordResetRequired bool
	AuthEpoch             int64
	TOTPConfigured        bool
	RealmRoles            []string
	Password              passwordDigest
}

type AccountStore struct {
	pool          *pgxpool.Pool
	clock         func() time.Time
	credentialKey []byte
}

func NewAccountStore(pool *pgxpool.Pool) (*AccountStore, error) {
	if pool == nil {
		return nil, ErrIdentityUnavailable
	}
	return &AccountStore{pool: pool, clock: time.Now}, nil
}

// BootstrapInitialAdministrator creates the deployment-managed administrator
// exactly once for a fresh embedded identity database. The transaction-scoped
// advisory lock also makes concurrent Server startups converge on one account.
func (s *AccountStore) BootstrapInitialAdministrator(ctx context.Context, password string) (bool, error) {
	if s == nil || s.pool == nil || len(password) < 16 || len(password) > 1024 {
		return false, ErrIdentityUnavailable
	}
	tx, err := s.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return false, ErrIdentityUnavailable
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err = tx.Exec(ctx, `SELECT pg_advisory_xact_lock($1)`, initialAdministratorLockID); err != nil {
		return false, ErrIdentityUnavailable
	}
	var populated bool
	if err = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM identity_accounts)`).Scan(&populated); err != nil {
		return false, ErrIdentityUnavailable
	}
	if populated {
		if err = tx.Commit(ctx); err != nil {
			return false, ErrIdentityUnavailable
		}
		return false, nil
	}
	digest, err := hashPassword(password)
	if err != nil {
		return false, ErrIdentityUnavailable
	}
	defer clear(digest.Hash)
	defer clear(digest.Salt)
	if _, err = tx.Exec(ctx, `
		INSERT INTO identity_accounts(
			subject,username,nickname,email,email_verified,enabled,password_hash,password_salt,
			password_memory_kib,password_iterations,password_parallelism,password_reset_required,realm_roles)
		VALUES($1,$2,$3,$4,true,true,$5,$6,$7,$8,$9,false,$10)`,
		uuid.New(), initialAdministratorUsername, initialAdministratorNickname, initialAdministratorEmail,
		digest.Hash, digest.Salt, digest.MemoryKiB, digest.Iterations, digest.Parallelism,
		initialAdministratorRoles); err != nil {
		return false, ErrIdentityUnavailable
	}
	if err = tx.Commit(ctx); err != nil {
		return false, ErrIdentityUnavailable
	}
	return true, nil
}

// ResetInitialAdministratorPassword rotates only the deployment-managed
// bootstrap administrator after an explicit offline operator action.
func (s *AccountStore) ResetInitialAdministratorPassword(ctx context.Context, password string) error {
	if s == nil || s.pool == nil || len(password) < 16 || len(password) > 1024 {
		return ErrInvalidCredentials
	}
	digest, err := hashPassword(password)
	if err != nil {
		return ErrInvalidCredentials
	}
	defer clear(digest.Hash)
	defer clear(digest.Salt)
	tx, err := s.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return ErrIdentityUnavailable
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err = tx.Exec(ctx, `SELECT pg_advisory_xact_lock($1)`, initialAdministratorLockID); err != nil {
		return ErrIdentityUnavailable
	}
	var subject uuid.UUID
	if err = tx.QueryRow(ctx, `
		SELECT subject FROM identity_accounts
		WHERE lower(username)=$1 AND enabled=true
		  AND $2=ANY(realm_roles) AND $3=ANY(realm_roles)
		FOR UPDATE`, initialAdministratorUsername, "SUPER_ADMIN", "TERMLINK_BOOTSTRAP_MANAGED_V1").Scan(&subject); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ErrInvalidCredentials
		}
		return ErrIdentityUnavailable
	}
	command, err := tx.Exec(ctx, `
		UPDATE identity_accounts SET password_hash=$2,password_salt=$3,password_memory_kib=$4,
			password_iterations=$5,password_parallelism=$6,password_reset_required=false,
			failed_login_count=0,locked_until=NULL,auth_epoch=auth_epoch+1,updated_at=now()
		WHERE subject=$1`, subject, digest.Hash, digest.Salt, digest.MemoryKiB, digest.Iterations, digest.Parallelism)
	if err != nil || command.RowsAffected() != 1 {
		return ErrIdentityUnavailable
	}
	if err = revokeSubjectTokens(ctx, tx, subject); err != nil {
		return ErrIdentityUnavailable
	}
	if err = tx.Commit(ctx); err != nil {
		return ErrIdentityUnavailable
	}
	return nil
}

func (s *AccountStore) Register(ctx context.Context, input auth.DirectRegistrationInput) error {
	return s.register(ctx, uuid.Nil, input, false)
}

func (s *AccountStore) RegisterAttempt(ctx context.Context, attemptID uuid.UUID, input auth.DirectRegistrationInput) error {
	if attemptID == uuid.Nil {
		return auth.ErrKeycloakAdminRejected
	}
	return s.register(ctx, attemptID, input, false)
}

func (s *AccountStore) RegisterVerifiedEmail(ctx context.Context, input auth.DirectRegistrationInput) error {
	return s.register(ctx, uuid.Nil, input, true)
}

func (s *AccountStore) RegisterVerifiedEmailAttempt(ctx context.Context, attemptID uuid.UUID, input auth.DirectRegistrationInput) error {
	if attemptID == uuid.Nil {
		return auth.ErrKeycloakAdminRejected
	}
	return s.register(ctx, attemptID, input, true)
}

func (s *AccountStore) register(ctx context.Context, attemptID uuid.UUID, input auth.DirectRegistrationInput, emailVerified bool) error {
	if s == nil || s.pool == nil || auth.ValidateDirectRegistrationInput(input) != nil {
		return auth.ErrKeycloakAdminRejected
	}
	digest, err := hashPassword(input.Password)
	if err != nil {
		return auth.ErrKeycloakAdminRejected
	}
	defer clear(digest.Hash)
	defer clear(digest.Salt)
	nickname := strings.TrimSpace(input.Nickname)
	if nickname == "" {
		nickname = strings.TrimSpace(input.Username)
	}
	_, err = s.pool.Exec(ctx, `
		INSERT INTO identity_accounts(
			subject,username,nickname,email,email_verified,password_hash,password_salt,
			password_memory_kib,password_iterations,password_parallelism,registration_attempt_id)
		VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)`,
		uuid.New(), strings.TrimSpace(input.Username), nickname, strings.ToLower(strings.TrimSpace(input.Email)),
		emailVerified, digest.Hash, digest.Salt, digest.MemoryKiB, digest.Iterations, digest.Parallelism,
		nullableUUID(attemptID))
	if err != nil {
		if isIdentityConflict(err) {
			return auth.ErrKeycloakAdminRejected
		}
		return auth.ErrKeycloakAdminUnavailable
	}
	return nil
}

func nullableUUID(value uuid.UUID) any {
	if value == uuid.Nil {
		return nil
	}
	return value
}

func isIdentityConflict(err error) bool {
	var postgresError *pgconn.PgError
	return errors.As(err, &postgresError) && postgresError.Code == "23505"
}

func (s *AccountStore) Authenticate(ctx context.Context, login, password string) (string, error) {
	account, err := s.loadForLogin(ctx, login)
	if err != nil {
		// Keep the not-found path computationally comparable to a real account.
		verifyDummyPassword(password)
		return "", fosite.ErrNotFound
	}
	defer clear(account.Password.Hash)
	defer clear(account.Password.Salt)
	now := s.clock().UTC()
	if !account.Enabled {
		verifyDummyPassword(password)
		return "", fosite.ErrNotFound
	}
	if account.PasswordResetRequired || len(account.Password.Hash) == 0 {
		verifyDummyPassword(password)
		return "", ErrAccountSetupRequired
	}
	if !verifyPassword(password, account.Password) {
		_, _ = s.pool.Exec(ctx, `
			UPDATE identity_accounts SET
				failed_login_count=least(failed_login_count+1,1000),
				locked_until=CASE WHEN failed_login_count+1 >= $2 THEN $3 ELSE locked_until END,
				updated_at=now()
			WHERE subject=$1`, account.Subject, maximumFailedLogins, now.Add(30*time.Second))
		return "", fosite.ErrNotFound
	}
	command, err := s.pool.Exec(ctx, `
		UPDATE identity_accounts SET failed_login_count=0,locked_until=NULL,last_login_at=$2,updated_at=now()
		WHERE subject=$1 AND enabled=true AND (locked_until IS NULL OR locked_until <= $2)`, account.Subject, now)
	if err != nil || command.RowsAffected() != 1 {
		return "", fosite.ErrNotFound
	}
	return account.Subject.String(), nil
}

func verifyDummyPassword(password string) {
	dummy := passwordDigest{
		Hash: make([]byte, passwordHashBytes), Salt: make([]byte, passwordSaltBytes),
		MemoryKiB: passwordMemoryKiB, Iterations: passwordIterations, Parallelism: passwordParallelism,
	}
	defer clear(dummy.Hash)
	defer clear(dummy.Salt)
	_ = verifyPassword(password, dummy)
}

func (s *AccountStore) loadForLogin(ctx context.Context, login string) (Account, error) {
	login = strings.ToLower(strings.TrimSpace(login))
	var account Account
	var memory int32
	var iterations int32
	var parallelism int16
	err := s.pool.QueryRow(ctx, `
		SELECT a.subject,a.username,a.nickname,a.email,a.email_verified,a.enabled,a.password_reset_required,
		       a.auth_epoch,a.password_hash,a.password_salt,a.password_memory_kib,a.password_iterations,
		       a.password_parallelism,EXISTS(SELECT 1 FROM identity_totp_credentials t WHERE t.subject=a.subject AND t.confirmed_at IS NOT NULL),
		       a.realm_roles
		FROM identity_accounts a
		WHERE lower(a.username)=$1 OR lower(a.email)=$1
		LIMIT 1`, login).Scan(
		&account.Subject, &account.Username, &account.Nickname, &account.Email, &account.EmailVerified,
		&account.Enabled, &account.PasswordResetRequired, &account.AuthEpoch, &account.Password.Hash,
		&account.Password.Salt, &memory, &iterations, &parallelism, &account.TOTPConfigured, &account.RealmRoles)
	if err != nil {
		return Account{}, err
	}
	account.Password.MemoryKiB = uint32(memory)
	account.Password.Iterations = uint32(iterations)
	account.Password.Parallelism = uint8(parallelism)
	return account, nil
}

func (s *AccountStore) LoadAccount(ctx context.Context, subject string) (Account, error) {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	if err != nil || id == uuid.Nil {
		return Account{}, ErrInvalidCredentials
	}
	var account Account
	err = s.pool.QueryRow(ctx, `
		SELECT a.subject,a.username,a.nickname,a.email,a.email_verified,a.enabled,a.password_reset_required,
		       a.auth_epoch,EXISTS(SELECT 1 FROM identity_totp_credentials t WHERE t.subject=a.subject AND t.confirmed_at IS NOT NULL),
		       a.realm_roles
		FROM identity_accounts a WHERE a.subject=$1`, id).Scan(
		&account.Subject, &account.Username, &account.Nickname, &account.Email, &account.EmailVerified,
		&account.Enabled, &account.PasswordResetRequired, &account.AuthEpoch, &account.TOTPConfigured, &account.RealmRoles)
	return account, err
}

func (s *AccountStore) HasConfiguredOTP(ctx context.Context, subject string) (bool, error) {
	account, err := s.LoadAccount(ctx, subject)
	if err != nil {
		return false, auth.ErrKeycloakAdminUnavailable
	}
	return account.Enabled && account.TOTPConfigured, nil
}

func (s *AccountStore) UpdateNickname(ctx context.Context, subject, nickname string) error {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	if err != nil || !auth.ValidateNickname(nickname) {
		return auth.ErrKeycloakAdminRejected
	}
	command, err := s.pool.Exec(ctx, `UPDATE identity_accounts SET nickname=$2,updated_at=now() WHERE subject=$1 AND enabled=true`, id, strings.TrimSpace(nickname))
	if err != nil {
		return auth.ErrKeycloakAdminUnavailable
	}
	if command.RowsAffected() != 1 {
		return auth.ErrKeycloakAdminRejected
	}
	return nil
}

func (s *AccountStore) UpdateVerifiedEmail(ctx context.Context, subject, email string) error {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	email = strings.ToLower(strings.TrimSpace(email))
	if err != nil || len(email) < 3 || len(email) > 320 || strings.ContainsAny(email, "\r\n\t ") || !strings.Contains(email, "@") {
		return auth.ErrKeycloakAdminRejected
	}
	command, err := s.pool.Exec(ctx, `UPDATE identity_accounts SET email=$2,email_verified=true,auth_epoch=auth_epoch+1,updated_at=now() WHERE subject=$1 AND enabled=true`, id, email)
	if err != nil {
		if isIdentityConflict(err) {
			return auth.ErrKeycloakAdminRejected
		}
		return auth.ErrKeycloakAdminUnavailable
	}
	if command.RowsAffected() != 1 {
		return auth.ErrKeycloakAdminRejected
	}
	return s.revokeSubjectTokens(ctx, id)
}

func (s *AccountStore) MarkRegistrationEmailVerified(ctx context.Context, rawEmail string) error {
	email := strings.ToLower(strings.TrimSpace(rawEmail))
	command, err := s.pool.Exec(ctx, `UPDATE identity_accounts SET email_verified=true,updated_at=now() WHERE lower(email)=$1 AND enabled=true`, email)
	if err != nil {
		return auth.ErrKeycloakAdminUnavailable
	}
	if command.RowsAffected() != 1 {
		return auth.ErrKeycloakAdminRejected
	}
	return nil
}

func (s *AccountStore) LookupRegistrationAttempt(ctx context.Context, attemptID uuid.UUID) (auth.RegistrationIdentityLookup, error) {
	if attemptID == uuid.Nil {
		return auth.RegistrationIdentityLookup{}, auth.ErrKeycloakAdminUnavailable
	}
	var subject uuid.UUID
	err := s.pool.QueryRow(ctx, `SELECT subject FROM identity_accounts WHERE registration_attempt_id=$1`, attemptID).Scan(&subject)
	if errors.Is(err, pgx.ErrNoRows) {
		return auth.RegistrationIdentityLookup{Status: auth.RegistrationIdentityAbsent}, nil
	}
	if err != nil {
		return auth.RegistrationIdentityLookup{}, auth.ErrKeycloakAdminUnavailable
	}
	return auth.RegistrationIdentityLookup{Status: auth.RegistrationIdentityExact, UserID: subject.String()}, nil
}

func (s *AccountStore) DeleteRegistrationIdentity(ctx context.Context, subject string) error {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	if err != nil || id == uuid.Nil {
		return auth.ErrKeycloakAdminRejected
	}
	_, err = s.pool.Exec(ctx, `DELETE FROM identity_accounts WHERE subject=$1`, id)
	if err != nil {
		return auth.ErrKeycloakAdminUnavailable
	}
	return nil
}

func (s *AccountStore) revokeSubjectTokens(ctx context.Context, subject uuid.UUID) error {
	return revokeSubjectTokens(ctx, s.pool, subject)
}

func revokeSubjectTokens(ctx context.Context, executor identityExecutor, subject uuid.UUID) error {
	_, err := executor.Exec(ctx, `
		UPDATE identity_oauth_sessions SET active=false,updated_at=now()
		WHERE request_payload->'session'->>'subject'=$1 AND active=true`, subject.String())
	return err
}
