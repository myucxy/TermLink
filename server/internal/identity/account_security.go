package identity

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base32"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/pquerna/otp/totp"
)

const (
	passwordResetLifetime = 15 * time.Minute
	passwordResetResend   = time.Minute
	recoveryCodeCount     = 10
)

var (
	ErrPasswordResetInvalid = errors.New("identity password reset is invalid")
	ErrMFAAlreadyConfigured = errors.New("identity authenticator is already configured")
	ErrMFAEnrollmentMissing = errors.New("identity authenticator enrollment is missing")
)

// BeginPasswordReset deliberately returns an empty result for an unknown,
// disabled, unverified, or recently requested account. Public handlers must
// return the same response in every case so this method cannot become an
// account-enumeration oracle.
func (s *AccountStore) BeginPasswordReset(ctx context.Context, login string) (email, rawToken string, deliver bool, err error) {
	login = strings.ToLower(strings.TrimSpace(login))
	if login == "" || len(login) > 320 {
		return "", "", false, nil
	}
	tx, err := s.pool.BeginTx(ctx, pgx.TxOptions{})
	if err != nil {
		return "", "", false, ErrIdentityUnavailable
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var subject uuid.UUID
	var enabled, verified, resetRequired bool
	var authEpoch int64
	err = tx.QueryRow(ctx, `
		SELECT subject,email,enabled,email_verified,password_reset_required,auth_epoch FROM identity_accounts
		WHERE lower(username)=$1 OR lower(email)=$1 LIMIT 1 FOR UPDATE`, login).
		Scan(&subject, &email, &enabled, &verified, &resetRequired, &authEpoch)
	if errors.Is(err, pgx.ErrNoRows) || !enabled || (!verified && !resetRequired) {
		return "", "", false, nil
	}
	if err != nil {
		return "", "", false, ErrIdentityUnavailable
	}
	var lastCreated *time.Time
	if err = tx.QueryRow(ctx, `
		SELECT max(created_at) FROM identity_password_reset_tokens
		WHERE subject=$1 AND auth_epoch=$2 AND consumed_at IS NULL`, subject, authEpoch).Scan(&lastCreated); err != nil {
		return "", "", false, ErrIdentityUnavailable
	}
	now := s.clock().UTC()
	if lastCreated != nil && lastCreated.Add(passwordResetResend).After(now) {
		return "", "", false, nil
	}
	raw := make([]byte, 32)
	if _, err = io.ReadFull(rand.Reader, raw); err != nil {
		return "", "", false, ErrIdentityUnavailable
	}
	defer clear(raw)
	rawToken = base64.RawURLEncoding.EncodeToString(raw)
	digest := passwordResetDigest(rawToken)
	if _, err = tx.Exec(ctx, `
		UPDATE identity_password_reset_tokens SET consumed_at=$2
		WHERE subject=$1 AND consumed_at IS NULL`, subject, now); err != nil {
		return "", "", false, ErrIdentityUnavailable
	}
	if _, err = tx.Exec(ctx, `
		INSERT INTO identity_password_reset_tokens(token_hash,subject,auth_epoch,expires_at)
		VALUES($1,$2,$3,$4)`, digest[:], subject, authEpoch, now.Add(passwordResetLifetime)); err != nil {
		return "", "", false, ErrIdentityUnavailable
	}
	if err = tx.Commit(ctx); err != nil {
		return "", "", false, ErrIdentityUnavailable
	}
	return email, rawToken, true, nil
}

func (s *AccountStore) CompletePasswordReset(ctx context.Context, rawToken, password, confirm string) error {
	rawToken = strings.TrimSpace(rawToken)
	if len(rawToken) < 32 || len(rawToken) > 128 || !validNewPassword(password, confirm) {
		return ErrPasswordResetInvalid
	}
	digest := passwordResetDigest(rawToken)
	newDigest, err := hashPassword(password)
	if err != nil {
		return ErrIdentityUnavailable
	}
	defer clear(newDigest.Hash)
	defer clear(newDigest.Salt)
	tx, err := s.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return ErrIdentityUnavailable
	}
	defer func() { _ = tx.Rollback(ctx) }()
	now := s.clock().UTC()
	var subject uuid.UUID
	err = tx.QueryRow(ctx, `
		SELECT token.subject FROM identity_password_reset_tokens token
		JOIN identity_accounts account ON account.subject=token.subject
		WHERE token.token_hash=$1 AND token.consumed_at IS NULL AND token.expires_at>$2
		  AND token.auth_epoch=account.auth_epoch AND account.enabled=true
		FOR UPDATE OF token,account`, digest[:], now).Scan(&subject)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrPasswordResetInvalid
	}
	if err != nil {
		return ErrIdentityUnavailable
	}
	command, err := tx.Exec(ctx, `
		UPDATE identity_accounts SET password_hash=$2,password_salt=$3,password_memory_kib=$4,
		password_iterations=$5,password_parallelism=$6,password_reset_required=false,email_verified=true,
		failed_login_count=0,locked_until=NULL,auth_epoch=auth_epoch+1,updated_at=$7
		WHERE subject=$1 AND enabled=true`, subject, newDigest.Hash, newDigest.Salt, newDigest.MemoryKiB,
		newDigest.Iterations, newDigest.Parallelism, now)
	if err != nil || command.RowsAffected() != 1 {
		return ErrPasswordResetInvalid
	}
	if _, err = tx.Exec(ctx, `
		UPDATE identity_password_reset_tokens SET consumed_at=$2
		WHERE subject=$1 AND consumed_at IS NULL`, subject, now); err != nil {
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

func (s *AccountStore) ChangePassword(ctx context.Context, subject, currentPassword, password, confirm, secondFactor string) error {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	if err != nil || id == uuid.Nil || !validNewPassword(password, confirm) {
		return ErrInvalidCredentials
	}
	newDigest, err := hashPassword(password)
	if err != nil {
		return ErrIdentityUnavailable
	}
	defer clear(newDigest.Hash)
	defer clear(newDigest.Salt)
	tx, err := s.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return ErrIdentityUnavailable
	}
	defer func() { _ = tx.Rollback(ctx) }()
	totpConfigured, err := s.verifyCurrentPasswordForUpdate(ctx, tx, id, currentPassword)
	if err != nil {
		return err
	}
	if totpConfigured {
		if err = s.verifySecondFactor(ctx, tx, id, secondFactor); err != nil {
			if errors.Is(err, ErrIdentityUnavailable) {
				return err
			}
			return ErrInvalidCredentials
		}
	}
	command, err := tx.Exec(ctx, `
		UPDATE identity_accounts SET password_hash=$2,password_salt=$3,password_memory_kib=$4,
		password_iterations=$5,password_parallelism=$6,password_reset_required=false,
		failed_login_count=0,locked_until=NULL,auth_epoch=auth_epoch+1,updated_at=now()
		WHERE subject=$1 AND enabled=true`, id, newDigest.Hash, newDigest.Salt, newDigest.MemoryKiB,
		newDigest.Iterations, newDigest.Parallelism)
	if err != nil {
		return ErrIdentityUnavailable
	}
	if command.RowsAffected() != 1 {
		return ErrInvalidCredentials
	}
	if err = revokeSubjectTokens(ctx, tx, id); err != nil {
		return ErrIdentityUnavailable
	}
	if err = tx.Commit(ctx); err != nil {
		return ErrIdentityUnavailable
	}
	return nil
}

