package transport

import (
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestCanonicalDirectTicketMatchesCrossLanguageVector(t *testing.T) {
	seed := make([]byte, ed25519.SeedSize)
	for index := range seed {
		seed[index] = byte(index)
	}
	private := ed25519.NewKeyFromSeed(seed)
	public := private.Public().(ed25519.PublicKey)
	ticketID := uuid.MustParse("00112233-4455-4677-8899-aabbccddeeff")
	accountID := uuid.MustParse("10213243-5465-4768-899a-abbccddeeff0")
	initiatorID := uuid.MustParse("20314253-6475-4869-8a9b-acbdcedff001")
	responderID := uuid.MustParse("30415263-7485-496a-8b9c-adbecfe0f102")
	connectionID := uuid.MustParse("40516273-8495-4a6b-8c9d-aebfd0e1f203")
	issuedAt := time.UnixMilli(1_900_000_000_000).UTC()
	expiresAt := issuedAt.Add(120 * time.Second)
	nonce := []byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15}
	keyID := []byte("direct-v1-vector")
	claims := marshalTicketClaims(ticketID, accountID, initiatorID, responderID, connectionID, 7, issuedAt, expiresAt, nonce, keyID)
	signature := ed25519.Sign(private, append([]byte(ticketDomain), claims...))
	ticket := append(append([]byte(nil), claims...), appendBytesField(nil, 13, signature)...)
	var vector struct {
		PublicKey string `json:"server_public_key_base64url"`
		Ticket    string `json:"ticket_protobuf_base64url"`
		SHA256    string `json:"ticket_sha256"`
	}
	path := filepath.Join("..", "..", "..", "protocol", "test-vectors", "direct-transport-ticket-v1.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read direct ticket vector: %v", err)
	}
	if err := json.Unmarshal(raw, &vector); err != nil {
		t.Fatalf("decode direct ticket vector: %v", err)
	}
	if got := base64.RawURLEncoding.EncodeToString(public); got != vector.PublicKey {
		t.Fatalf("public key=%s, want vector %s", got, vector.PublicKey)
	}
	if got := base64.RawURLEncoding.EncodeToString(ticket); got != vector.Ticket {
		t.Fatalf("ticket=%s, want canonical vector %s", got, vector.Ticket)
	}
	digest := sha256.Sum256(ticket)
	if got := hex.EncodeToString(digest[:]); got != vector.SHA256 {
		t.Fatalf("ticket sha256=%s, want %s", got, vector.SHA256)
	}
}
