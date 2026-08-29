package accountdeletion

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/binary"
	"errors"
	"io"

	"github.com/google/uuid"
)

const (
	IdentityHandleKeySize        = 32
	IdentityHandleCiphertextSize = 64
	identityHandlePlaintextSize  = 36
	identityHandleMaxKeyVersion  = int16(32767)
)

var ErrIdentityHandleInvalid = errors.New("account deletion identity handle invalid")

type IdentityHandleCodec struct {
	keys map[int16]cipher.AEAD
	rand io.Reader
}

// NewIdentityHandleCodec copies and expands every AES-256 key. The caller may
// rotate or clear its source buffers after this returns. A codec may contain
// multiple positive versions so jobs encrypted before rotation remain usable.
func NewIdentityHandleCodec(keys map[int16][]byte) (*IdentityHandleCodec, error) {
	if len(keys) == 0 || len(keys) > 32 {
		return nil, ErrInvalidConfiguration
	}
	result := &IdentityHandleCodec{keys: make(map[int16]cipher.AEAD, len(keys)), rand: rand.Reader}
	for version, key := range keys {
		if version < 1 || len(key) != IdentityHandleKeySize {
			return nil, ErrInvalidConfiguration
		}
		keyCopy := append([]byte(nil), key...)
		block, err := aes.NewCipher(keyCopy)
		clear(keyCopy)
		if err != nil {
			return nil, ErrInvalidConfiguration
		}
		aead, err := cipher.NewGCM(block)
		if err != nil || aead.NonceSize()+identityHandlePlaintextSize+aead.Overhead() != IdentityHandleCiphertextSize {
			return nil, ErrInvalidConfiguration
		}
		result.keys[version] = aead
	}
	return result, nil
}

// Encrypt accepts only the canonical lower-case hyphenated Keycloak UUID.
// The returned bytes are nonce||ciphertext||tag and contain no embedded key
// version; PostgreSQL stores the key version in its separately constrained
// column and both values are immutable once bound.
func (c *IdentityHandleCodec) Encrypt(accountID, requestID uuid.UUID, keyVersion int16, keycloakSubject string) ([]byte, error) {
	aead, ok := c.identityHandleAEAD(accountID, requestID, keyVersion)
	if !ok || !canonicalKeycloakSubject(keycloakSubject) {
		return nil, ErrIdentityHandleInvalid
	}
	nonce := make([]byte, aead.NonceSize())
	if _, err := io.ReadFull(c.rand, nonce); err != nil {
		return nil, ErrIdentityHandleInvalid
	}
	result := make([]byte, len(nonce), IdentityHandleCiphertextSize)
	copy(result, nonce)
	result = aead.Seal(result, nonce, []byte(keycloakSubject), identityHandleAAD(accountID, requestID, keyVersion))
	if len(result) != IdentityHandleCiphertextSize {
		return nil, ErrIdentityHandleInvalid
	}
	return result, nil
}

func (c *IdentityHandleCodec) Decrypt(accountID, requestID uuid.UUID, keyVersion int16, encrypted []byte) (string, error) {
	aead, ok := c.identityHandleAEAD(accountID, requestID, keyVersion)
	if !ok || len(encrypted) != IdentityHandleCiphertextSize {
		return "", ErrIdentityHandleInvalid
	}
	nonceSize := aead.NonceSize()
	plaintext, err := aead.Open(nil, encrypted[:nonceSize], encrypted[nonceSize:], identityHandleAAD(accountID, requestID, keyVersion))
	if err != nil || len(plaintext) != identityHandlePlaintextSize {
		return "", ErrIdentityHandleInvalid
	}
	subject := string(plaintext)
	clear(plaintext)
	if !canonicalKeycloakSubject(subject) {
		return "", ErrIdentityHandleInvalid
	}
	return subject, nil
}

func (c *IdentityHandleCodec) identityHandleAEAD(accountID, requestID uuid.UUID, keyVersion int16) (cipher.AEAD, bool) {
	if c == nil || accountID == uuid.Nil || requestID == uuid.Nil || keyVersion < 1 || keyVersion > identityHandleMaxKeyVersion {
		return nil, false
	}
	aead, ok := c.keys[keyVersion]
	return aead, ok
}

func identityHandleAAD(accountID, requestID uuid.UUID, keyVersion int16) []byte {
	const domain = "termlink/account-deletion/identity-handle/v1\x00"
	result := make([]byte, len(domain)+16+16+2)
	copy(result, domain)
	offset := len(domain)
	copy(result[offset:], accountID[:])
	offset += 16
	copy(result[offset:], requestID[:])
	offset += 16
	binary.BigEndian.PutUint16(result[offset:], uint16(keyVersion))
	return result
}

func canonicalKeycloakSubject(subject string) bool {
	parsed, err := uuid.Parse(subject)
	return err == nil && parsed != uuid.Nil && parsed.String() == subject
}
