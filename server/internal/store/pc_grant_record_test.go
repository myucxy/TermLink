package store

import (
	"encoding/base64"
	"encoding/json"
	"testing"

	"github.com/google/uuid"
)

func TestPCGrantRecordJSONIncludesSigningPublicKey(t *testing.T) {
	signingKey := make([]byte, 32)
	exchangeKey := make([]byte, 32)
	for i := range signingKey {
		signingKey[i] = byte(i)
		exchangeKey[i] = byte(31 - i)
	}

	payload, err := json.Marshal(PCGrantRecord{
		DeviceID:          uuid.MustParse("f163dd18-c6a7-4960-af76-416b98be56ce"),
		DeviceType:        "ANDROID",
		DeviceName:        "Peer",
		SigningPublicKey:  signingKey,
		ExchangePublicKey: exchangeKey,
		KeyID:             "peer-key",
		Permission:        "MANAGE",
	})
	if err != nil {
		t.Fatal(err)
	}

	var document map[string]any
	if err := json.Unmarshal(payload, &document); err != nil {
		t.Fatal(err)
	}
	if got := document["signing_public_key"]; got != base64.StdEncoding.EncodeToString(signingKey) {
		t.Fatalf("signing_public_key=%v, want the 32-byte device signing key", got)
	}
	if got := document["exchange_public_key"]; got != base64.StdEncoding.EncodeToString(exchangeKey) {
		t.Fatalf("exchange_public_key=%v, want the 32-byte device exchange key", got)
	}
}
