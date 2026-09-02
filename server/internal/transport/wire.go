// Package transport implements the independent, opaque direct-transport
// signalling control plane. It never parses candidate or terminal ciphertext.
package transport

import (
	"bytes"
	"errors"
	"fmt"
	"math"

	"google.golang.org/protobuf/encoding/protowire"
)

const (
	ProtocolVersion uint32 = 1
	MaxSignalBytes         = 64 << 10
	maxCipherBytes         = MaxSignalBytes - 128
)

var ErrInvalidSignal = errors.New("invalid transport signal")

type SignalType uint32

const (
	SignalPathOffer          SignalType = 1
	SignalPathAnswer         SignalType = 2
	SignalCandidateUpdate    SignalType = 3
	SignalNomination         SignalType = 4
	SignalNominationResult   SignalType = 5
	SignalCutoverProposal    SignalType = 6
	SignalCutoverAccept      SignalType = 7
	SignalCutoverCommit      SignalType = 8
	SignalFallback           SignalType = 9
	SignalPathClose          SignalType = 10
	SignalCheckBatchV2       SignalType = 11
	SignalNominationV2       SignalType = 12
	SignalNominationAckV2    SignalType = 13
	SignalNominationResultV2 SignalType = 14
)

type SignalEnvelope struct {
	ProtocolVersion   uint32
	SignalType        SignalType
	ConnectionID      []byte
	SenderDeviceID    []byte
	RecipientDeviceID []byte
	PathEpoch         uint64
	SignalSeq         uint64
	SignalACK         uint64
	SentAtMS          int64
	KeyID             []byte
	Nonce             []byte
	Ciphertext        []byte
}

func UnmarshalSignal(frame []byte) (SignalEnvelope, error) {
	if len(frame) == 0 || len(frame) > MaxSignalBytes {
		return SignalEnvelope{}, fmt.Errorf("%w: invalid frame size", ErrInvalidSignal)
	}
	raw := frame
	var result SignalEnvelope
	var seen uint16
	for len(frame) > 0 {
		number, kind, n := protowire.ConsumeTag(frame)
		if n < 0 {
			return SignalEnvelope{}, signalParseError(n)
		}
		frame = frame[n:]
		if number < 1 || number > 12 {
			return SignalEnvelope{}, fmt.Errorf("%w: unknown field %d", ErrInvalidSignal, number)
		}
		mask := uint16(1) << uint(number-1)
		if seen&mask != 0 {
			return SignalEnvelope{}, fmt.Errorf("%w: duplicate field %d", ErrInvalidSignal, number)
		}
		seen |= mask
		switch number {
		case 1, 2, 6, 7, 8, 9:
			if kind != protowire.VarintType {
				return SignalEnvelope{}, fmt.Errorf("%w: wrong wire type", ErrInvalidSignal)
			}
			value, consumed := protowire.ConsumeVarint(frame)
			if consumed < 0 {
				return SignalEnvelope{}, signalParseError(consumed)
			}
			frame = frame[consumed:]
			switch number {
			case 1:
				result.ProtocolVersion = uint32(value)
			case 2:
				result.SignalType = SignalType(value)
			case 6:
				result.PathEpoch = value
			case 7:
				result.SignalSeq = value
			case 8:
				result.SignalACK = value
			case 9:
				result.SentAtMS = int64(value)
			}
		case 3, 4, 5, 10, 11, 12:
			if kind != protowire.BytesType {
				return SignalEnvelope{}, fmt.Errorf("%w: wrong wire type", ErrInvalidSignal)
			}
			value, consumed := protowire.ConsumeBytes(frame)
			if consumed < 0 {
				return SignalEnvelope{}, signalParseError(consumed)
			}
			frame = frame[consumed:]
			value = append([]byte(nil), value...)
			switch number {
			case 3:
				result.ConnectionID = value
			case 4:
				result.SenderDeviceID = value
			case 5:
				result.RecipientDeviceID = value
			case 10:
				result.KeyID = value
			case 11:
				result.Nonce = value
			case 12:
				result.Ciphertext = value
			}
		}
	}
	if err := result.Validate(); err != nil {
		return SignalEnvelope{}, err
	}
	if !bytes.Equal(raw, marshalSignalCanonical(result)) {
		return SignalEnvelope{}, fmt.Errorf("%w: non-canonical encoding", ErrInvalidSignal)
	}
	return result, nil
}

func marshalSignalCanonical(envelope SignalEnvelope) []byte {
	result := make([]byte, 0, len(envelope.Ciphertext)+128)
	result = appendVarintField(result, 1, uint64(envelope.ProtocolVersion))
	result = appendVarintField(result, 2, uint64(envelope.SignalType))
	result = appendBytesField(result, 3, envelope.ConnectionID)
	result = appendBytesField(result, 4, envelope.SenderDeviceID)
	result = appendBytesField(result, 5, envelope.RecipientDeviceID)
	result = appendVarintField(result, 6, envelope.PathEpoch)
	result = appendVarintField(result, 7, envelope.SignalSeq)
	if envelope.SignalACK != 0 {
		result = appendVarintField(result, 8, envelope.SignalACK)
	}
	result = appendVarintField(result, 9, uint64(envelope.SentAtMS))
	result = appendBytesField(result, 10, envelope.KeyID)
	result = appendBytesField(result, 11, envelope.Nonce)
	result = appendBytesField(result, 12, envelope.Ciphertext)
	return result
}

func (e SignalEnvelope) Validate() error {
	if e.ProtocolVersion != ProtocolVersion || e.SignalType < SignalPathOffer || e.SignalType > SignalNominationResultV2 {
		return fmt.Errorf("%w: unsupported version or type", ErrInvalidSignal)
	}
	if len(e.ConnectionID) != 16 || len(e.SenderDeviceID) != 16 || len(e.RecipientDeviceID) != 16 {
		return fmt.Errorf("%w: IDs must be 16 bytes", ErrInvalidSignal)
	}
	if e.PathEpoch == 0 || e.PathEpoch > math.MaxInt64 || e.SignalSeq == 0 || e.SignalSeq > math.MaxInt64 || e.SignalACK > math.MaxInt64 || e.SentAtMS <= 0 {
		return fmt.Errorf("%w: invalid sequence metadata", ErrInvalidSignal)
	}
	if len(e.KeyID) == 0 || len(e.KeyID) > 64 || len(e.Nonce) != 12 || len(e.Ciphertext) < 16 || len(e.Ciphertext) > maxCipherBytes {
		return fmt.Errorf("%w: invalid encrypted payload metadata", ErrInvalidSignal)
	}
	return nil
}

func signalParseError(code int) error {
	return fmt.Errorf("%w: %v", ErrInvalidSignal, protowire.ParseError(code))
}
