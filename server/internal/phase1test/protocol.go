package phase1test

import (
	"encoding/binary"
	"errors"
	"fmt"
)

const (
	ProtocolVersion byte = 1
	HeaderSize           = 24
	MaxFrameSize         = 1 << 20
)

var protocolMagic = [4]byte{'T', 'L', 'T', '1'}

const (
	FrameResume        byte = 1
	FrameCumulativeACK byte = 2
	FrameInput         byte = 3
	FrameResizeRequest byte = 4
	FrameLeaseRequest  byte = 5
	FrameLeaseRelease  byte = 6
	FrameWelcome       byte = 129
	FrameOutput        byte = 130
	FrameResize        byte = 131
	FrameLifecycle     byte = 132
	FrameGap           byte = 133
	FrameInputAccepted byte = 134
	FrameLeaseGranted  byte = 135
	FrameLeaseRevoked  byte = 136
	FrameFlowControl   byte = 137
	FrameError         byte = 138
	FrameTitle         byte = 139
)

const (
	LifecycleRunning     byte = 1
	LifecycleInterrupted byte = 2
	LifecycleEnded       byte = 3
)

type Frame struct {
	Type     byte
	Sequence uint64
	ACK      uint64
	Payload  []byte
}

func EncodeFrame(frame Frame) ([]byte, error) {
	if len(frame.Payload) > MaxFrameSize-HeaderSize {
		return nil, fmt.Errorf("payload exceeds phase 1 test frame limit")
	}
	encoded := make([]byte, HeaderSize+len(frame.Payload))
	copy(encoded[0:4], protocolMagic[:])
	encoded[4] = ProtocolVersion
	encoded[5] = frame.Type
	binary.BigEndian.PutUint64(encoded[8:16], frame.Sequence)
	binary.BigEndian.PutUint64(encoded[16:24], frame.ACK)
	copy(encoded[HeaderSize:], frame.Payload)
	return encoded, nil
}

func DecodeFrame(encoded []byte) (Frame, error) {
	if len(encoded) < HeaderSize {
		return Frame{}, errors.New("phase 1 test frame is shorter than its header")
	}
	if len(encoded) > MaxFrameSize {
		return Frame{}, errors.New("phase 1 test frame exceeds limit")
	}
	if string(encoded[0:4]) != string(protocolMagic[:]) {
		return Frame{}, errors.New("phase 1 test frame magic mismatch")
	}
	if encoded[4] != ProtocolVersion {
		return Frame{}, fmt.Errorf("unsupported phase 1 test version %d", encoded[4])
	}
	if encoded[6] != 0 || encoded[7] != 0 {
		return Frame{}, errors.New("phase 1 test frame flags must be zero")
	}
	return Frame{
		Type:     encoded[5],
		Sequence: binary.BigEndian.Uint64(encoded[8:16]),
		ACK:      binary.BigEndian.Uint64(encoded[16:24]),
		Payload:  append([]byte(nil), encoded[HeaderSize:]...),
	}, nil
}

func Uint16Pair(first, second uint16) []byte {
	payload := make([]byte, 4)
	binary.BigEndian.PutUint16(payload[0:2], first)
	binary.BigEndian.PutUint16(payload[2:4], second)
	return payload
}

func Uint64Pair(first, second uint64) []byte {
	payload := make([]byte, 16)
	binary.BigEndian.PutUint64(payload[0:8], first)
	binary.BigEndian.PutUint64(payload[8:16], second)
	return payload
}
