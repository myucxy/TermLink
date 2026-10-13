package realtime

import (
	"bytes"
	"errors"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"github.com/termlink/termlink/server/internal/store"
)

func TestBrokerOutputCapabilityAndDirection(t *testing.T) {
	const capability = "terminal_broker_captures_v1"
	withHistory := append(append([]string{}, requiredCapabilities...), "host_history_v1", capability)
	withoutHistory := append(append([]string{}, requiredCapabilities...), capability)
	if !containsRelayCapability(negotiatedCapabilities(withHistory), capability) ||
		containsRelayCapability(negotiatedCapabilities(withoutHistory), capability) ||
		containsRelayCapability(negotiatedCapabilities(requiredCapabilities), capability) {
		t.Fatal("broker routing capability requires both broker and host history offers")
	}
	if !allowedMessage("pc", realtimewire.BrokerOutput) ||
		allowedMessage("android", realtimewire.BrokerOutput) ||
		validEnvelopeACK(realtimewire.Envelope{MessageType: realtimewire.BrokerOutput, ACK: 1}) {
		t.Fatal("BrokerOutput direction or cumulative ACK metadata is invalid")
	}

	for _, missing := range []struct {
		peer       string
		capability string
	}{
		{"pc", capability},
		{"android", capability},
		{"pc", "host_history_v1"},
		{"android", "host_history_v1"},
	} {
		t.Run("missing "+missing.peer+" "+missing.capability, func(t *testing.T) {
			hub := NewHubWithObserver(nil, nil, nil, nil)
			userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
			android, pc := activeRoutePeers(hub, userID, androidID, pcID)
			android.capabilities = map[string]bool{capability: true, "host_history_v1": true}
			pc.capabilities = map[string]bool{capability: true, "host_history_v1": true}
			if missing.peer == "pc" {
				delete(pc.capabilities, missing.capability)
			} else {
				delete(android.capabilities, missing.capability)
			}
			hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
			envelope := realtimewire.Envelope{MessageType: realtimewire.BrokerOutput, RecipientDeviceID: androidID[:], Sequence: 1}
			if err := pc.routePCFrame(envelope, sessionID, androidID, []byte("encrypted-broker-reference")); !errors.Is(err, store.ErrDenied) {
				t.Fatalf("unnegotiated BrokerOutput route error = %v", err)
			}
			if got := pcRouteHighWater(pc, sessionID, androidID); got != 0 {
				t.Fatalf("rejected BrokerOutput advanced terminal sequence to %d", got)
			}
			select {
			case <-android.send:
				t.Fatal("unnegotiated BrokerOutput reached Android")
			default:
			}
		})
	}
}

