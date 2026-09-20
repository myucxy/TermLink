// Package keywrap wraps a server master secret at rest with a passphrase.
//
// Threat model: anyone with read access to the PostgreSQL data otherwise
// recovers every derived key (cookies, tokens, admin routes). With a
// passphrase configured, the database only holds an AES-256-GCM ciphertext
// whose key is derived from the operator's passphrase via argon2id, so the
// database alone no longer suffices.
package keywrap

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"strings"

	"golang.org/x/crypto/argon2"
)

// Prefix marks a wrapped value in the runtime secret store.
const Prefix = "termlink-wrapped-v1:"

const (
	saltSize  = 16
	nonceSize = 12
	keySize   = 32
	// Argon2id parameters follow the OWASP storage baseline for an offline
	// attacker; the wrap happens once per server start, so the cost is fine.
	argonTime    = 3
	argonMemory  = 64 * 1024
	argonThreads = 1
)

var ErrAuthFailed = errors.New("key wrap authentication failed")

// IsWrapped reports whether the stored value is a wrapped secret.
func IsWrapped(value string) bool {
	return strings.HasPrefix(value, Prefix)
}

// Wrap seals the master secret with a passphrase and returns the value to
// store in the database.
func Wrap(passphrase string, master []byte) (string, error) {
	if strings.TrimSpace(passphrase) == "" {
		return "", errors.New("key wrap passphrase is required")
	}
	if len(master) < 32 {
		return "", errors.New("master secret is too short to wrap")
	}
	salt := make([]byte, saltSize)
	if _, err := rand.Read(salt); err != nil {
		return "", fmt.Errorf("generate wrap salt: %w", err)
	}
	nonce := make([]byte, nonceSize)
	if _, err := rand.Read(nonce); err != nil {
		return "", fmt.Errorf("generate wrap nonce: %w", err)
	}
	key := argon2.IDKey([]byte(passphrase), salt, argonTime, argonMemory, argonThreads, keySize)
	aead, err := newAEAD(key)
	if err != nil {
		return "", err
	}
	sealed := aead.Seal(nil, nonce, master, []byte(Prefix))
	stored := append(append(append([]byte{}, salt...), nonce...), sealed...)
	return Prefix + base64.RawURLEncoding.EncodeToString(stored), nil
}

// Unwrap recovers the master secret. A wrong passphrase or a tampered value
// fails authentication without leaking which one occurred.
func Unwrap(passphrase, wrapped string) ([]byte, error) {
	if !IsWrapped(wrapped) {
		return nil, errors.New("value is not a wrapped secret")
	}
	stored, err := base64.RawURLEncoding.DecodeString(strings.TrimPrefix(wrapped, Prefix))
	if err != nil {
		return nil, fmt.Errorf("decode wrapped secret: %w", err)
	}
	if len(stored) < saltSize+nonceSize+keySize {
		return nil, fmt.Errorf("wrapped secret is truncated")
	}
	salt := stored[:saltSize]
	nonce := stored[saltSize : saltSize+nonceSize]
	sealed := stored[saltSize+nonceSize:]
	key := argon2.IDKey([]byte(passphrase), salt, argonTime, argonMemory, argonThreads, keySize)
	aead, err := newAEAD(key)
	if err != nil {
		return nil, err
	}
	master, err := aead.Open(nil, nonce, sealed, []byte(Prefix))
	if err != nil {
		return nil, fmt.Errorf("%w: passphrase does not match or value is tampered", ErrAuthFailed)
	}
	return master, nil
}

func newAEAD(key []byte) (cipher.AEAD, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, fmt.Errorf("initialize wrap cipher: %w", err)
	}
	return cipher.NewGCM(block)
}
