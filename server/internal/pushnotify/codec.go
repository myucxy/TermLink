package pushnotify

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"strings"

	"github.com/google/uuid"
)

// Codec binds encrypted tokens to account/device/topic/environment. Hash and
// encryption subkeys are domain-separated; neither caller keys nor plaintext
// tokens are retained in a queued Delivery.
type Codec struct {
	aead    cipher.AEAD
	hashKey []byte
}

func New(key []byte) (*Codec, error) {
	if len(key) != 32 {
		return nil, ErrConfiguration
	}
	derive := func(label string) []byte {
		mac := hmac.New(sha256.New, key)
		_, _ = mac.Write([]byte(label))
		return mac.Sum(nil)
	}
	block, err := aes.NewCipher(derive("TERMLINK-APNS-TOKEN-AESGCM-V1"))
	if err != nil {
		return nil, ErrConfiguration
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, ErrConfiguration
	}
	return &Codec{aead: aead, hashKey: derive("TERMLINK-APNS-TOKEN-HMAC-V1")}, nil
}

// TokenHash is global for one topic/environment, independent of account, so a
// re-registered token can have a single current owner without storing the token.
func (c *Codec) TokenHash(token, topic, environment string) ([]byte, error) {
	token, err := normalizeToken(token)
	if c == nil || c.aead == nil || err != nil || !validTopic(topic) || !validEnvironment(environment) {
		return nil, ErrToken
	}
	mac := hmac.New(sha256.New, c.hashKey)
	_, _ = mac.Write(boundFields("TERMLINK-APNS-TOKEN-HASH-V1", environment, topic, token))
	return mac.Sum(nil), nil
}

func (c *Codec) Seal(token string, accountID, deviceID uuid.UUID, topic, environment string) ([]byte, error) {
	token, err := normalizeToken(token)
	if c == nil || c.aead == nil || err != nil || accountID == uuid.Nil || deviceID == uuid.Nil || !validTopic(topic) || !validEnvironment(environment) {
		return nil, ErrToken
	}
	nonce := make([]byte, c.aead.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, ErrCipher
	}
	result := append([]byte{1}, nonce...)
	return c.aead.Seal(result, nonce, []byte(token), tokenAAD(accountID, deviceID, topic, environment)), nil
}

func (c *Codec) Open(sealed []byte, accountID, deviceID uuid.UUID, topic, environment string) (string, error) {
	if c == nil || c.aead == nil || accountID == uuid.Nil || deviceID == uuid.Nil || !validTopic(topic) || !validEnvironment(environment) ||
		len(sealed) < 1+c.aead.NonceSize()+c.aead.Overhead() || len(sealed) > 1+c.aead.NonceSize()+c.aead.Overhead()+1024 || sealed[0] != 1 {
		return "", ErrCipher
	}
	nonce := sealed[1 : 1+c.aead.NonceSize()]
	plain, err := c.aead.Open(nil, nonce, sealed[1+c.aead.NonceSize():], tokenAAD(accountID, deviceID, topic, environment))
	if err != nil {
		return "", ErrCipher
	}
	defer clear(plain)
	token, err := normalizeToken(string(plain))
	if err != nil {
		return "", ErrCipher
	}
	return token, nil
}

func tokenAAD(accountID, deviceID uuid.UUID, topic, environment string) []byte {
	return boundFields("TERMLINK-APNS-TOKEN-AAD-V1", accountID.String(), deviceID.String(), topic, environment)
}

func boundFields(values ...string) []byte {
	var result []byte
	for _, value := range values {
		result = binary.BigEndian.AppendUint32(result, uint32(len(value)))
		result = append(result, value...)
	}
	return result
}

func normalizeToken(token string) (string, error) {
	if len(token) < 32 || len(token) > 1024 || len(token)%2 != 0 {
		return "", ErrToken
	}
	if _, err := hex.DecodeString(token); err != nil {
		return "", ErrToken
	}
	return strings.ToLower(token), nil
}

func validEnvironment(value string) bool {
	return value == EnvironmentSandbox || value == EnvironmentProduction
}

func validTopic(topic string) bool {
	if len(topic) < 1 || len(topic) > 255 || topic[0] == '.' || topic[len(topic)-1] == '.' || strings.Contains(topic, "..") {
		return false
	}
	for _, value := range topic {
		if value != '.' && value != '-' && (value < 'a' || value > 'z') && (value < 'A' || value > 'Z') && (value < '0' || value > '9') {
			return false
		}
	}
	return true
}
