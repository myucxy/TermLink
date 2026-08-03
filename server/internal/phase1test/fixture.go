package phase1test

import (
	"encoding/binary"
	"fmt"

	"github.com/termlink/termlink/server/internal/lanreplay"
)

func fixtureFrames(bundle *lanreplay.Bundle, scenario string) ([]Frame, error) {
	includeEnded := scenario == "exited"
	frames := make([]Frame, 0, len(bundle.Events))
	for _, event := range bundle.Events {
		frame, ended, err := fixtureFrame(bundle, event)
		if err != nil {
			return nil, err
		}
		if ended && !includeEnded {
			continue
		}
		frames = append(frames, frame)
	}

	if scenario == "history-gap" {
		trimmed := frames[:0]
		for _, frame := range frames {
			if frame.Sequence <= 4 {
				trimmed = append(trimmed, frame)
			}
		}
		trimmed = append(trimmed, Frame{
			Type:     FrameGap,
			Sequence: 7,
			Payload:  Uint64Pair(5, 6),
		})
		frames = trimmed
	}
	return frames, nil
}

func fixtureFrame(bundle *lanreplay.Bundle, event lanreplay.Event) (Frame, bool, error) {
	frame := Frame{Sequence: event.Sequence}
	switch event.Type {
	case "output":
		frame.Type = FrameOutput
		start := int(*event.DataOffset)
		end := start + *event.DataLength
		frame.Payload = append([]byte(nil), bundle.Output[start:end]...)
	case "resize":
		frame.Type = FrameResize
		frame.Payload = Uint16Pair(uint16(*event.Columns), uint16(*event.Rows))
	case "lifecycle":
		frame.Type = FrameLifecycle
		frame.Payload = make([]byte, 5)
		switch event.State {
		case "started":
			frame.Payload[0] = LifecycleRunning
		case "ended":
			frame.Payload[0] = LifecycleEnded
			binary.BigEndian.PutUint32(frame.Payload[1:5], uint32(*event.ExitCode))
			return frame, true, nil
		default:
			return Frame{}, false, fmt.Errorf("unsupported fixture lifecycle %q", event.State)
		}
	default:
		return Frame{}, false, fmt.Errorf("unsupported fixture event %q", event.Type)
	}
	return frame, false, nil
}
