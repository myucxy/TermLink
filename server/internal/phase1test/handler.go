package phase1test

import (
	"crypto/subtle"
	"encoding/binary"
	"fmt"
	"log/slog"
	"net/http"
	"regexp"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/lanreplay"
)

const SessionPathPrefix = "/v1/test/session/"

var caseNamePattern = regexp.MustCompile(`^[a-z0-9][a-z0-9._-]{0,127}$`)

var supportedScenarios = map[string]bool{
	"steady":          true,
	"disconnect-once": true,
	"history-gap":     true,
	"slow-consumer":   true,
	"exited":          true,
}

type Handler struct {
	BundleRoot    string
	Token         string
	Logger        *slog.Logger
	ACKTimeout    time.Duration
	LeaseDuration time.Duration
}

func (handler Handler) ServeHTTP(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodGet {
		http.Error(response, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !handler.authorized(request) {
		http.Error(response, "unauthorized", http.StatusUnauthorized)
		return
	}
	caseName := strings.TrimPrefix(request.URL.Path, SessionPathPrefix)
	if caseName == request.URL.Path || !caseNamePattern.MatchString(caseName) {
		http.NotFound(response, request)
		return
	}
	scenario := request.URL.Query().Get("scenario")
	if scenario == "" {
		scenario = "steady"
	}
	if !supportedScenarios[scenario] {
		http.Error(response, "unsupported test scenario", http.StatusBadRequest)
		return
	}

	bundle, err := lanreplay.LoadBundle(handler.BundleRoot, caseName)
	if err != nil {
		handler.logger().Warn("phase 1 fixture rejected", "case", caseName, "error", err)
		http.Error(response, "test fixture unavailable", http.StatusNotFound)
		return
	}
	frames, err := fixtureFrames(bundle, scenario)
	if err != nil {
		handler.logger().Error("phase 1 fixture conversion failed", "case", caseName, "error", err)
		http.Error(response, "test fixture unavailable", http.StatusInternalServerError)
		return
	}
	maximumSequence := frames[len(frames)-1].Sequence

	upgrader := websocket.Upgrader{CheckOrigin: func(request *http.Request) bool {
		return request.Header.Get("Origin") == ""
	}}
	connection, err := upgrader.Upgrade(response, request, nil)
	if err != nil {
		return
	}
	peer := newPeer(connection, handler.leaseDuration(), maximumSequence)
	defer peer.closeSocket()

	resume, err := peer.readInitialResume()
	if err != nil || resume.ACK > maximumSequence {
		peer.closeWithCode(websocket.CloseProtocolError, "valid Resume required")
		return
	}
	peer.highestACK = resume.ACK
	peer.lastSent = resume.ACK
	if err := peer.writeFrame(Frame{Type: FrameWelcome, Payload: []byte(caseName)}); err != nil {
		return
	}
	peer.startReader()

	window := uint64(4)
	if scenario == "slow-consumer" {
		window = 2
	}
	for _, frame := range frames {
		if frame.Sequence <= resume.ACK {
			continue
		}
		if !peer.waitForWindow(window, handler.ackTimeout()) {
			_ = peer.writeFrame(Frame{
				Type:    FrameFlowControl,
				Payload: uint32Payload(uint32(window)),
			})
			if !peer.waitForWindow(window, handler.ackTimeout()) {
				peer.closeWithCode(websocket.ClosePolicyViolation, "slow consumer")
				return
			}
		}
		peer.setLastSent(frame.Sequence)
		if err := peer.writeFrame(frame); err != nil {
			return
		}
		if scenario == "disconnect-once" && resume.ACK < 6 && frame.Sequence == 6 {
			peer.closeWithCode(websocket.CloseServiceRestart, "test disconnect")
			return
		}
	}

	if scenario == "exited" {
		peer.closeWithCode(websocket.CloseNormalClosure, "fixture exited")
		return
	}
	select {
	case <-peer.done:
	case <-request.Context().Done():
	}
}

type peer struct {
	connection    *websocket.Conn
	leaseDuration time.Duration
	maxSequence   uint64
	writeMutex    sync.Mutex
	stateMutex    sync.Mutex
	highestACK    uint64
	lastSent      uint64
	ackChanged    chan struct{}
	done          chan struct{}
	doneOnce      sync.Once
	acceptedInput map[string]struct{}
}

func newPeer(connection *websocket.Conn, leaseDuration time.Duration, maxSequence uint64) *peer {
	connection.SetReadLimit(MaxFrameSize)
	return &peer{
		connection:    connection,
		leaseDuration: leaseDuration,
		maxSequence:   maxSequence,
		ackChanged:    make(chan struct{}, 1),
		done:          make(chan struct{}),
		acceptedInput: make(map[string]struct{}),
	}
}

func (peer *peer) readInitialResume() (Frame, error) {
	_ = peer.connection.SetReadDeadline(time.Now().Add(5 * time.Second))
	messageType, encoded, err := peer.connection.ReadMessage()
	_ = peer.connection.SetReadDeadline(time.Time{})
	if err != nil {
		return Frame{}, err
	}
	if messageType != websocket.BinaryMessage {
		return Frame{}, fmt.Errorf("Resume must be binary")
	}
	frame, err := DecodeFrame(encoded)
	if err != nil {
		return Frame{}, err
	}
	if frame.Type != FrameResume || frame.Sequence != 0 || len(frame.Payload) != 0 {
		return Frame{}, fmt.Errorf("first frame is not Resume")
	}
	return frame, nil
}

func (peer *peer) startReader() {
	go func() {
		defer peer.finish()
		for {
			messageType, encoded, err := peer.connection.ReadMessage()
			if err != nil {
				return
			}
			if messageType != websocket.BinaryMessage {
				peer.closeWithCode(websocket.CloseUnsupportedData, "binary frames required")
				return
			}
			frame, err := DecodeFrame(encoded)
			if err != nil || !peer.handleClientFrame(frame) {
				peer.closeWithCode(websocket.CloseProtocolError, "invalid client frame")
				return
			}
		}
	}()
}

func (peer *peer) handleClientFrame(frame Frame) bool {
	if frame.Sequence != 0 {
		return false
	}
	switch frame.Type {
	case FrameCumulativeACK:
		if len(frame.Payload) != 0 {
			return false
		}
		peer.stateMutex.Lock()
		if frame.ACK < peer.highestACK || frame.ACK > peer.lastSent {
			peer.stateMutex.Unlock()
			return false
		}
		peer.highestACK = frame.ACK
		peer.stateMutex.Unlock()
		select {
		case peer.ackChanged <- struct{}{}:
		default:
		}
		return true
	case FrameInput:
		if frame.ACK != 0 || len(frame.Payload) <= 16 {
			return false
		}
		inputID := string(frame.Payload[:16])
		peer.stateMutex.Lock()
		peer.acceptedInput[inputID] = struct{}{}
		peer.stateMutex.Unlock()
		return peer.writeFrame(Frame{
			Type:    FrameInputAccepted,
			Payload: append([]byte(nil), frame.Payload[:16]...),
		}) == nil
	case FrameResizeRequest:
		return frame.ACK == 0 && len(frame.Payload) == 4 &&
			binary.BigEndian.Uint16(frame.Payload[0:2]) > 0 &&
			binary.BigEndian.Uint16(frame.Payload[2:4]) > 0
	case FrameLeaseRequest:
		if frame.ACK != 0 || len(frame.Payload) != 0 {
			return false
		}
		leaseID := "phase1-test-lease"
		payload := make([]byte, 8+len(leaseID))
		binary.BigEndian.PutUint64(payload[0:8], uint64(time.Now().Add(peer.leaseDuration).UnixMilli()))
		copy(payload[8:], leaseID)
		return peer.writeFrame(Frame{Type: FrameLeaseGranted, Payload: payload}) == nil
	case FrameLeaseRelease:
		if frame.ACK != 0 || len(frame.Payload) == 0 {
			return false
		}
		return peer.writeFrame(Frame{
			Type:    FrameLeaseRevoked,
			Payload: []byte("released by test client"),
		}) == nil
	default:
		return false
	}
}

func (peer *peer) waitForWindow(window uint64, timeout time.Duration) bool {
	deadline := time.NewTimer(timeout)
	defer deadline.Stop()
	for {
		peer.stateMutex.Lock()
		withinWindow := peer.lastSent-peer.highestACK < window
		peer.stateMutex.Unlock()
		if withinWindow {
			return true
		}
		select {
		case <-peer.ackChanged:
		case <-peer.done:
			return false
		case <-deadline.C:
			return false
		}
	}
}

func (peer *peer) setLastSent(sequence uint64) {
	peer.stateMutex.Lock()
	peer.lastSent = sequence
	peer.stateMutex.Unlock()
}

func (peer *peer) writeFrame(frame Frame) error {
	encoded, err := EncodeFrame(frame)
	if err != nil {
		return err
	}
	peer.writeMutex.Lock()
	defer peer.writeMutex.Unlock()
	return peer.connection.WriteMessage(websocket.BinaryMessage, encoded)
}

func (peer *peer) closeWithCode(code int, reason string) {
	peer.writeMutex.Lock()
	defer peer.writeMutex.Unlock()
	_ = peer.connection.WriteControl(
		websocket.CloseMessage,
		websocket.FormatCloseMessage(code, reason),
		time.Now().Add(time.Second),
	)
}

func (peer *peer) closeSocket() {
	peer.finish()
	_ = peer.connection.Close()
}

func (peer *peer) finish() {
	peer.doneOnce.Do(func() { close(peer.done) })
}

func (handler Handler) authorized(request *http.Request) bool {
	const prefix = "Bearer "
	header := request.Header.Get("Authorization")
	if !strings.HasPrefix(header, prefix) || handler.Token == "" {
		return false
	}
	provided := []byte(strings.TrimPrefix(header, prefix))
	expected := []byte(handler.Token)
	return len(provided) == len(expected) && subtle.ConstantTimeCompare(provided, expected) == 1
}

func (handler Handler) ackTimeout() time.Duration {
	if handler.ACKTimeout > 0 {
		return handler.ACKTimeout
	}
	return 2 * time.Second
}

func (handler Handler) leaseDuration() time.Duration {
	if handler.LeaseDuration > 0 {
		return handler.LeaseDuration
	}
	return 30 * time.Second
}

func (handler Handler) logger() *slog.Logger {
	if handler.Logger != nil {
		return handler.Logger
	}
	return slog.Default()
}

func uint32Payload(value uint32) []byte {
	payload := make([]byte, 4)
	binary.BigEndian.PutUint32(payload, value)
	return payload
}
