package transport

import (
	"bytes"
	"encoding/binary"
	"math"
	"testing"
	"time"

	"github.com/google/uuid"
	"google.golang.org/protobuf/encoding/protowire"
)

func TestUnmarshalSignalValidatesOpaqueOuterEnvelope(t *testing.T) {
	sender, recipient, connection := uuid.New(), uuid.New(), uuid.New()
	frame := testSignalFrame(connection, sender, recipient, 7, 1, SignalPathOffer, 32)
	envelope, err := UnmarshalSignal(frame)
	if err != nil {
		t.Fatalf("UnmarshalSignal: %v", err)
	}
	if envelope.PathEpoch != 7 || envelope.SignalSeq != 1 || envelope.SignalType != SignalPathOffer || !bytes.Equal(envelope.Ciphertext, bytes.Repeat([]byte{0x5a}, 32)) {
		t.Fatalf("unexpected envelope metadata: %+v", envelope)
	}
	if canonical := marshalSignalCanonical(envelope); !bytes.Equal(frame, canonical) {
		t.Fatalf("canonical reserialize changed wire:\nreceived=%x\nencoded =%x", frame, canonical)
	}
	multipair, err := UnmarshalSignal(testSignalFrame(
		connection, sender, recipient, 7, 1, SignalCheckBatchV2, 32,
	))
	if err != nil || multipair.SignalType != SignalCheckBatchV2 {
		t.Fatalf("multipair signal envelope=%+v error=%v", multipair, err)
	}
	withACK := envelope
	withACK.SignalACK = 1
	ackEnvelope, err := UnmarshalSignal(marshalSignalCanonical(withACK))
	if err != nil || ackEnvelope.SignalACK != 1 {
		t.Fatalf("canonical non-zero ACK envelope=%+v error=%v", ackEnvelope, err)
	}

	for name, invalid := range map[string][]byte{
		"empty":                nil,
		"oversize":             make([]byte, MaxSignalBytes+1),
		"duplicate":            append(append([]byte(nil), frame...), appendVarintField(nil, 1, 1)...),
		"unknown":              append(append([]byte(nil), frame...), appendVarintField(nil, 13, 1)...),
		"shuffled fields":      shuffledSignalFields(frame),
		"non-minimal tag":      append([]byte{0x88, 0x00, 0x01}, frame[2:]...),
		"non-minimal value":    append([]byte{0x08, 0x81, 0x00}, frame[2:]...),
		"non-minimal length":   nonMinimalConnectionLength(frame),
		"explicit default ack": insertExplicitZeroACK(frame),
		"zero epoch":           testSignalFrame(connection, sender, recipient, 0, 1, SignalPathOffer, 32),
		"zero seq":             testSignalFrame(connection, sender, recipient, 1, 0, SignalPathOffer, 32),
		"short cipher":         testSignalFrame(connection, sender, recipient, 1, 1, SignalPathOffer, 15),
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := UnmarshalSignal(invalid); err == nil {
				t.Fatal("invalid signal accepted")
			}
		})
	}

	overflowEpoch := testSignalFrame(connection, sender, recipient, uint64(math.MaxInt64)+1, 1, SignalPathOffer, 32)
	if _, err := UnmarshalSignal(overflowEpoch); err == nil {
		t.Fatal("epoch above Int64 accepted")
	}
	overflowSequence := testSignalFrame(connection, sender, recipient, 1, uint64(math.MaxInt64)+1, SignalPathOffer, 32)
	if _, err := UnmarshalSignal(overflowSequence); err == nil {
		t.Fatal("sequence above Int64 accepted")
	}
}

func testSignalFrame(connection, sender, recipient uuid.UUID, epoch, sequence uint64, signalType SignalType, cipherBytes int) []byte {
	var frame []byte
	frame = appendVarintField(frame, 1, 1)
	frame = appendVarintField(frame, 2, uint64(signalType))
	frame = appendBytesField(frame, 3, connection[:])
	frame = appendBytesField(frame, 4, sender[:])
	frame = appendBytesField(frame, 5, recipient[:])
	frame = appendVarintField(frame, 6, epoch)
	frame = appendVarintField(frame, 7, sequence)
	frame = appendVarintField(frame, 9, uint64(time.Now().UnixMilli()))
	frame = appendBytesField(frame, 10, []byte("pair-key"))
	nonce := make([]byte, 12)
	binary.BigEndian.PutUint64(nonce[:8], sequence)
	binary.BigEndian.PutUint32(nonce[8:], uint32(signalType))
	frame = appendBytesField(frame, 11, nonce)
	frame = appendBytesField(frame, 12, bytes.Repeat([]byte{0x5a}, cipherBytes))
	return frame
}

func shuffledSignalFields(frame []byte) []byte {
	// The canonical test frame begins with the one-byte fields 1 and 2.
	result := make([]byte, 0, len(frame))
	result = append(result, frame[2:4]...)
	result = append(result, frame[:2]...)
	return append(result, frame[4:]...)
}

func nonMinimalConnectionLength(frame []byte) []byte {
	const connectionOffset = 4
	result := append([]byte(nil), frame[:connectionOffset+1]...)
	result = append(result, 0x90, 0x00)
	return append(result, frame[connectionOffset+2:]...)
}

func insertExplicitZeroACK(frame []byte) []byte {
	rest := frame
	offset := 0
	for len(rest) > 0 {
		number, kind, tagBytes := protowire.ConsumeTag(rest)
		if tagBytes < 0 {
			return nil
		}
		fieldBytes := protowire.ConsumeFieldValue(number, kind, rest[tagBytes:])
		if fieldBytes < 0 {
			return nil
		}
		if number == 9 {
			result := append([]byte(nil), frame[:offset]...)
			result = appendVarintField(result, 8, 0)
			return append(result, frame[offset:]...)
		}
		advance := tagBytes + fieldBytes
		offset += advance
		rest = rest[advance:]
	}
	return nil
}