func validNewPassword(password, confirm string) bool {
	return len(password) >= 8 && len(password) <= 1024 && password == confirm
}

func passwordResetDigest(rawToken string) [32]byte {
	return sha256.Sum256([]byte("TermLink password reset V1\x00" + strings.TrimSpace(rawToken)))
}

func (s *AccountStore) BeginTOTPEnrollment(ctx context.Context, subject, currentPassword string) (secret, provisioningURI string, err error) {
	account, err := s.LoadAccount(ctx, subject)
	if err != nil || !account.Enabled {
		return "", "", ErrInvalidCredentials
	}
	if account.TOTPConfigured {
		return "", "", ErrMFAAlreadyConfigured
	}
	authenticatedSubject, err := s.Authenticate(ctx, account.Username, currentPassword)
	if err != nil || authenticatedSubject != subject {
		return "", "", ErrInvalidCredentials
	}
	key, err := totp.Generate(totp.GenerateOpts{Issuer: "TermLink", AccountName: account.Username, SecretSize: 20})
	if err != nil {
		return "", "", ErrIdentityUnavailable
	}
	secret = key.Secret()
	encrypted, err := sealTOTPSecret(s.credentialKey, account.Subject, []byte(secret))
	if err != nil {
		return "", "", ErrIdentityUnavailable
	}
	command, err := s.pool.Exec(ctx, `
		INSERT INTO identity_totp_credentials(subject,secret_ciphertext,last_accepted_counter,confirmed_at)
		VALUES($1,$2,-1,NULL)
		ON CONFLICT(subject) DO UPDATE SET secret_ciphertext=EXCLUDED.secret_ciphertext,
		last_accepted_counter=-1,confirmed_at=NULL,updated_at=now()
		WHERE identity_totp_credentials.confirmed_at IS NULL`, account.Subject, encrypted)
	if err != nil {
		return "", "", ErrIdentityUnavailable
	}
	if command.RowsAffected() != 1 {
		return "", "", ErrMFAAlreadyConfigured
	}
	return secret, key.URL(), nil
}

