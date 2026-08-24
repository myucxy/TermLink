package realtimewire

import (
	"bytes"
	"encoding/hex"
	"errors"
	"testing"

	"google.golang.org/protobuf/encoding/protowire"
)

func testEnvelope() Envelope {
	return Envelope{
		ProtocolVersion: 1, MessageType: OutputChunk,
		SessionID: bytes.Repeat([]byte{0x11}, 16), SenderDeviceID: bytes.Repeat([]byte{0x22}, 16),
		Direction: PCToAndroid, Sequence: 42, ACK: 7, SentAtMS: 1_800_000_000_123,
		KeyID: []byte("epoch-01"), Nonce: bytes.Repeat([]byte{0x33}, 12), Ciphertext: bytes.Repeat([]byte{0x44}, 32),
	}
}

func TestEnvelopeRoundTripAndStableVector(t *testing.T) {
	want := testEnvelope()
	encoded, err := want.Marshal()
	if err != nil {
		t.Fatal(err)
	}
	const vector = "080110141a10111111111111111111111111111111112210222222222222222222222222222222222801302a380740fba0f1c2b1344a0865706f63682d3031520c3333333333333333333333335a204444444444444444444444444444444444444444444444444444444444444444"
	if hex.EncodeToString(encoded) != vector {
		t.Fatalf("wire vector changed:\n%s", hex.EncodeToString(encoded))
	}
	got, err := Unmarshal(encoded)
	if err != nil {
		t.Fatal(err)
	}
	if got.Sequence != want.Sequence || !bytes.Equal(got.Ciphertext, want.Ciphertext) {
		t.Fatalf("round trip mismatch: %#v", got)
	}
	associated, err := got.AssociatedData()
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(associated, got.Ciphertext) {
		t.Fatal("associated data included ciphertext")
	}
}

func TestEnvelopeRejectsDuplicateSecurityField(t *testing.T) {
	encoded, err := testEnvelope().Marshal()
	if err != nil {
		t.Fatal(err)
	}
	encoded = append(encoded, protowire.AppendTag(nil, 6, protowire.VarintType)...)
	encoded = protowire.AppendVarint(encoded, 99)
	_, err = Unmarshal(encoded)
	if !errors.Is(err, ErrInvalidEnvelope) {
		t.Fatalf("error = %v", err)
	}
}

func TestEnvelopeRejectsTLT1(t *testing.T) {
	if _, err := Unmarshal([]byte("TLT1")); !errors.Is(err, ErrInvalidEnvelope) {
		t.Fatalf("error = %v", err)
	}
}