func TestUnnegotiatedBrokerOutputDoesNotConsumeSequence(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	android.capabilities = map[string]bool{"terminal_broker_captures_v1": true, "host_history_v1": true}
	pc.capabilities = capabilitySet(requiredCapabilities)
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	serverSocket, clientSocket := newRelayWebSocketPair(t)
	pc.socket = serverSocket
	go pc.readLoop()
	t.Cleanup(func() {
		_ = clientSocket.Close()
		select {
		case <-pc.done:
		case <-time.After(time.Second):
			t.Error("PC read loop did not stop")
		}
	})

	broker := relayTestEnvelope(t, realtimewire.BrokerOutput, realtimewire.PCToAndroid, pcID, androidID, sessionID, 1)
	if err := clientSocket.WriteMessage(websocket.BinaryMessage, broker); err != nil {
		t.Fatal(err)
	}
	output := relayTestEnvelope(t, realtimewire.OutputChunk, realtimewire.PCToAndroid, pcID, androidID, sessionID, 1)
	if err := clientSocket.WriteMessage(websocket.BinaryMessage, output); err != nil {
		t.Fatal(err)
	}
	select {
	case got := <-android.send:
		if !bytes.Equal(got, output) {
			t.Fatal("unnegotiated BrokerOutput reached Android")
		}
	case <-time.After(time.Second):
		t.Fatal("output after rejected BrokerOutput did not route")
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 1 {
		t.Fatalf("terminal high-water after rejection = %d", got)
	}
}

func TestBrokerOutputUsesOrderedSequenceWhileCaptureRPCBypassesTerminalACK(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID, pcID, androidID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	android, pc := activeRoutePeers(hub, userID, androidID, pcID)
	android.capabilities = map[string]bool{"terminal_broker_captures_v1": true, "host_history_v1": true}
	pc.capabilities = map[string]bool{"terminal_broker_captures_v1": true, "host_history_v1": true}
	hub.subscribe(android, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})

	broker := realtimewire.Envelope{MessageType: realtimewire.BrokerOutput, RecipientDeviceID: androidID[:], Sequence: 1}
	brokerFrame := []byte("encrypted-broker-reference")
	if err := pc.routePCFrame(broker, sessionID, androidID, brokerFrame); err != nil {
		t.Fatal(err)
	}
	if got := <-android.send; !bytes.Equal(got, brokerFrame) || pcRouteHighWater(pc, sessionID, androidID) != 1 {
		t.Fatal("BrokerOutput bypassed ordered terminal delivery")
	}
	android.releaseQueueBytes(int64(len(brokerFrame)))
	if err := pc.routePCFrame(broker, sessionID, androidID, []byte("replacement-reference")); !errors.Is(err, errPCSequenceRejected) {
		t.Fatalf("BrokerOutput accepted a same-sequence replacement: %v", err)
	}
	broker.Sequence = 3
	if err := pc.routePCFrame(broker, sessionID, androidID, []byte("out-of-order")); !errors.Is(err, errPCSequenceRejected) {
		t.Fatalf("BrokerOutput skipped a terminal sequence: %v", err)
	}

	// FlowControl's encrypted pause/window payload is owned by the Agent. The
	// Relay routes its ACK metadata but does not infer or bypass the PC window.
	flow := realtimewire.Envelope{MessageType: realtimewire.FlowControl, RecipientDeviceID: pcID[:]}
	if err := android.route(flow, sessionID, []byte("encrypted-zero-window")); err != nil {
		t.Fatalf("flow pause route: %v", err)
	}
	pc.releaseQueueBytes(int64(len(<-pc.send)))
	request := realtimewire.Envelope{MessageType: realtimewire.HostHistoryRequest, RecipientDeviceID: pcID[:]}
	if err := android.route(request, sessionID, []byte("encrypted-capture-request")); err != nil {
		t.Fatalf("capture request with terminal event unacked: %v", err)
	}
	pc.releaseQueueBytes(int64(len(<-pc.send)))

	for chunk := 0; chunk < 65; chunk++ {
		response := realtimewire.Envelope{MessageType: realtimewire.HostHistoryResponse, RecipientDeviceID: androidID[:], Sequence: uint64(900 + chunk)}
		frame := bytes.Repeat([]byte{byte(chunk)}, 512<<10)
		if err := pc.routePCFrame(response, sessionID, androidID, frame); err != nil {
			t.Fatalf("capture chunk %d route: %v", chunk, err)
		}
		if got := <-android.send; !bytes.Equal(got, frame) {
			t.Fatalf("capture chunk %d changed in Relay", chunk)
		}
		android.releaseQueueBytes(int64(len(frame)))
		control := realtimewire.Envelope{MessageType: realtimewire.HostHistoryControl, RecipientDeviceID: pcID[:]}
		if err := android.route(control, sessionID, []byte("encrypted-chunk-ack")); err != nil {
			t.Fatalf("capture chunk ACK %d route: %v", chunk, err)
		}
		pc.releaseQueueBytes(int64(len(<-pc.send)))
	}
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 1 {
		t.Fatalf("capture RPC advanced terminal high-water to %d", got)
	}
	broker.Sequence = 2
	if err := pc.routePCFrame(broker, sessionID, androidID, []byte("next-ordered-output")); err != nil {
		t.Fatalf("ordered event after capture RPC: %v", err)
	}
	android.releaseQueueBytes(int64(len(<-android.send)))
	if got := pcRouteHighWater(pc, sessionID, androidID); got != 2 {
		t.Fatalf("ordered terminal high-water after capture RPC = %d", got)
	}
}