func (s *AccountStore) ConfirmTOTPEnrollment(ctx context.Context, subject, passcode string) ([]string, error) {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	passcode = strings.TrimSpace(passcode)
	if err != nil || id == uuid.Nil || !isSixDigitCode(passcode) {
		return nil, ErrInvalidTOTP
	}
	tx, err := s.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return nil, ErrIdentityUnavailable
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var encrypted []byte
	err = tx.QueryRow(ctx, `
		SELECT t.secret_ciphertext FROM identity_totp_credentials t
		JOIN identity_accounts a ON a.subject=t.subject
		WHERE t.subject=$1 AND t.confirmed_at IS NULL AND a.enabled=true
		FOR UPDATE OF t,a`, id).Scan(&encrypted)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, ErrMFAEnrollmentMissing
	}
	if err != nil {
		return nil, ErrIdentityUnavailable
	}
	secretBytes, err := openTOTPSecret(s.credentialKey, id, encrypted)
	if err != nil {
		return nil, ErrIdentityUnavailable
	}
	defer clear(secretBytes)
	matched := matchingTOTPCounter(string(secretBytes), passcode, -1, s.clock().UTC())
	if matched < 0 {
		return nil, ErrInvalidTOTP
	}
	codes, hashes, err := generateRecoveryCodes(id)
	if err != nil {
		return nil, ErrIdentityUnavailable
	}
	command, err := tx.Exec(ctx, `
		UPDATE identity_totp_credentials SET confirmed_at=now(),last_accepted_counter=$2,updated_at=now()
		WHERE subject=$1 AND confirmed_at IS NULL`, id, matched)
	if err != nil || command.RowsAffected() != 1 {
		return nil, ErrIdentityUnavailable
	}
	if _, err = tx.Exec(ctx, `DELETE FROM identity_recovery_codes WHERE subject=$1`, id); err != nil {
		return nil, ErrIdentityUnavailable
	}
	for _, hash := range hashes {
		if _, err = tx.Exec(ctx, `INSERT INTO identity_recovery_codes(subject,code_hash) VALUES($1,$2)`, id, hash[:]); err != nil {
			return nil, ErrIdentityUnavailable
		}
	}
	if _, err = tx.Exec(ctx, `
		UPDATE identity_accounts SET auth_epoch=auth_epoch+1,updated_at=now()
		WHERE subject=$1`, id); err != nil {
		return nil, ErrIdentityUnavailable
	}
	if err = revokeSubjectTokens(ctx, tx, id); err != nil {
		return nil, ErrIdentityUnavailable
	}
	if err = tx.Commit(ctx); err != nil {
		return nil, ErrIdentityUnavailable
	}
	return codes, nil
}

