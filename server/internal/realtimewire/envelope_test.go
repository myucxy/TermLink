package realtimewire

import (
	"bytes"
	"encoding/hex"
	"errors"
	"strings"
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

func TestEnvelopeRejectsOversizedFieldsBeforeCopy(t *testing.T) {
	for _, test := range []struct {
		name   string
		field  protowire.Number
		bytes  int
		reason string
	}{
		{"session ID", 3, 17, "invalid ID length"},
		{"key ID", 9, 129, "invalid key ID length"},
		{"nonce", 10, 13, "invalid nonce length"},
		{"ciphertext", 11, MaxCiphertextBytes + 1, "invalid ciphertext length"},
		{"recipient ID", 12, 17, "invalid recipient ID length"},
	} {
		t.Run(test.name, func(t *testing.T) {
			frame := protowire.AppendTag(nil, test.field, protowire.BytesType)
			frame = protowire.AppendBytes(frame, bytes.Repeat([]byte{0x45}, test.bytes))
			if len(frame) > MaxFrameBytes {
				t.Fatal("test field exceeded the separate frame limit")
			}
			_, err := Unmarshal(frame)
			if err == nil || !errors.Is(err, ErrInvalidEnvelope) || !strings.Contains(err.Error(), test.reason) {
				t.Fatalf("oversized field error = %v, want %q", err, test.reason)
			}
		})
	}
	if _, err := Unmarshal(bytes.Repeat([]byte{0x45}, MaxFrameBytes+1)); !errors.Is(err, ErrInvalidEnvelope) {
		t.Fatalf("oversized frame error = %v", err)
	}
}

func TestHostHistoryAndTerminationEnvelopeRoundTrips(t *testing.T) {
	tests := []struct {
		name      string
		message   MessageType
		direction Direction
	}{
		{name: "android request", message: HostHistoryRequest, direction: AndroidToPC},
		{name: "android control", message: HostHistoryControl, direction: AndroidToPC},
		{name: "pc response", message: HostHistoryResponse, direction: PCToAndroid},
		{name: "termination response", message: SessionTerminateResponse, direction: PCToAndroid},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			want := testEnvelope()
			want.MessageType = test.message
			want.Direction = test.direction
			want.RecipientDeviceID = bytes.Repeat([]byte{0x55}, 16)
			encoded, err := want.Marshal()
			if err != nil {
				t.Fatalf("marshal request-correlated envelope: %v", err)
			}
			got, err := Unmarshal(encoded)
			if err != nil {
				t.Fatalf("unmarshal request-correlated envelope: %v", err)
			}
			if got.MessageType != want.MessageType || got.Direction != want.Direction ||
				!bytes.Equal(got.RecipientDeviceID, want.RecipientDeviceID) ||
				!bytes.Equal(got.Ciphertext, want.Ciphertext) {
				t.Fatalf("request-correlated envelope changed across wire: got=%#v want=%#v", got, want)
			}
		})
	}
}

func TestBrokerOutputEnvelopeVersionAndType(t *testing.T) {
	want := testEnvelope()
	want.MessageType = BrokerOutput
	want.ACK = 0
	encoded, err := want.Marshal()
	if err != nil {
		t.Fatal(err)
	}
	got, err := Unmarshal(encoded)
	if err != nil || got.MessageType != BrokerOutput || got.Sequence != want.Sequence ||
		!bytes.Equal(got.Ciphertext, want.Ciphertext) {
		t.Fatalf("broker output round trip: got=%#v err=%v", got, err)
	}
	unsupportedWire := append([]byte(nil), encoded...)
	if len(unsupportedWire) < 2 || unsupportedWire[0] != 0x08 || unsupportedWire[1] != 0x01 {
		t.Fatal("broker envelope protocol version field changed")
	}
	unsupportedWire[1] = 0x02
	if _, err := Unmarshal(unsupportedWire); !errors.Is(err, ErrInvalidEnvelope) {
		t.Fatalf("unknown wire protocol version error = %v", err)
	}
	for _, invalid := range []struct {
		name string
		edit func(*Envelope)
	}{
		{"future protocol version", func(e *Envelope) { e.ProtocolVersion = 2 }},
		{"unknown broker message type", func(e *Envelope) { e.MessageType = BrokerOutput + 1 }},
	} {
		t.Run(invalid.name, func(t *testing.T) {
			candidate := want
			invalid.edit(&candidate)
			if _, err := candidate.Marshal(); !errors.Is(err, ErrInvalidEnvelope) {
				t.Fatalf("invalid broker envelope error = %v", err)
			}
		})
	}
}
