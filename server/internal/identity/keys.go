package identity

import (
	"context"
	"crypto"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"sync/atomic"
	"time"

	"github.com/coreos/go-oidc/v3/oidc"
	"github.com/go-jose/go-jose/v3"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type databaseOIDCKeySet struct {
	pool  *pgxpool.Pool
	clock func() time.Time
}

func (s databaseOIDCKeySet) VerifySignature(ctx context.Context, rawJWT string) ([]byte, error) {
	keys, err := loadVerificationKeys(ctx, s.pool, s.clock().UTC())
	if err != nil {
		return nil, err
	}
	publicKeys := make([]crypto.PublicKey, 0, len(keys))
	for _, key := range keys {
		publicKeys = append(publicKeys, key.Key)
	}
	return (&oidc.StaticKeySet{PublicKeys: publicKeys}).VerifySignature(ctx, rawJWT)
}

const (
	identityKeyAlgorithm  = "RS256"
	identityKeyAADDomain  = "TermLink embedded OIDC signing key V1"
	identityKeyLockID     = int64(0x544c4944454e544b)
	identitySigningPeriod = 180 * 24 * time.Hour
	identityVerifyOverlap = 30 * 24 * time.Hour
	identityRotationLead  = 7 * 24 * time.Hour
)

type signingKey struct {
	Private     *jose.JSONWebKey
	Public      jose.JSONWebKey
	NotBefore   time.Time
	SignUntil   time.Time
	VerifyUntil time.Time
}

type signingKeyManager struct {
	pool          *pgxpool.Pool
	encryptionKey [32]byte
	clock         func() time.Time
	current       atomic.Pointer[signingKey]
}

func newSigningKeyManager(ctx context.Context, pool *pgxpool.Pool, encryptionKey []byte, clock func() time.Time) (*signingKeyManager, error) {
	if pool == nil || len(encryptionKey) != 32 || clock == nil {
		return nil, ErrIdentityUnavailable
	}
	manager := &signingKeyManager{pool: pool, clock: clock}
	copy(manager.encryptionKey[:], encryptionKey)
	if err := manager.Refresh(ctx); err != nil {
		clear(manager.encryptionKey[:])
		return nil, err
	}
	return manager, nil
}

func (m *signingKeyManager) Current() (signingKey, error) {
	if m == nil {
		return signingKey{}, ErrIdentityUnavailable
	}
	key := m.current.Load()
	if key == nil || key.Private == nil {
		return signingKey{}, ErrIdentityUnavailable
	}
	return *key, nil
}

func (m *signingKeyManager) Refresh(ctx context.Context) error {
	if m == nil {
		return ErrIdentityUnavailable
	}
	key, err := loadOrCreateSigningKey(ctx, m.pool, m.encryptionKey[:], m.clock().UTC())
	if err != nil {
		return err
	}
	m.current.Store(&key)
	return nil
}

func (m *signingKeyManager) Clear() {
	if m != nil {
		clear(m.encryptionKey[:])
	}
}

func loadOrCreateSigningKey(ctx context.Context, pool *pgxpool.Pool, encryptionKey []byte, now time.Time) (signingKey, error) {
	if pool == nil || len(encryptionKey) < 32 {
		return signingKey{}, ErrIdentityUnavailable
	}
	tx, err := pool.BeginTx(ctx, pgx.TxOptions{})
	if err != nil {
		return signingKey{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err = tx.Exec(ctx, `SELECT pg_advisory_xact_lock($1)`, identityKeyLockID); err != nil {
		return signingKey{}, err
	}
	key, err := selectSigningKey(ctx, tx, encryptionKey, now, now.Add(identityRotationLead))
	if err == nil {
		return key, tx.Commit(ctx)
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return signingKey{}, err
	}
	privateKey, err := rsa.GenerateKey(rand.Reader, 3072)
	if err != nil {
		return signingKey{}, fmt.Errorf("generate OIDC signing key: %w", err)
	}
	if err = privateKey.Validate(); err != nil {
		return signingKey{}, fmt.Errorf("validate OIDC signing key: %w", err)
	}
	keyIDBytes := make([]byte, 18)
	if _, err = rand.Read(keyIDBytes); err != nil {
		return signingKey{}, err
	}
	keyID := "tl-" + base64.RawURLEncoding.EncodeToString(keyIDBytes)
	clear(keyIDBytes)
	publicJWK := jose.JSONWebKey{
		Key: &privateKey.PublicKey, KeyID: keyID, Algorithm: identityKeyAlgorithm, Use: "sig",
	}
	publicJSON, err := json.Marshal(publicJWK)
	if err != nil {
		return signingKey{}, err
	}
	privateDER, err := x509.MarshalPKCS8PrivateKey(privateKey)
	if err != nil {
		return signingKey{}, err
	}
	defer clear(privateDER)
	ciphertext, err := sealSigningKey(encryptionKey[:32], keyID, privateDER)
	if err != nil {
		return signingKey{}, err
	}
	notBefore := now.UTC().Add(-time.Minute)
	signUntil := now.UTC().Add(identitySigningPeriod)
	verifyUntil := signUntil.Add(identityVerifyOverlap)
	_, err = tx.Exec(ctx, `
		INSERT INTO identity_signing_keys(
			key_id,algorithm,public_jwk,private_key_ciphertext,not_before,sign_until,verify_until)
		VALUES($1,$2,$3,$4,$5,$6,$7)`, keyID, identityKeyAlgorithm, publicJSON, ciphertext, notBefore, signUntil, verifyUntil)
	if err != nil {
		return signingKey{}, err
	}
	if err = tx.Commit(ctx); err != nil {
		return signingKey{}, err
	}
	return signingKey{
		Private: &jose.JSONWebKey{Key: privateKey, KeyID: keyID, Algorithm: identityKeyAlgorithm, Use: "sig"},
		Public:  publicJWK, NotBefore: notBefore, SignUntil: signUntil, VerifyUntil: verifyUntil,
	}, nil
}

func selectSigningKey(ctx context.Context, tx pgx.Tx, encryptionKey []byte, now, requiredUntil time.Time) (signingKey, error) {
	var keyID, algorithm string
	var publicJSON, ciphertext []byte
	var notBefore, signUntil, verifyUntil time.Time
	err := tx.QueryRow(ctx, `
		SELECT key_id,algorithm,public_jwk,private_key_ciphertext,not_before,sign_until,verify_until
		FROM identity_signing_keys
		WHERE not_before <= $1 AND sign_until > $2
		ORDER BY not_before DESC,key_id DESC LIMIT 1`, now.UTC(), requiredUntil.UTC()).Scan(
		&keyID, &algorithm, &publicJSON, &ciphertext, &notBefore, &signUntil, &verifyUntil)
	if err != nil {
		return signingKey{}, err
	}
	if algorithm != identityKeyAlgorithm {
		return signingKey{}, errors.New("unsupported embedded identity signing algorithm")
	}
	var publicJWK jose.JSONWebKey
	if err = json.Unmarshal(publicJSON, &publicJWK); err != nil || publicJWK.KeyID != keyID || !publicJWK.Valid() || !publicJWK.IsPublic() {
		return signingKey{}, errors.New("stored embedded identity public key is invalid")
	}
	privateDER, err := openSigningKey(encryptionKey[:32], keyID, ciphertext)
	if err != nil {
		return signingKey{}, err
	}
	defer clear(privateDER)
	parsed, err := x509.ParsePKCS8PrivateKey(privateDER)
	privateKey, ok := parsed.(*rsa.PrivateKey)
	publicKey, publicOK := publicJWK.Key.(*rsa.PublicKey)
	if err != nil || !ok || !publicOK || privateKey.Validate() != nil || privateKey.PublicKey.N.Cmp(publicKey.N) != 0 {
		return signingKey{}, errors.New("stored embedded identity private key is invalid")
	}
	return signingKey{
		Private: &jose.JSONWebKey{Key: privateKey, KeyID: keyID, Algorithm: identityKeyAlgorithm, Use: "sig"},
		Public:  publicJWK, NotBefore: notBefore, SignUntil: signUntil, VerifyUntil: verifyUntil,
	}, nil
}

func loadVerificationKeys(ctx context.Context, pool *pgxpool.Pool, now time.Time) (map[string]jose.JSONWebKey, error) {
	rows, err := pool.Query(ctx, `
		SELECT key_id,public_jwk FROM identity_signing_keys
		WHERE not_before <= $1 AND verify_until > $1 ORDER BY not_before DESC`, now.UTC())
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make(map[string]jose.JSONWebKey)
	for rows.Next() {
		var keyID string
		var encoded []byte
		if err = rows.Scan(&keyID, &encoded); err != nil {
			return nil, err
		}
		var key jose.JSONWebKey
		if err = json.Unmarshal(encoded, &key); err != nil || key.KeyID != keyID || !key.Valid() || !key.IsPublic() {
			return nil, errors.New("stored embedded identity verification key is invalid")
		}
		result[keyID] = key
	}
	if err = rows.Err(); err != nil {
		return nil, err
	}
	if len(result) == 0 {
		return nil, errors.New("embedded identity verification keys are unavailable")
	}
	return result, nil
}

func sealSigningKey(key []byte, keyID string, plaintext []byte) ([]byte, error) {
	aead, err := signingKeyAEAD(key)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, aead.NonceSize())
	if _, err = rand.Read(nonce); err != nil {
		return nil, err
	}
	return aead.Seal(nonce, nonce, plaintext, signingKeyAAD(keyID)), nil
}

func openSigningKey(key []byte, keyID string, ciphertext []byte) ([]byte, error) {
	aead, err := signingKeyAEAD(key)
	if err != nil || len(ciphertext) < aead.NonceSize()+aead.Overhead() {
		return nil, ErrIdentityUnavailable
	}
	return aead.Open(nil, ciphertext[:aead.NonceSize()], ciphertext[aead.NonceSize():], signingKeyAAD(keyID))
}

func signingKeyAEAD(key []byte) (cipher.AEAD, error) {
	if len(key) != 32 {
		return nil, ErrIdentityUnavailable
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(block)
}

func signingKeyAAD(keyID string) []byte {
	digest := sha256.Sum256([]byte(identityKeyAADDomain + "\x00" + keyID))
	return digest[:]
}
