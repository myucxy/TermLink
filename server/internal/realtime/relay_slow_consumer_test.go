package realtime

import (
	"bytes"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/observability"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"github.com/termlink/termlink/server/internal/store"
)

// TestProductionRelaySlowConsumerKeepsAgentForDurableReplay exercises the same
// protobuf Envelope path used by authenticated Agent connections. A saturated
// Android queue closes only that subscriber. The PC route still advances so a
// replacement Android subscriber can request replay from its durable ACK
// without forcing the Agent into a reconnect loop.
func TestProductionRelaySlowConsumerKeepsAgentForDurableReplay(t *testing.T) {
	metrics := observability.New(nil)
	hub := NewHubWithObserver(nil, nil, nil, metrics)

	userID := uuid.New()
	sessionID := uuid.New()
	sourceID := uuid.New()
	slowID := uuid.New()

	sourceServer, sourceClient := newRelayWebSocketPair(t)
	slowServer, slowClient := newRelayWebSocketPair(t)

	source := testPeer(hub, sourceID)
	source.socket = sourceServer
	source.role = "pc"
	source.principal.UserID = userID

	slow := testPeer(hub, slowID)
	slow.socket = slowServer
	slow.role = "android"
	slow.principal.UserID = userID
	slow.send = make(chan []byte, 1)
	slow.send <- []byte("already queued")

	if !hub.add(source) || !hub.add(slow) {
		t.Fatal("failed to register test peers")
	}
	hub.subscribe(slow, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: sourceID})

	go source.readLoop()

	writeProductionEnvelope(t, sourceClient, productionOutputFrame(t, sourceID, slowID, sessionID, 1))
	waitPeerClosed(t, slow.done)

	slowClient.SetReadDeadline(time.Now().Add(time.Second))
	_, _, err := slowClient.ReadMessage()
	var closeErr *websocket.CloseError
	if !errors.As(err, &closeErr) || closeErr.Code != websocket.CloseTryAgainLater {
		t.Fatalf("slow consumer close = %v, want websocket close code %d", err, websocket.CloseTryAgainLater)
	}

	if got := pcRouteHighWater(source, sessionID, slowID); got != 1 {
		t.Fatalf("offline slow-consumer route high-water = %d, want 1", got)
	}
	select {
	case <-source.done:
		t.Fatal("slow Android subscriber unexpectedly closed the PC Agent")
	case <-time.After(100 * time.Millisecond):
	}

	body := scrapeRealtimeMetrics(t, metrics)
	if !strings.Contains(body, "termlink_realtime_slow_consumer_disconnects_total 1") {
		t.Fatalf("slow consumer metric did not increment: %s", body)
	}
}

