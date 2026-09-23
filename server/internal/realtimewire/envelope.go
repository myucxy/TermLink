// Package realtimewire implements the small, security-sensitive protobuf
// Envelope codec used by the Relay. Payload ciphertext remains opaque.
package realtimewire

import (
	"errors"
	"fmt"

	"google.golang.org/protobuf/encoding/protowire"
)

const (
	ProtocolVersion    uint32 = 1
	MaxFrameBytes             = 1 << 20
	MaxCiphertextBytes        = MaxFrameBytes - 256
)

type Direction uint32

const (
	DirectionUnspecified Direction = 0
	PCToAndroid          Direction = 1
	AndroidToPC          Direction = 2
)

type MessageType uint32

const (
	MessageUnspecified       MessageType = 0
	Heartbeat                MessageType = 1
	Goodbye                  MessageType = 2
	SessionUpsert            MessageType = 10
	SessionRemoved           MessageType = 11
	Subscribe                MessageType = 12
	Unsubscribe              MessageType = 13
	OutputChunk              MessageType = 20
	OutputACK                MessageType = 21
	FlowControl              MessageType = 22
	Gap                      MessageType = 23
	InputEvent               MessageType = 30
	InputAccepted            MessageType = 31
	InputRejected            MessageType = 32
	ResizeRequest            MessageType = 40
	ResizeApplied            MessageType = 41
	SnapshotOffer            MessageType = 50
	SnapshotChunk            MessageType = 51
	ResumeRequest            MessageType = 52
	LeaseRequest             MessageType = 60
	LeaseGranted             MessageType = 61
	LeaseDenied              MessageType = 62
	LeaseRevoked             MessageType = 63
	ThemeChanged             MessageType = 70
	SessionStarted           MessageType = 80
	SessionInterrupted       MessageType = 81
	SessionEnded             MessageType = 82
	SessionTerminateRequest  MessageType = 83
	HostHistoryRequest       MessageType = 84
	HostHistoryResponse      MessageType = 85
	HostHistoryControl       MessageType = 86
	SessionTerminateResponse MessageType = 87
	SessionCatalogRequest    MessageType = 88
	SessionCatalogResponse   MessageType = 89
	SessionCleanupRequest    MessageType = 90
	SessionCleanupResponse   MessageType = 91
)

var ErrInvalidEnvelope = errors.New("invalid production envelope")

type Envelope struct {
	ProtocolVersion   uint32
	MessageType       MessageType
	SessionID         []byte
	SenderDeviceID    []byte
	Direction         Direction
	Sequence          uint64
	ACK               uint64
	SentAtMS          int64
	KeyID             []byte
	Nonce             []byte
	Ciphertext        []byte
	RecipientDeviceID []byte
}

func (e Envelope) Marshal() ([]byte, error) {
	if err := e.Validate(); err != nil {
		return nil, err
	}
	result := e.appendFields(nil, true)
	if len(result) > MaxFrameBytes {
		return nil, fmt.Errorf("%w: frame too large", ErrInvalidEnvelope)
	}
	return result, nil
}

// AssociatedData returns the deterministic protobuf encoding of fields 1-10.
func (e Envelope) AssociatedData() ([]byte, error) {
	if err := e.ValidateMetadata(); err != nil {
		return nil, err
	}
	return e.appendFields(nil, false), nil
}

func (e Envelope) appendFields(dst []byte, includeCiphertext bool) []byte {
	dst = appendVarint(dst, 1, uint64(e.ProtocolVersion))
	dst = appendVarint(dst, 2, uint64(e.MessageType))
	dst = appendBytes(dst, 3, e.SessionID)
	dst = appendBytes(dst, 4, e.SenderDeviceID)
	dst = appendVarint(dst, 5, uint64(e.Direction))
	dst = appendVarint(dst, 6, e.Sequence)
	if e.ACK != 0 {
		dst = appendVarint(dst, 7, e.ACK)
	}
	dst = appendVarint(dst, 8, uint64(e.SentAtMS))
	dst = appendBytes(dst, 9, e.KeyID)
	dst = appendBytes(dst, 10, e.Nonce)
	if includeCiphertext {
		dst = appendBytes(dst, 11, e.Ciphertext)
	}
	if len(e.RecipientDeviceID) > 0 {
		dst = appendBytes(dst, 12, e.RecipientDeviceID)
	}
	return dst
}

