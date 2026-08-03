package phase1test

import (
	"bytes"
	"encoding/binary"
	"errors"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

const (
	testCase  = "smoke-powershell-v1"
	testToken = "phase1-test-token"
)

func TestProtocolRoundTripAndRejectsWrongMagic(t *testing.T) {
	want := Frame{Type: FrameOutput, Sequence: 42, ACK: 17, Payload: []byte{0, 1, 2, 255}}
	encoded, err := EncodeFrame(want)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	got, err := DecodeFrame(encoded)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}
	if got.Type != want.Type || got.Sequence != want.Sequence || got.ACK != want.ACK ||
		!bytes.Equal(got.Payload, want.Payload) {
		t.Fatalf("round trip = %+v, want %+v", got, want)
	}

	encoded[0] = 'X'
	if _, err := DecodeFrame(encoded); err == nil {
		t.Fatal("wrong magic was accepted")
	}
}

func TestDisconnectOnceResumesAfterHighestCumulativeACK(t *testing.T) {
	server := newTestServer(t, 200*time.Millisecond)
	url := sessionURL(server, "disconnect-once")

	first := dial(t, url)
	writeClientFrame(t, first, Frame{Type: FrameResume})
	readType(t, first, FrameWelcome)
	var lastACK uint64
	for {
		frame, err := readFrame(first)
		if err != nil {
			var closeError *websocket.CloseError
			if !errors.As(err, &closeError) || closeError.Code != websocket.CloseServiceRestart {
				t.Fatalf("initial connection close = %v", err)
			}
			break
		}
		lastACK = frame.Sequence
		writeClientFrame(t, first, Frame{Type: FrameCumulativeACK, ACK: lastACK})
	}
	if lastACK != 6 {
		t.Fatalf("initial ACK = %d, want 6", lastACK)
	}
	_ = first.Close()

	second := dial(t, url)
	defer second.Close()
	writeClientFrame(t, second, Frame{Type: FrameResume, ACK: lastACK})
	readType(t, second, FrameWelcome)
	firstResumed, err := readFrame(second)
	if err != nil {
		t.Fatalf("read resumed event: %v", err)
	}
	if firstResumed.Sequence != 7 {
		t.Fatalf("first resumed sequence = %d, want 7", firstResumed.Sequence)
	}
}

func TestInputAcceptedLeaseAndCumulativeACK(t *testing.T) {
	server := newTestServer(t, 200*time.Millisecond)
	connection := dial(t, sessionURL(server, "steady"))
	defer connection.Close()
	writeClientFrame(t, connection, Frame{Type: FrameResume})
	readType(t, connection, FrameWelcome)

	for sequence := uint64(1); sequence <= 13; sequence++ {
		frame, err := readFrame(connection)
		if err != nil {
			t.Fatalf("read fixture sequence %d: %v", sequence, err)
		}
		if frame.Sequence != sequence {
			t.Fatalf("fixture sequence = %d, want %d", frame.Sequence, sequence)
		}
		writeClientFrame(t, connection, Frame{Type: FrameCumulativeACK, ACK: sequence})
	}

	writeClientFrame(t, connection, Frame{Type: FrameLeaseRequest})
	lease := readType(t, connection, FrameLeaseGranted)
	if len(lease.Payload) <= 8 || int64(binary.BigEndian.Uint64(lease.Payload[:8])) <= time.Now().UnixMilli() {
		t.Fatalf("invalid lease payload")
	}

	inputID := []byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15}
	writeClientFrame(t, connection, Frame{
		Type:    FrameInput,
		Payload: append(append([]byte(nil), inputID...), []byte("echo phase1")...),
	})
	accepted := readType(t, connection, FrameInputAccepted)
	if !bytes.Equal(accepted.Payload, inputID) {
		t.Fatalf("accepted input ID differs")
	}
}

