package filetransfer

import (
	"bytes"
	"testing"

	"github.com/google/uuid"
	"google.golang.org/protobuf/encoding/protowire"
)

func TestParseEnvelopeAcceptsOnlyBoundedCanonicalRoutingHeader(t *testing.T) {
	transferID, senderID, recipientID := uuid.New(), uuid.New(), uuid.New()
	frame := testEnvelopeFrame(transferID, senderID, recipientID, 3, 9)
	parsed, err := ParseEnvelope(frame)
	if err != nil {
		t.Fatal(err)
	}
	if parsed.ProtocolVersion != ProtocolVersion || parsed.MessageType != 3 || parsed.Sequence != 9 ||
		!bytes.Equal(parsed.TransferID, transferID[:]) || !bytes.Equal(parsed.SenderDeviceID, senderID[:]) ||
		!bytes.Equal(parsed.RecipientDeviceID, recipientID[:]) {
		t.Fatalf("unexpected parsed routing header: %+v", parsed)
	}

	for name, mutate := range map[string]func([]byte) []byte{
		"empty":    func([]byte) []byte { return nil },
		"oversize": func([]byte) []byte { return make([]byte, MaxFrameBytes+1) },
		"duplicate": func(value []byte) []byte {
			return protowire.AppendVarint(protowire.AppendTag(value, 6, protowire.VarintType), 10)
		},
		"unknown": func(value []byte) []byte {
			return protowire.AppendVarint(protowire.AppendTag(value, 11, protowire.VarintType), 1)
		},
		"zero-sequence": func([]byte) []byte {
			return testEnvelopeFrame(transferID, senderID, recipientID, 3, 0)
		},
		"same-peer": func([]byte) []byte {
			return testEnvelopeFrame(transferID, senderID, senderID, 3, 1)
		},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := ParseEnvelope(mutate(append([]byte(nil), frame...))); err == nil {
				t.Fatal("malformed file-transfer envelope was accepted")
			}
		})
	}
}

func testEnvelopeFrame(
	transferID, senderID, recipientID uuid.UUID,
	messageType, sequence uint64,
) []byte {
	var frame []byte
	frame = appendVarintField(frame, 1, ProtocolVersion)
	frame = appendBytesField(frame, 2, transferID[:])
	frame = appendBytesField(frame, 3, senderID[:])
	frame = appendBytesField(frame, 4, recipientID[:])
	frame = appendVarintField(frame, 5, messageType)
	frame = appendVarintField(frame, 6, sequence)
	frame = appendVarintField(frame, 7, 1)
	frame = appendBytesField(frame, 8, []byte("file-key-id"))
	frame = appendBytesField(frame, 9, make([]byte, nonceBytes))
	frame = appendBytesField(frame, 10, make([]byte, 16))
	return frame
}

func appendVarintField(target []byte, number protowire.Number, value uint64) []byte {
	target = protowire.AppendTag(target, number, protowire.VarintType)
	return protowire.AppendVarint(target, value)
}

func appendBytesField(target []byte, number protowire.Number, value []byte) []byte {
	target = protowire.AppendTag(target, number, protowire.BytesType)
	return protowire.AppendBytes(target, value)
}