// TestProductionRelayRoutesOrderedReplayAfterReconnectACK keeps the PC on one
// physical websocket while an Android recipient is replaced. PC output enters
// through readLoop, including its acceptPCSequence gate, and leaves through the
// replacement Android peer's writeLoop. The subscription is installed directly
// because route's authorization collaborators are concrete PostgreSQL/Valkey
// clients; sendSubscriptionToPC is the production post-authorization path.
func TestProductionRelayRoutesOrderedReplayAfterReconnectACK(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID := uuid.New()
	sessionID := uuid.New()
	pcID := uuid.New()
	androidID := uuid.New()

	pcServer, pcClient := newRelayWebSocketPair(t)
	pc := testPeer(hub, pcID)
	pc.socket = pcServer
	pc.role = "pc"
	pc.principal.UserID = userID
	pc.send = make(chan []byte, peerQueueFrames)
	if !hub.add(pc) {
		t.Fatal("failed to register PC peer")
	}
	defer pc.close(websocket.CloseNormalClosure, "test complete")
	go pc.readLoop()
	go pc.writeLoop()

	oldAndroidServer, oldAndroidClient := newRelayWebSocketPair(t)
	oldAndroid := testPeer(hub, androidID)
	oldAndroid.socket = oldAndroidServer
	oldAndroid.role = "android"
	oldAndroid.principal.UserID = userID
	oldAndroid.send = make(chan []byte, peerQueueFrames)
	if !hub.add(oldAndroid) {
		t.Fatal("failed to register initial Android peer")
	}
	hub.subscribe(oldAndroid, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	go oldAndroid.writeLoop()

	frames := make(map[uint64][]byte, 57)
	for sequence := uint64(1); sequence <= 57; sequence++ {
		frame := productionOutputFrame(t, pcID, androidID, sessionID, sequence)
		frames[sequence] = frame
		writeProductionEnvelope(t, pcClient, frame)
		if received := readProductionEnvelope(t, oldAndroidClient); !bytes.Equal(received, frame) {
			t.Fatalf("initial Android frame %d changed in Relay", sequence)
		}
	}
	pc.pcReplayMu.Lock()
	highWater := pc.pcReplayRoutes[pcRouteKey{sessionID: sessionID, recipientID: androidID}].highWater
	pc.pcReplayMu.Unlock()
	if highWater != 57 {
		t.Fatalf("PC route high-water = %d, want 57", highWater)
	}

	newAndroidServer, newAndroidClient := newRelayWebSocketPair(t)
	newAndroid := testPeer(hub, androidID)
	newAndroid.socket = newAndroidServer
	newAndroid.role = "android"
	newAndroid.principal.UserID = userID
	newAndroid.send = make(chan []byte, peerQueueFrames)
	if !hub.add(newAndroid) {
		t.Fatal("failed to register replacement Android peer")
	}
	defer newAndroid.close(websocket.CloseNormalClosure, "test complete")
	go newAndroid.writeLoop()
	select {
	case <-oldAndroid.done:
	case <-time.After(time.Second):
		t.Fatal("initial Android peer was not retired on reconnect")
	}

	// This is the same state transition performed after route authorizes a
	// reconnecting Android SUBSCRIBE carrying its cumulative output ACK.
	hub.subscribe(newAndroid, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID})
	subscribeFrame := productionSubscribeFrame(t, androidID, pcID, sessionID, 1, 51)
	hub.sendSubscriptionToPC(newAndroid, pcID, sessionID, 51, subscribeFrame)
	if received := readProductionEnvelope(t, pcClient); !bytes.Equal(received, subscribeFrame) {
		t.Fatal("PC did not receive the byte-identical reconnect SUBSCRIBE with ACK=51")
	}
	pc.pcReplayMu.Lock()
	epoch := pc.pcReplayRoutes[pcRouteKey{sessionID: sessionID, recipientID: androidID}].epoch
	if epoch == nil {
		pc.pcReplayMu.Unlock()
		t.Fatal("ACK=51 did not open a replay epoch for the current PC route")
	}
	epochNext, epochTarget := epoch.next, epoch.target
	pc.pcReplayMu.Unlock()
	if epochNext != 52 || epochTarget != 57 {
		t.Fatalf("replay epoch = %d..%d, want 52..57", epochNext, epochTarget)
	}

	for sequence := uint64(52); sequence <= 57; sequence++ {
		writeProductionEnvelope(t, pcClient, frames[sequence])
		if received := readProductionEnvelope(t, newAndroidClient); !bytes.Equal(received, frames[sequence]) {
			t.Fatalf("replayed frame %d changed or did not reach replacement Android", sequence)
		}
		select {
		case <-pc.done:
			t.Fatalf("PC disconnected while replaying sequence %d", sequence)
		default:
		}
	}
	pc.pcReplayMu.Lock()
	epoch = pc.pcReplayRoutes[pcRouteKey{sessionID: sessionID, recipientID: androidID}].epoch
	pc.pcReplayMu.Unlock()
	if epoch != nil {
		t.Fatal("ordered replay did not complete the ACK=51 epoch at sequence 57")
	}

	// A fresh post-replay sequence proves readLoop remained live after the
	// replay epoch completed, rather than merely delaying a disconnect.
	frame58 := productionOutputFrame(t, pcID, androidID, sessionID, 58)
	writeProductionEnvelope(t, pcClient, frame58)
	if received := readProductionEnvelope(t, newAndroidClient); !bytes.Equal(received, frame58) {
		t.Fatal("PC could not route sequence 58 after ordered replay")
	}
	select {
	case <-pc.done:
		t.Fatal("PC disconnected after completing legal ordered replay")
	default:
	}
}

// TestProductionRelayReplaysSubscriptionWhenPCReconnects proves the live
// socket behavior behind the fast reconnect path: an Android subscription is
// retained while the owning Windows device replaces its websocket, and the
// replacement can route output without waiting for another Android refresh.
func TestProductionRelayReplaysSubscriptionWhenPCReconnects(t *testing.T) {
	hub := NewHubWithObserver(nil, nil, nil, nil)
	userID := uuid.New()
	sessionID := uuid.New()
	pcID := uuid.New()
	androidID := uuid.New()

	androidServer, androidClient := newRelayWebSocketPair(t)
	android := testPeer(hub, androidID)
	android.socket = androidServer
	android.role = "android"
	android.principal.UserID = userID
	android.send = make(chan []byte, peerQueueFrames)
	if !hub.add(android) {
		t.Fatal("failed to register Android peer")
	}
	defer android.close(websocket.CloseNormalClosure, "test complete")
	go android.writeLoop()
	subscribeFrame := productionSubscribeFrame(t, androidID, pcID, sessionID, 1, 17)
	if err := hub.activateSubscription(
		android,
		sessionID,
		store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: pcID},
		17,
		subscribeFrame,
	); err != nil {
		t.Fatalf("cache subscription while PC absent: %v", err)
	}

	oldPC := testPeer(hub, pcID)
	oldPC.role = "pc"
	oldPC.principal.UserID = userID
	if !hub.add(oldPC) {
		t.Fatal("failed to register initial PC peer")
	}

	replacementServer, replacementClient := newRelayWebSocketPair(t)
	replacement := testPeer(hub, pcID)
	replacement.socket = replacementServer
	replacement.role = "pc"
	replacement.principal.UserID = userID
	replacement.send = make(chan []byte, peerQueueFrames)
	if !hub.add(replacement) {
		t.Fatal("failed to register replacement PC peer")
	}
	defer replacement.close(websocket.CloseNormalClosure, "test complete")
	go replacement.writeLoop()
	hub.replaySubscriptionsToPC(replacement)
	if received := readProductionEnvelope(t, replacementClient); !bytes.Equal(received, subscribeFrame) {
		t.Fatal("replacement PC did not receive the byte-identical retained SUBSCRIBE")
	}

	go replacement.readLoop()
	outputFrame := productionOutputFrame(t, pcID, androidID, sessionID, 18)
	writeProductionEnvelope(t, replacementClient, outputFrame)
	if received := readProductionEnvelope(t, androidClient); !bytes.Equal(received, outputFrame) {
		t.Fatal("replacement PC output did not reach the existing Android subscription")
	}
	select {
	case <-replacement.done:
		t.Fatal("replacement PC disconnected while routing output")
	default:
	}
}