func Unmarshal(frame []byte) (Envelope, error) {
	if len(frame) == 0 || len(frame) > MaxFrameBytes {
		return Envelope{}, fmt.Errorf("%w: invalid frame size", ErrInvalidEnvelope)
	}
	var result Envelope
	var seen uint16
	for len(frame) > 0 {
		number, kind, count := protowire.ConsumeTag(frame)
		if count < 0 {
			return Envelope{}, parseError(count)
		}
		frame = frame[count:]
		if number >= 1 && number <= 12 {
			mask := uint16(1) << uint(number-1)
			if seen&mask != 0 {
				return Envelope{}, fmt.Errorf("%w: duplicate field %d", ErrInvalidEnvelope, number)
			}
			seen |= mask
		}
		switch number {
		case 1, 2, 5, 6, 7, 8:
			if kind != protowire.VarintType {
				return Envelope{}, fieldTypeError(number)
			}
			value, n := protowire.ConsumeVarint(frame)
			if n < 0 {
				return Envelope{}, parseError(n)
			}
			frame = frame[n:]
			switch number {
			case 1:
				result.ProtocolVersion = uint32(value)
			case 2:
				result.MessageType = MessageType(value)
			case 5:
				result.Direction = Direction(value)
			case 6:
				result.Sequence = value
			case 7:
				result.ACK = value
			case 8:
				result.SentAtMS = int64(value)
			}
		case 3, 4, 9, 10, 11, 12:
			if kind != protowire.BytesType {
				return Envelope{}, fieldTypeError(number)
			}
			value, n := protowire.ConsumeBytes(frame)
			if n < 0 {
				return Envelope{}, parseError(n)
			}
			frame = frame[n:]
			value = append([]byte(nil), value...)
			switch number {
			case 3:
				result.SessionID = value
			case 4:
				result.SenderDeviceID = value
			case 9:
				result.KeyID = value
			case 10:
				result.Nonce = value
			case 11:
				result.Ciphertext = value
			case 12:
				result.RecipientDeviceID = value
			}
		default:
			n := protowire.ConsumeFieldValue(number, kind, frame)
			if n < 0 {
				return Envelope{}, parseError(n)
			}
			frame = frame[n:]
		}
	}
	if err := result.Validate(); err != nil {
		return Envelope{}, err
	}
	return result, nil
}

func (e Envelope) Validate() error {
	if err := e.ValidateMetadata(); err != nil {
		return err
	}
	if len(e.Ciphertext) < 16 || len(e.Ciphertext) > MaxCiphertextBytes {
		return fmt.Errorf("%w: invalid ciphertext length", ErrInvalidEnvelope)
	}
	return nil
}

func (e Envelope) ValidateMetadata() error {
	if e.ProtocolVersion != ProtocolVersion {
		return fmt.Errorf("%w: unsupported version", ErrInvalidEnvelope)
	}
	if !knownMessage(e.MessageType) {
		return fmt.Errorf("%w: unknown message type", ErrInvalidEnvelope)
	}
	if len(e.SessionID) != 16 || len(e.SenderDeviceID) != 16 {
		return fmt.Errorf("%w: IDs must be 16 bytes", ErrInvalidEnvelope)
	}
	if e.Direction != PCToAndroid && e.Direction != AndroidToPC {
		return fmt.Errorf("%w: invalid direction", ErrInvalidEnvelope)
	}
	if e.Sequence == 0 || e.SentAtMS <= 0 {
		return fmt.Errorf("%w: sequence and timestamp required", ErrInvalidEnvelope)
	}
	if len(e.KeyID) < 8 || len(e.KeyID) > 128 || len(e.Nonce) != 12 {
		return fmt.Errorf("%w: invalid key metadata", ErrInvalidEnvelope)
	}
	if len(e.RecipientDeviceID) != 0 && len(e.RecipientDeviceID) != 16 {
		return fmt.Errorf("%w: recipient ID must be empty or 16 bytes", ErrInvalidEnvelope)
	}
	return nil
}

func knownMessage(value MessageType) bool {
	switch value {
	case Heartbeat, Goodbye, SessionUpsert, SessionRemoved, Subscribe, Unsubscribe,
		OutputChunk, OutputACK, FlowControl, Gap, InputEvent, InputAccepted,
		InputRejected, ResizeRequest, ResizeApplied, SnapshotOffer, SnapshotChunk,
		ResumeRequest, LeaseRequest, LeaseGranted, LeaseDenied, LeaseRevoked,
		ThemeChanged, SessionStarted, SessionInterrupted, SessionEnded,
		SessionTerminateRequest, HostHistoryRequest, HostHistoryResponse, HostHistoryControl,
		SessionTerminateResponse, SessionCatalogRequest, SessionCatalogResponse, SessionCleanupRequest, SessionCleanupResponse:
		return true
	default:
		return false
	}
}

func appendVarint(dst []byte, number protowire.Number, value uint64) []byte {
	dst = protowire.AppendTag(dst, number, protowire.VarintType)
	return protowire.AppendVarint(dst, value)
}

func appendBytes(dst []byte, number protowire.Number, value []byte) []byte {
	dst = protowire.AppendTag(dst, number, protowire.BytesType)
	return protowire.AppendBytes(dst, value)
}

func parseError(code int) error {
	return fmt.Errorf("%w: %v", ErrInvalidEnvelope, protowire.ParseError(code))
}
func fieldTypeError(number protowire.Number) error {
	return fmt.Errorf("%w: wrong wire type for field %d", ErrInvalidEnvelope, number)
}
