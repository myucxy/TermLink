package realtime

import (
	"encoding/hex"
	"encoding/json"
	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"os"
	"testing"
)

// Captured by the production Mac encrypted transport while a test recipient
// follows Android's zero-cursor / held-flow / resubscribe / V2 history contract.
// Keep the actual Relay replay validator as the arbiter of ciphertext identity.
func TestMacAndroidReplayEnvelopeConformance(t *testing.T) {
	data, err := os.ReadFile("testdata/macos_android_replay.json")
	if err != nil {
		t.Fatal(err)
	}
	var frames []struct {
		Type   realtimewire.MessageType `json:"type"`
		Seq    uint64                   `json:"seq"`
		ACK    uint64                   `json:"ack"`
		SHA256 string                   `json:"sha256"`
	}
	if err = json.Unmarshal(data, &frames); err != nil {
		t.Fatal(err)
	}
	p := &peer{}
	session, recipient := uuid.New(), uuid.New()
	state := p.pcReplayRoute(pcRouteKey{sessionID: session, recipientID: recipient})
	seen := map[realtimewire.MessageType]bool{}
	replayed := 0
	for i, frame := range frames {
		if !validEnvelopeACK(realtimewire.Envelope{MessageType: frame.Type, Sequence: frame.Seq, ACK: frame.ACK}) {
			t.Fatalf("frame %d ACK", i)
		}
		seen[frame.Type] = true
		switch frame.Type {
		case realtimewire.TerminalAttachResult, realtimewire.HostHistoryResponse, realtimewire.ResizeResponse, realtimewire.SessionTerminateResponse:
			continue
		}
		raw, err := hex.DecodeString(frame.SHA256)
		if err != nil || len(raw) != 32 {
			t.Fatalf("frame %d digest", i)
		}
		var hash [32]byte
		copy(hash[:], raw)
		if frame.Seq <= state.highWater {
			replayed++
		}
		commit, ok := state.validate(frame.Seq, frame.Type, hash)
		if !ok {
			t.Fatalf("production Mac frame rejected by Relay: index=%d type=%d seq=%d highWater=%d", i, frame.Type, frame.Seq, state.highWater)
		}
		state.commit(commit)
	}
	if replayed == 0 || !seen[realtimewire.SessionStarted] || !seen[realtimewire.OutputChunk] || !seen[realtimewire.HostHistoryResponse] {
		t.Fatal("missing startup/output/history or identical replay coverage")
	}
}
