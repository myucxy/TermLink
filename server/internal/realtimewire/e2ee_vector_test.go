package realtimewire

import (
	"bytes"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"testing"

	"github.com/google/uuid"
	"golang.org/x/crypto/chacha20poly1305"
	"golang.org/x/crypto/curve25519"
	"golang.org/x/crypto/hkdf"
)

type e2eeVector struct {
	PCPrivateKey      string `json:"pc_private_key"`
	PCPublicKey       string `json:"pc_public_key"`
	AndroidPrivateKey string `json:"android_private_key"`
	AndroidPublicKey  string `json:"android_public_key"`
	PCDeviceID        string `json:"pc_device_id"`
	AndroidDeviceID   string `json:"android_device_id"`
	SessionID         string `json:"session_id"`
	KeyEpoch          int64  `json:"key_epoch"`
	KeyID             string `json:"key_id_utf8"`
	Direction         byte   `json:"direction"`
	Sequence          uint64 `json:"sequence"`
	Nonce             string `json:"nonce"`
	PairwiseKey       string `json:"pairwise_key"`
	SessionKey        string `json:"session_key"`
	AssociatedData    string `json:"associated_data"`
	Plaintext         string `json:"plaintext"`
	Ciphertext        string `json:"ciphertext"`
}

func TestCrossPlatformE2EEVector(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "protocol", "test-vectors", "e2ee-session-v1.json"))
	if err != nil {
		t.Fatal(err)
	}
	var v e2eeVector
	if err := json.Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	pcPrivate := decodeHex(t, v.PCPrivateKey)
	androidPrivate := decodeHex(t, v.AndroidPrivateKey)
	pcPublic, err := curve25519.X25519(pcPrivate, curve25519.Basepoint)
	if err != nil {
		t.Fatal(err)
	}
	androidPublic, err := curve25519.X25519(androidPrivate, curve25519.Basepoint)
	if err != nil {
		t.Fatal(err)
	}
	requireHex(t, "pc public", pcPublic, v.PCPublicKey)
	requireHex(t, "android public", androidPublic, v.AndroidPublicKey)
	shared, err := curve25519.X25519(pcPrivate, androidPublic)
	if err != nil {
		t.Fatal(err)
	}
	pcID := uuid.MustParse(v.PCDeviceID)
	androidID := uuid.MustParse(v.AndroidDeviceID)
	epoch := make([]byte, 8)
	binary.BigEndian.PutUint64(epoch, uint64(v.KeyEpoch))
	pairInfo := bytes.Join([][]byte{[]byte("TERMLINK-PAIRWISE-V1"), {0}, pcID[:], androidID[:], epoch}, nil)
	salt := sha256.Sum256([]byte("TERMLINK-E2EE-V1"))
	pair := make([]byte, 32)
	if _, err := io.ReadFull(hkdf.New(sha256.New, shared, salt[:], pairInfo), pair); err != nil {
		t.Fatal(err)
	}
	requireHex(t, "pairwise key", pair, v.PairwiseKey)
	sessionID := uuid.MustParse(v.SessionID)
	sessionInfo := bytes.Join([][]byte{[]byte("TERMLINK-SESSION-KEY-V1"), {0}, sessionID[:], {v.Direction}, []byte(v.KeyID)}, nil)
	key := make([]byte, 32)
	if _, err := io.ReadFull(hkdf.New(sha256.New, pair, salt[:], sessionInfo), key); err != nil {
		t.Fatal(err)
	}
	requireHex(t, "session key", key, v.SessionKey)
	nonce := decodeHex(t, v.Nonce)
	aad := decodeHex(t, v.AssociatedData)
	cipher, err := chacha20poly1305.New(key)
	if err != nil {
		t.Fatal(err)
	}
	ciphertext := cipher.Seal(nil, nonce, decodeHex(t, v.Plaintext), aad)
	requireHex(t, "ciphertext", ciphertext, v.Ciphertext)
	plaintext, err := cipher.Open(nil, nonce, ciphertext, aad)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(plaintext, decodeHex(t, v.Plaintext)) {
		t.Fatal("plaintext mismatch")
	}
}

func decodeHex(t *testing.T, value string) []byte {
	t.Helper()
	result, err := hex.DecodeString(value)
	if err != nil {
		t.Fatal(err)
	}
	return result
}
func requireHex(t *testing.T, label string, got []byte, want string) {
	t.Helper()
	if hex.EncodeToString(got) != want {
		t.Fatalf("%s = %x", label, got)
	}
}
