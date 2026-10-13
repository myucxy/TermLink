package realtime

import (
	"bytes"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"github.com/termlink/termlink/server/internal/store"
	"google.golang.org/protobuf/encoding/protowire"
)

func TestHostHistoryOpaqueFramesCrossWebSocketUnchanged(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	androidSocket, androidClient := newRelayWebSocketPair(t)
	pcSocket, pcClient := newRelayWebSocketPair(t)
	android.socket = androidSocket
	pc.socket = pcSocket
	android.capabilities = map[string]bool{"host_history_v1": true}
	pc.capabilities = map[string]bool{"host_history_v1": true}
	android.socket.SetReadLimit(realtimewire.MaxFrameBytes)
	pc.socket.SetReadLimit(realtimewire.MaxFrameBytes)
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	go android.writeLoop()
	go android.readLoop()
	go pc.writeLoop()
	go pc.readLoop()
	t.Cleanup(func() {
		android.close(1000, "test complete")
		pc.close(1000, "test complete")
	})

	for index, step := range []struct {
		name      string
		message   realtimewire.MessageType
		direction realtimewire.Direction
	}{
		{"begin", realtimewire.HostHistoryRequest, realtimewire.AndroidToPC},
		{"begin page chunk", realtimewire.HostHistoryResponse, realtimewire.PCToAndroid},
		{"next page", realtimewire.HostHistoryRequest, realtimewire.AndroidToPC},
		{"next page chunk", realtimewire.HostHistoryResponse, realtimewire.PCToAndroid},
		{"cancel", realtimewire.HostHistoryControl, realtimewire.AndroidToPC},
		{"release", realtimewire.HostHistoryRequest, realtimewire.AndroidToPC},
		{"release response", realtimewire.HostHistoryResponse, realtimewire.PCToAndroid},
	} {
		t.Run(step.name, func(t *testing.T) {
			senderID, recipientID := androidID, pcID
			senderClient, recipientClient := androidClient, pcClient
			if step.direction == realtimewire.PCToAndroid {
				senderID, recipientID = pcID, androidID
				senderClient, recipientClient = pcClient, androidClient
			}
			envelope := realtimewire.Envelope{
				ProtocolVersion: realtimewire.ProtocolVersion,
				MessageType:     step.message,
				SessionID:       sessionID[:], SenderDeviceID: senderID[:],
				Direction: step.direction, Sequence: uint64(index + 1),
				SentAtMS: time.Now().UnixMilli(), KeyID: []byte("epoch-01"),
				Nonce:             bytes.Repeat([]byte{byte(index + 1)}, 12),
				Ciphertext:        bytes.Repeat([]byte{byte(index + 1)}, 512<<10),
				RecipientDeviceID: recipientID[:],
			}
			frame, err := envelope.Marshal()
			if err != nil {
				t.Fatal(err)
			}
			if step.name == "begin page chunk" {
				frame = protowire.AppendTag(frame, 99, protowire.BytesType)
				frame = protowire.AppendBytes(frame, []byte("future-opaque-field"))
			}
			writeProductionEnvelope(t, senderClient, frame)
			if got := readProductionEnvelope(t, recipientClient); !bytes.Equal(got, frame) {
				t.Fatalf("Relay changed frozen-history ciphertext: got %d bytes, want %d", len(got), len(frame))
			}
		})
	}

	// Delta uses the same encrypted request/response lane. Drain each fragment
	// before sending the next; the Relay never buffers the 8 MiB object at once.
	deltaRequest := relayTestEnvelope(t, realtimewire.HostHistoryRequest,
		realtimewire.AndroidToPC, androidID, pcID, sessionID, 8)
	writeProductionEnvelope(t, androidClient, deltaRequest)
	if got := readProductionEnvelope(t, pcClient); !bytes.Equal(got, deltaRequest) {
		t.Fatal("Relay changed the Delta request")
	}
	for chunk := 0; chunk < 16; chunk++ {
		frame := relayTestEnvelope(t, realtimewire.HostHistoryResponse,
			realtimewire.PCToAndroid, pcID, androidID, sessionID, uint64(900+chunk))
		envelope, err := realtimewire.Unmarshal(frame)
		if err != nil {
			t.Fatal(err)
		}
		envelope.Ciphertext = bytes.Repeat([]byte{byte(chunk)}, 512<<10)
		frame, err = envelope.Marshal()
		if err != nil {
			t.Fatal(err)
		}
		writeProductionEnvelope(t, pcClient, frame)
		if got := readProductionEnvelope(t, androidClient); !bytes.Equal(got, frame) {
			t.Fatalf("Relay changed Delta chunk %d", chunk)
		}
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 0 {
		t.Fatalf("Delta chunks advanced ordered terminal high-water to %d", got)
	}
}
