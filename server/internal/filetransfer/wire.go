// Package filetransfer implements the storage-free File Transfer V2 relay.
// The server deliberately parses only the bounded routing header; ciphertext
// remains opaque end-to-end encrypted data.
package filetransfer

import (
	"errors"

	"github.com/google/uuid"
	"google.golang.org/protobuf/encoding/protowire"
)

const (
	ProtocolVersion    uint64 = 2
	MaxFrameBytes             = 320 << 10
	MaxCiphertextBytes        = (256 << 10) + (8 << 10)
	maxKeyIDBytes             = 128
	minKeyIDBytes             = 8
	nonceBytes                = 12
)

type Envelope struct {
	ProtocolVersion   uint64
	TransferID        []byte
	SenderDeviceID    []byte
	RecipientDeviceID []byte
	MessageType       uint64
	Sequence          uint64
	KeyEpoch          uint64
	KeyID             []byte
	Nonce             []byte
	Ciphertext        []byte
}

func ParseEnvelope(frame []byte) (Envelope, error) {
	var value Envelope
	if len(frame) == 0 || len(frame) > MaxFrameBytes {
		return value, errors.New("file-transfer frame size is invalid")
	}
	seen := uint16(0)
	for len(frame) > 0 {
		number, wireType, count := protowire.ConsumeTag(frame)
		if count < 0 || number < 1 || number > 10 {
			return Envelope{}, errors.New("file-transfer frame has an unsupported field")
		}
		bit := uint16(1) << (number - 1)
		if seen&bit != 0 {
			return Envelope{}, errors.New("file-transfer frame has a duplicate field")
		}
		seen |= bit
		frame = frame[count:]
		switch number {
		case 1, 5, 6, 7:
			if wireType != protowire.VarintType {
				return Envelope{}, errors.New("file-transfer frame has an invalid scalar")
			}
			field, consumed := protowire.ConsumeVarint(frame)
			if consumed < 0 {
				return Envelope{}, errors.New("file-transfer frame has an invalid varint")
			}
			frame = frame[consumed:]
			switch number {
			case 1:
				value.ProtocolVersion = field
			case 5:
				value.MessageType = field
			case 6:
				value.Sequence = field
			case 7:
				value.KeyEpoch = field
			}
		default:
			if wireType != protowire.BytesType {
				return Envelope{}, errors.New("file-transfer frame has an invalid byte field")
			}
			field, consumed := protowire.ConsumeBytes(frame)
			if consumed < 0 {
				return Envelope{}, errors.New("file-transfer frame has invalid bytes")
			}
			frame = frame[consumed:]
			copyOf := append([]byte(nil), field...)
			switch number {
			case 2:
				value.TransferID = copyOf
			case 3:
				value.SenderDeviceID = copyOf
			case 4:
				value.RecipientDeviceID = copyOf
			case 8:
				value.KeyID = copyOf
			case 9:
				value.Nonce = copyOf
			case 10:
				value.Ciphertext = copyOf
			}
		}
	}
	if seen != (1<<10)-1 || value.ProtocolVersion != ProtocolVersion ||
		value.MessageType < 1 || value.MessageType > 8 || value.Sequence == 0 || value.KeyEpoch == 0 ||
		len(value.TransferID) != 16 || len(value.SenderDeviceID) != 16 || len(value.RecipientDeviceID) != 16 ||
		len(value.KeyID) < minKeyIDBytes || len(value.KeyID) > maxKeyIDBytes || len(value.Nonce) != nonceBytes ||
		len(value.Ciphertext) < 16 || len(value.Ciphertext) > MaxCiphertextBytes {
		return Envelope{}, errors.New("file-transfer frame metadata is invalid")
	}
	transferID, transferErr := uuid.FromBytes(value.TransferID)
	senderID, senderErr := uuid.FromBytes(value.SenderDeviceID)
	recipientID, recipientErr := uuid.FromBytes(value.RecipientDeviceID)
	if transferErr != nil || senderErr != nil || recipientErr != nil || transferID == uuid.Nil ||
		senderID == uuid.Nil || recipientID == uuid.Nil || senderID == recipientID {
		return Envelope{}, errors.New("file-transfer identifiers are invalid")
	}
	return value, nil
}
