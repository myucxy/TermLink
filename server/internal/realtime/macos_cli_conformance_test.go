package realtime

import (
	"encoding/hex"
	"encoding/json"
	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"os"
	"testing"
)

// These frame digests and sequences come from TerminalRelayValidation running
// a real macOS PTY/native core through the production encrypted C# transport.
// Validate them with the Go Relay's actual ACK/replay rules, not its C# fixture.
func TestMacCLIProductionEnvelopeConformance(t *testing.T) {
	raw, err := os.ReadFile("testdata/macos_cli_envelopes.json")
	if err != nil {
		t.Fatal(err)
	}
	var frames []struct {
		Type   realtimewire.MessageType `json:"type"`
		Seq    uint64                   `json:"seq"`
		ACK    uint64                   `json:"ack"`
		SHA256 string                   `json:"sha256"`
	}
	if err = json.Unmarshal(raw, &frames); err != nil {
		t.Fatal(err)
	}
	p := &peer{}
	session, recipient := uuid.New(), uuid.New()
	state := p.pcReplayRoute(pcRouteKey{sessionID: session, recipientID: recipient})
	seen := map[realtimewire.MessageType]bool{}
	for index, frame := range frames {
		envelope := realtimewire.Envelope{MessageType: frame.Type, Sequence: frame.Seq, ACK: frame.ACK}
		if !validEnvelopeACK(envelope) {
			t.Fatalf("frame %d has invalid ACK", index)
		}
		seen[frame.Type] = true
		if frame.Type == realtimewire.HostHistoryResponse {
			p.advancePCVisibleACKLocked(session, recipient, frame.Seq)
		}
		if frame.Type == realtimewire.HostHistoryResponse || frame.Type == realtimewire.TerminalAttachResult || frame.Type == realtimewire.ResizeResponse || frame.Type == realtimewire.SessionTerminateResponse {
			continue
		}
		bytes, err := hex.DecodeString(frame.SHA256)
		if err != nil || len(bytes) != 32 {
			t.Fatalf("frame %d bad digest", index)
		}
		var digest [32]byte
		copy(digest[:], bytes)
		commit, ok := state.validate(frame.Seq, frame.Type, digest)
		if !ok {
			t.Fatalf("macOS frame %d type=%d seq=%d rejected by actual Relay replay contract", index, frame.Type, frame.Seq)
		}
		state.commit(commit)
	}
	for _, required := range []realtimewire.MessageType{realtimewire.TerminalAttachResult, realtimewire.HostHistoryResponse, realtimewire.InputAccepted, realtimewire.InputRejected, realtimewire.OutputChunk, realtimewire.ResizeApplied} {
		if !seen[required] {
			t.Fatalf("missing required actual Mac frame type %d", required)
		}
	}
}