func (s *AccountStore) VerifySecondFactor(ctx context.Context, subject, code string) error {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	if err != nil || id == uuid.Nil {
		return ErrInvalidTOTP
	}
	return s.verifySecondFactor(ctx, s.pool, id, code)
}

func (s *AccountStore) verifySecondFactor(ctx context.Context, executor identityExecutor, id uuid.UUID, code string) error {
	code = strings.TrimSpace(code)
	if isSixDigitCode(code) {
		return s.verifyTOTP(ctx, executor, id, code)
	}
	if !validRecoveryCode(code) {
		return ErrInvalidTOTP
	}
	digest := recoveryCodeDigest(id, code)
	command, err := executor.Exec(ctx, `
		UPDATE identity_recovery_codes SET consumed_at=now()
		WHERE subject=$1 AND code_hash=$2 AND consumed_at IS NULL`, id, digest[:])
	if err != nil {
		return ErrIdentityUnavailable
	}
	if command.RowsAffected() != 1 {
		return ErrInvalidTOTP
	}
	return nil
}

func (s *AccountStore) DisableTOTP(ctx context.Context, subject, currentPassword, secondFactor string) error {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	if err != nil || id == uuid.Nil {
		return ErrInvalidCredentials
	}
	tx, err := s.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return ErrIdentityUnavailable
	}
	defer func() { _ = tx.Rollback(ctx) }()
	totpConfigured, err := s.verifyCurrentPasswordForUpdate(ctx, tx, id, currentPassword)
	if err != nil {
		return err
	}
	if !totpConfigured {
		return ErrInvalidCredentials
	}
	if err = s.verifySecondFactor(ctx, tx, id, secondFactor); err != nil {
		if errors.Is(err, ErrIdentityUnavailable) {
			return err
		}
		return ErrInvalidCredentials
	}
	if _, err = tx.Exec(ctx, `DELETE FROM identity_totp_credentials WHERE subject=$1`, id); err != nil {
		return ErrIdentityUnavailable
	}
	if _, err = tx.Exec(ctx, `DELETE FROM identity_recovery_codes WHERE subject=$1`, id); err != nil {
		return ErrIdentityUnavailable
	}
	if _, err = tx.Exec(ctx, `
		UPDATE identity_accounts SET auth_epoch=auth_epoch+1,updated_at=now()
		WHERE subject=$1`, id); err != nil {
		return ErrIdentityUnavailable
	}
	if err = revokeSubjectTokens(ctx, tx, id); err != nil {
		return ErrIdentityUnavailable
	}
	if err = tx.Commit(ctx); err != nil {
		return ErrIdentityUnavailable
	}
	return nil
}

func (s *AccountStore) RegenerateRecoveryCodes(ctx context.Context, subject, currentPassword, secondFactor string) ([]string, error) {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	if err != nil || id == uuid.Nil {
		return nil, ErrInvalidCredentials
	}
	codes, hashes, err := generateRecoveryCodes(id)
	if err != nil {
		return nil, ErrIdentityUnavailable
	}
	tx, err := s.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return nil, ErrIdentityUnavailable
	}
	defer func() { _ = tx.Rollback(ctx) }()
	totpConfigured, err := s.verifyCurrentPasswordForUpdate(ctx, tx, id, currentPassword)
	if err != nil {
		return nil, err
	}
	if !totpConfigured {
		return nil, ErrInvalidCredentials
	}
	if err = s.verifySecondFactor(ctx, tx, id, secondFactor); err != nil {
		if errors.Is(err, ErrIdentityUnavailable) {
			return nil, err
		}
		return nil, ErrInvalidCredentials
	}
	if _, err = tx.Exec(ctx, `DELETE FROM identity_recovery_codes WHERE subject=$1`, id); err != nil {
		return nil, ErrIdentityUnavailable
	}
	for _, hash := range hashes {
		if _, err = tx.Exec(ctx, `INSERT INTO identity_recovery_codes(subject,code_hash) VALUES($1,$2)`, id, hash[:]); err != nil {
			return nil, ErrIdentityUnavailable
		}
	}
	if _, err = tx.Exec(ctx, `
		UPDATE identity_accounts SET auth_epoch=auth_epoch+1,updated_at=now()
		WHERE subject=$1`, id); err != nil {
		return nil, ErrIdentityUnavailable
	}
	if err = revokeSubjectTokens(ctx, tx, id); err != nil {
		return nil, ErrIdentityUnavailable
	}
	if err = tx.Commit(ctx); err != nil {
		return nil, ErrIdentityUnavailable
	}
	return codes, nil
}