func TestHistoryGapIsExplicitAndDoesNotSendMissingEvents(t *testing.T) {
	server := newTestServer(t, 200*time.Millisecond)
	connection := dial(t, sessionURL(server, "history-gap"))
	defer connection.Close()
	writeClientFrame(t, connection, Frame{Type: FrameResume})
	readType(t, connection, FrameWelcome)
	for sequence := uint64(1); sequence <= 4; sequence++ {
		frame, err := readFrame(connection)
		if err != nil || frame.Sequence != sequence {
			t.Fatalf("sequence %d: frame=%+v err=%v", sequence, frame, err)
		}
		writeClientFrame(t, connection, Frame{Type: FrameCumulativeACK, ACK: sequence})
	}
	gap := readType(t, connection, FrameGap)
	if gap.Sequence != 7 || len(gap.Payload) != 16 ||
		binary.BigEndian.Uint64(gap.Payload[:8]) != 5 ||
		binary.BigEndian.Uint64(gap.Payload[8:]) != 6 {
		t.Fatalf("invalid gap frame: %+v", gap)
	}
}

func TestSlowConsumerReceivesFlowControlThenDisconnects(t *testing.T) {
	server := newTestServer(t, 40*time.Millisecond)
	connection := dial(t, sessionURL(server, "slow-consumer"))
	defer connection.Close()
	writeClientFrame(t, connection, Frame{Type: FrameResume})
	readType(t, connection, FrameWelcome)
	readType(t, connection, FrameLifecycle)
	readType(t, connection, FrameResize)
	flow := readType(t, connection, FrameFlowControl)
	if len(flow.Payload) != 4 || binary.BigEndian.Uint32(flow.Payload) != 2 {
		t.Fatalf("invalid flow control payload")
	}
	_, err := readFrame(connection)
	var closeError *websocket.CloseError
	if !errors.As(err, &closeError) || closeError.Code != websocket.ClosePolicyViolation {
		t.Fatalf("slow consumer close = %v", err)
	}
}

func TestHandlerRejectsMissingToken(t *testing.T) {
	server := newTestServer(t, 100*time.Millisecond)
	response, err := http.Get(server.URL + SessionPathPrefix + testCase)
	if err != nil {
		t.Fatalf("GET without token: %v", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusUnauthorized {
		t.Fatalf("status = %d, want %d", response.StatusCode, http.StatusUnauthorized)
	}
}

func newTestServer(t *testing.T, ackTimeout time.Duration) *httptest.Server {
	t.Helper()
	server := httptest.NewServer(Handler{
		BundleRoot: repositoryBundleRoot(t),
		Token:      testToken,
		ACKTimeout: ackTimeout,
	})
	t.Cleanup(server.Close)
	return server
}

func sessionURL(server *httptest.Server, scenario string) string {
	return "ws" + strings.TrimPrefix(server.URL, "http") + SessionPathPrefix + testCase +
		"?scenario=" + scenario
}

func dial(t *testing.T, url string) *websocket.Conn {
	t.Helper()
	header := http.Header{}
	header.Set("Authorization", "Bearer "+testToken)
	connection, response, err := websocket.DefaultDialer.Dial(url, header)
	if err != nil {
		if response != nil {
			t.Fatalf("dial status %d: %v", response.StatusCode, err)
		}
		t.Fatalf("dial: %v", err)
	}
	return connection
}

func writeClientFrame(t *testing.T, connection *websocket.Conn, frame Frame) {
	t.Helper()
	encoded, err := EncodeFrame(frame)
	if err != nil {
		t.Fatalf("encode client frame: %v", err)
	}
	if err := connection.WriteMessage(websocket.BinaryMessage, encoded); err != nil {
		t.Fatalf("write client frame: %v", err)
	}
}

func readType(t *testing.T, connection *websocket.Conn, frameType byte) Frame {
	t.Helper()
	frame, err := readFrame(connection)
	if err != nil {
		t.Fatalf("read type %d: %v", frameType, err)
	}
	if frame.Type != frameType {
		t.Fatalf("frame type = %d, want %d", frame.Type, frameType)
	}
	return frame
}

func readFrame(connection *websocket.Conn) (Frame, error) {
	messageType, encoded, err := connection.ReadMessage()
	if err != nil {
		return Frame{}, err
	}
	if messageType != websocket.BinaryMessage {
		return Frame{}, errors.New("non-binary WebSocket message")
	}
	return DecodeFrame(encoded)
}

func repositoryBundleRoot(t *testing.T) string {
	t.Helper()
	root, err := filepath.Abs(filepath.Join("..", "..", "..", "testdata", "terminal-golden-streams"))
	if err != nil {
		t.Fatalf("resolve testdata root: %v", err)
	}
	return root
}
