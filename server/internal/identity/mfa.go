package identity

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"errors"
	"io"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/pquerna/otp/totp"
)

var ErrInvalidTOTP = errors.New("identity authenticator code is invalid")

const totpPeriodSeconds = int64(30)

type identityExecutor interface {
	Exec(context.Context, string, ...any) (pgconn.CommandTag, error)
	QueryRow(context.Context, string, ...any) pgx.Row
}

// VerifyTOTP accepts the current counter and one counter of clock skew in
// either direction. The successful counter is advanced atomically, so the
// same six-digit code cannot be replayed even through concurrent requests.
func (s *AccountStore) VerifyTOTP(ctx context.Context, subject, passcode string) error {
	id, err := uuid.Parse(strings.TrimSpace(subject))
	if err != nil || id == uuid.Nil {
		return ErrInvalidTOTP
	}
	return s.verifyTOTP(ctx, s.pool, id, passcode)
}

func (s *AccountStore) verifyTOTP(ctx context.Context, executor identityExecutor, id uuid.UUID, passcode string) error {
	passcode = strings.TrimSpace(passcode)
	if executor == nil || len(passcode) != 6 || len(s.credentialKey) != 32 {
		return ErrInvalidTOTP
	}
	if _, err := strconv.Atoi(passcode); err != nil {
		return ErrInvalidTOTP
	}
	var encrypted []byte
	var lastCounter int64
	err := executor.QueryRow(ctx, `
		SELECT secret_ciphertext,last_accepted_counter
		FROM identity_totp_credentials
		WHERE subject=$1 AND confirmed_at IS NOT NULL`, id).Scan(&encrypted, &lastCounter)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrInvalidTOTP
	}
	if err != nil {
		return ErrIdentityUnavailable
	}
	secretBytes, err := openTOTPSecret(s.credentialKey, id, encrypted)
	if err != nil {
		return ErrIdentityUnavailable
	}
	defer clear(secretBytes)
	matched := matchingTOTPCounter(string(secretBytes), passcode, lastCounter, s.clock().UTC())
	if matched < 0 {
		return ErrInvalidTOTP
	}
	command, err := executor.Exec(ctx, `
		UPDATE identity_totp_credentials SET last_accepted_counter=$2,updated_at=now()
		WHERE subject=$1 AND confirmed_at IS NOT NULL AND last_accepted_counter < $2`, id, matched)
	if err != nil {
		return ErrIdentityUnavailable
	}
	if command.RowsAffected() != 1 {
		return ErrInvalidTOTP
	}
	return nil
}

func matchingTOTPCounter(secret, passcode string, lastCounter int64, now time.Time) int64 {
	nowCounter := now.Unix() / totpPeriodSeconds
	for _, counter := range []int64{nowCounter, nowCounter - 1, nowCounter + 1} {
		if counter < 0 || counter <= lastCounter {
			continue
		}
		candidate, err := totp.GenerateCode(secret, time.Unix(counter*totpPeriodSeconds, 0).UTC())
		if err == nil && subtle.ConstantTimeCompare([]byte(candidate), []byte(passcode)) == 1 {
			return counter
		}
	}
	return -1
}

func sealTOTPSecret(key []byte, subject uuid.UUID, plaintext []byte) ([]byte, error) {
	aead, err := totpAEAD(key)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, aead.NonceSize())
	if _, err = io.ReadFull(rand.Reader, nonce); err != nil {
		return nil, err
	}
	return aead.Seal(nonce, nonce, plaintext, totpAAD(subject)), nil
}

func openTOTPSecret(key []byte, subject uuid.UUID, encrypted []byte) ([]byte, error) {
	aead, err := totpAEAD(key)
	if err != nil || len(encrypted) < aead.NonceSize()+aead.Overhead()+16 {
		return nil, ErrIdentityUnavailable
	}
	return aead.Open(nil, encrypted[:aead.NonceSize()], encrypted[aead.NonceSize():], totpAAD(subject))
}

func totpAEAD(key []byte) (cipher.AEAD, error) {
	if len(key) != 32 {
		return nil, ErrIdentityUnavailable
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(block)
}

func totpAAD(subject uuid.UUID) []byte {
	digest := sha256.Sum256(append([]byte("TermLink embedded TOTP V1\x00"), subject[:]...))
	return digest[:]
}
