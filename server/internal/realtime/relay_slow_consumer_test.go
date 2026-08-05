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

// TestProductionRelaySlowConsumerIsolation exercises the same protobuf
// Envelope path used by authenticated Agent connections. A bounded target
// queue is intentionally left undrained; once its enqueue deadline expires,
// only that target receives CloseTryAgainLater (1013). The source can still
// route a later production frame to another Android subscriber.
func TestProductionRelaySlowConsumerIsolation(t *testing.T) {
	metrics := observability.New(nil)
	hub := NewHubWithObserver(nil, nil, nil, metrics)

	userID := uuid.New()
	sessionID := uuid.New()
	sourceID := uuid.New()
	slowID := uuid.New()
	healthyID := uuid.New()

	sourceServer, sourceClient := newRelayWebSocketPair(t)
	slowServer, slowClient := newRelayWebSocketPair(t)
	healthyServer, _ := newRelayWebSocketPair(t)

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

	healthy := testPeer(hub, healthyID)
	healthy.socket = healthyServer
	healthy.role = "android"
	healthy.principal.UserID = userID
	healthy.send = make(chan []byte, 1)

	if !hub.add(source) || !hub.add(slow) || !hub.add(healthy) {
		t.Fatal("failed to register test peers")
	}
	hub.subscribe(slow, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: sourceID})
	hub.subscribe(healthy, sessionID, store.SessionRouteAuthorization{Permission: "VIEW", PCDeviceID: sourceID})

	go source.readLoop()

	writeProductionEnvelope(t, sourceClient, productionOutputFrame(t, sourceID, slowID, sessionID, 1))
	waitPeerClosed(t, slow.done)

	slowClient.SetReadDeadline(time.Now().Add(time.Second))
	_, _, err := slowClient.ReadMessage()
	var closeErr *websocket.CloseError
	if !errors.As(err, &closeErr) || closeErr.Code != websocket.CloseTryAgainLater {
		t.Fatalf("slow consumer close = %v, want websocket close code %d", err, websocket.CloseTryAgainLater)
	}

	// The source remains live and can route subsequent output to a healthy
	// subscriber after the slow target was retired.
	writeProductionEnvelope(t, sourceClient, productionOutputFrame(t, sourceID, healthyID, sessionID, 2))
	select {
	case frame := <-healthy.send:
		envelope, err := realtimewire.Unmarshal(frame)
		if err != nil {
			t.Fatalf("healthy subscriber received invalid production envelope: %v", err)
		}
		if envelope.Sequence != 2 || !bytes.Equal(envelope.RecipientDeviceID, healthyID[:]) {
			t.Fatalf("healthy subscriber envelope = sequence %d recipient %x, want sequence 2 recipient %x", envelope.Sequence, envelope.RecipientDeviceID, healthyID[:])
		}
	case <-time.After(2 * time.Second):
		t.Fatal("healthy subscriber did not receive output after slow consumer close")
	}
	select {
	case <-source.done:
		t.Fatal("source connection was closed with slow consumer")
	default:
	}
	select {
	case <-healthy.done:
		t.Fatal("healthy subscriber was closed with slow consumer")
	default:
	}

	body := scrapeRealtimeMetrics(t, metrics)
	if !strings.Contains(body, "termlink_realtime_slow_consumer_disconnects_total 1") {
		t.Fatalf("slow consumer metric did not increment: %s", body)
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

func writeProductionEnvelope(t *testing.T, client *websocket.Conn, frame []byte) {
	t.Helper()
	if err := client.WriteMessage(websocket.BinaryMessage, frame); err != nil {
		t.Fatalf("write production envelope: %v", err)
	}
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
