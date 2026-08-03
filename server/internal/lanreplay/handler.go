package lanreplay

import (
	"crypto/subtle"
	"fmt"
	"log/slog"
	"net/http"
	"strings"
	"time"

	"github.com/gorilla/websocket"
)

const replayPathPrefix = "/v1/test/replay/"

type Handler struct {
	BundleRoot string
	Token      string
	Logger     *slog.Logger
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
	caseName := strings.TrimPrefix(request.URL.Path, replayPathPrefix)
	if caseName == request.URL.Path || !caseNamePattern.MatchString(caseName) {
		http.NotFound(response, request)
		return
	}

	bundle, err := LoadBundle(handler.BundleRoot, caseName)
	if err != nil {
		handler.logger().Warn("LAN replay bundle rejected", "case", caseName, "error", err)
		http.Error(response, "replay fixture unavailable", http.StatusNotFound)
		return
	}

	upgrader := websocket.Upgrader{
		CheckOrigin: func(request *http.Request) bool {
			return request.Header.Get("Origin") == ""
		},
	}
	connection, err := upgrader.Upgrade(response, request, nil)
	if err != nil {
		return
	}
	defer connection.Close()

	recordedTiming := request.URL.Query().Get("timing") == "recorded"
	var previousElapsed uint64
	for _, event := range bundle.Events {
		if recordedTiming && previousElapsed > 0 && event.ElapsedUS > previousElapsed {
			delay := time.Duration(event.ElapsedUS-previousElapsed) * time.Microsecond
			if delay > 250*time.Millisecond {
				delay = 250 * time.Millisecond
			}
			time.Sleep(delay)
		}
		frame, err := bundle.Frame(event)
		if err != nil {
			handler.logger().Error("encode LAN replay frame", "case", caseName, "error", err)
			return
		}
		if err := connection.WriteMessage(websocket.BinaryMessage, frame); err != nil {
			return
		}
		previousElapsed = event.ElapsedUS
	}
	_ = connection.WriteControl(
		websocket.CloseMessage,
		websocket.FormatCloseMessage(websocket.CloseNormalClosure, "replay complete"),
		time.Now().Add(time.Second),
	)
}

func (handler Handler) authorized(request *http.Request) bool {
	const prefix = "Bearer "
	header := request.Header.Get("Authorization")
	if !strings.HasPrefix(header, prefix) || handler.Token == "" {
		return false
	}
	provided := []byte(strings.TrimPrefix(header, prefix))
	expected := []byte(handler.Token)
	if len(provided) != len(expected) {
		return false
	}
	return subtle.ConstantTimeCompare(provided, expected) == 1
}

func (handler Handler) logger() *slog.Logger {
	if handler.Logger != nil {
		return handler.Logger
	}
	return slog.Default()
}

func HealthHandler(response http.ResponseWriter, _ *http.Request) {
	response.Header().Set("Content-Type", "application/json")
	response.WriteHeader(http.StatusOK)
	_, _ = fmt.Fprint(response, `{"status":"ok","mode":"lan-replay-test"}`)
}
