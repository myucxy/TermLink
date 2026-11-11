package realtime

import (
	"encoding/hex"
	"encoding/json"
	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"os"
	"testing"
)

// These digests are from the production C# E2EE transport and the source-parser
// fragment/recovery regression, not a replacement implementation of Go routing.
func TestMacCodexRetainedGapReplayConformance(t *testing.T) {
	for _, name := range []string{"macos_codex_live.json", "macos_codex_gap.json"} {
		t.Run(name, func(t *testing.T) {
			data, e := os.ReadFile("testdata/" + name)
			if e != nil {
				t.Fatal(e)
			}
			var frames []struct {
				Type realtimewire.MessageType `json:"type"`
				Seq  uint64                   `json:"seq"`
				ACK  uint64                   `json:"ack"`
				Hash string                   `json:"sha256"`
			}
			if e = json.Unmarshal(data, &frames); e != nil {
				t.Fatal(e)
			}
			peer := &peer{}
			session, recipient := uuid.New(), uuid.New()
			route := peer.pcReplayRoute(pcRouteKey{sessionID: session, recipientID: recipient})
			duplicates := 0
			gap := false
			for i, frame := range frames {
				if !validEnvelopeACK(realtimewire.Envelope{MessageType: frame.Type, ACK: frame.ACK, Sequence: frame.Seq}) {
					t.Fatalf("frame %d invalid ACK", i)
				}
				switch frame.Type {
				case realtimewire.TerminalAttachResult, realtimewire.HostHistoryResponse, realtimewire.ResizeResponse, realtimewire.SessionTerminateResponse:
					continue
				}
				raw, e := hex.DecodeString(frame.Hash)
				if e != nil || len(raw) != 32 {
					t.Fatalf("frame %d bad digest", i)
				}
				var hash [32]byte
				copy(hash[:], raw)
				if frame.Seq <= route.highWater {
					duplicates++
				}
				if frame.Type == realtimewire.Gap {
					gap = true
				}
				commit, ok := route.validate(frame.Seq, frame.Type, hash)
				if !ok {
					t.Fatalf("C# frame %d type=%d seq=%d rejected by actual Go replay fence", i, frame.Type, frame.Seq)
				}
				route.commit(commit)
			}
			if duplicates == 0 {
				t.Fatal("missing retained ciphertext replay")
			}
			if name == "macos_codex_gap.json" && !gap {
				t.Fatal("missing Gap replay")
			}
		})
	}
}