func productionOutputFrame(t *testing.T, sender, recipient, sessionID uuid.UUID, sequence uint64) []byte {
	t.Helper()
	envelope := realtimewire.Envelope{
		ProtocolVersion:   realtimewire.ProtocolVersion,
		MessageType:       realtimewire.OutputChunk,
		SessionID:         sessionID[:],
		SenderDeviceID:    sender[:],
		Direction:         realtimewire.PCToAndroid,
		Sequence:          sequence,
		SentAtMS:          time.Now().UnixMilli(),
		KeyID:             []byte("epoch-01"),
		Nonce:             bytes.Repeat([]byte{byte(sequence)}, 12),
		Ciphertext:        bytes.Repeat([]byte{0x44}, 32),
		RecipientDeviceID: recipient[:],
	}
	frame, err := envelope.Marshal()
	if err != nil {
		t.Fatalf("marshal production envelope: %v", err)
	}
	return frame
}

func productionSubscribeFrame(t *testing.T, sender, recipient, sessionID uuid.UUID, sequence, ack uint64) []byte {
	t.Helper()
	envelope := realtimewire.Envelope{
		ProtocolVersion:   realtimewire.ProtocolVersion,
		MessageType:       realtimewire.Subscribe,
		SessionID:         sessionID[:],
		SenderDeviceID:    sender[:],
		Direction:         realtimewire.AndroidToPC,
		Sequence:          sequence,
		ACK:               ack,
		SentAtMS:          time.Now().UnixMilli(),
		KeyID:             []byte("epoch-01"),
		Nonce:             bytes.Repeat([]byte{byte(sequence)}, 12),
		Ciphertext:        bytes.Repeat([]byte{0x55}, 32),
		RecipientDeviceID: recipient[:],
	}
	frame, err := envelope.Marshal()
	if err != nil {
		t.Fatalf("marshal production subscribe envelope: %v", err)
	}
	return frame
}

func writeProductionEnvelope(t *testing.T, client *websocket.Conn, frame []byte) {
	t.Helper()
	if err := client.WriteMessage(websocket.BinaryMessage, frame); err != nil {
		t.Fatalf("write production envelope: %v", err)
	}
}

func readProductionEnvelope(t *testing.T, client *websocket.Conn) []byte {
	t.Helper()
	if err := client.SetReadDeadline(time.Now().Add(2 * time.Second)); err != nil {
		t.Fatalf("set production envelope read deadline: %v", err)
	}
	messageType, frame, err := client.ReadMessage()
	if err != nil {
		t.Fatalf("read production envelope: %v", err)
	}
	if messageType != websocket.BinaryMessage {
		t.Fatalf("production envelope websocket type = %d, want binary", messageType)
	}
	return frame
}

func waitPeerClosed(t *testing.T, done <-chan struct{}) {
	t.Helper()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("slow consumer was not closed within the bounded enqueue deadline")
	}
}

func newRelayWebSocketPair(t *testing.T) (*websocket.Conn, *websocket.Conn) {
	t.Helper()
	accepted := make(chan *websocket.Conn, 1)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		upgrader := websocket.Upgrader{CheckOrigin: func(*http.Request) bool { return true }}
		connection, err := upgrader.Upgrade(w, r, nil)
		if err != nil {
			return
		}
		accepted <- connection
		<-r.Context().Done()
	}))
	client, response, err := websocket.DefaultDialer.Dial("ws"+strings.TrimPrefix(server.URL, "http"), nil)
	if response != nil {
		response.Body.Close()
	}
	if err != nil {
		server.Close()
		t.Fatalf("dial websocket test pair: %v", err)
	}
	serverSocket := <-accepted
	t.Cleanup(func() {
		_ = client.Close()
		_ = serverSocket.Close()
		server.Close()
	})
	return serverSocket, client
}

func scrapeRealtimeMetrics(t *testing.T, metrics *observability.Metrics) string {
	t.Helper()
	server := httptest.NewServer(metrics.Handler())
	defer server.Close()
	response, err := http.Get(server.URL + "/metrics")
	if err != nil {
		t.Fatalf("scrape realtime metrics: %v", err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatalf("read realtime metrics: %v", err)
	}
	return string(body)
}