func (s *AccountStore) verifyCurrentPasswordForUpdate(ctx context.Context, tx pgx.Tx, subject uuid.UUID, password string) (bool, error) {
	var digest passwordDigest
	var memory int32
	var iterations int32
	var parallelism int16
	var enabled, totpConfigured bool
	err := tx.QueryRow(ctx, `
		SELECT a.enabled,a.password_hash,a.password_salt,a.password_memory_kib,
		       a.password_iterations,a.password_parallelism,
		       EXISTS(SELECT 1 FROM identity_totp_credentials t WHERE t.subject=a.subject AND t.confirmed_at IS NOT NULL)
		FROM identity_accounts a WHERE a.subject=$1 FOR UPDATE`, subject).Scan(
		&enabled, &digest.Hash, &digest.Salt, &memory, &iterations, &parallelism, &totpConfigured)
	if errors.Is(err, pgx.ErrNoRows) || !enabled {
		return false, ErrInvalidCredentials
	}
	if err != nil {
		return false, ErrIdentityUnavailable
	}
	defer clear(digest.Hash)
	defer clear(digest.Salt)
	digest.MemoryKiB = uint32(memory)
	digest.Iterations = uint32(iterations)
	digest.Parallelism = uint8(parallelism)
	if len(digest.Hash) == 0 || !verifyPassword(password, digest) {
		return false, ErrInvalidCredentials
	}
	return totpConfigured, nil
}

func generateRecoveryCodes(subject uuid.UUID) ([]string, [][32]byte, error) {
	result := make([]string, 0, recoveryCodeCount)
	hashes := make([][32]byte, 0, recoveryCodeCount)
	for len(result) < recoveryCodeCount {
		raw := make([]byte, 10)
		if _, err := io.ReadFull(rand.Reader, raw); err != nil {
			return nil, nil, err
		}
		encoded := base32.StdEncoding.WithPadding(base32.NoPadding).EncodeToString(raw)
		clear(raw)
		code := fmt.Sprintf("%s-%s-%s-%s", encoded[0:4], encoded[4:8], encoded[8:12], encoded[12:16])
		result = append(result, code)
		hashes = append(hashes, recoveryCodeDigest(subject, code))
	}
	return result, hashes, nil
}

func validRecoveryCode(code string) bool {
	normalized := strings.ToUpper(strings.ReplaceAll(strings.TrimSpace(code), "-", ""))
	if len(normalized) != 16 {
		return false
	}
	for _, value := range normalized {
		if (value < 'A' || value > 'Z') && (value < '2' || value > '7') {
			return false
		}
	}
	return true
}

func isSixDigitCode(code string) bool {
	if len(code) != 6 {
		return false
	}
	for _, value := range code {
		if value < '0' || value > '9' {
			return false
		}
	}
	return true
}

func recoveryCodeDigest(subject uuid.UUID, code string) [32]byte {
	normalized := strings.ToUpper(strings.ReplaceAll(strings.TrimSpace(code), "-", ""))
	input := make([]byte, 0, len("TermLink recovery code V1\x00")+len(subject)+len(normalized))
	input = append(input, []byte("TermLink recovery code V1\x00")...)
	input = append(input, subject[:]...)
	input = append(input, normalized...)
	result := sha256.Sum256(input)
	clear(input)
	return result
}
