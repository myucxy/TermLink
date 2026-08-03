package lanreplay

import (
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
)

const (
	ProtocolVersion byte = 1
	FrameStarted    byte = 1
	FrameResize     byte = 2
	FrameOutput     byte = 3
	FrameEnded      byte = 4
	frameHeaderSize      = 18
)

type StartedMetadata struct {
	CaseName         string `json:"case_name"`
	EventCount       int    `json:"event_count"`
	OutputEventCount int    `json:"output_event_count"`
	OutputBytes      int    `json:"output_bytes"`
	OutputSHA256     string `json:"output_sha256"`
}

func (bundle *Bundle) Frame(event Event) ([]byte, error) {
	var (
		frameType byte
		payload   []byte
		err       error
	)
	switch event.Type {
	case "output":
		frameType = FrameOutput
		start := int(*event.DataOffset)
		end := start + *event.DataLength
		payload = bundle.Output[start:end]
	case "resize":
		frameType = FrameResize
		payload = make([]byte, 4)
		binary.BigEndian.PutUint16(payload[0:2], uint16(*event.Columns))
		binary.BigEndian.PutUint16(payload[2:4], uint16(*event.Rows))
	case "lifecycle":
		switch event.State {
		case "started":
			frameType = FrameStarted
			sum := sha256.Sum256(bundle.Output)
			payload, err = json.Marshal(StartedMetadata{
				CaseName:         bundle.Manifest.CaseName,
				EventCount:       bundle.Manifest.Statistics.EventCount,
				OutputEventCount: bundle.Manifest.Statistics.OutputEventCount,
				OutputBytes:      bundle.Manifest.Statistics.OutputBytes,
				OutputSHA256:     hex.EncodeToString(sum[:]),
			})
		case "ended":
			frameType = FrameEnded
			payload = make([]byte, 4)
			binary.BigEndian.PutUint32(payload, uint32(*event.ExitCode))
		default:
			return nil, fmt.Errorf("unsupported lifecycle state")
		}
	default:
		return nil, fmt.Errorf("unsupported event type")
	}
	if err != nil {
		return nil, fmt.Errorf("encode started metadata: %w", err)
	}

	frame := make([]byte, frameHeaderSize+len(payload))
	frame[0] = ProtocolVersion
	frame[1] = frameType
	binary.BigEndian.PutUint64(frame[2:10], event.Sequence)
	binary.BigEndian.PutUint64(frame[10:18], event.ElapsedUS)
	copy(frame[frameHeaderSize:], payload)
	return frame, nil
}
