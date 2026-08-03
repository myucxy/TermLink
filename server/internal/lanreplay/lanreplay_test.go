package lanreplay

import (
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"

	"github.com/gorilla/websocket"
)

const testCaseName = "smoke-powershell-v1"

func TestLoadRepositoryBundle(t *testing.T) {
	bundle := loadTestBundle(t)

	if bundle.Manifest.Statistics.EventCount != 14 {
		t.Fatalf("event count = %d, want 14", bundle.Manifest.Statistics.EventCount)
	}
	if bundle.Manifest.Statistics.OutputEventCount != 10 {
		t.Fatalf("output event count = %d, want 10", bundle.Manifest.Statistics.OutputEventCount)
	}
	if len(bundle.Output) != 702 {
		t.Fatalf("output bytes = %d, want 702", len(bundle.Output))
	}
}

func TestHandlerStreamsExactGoldenOutput(t *testing.T) {
	const token = "test-token-12345"
	bundleRoot := repositoryBundleRoot(t)
	server := httptest.NewServer(Handler{BundleRoot: bundleRoot, Token: token})
	t.Cleanup(server.Close)

	requestHeader := http.Header{}
	requestHeader.Set("Authorization", "Bearer "+token)
	websocketURL := "ws" + strings.TrimPrefix(server.URL, "http") + replayPathPrefix + testCaseName
	connection, response, err := websocket.DefaultDialer.Dial(websocketURL, requestHeader)
	if err != nil {
		if response != nil {
			t.Fatalf("dial status %d: %v", response.StatusCode, err)
		}
		t.Fatalf("dial replay: %v", err)
	}
	defer connection.Close()

	var (
		expectedSequence uint64 = 1
		output           []byte
		started          StartedMetadata
		ended            bool
	)
	for {
		messageType, frame, err := connection.ReadMessage()
		if err != nil {
			var closeError *websocket.CloseError
			if errors.As(err, &closeError) && closeError.Code == websocket.CloseNormalClosure {
				break
			}
			t.Fatalf("read frame: %v", err)
		}
		if messageType != websocket.BinaryMessage || len(frame) < frameHeaderSize {
			t.Fatalf("invalid WebSocket message")
		}
		if frame[0] != ProtocolVersion {
			t.Fatalf("protocol version = %d", frame[0])
		}
		sequence := binary.BigEndian.Uint64(frame[2:10])
		if sequence != expectedSequence {
			t.Fatalf("sequence = %d, want %d", sequence, expectedSequence)
		}
		expectedSequence++
		payload := frame[frameHeaderSize:]

		switch frame[1] {
		case FrameStarted:
			if err := json.Unmarshal(payload, &started); err != nil {
				t.Fatalf("decode started: %v", err)
			}
		case FrameResize:
			if len(payload) != 4 {
				t.Fatalf("resize payload length = %d", len(payload))
			}
		case FrameOutput:
			output = append(output, payload...)
		case FrameEnded:
			if len(payload) != 4 || int32(binary.BigEndian.Uint32(payload)) != 17 {
				t.Fatalf("invalid ended payload")
			}
			ended = true
		default:
			t.Fatalf("unknown frame type %d", frame[1])
		}
	}

	if !ended || started.CaseName != testCaseName || started.EventCount != 14 {
		t.Fatalf("invalid replay boundaries: started=%+v ended=%v", started, ended)
	}
	if len(output) != started.OutputBytes {
		t.Fatalf("received %d output bytes, want %d", len(output), started.OutputBytes)
	}
	sum := sha256.Sum256(output)
	if hex.EncodeToString(sum[:]) != started.OutputSHA256 {
		t.Fatalf("output SHA-256 mismatch")
	}

	bundle := loadTestBundle(t)
	if string(output) != string(bundle.Output) {
		t.Fatalf("streamed output differs from Golden Stream")
	}
}

func TestHandlerRejectsMissingToken(t *testing.T) {
	server := httptest.NewServer(Handler{
		BundleRoot: repositoryBundleRoot(t),
		Token:      "test-token-12345",
	})
	t.Cleanup(server.Close)

	response, err := http.Get(server.URL + replayPathPrefix + testCaseName)
	if err != nil {
		t.Fatalf("GET replay: %v", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusUnauthorized {
		t.Fatalf("status = %d, want %d", response.StatusCode, http.StatusUnauthorized)
	}
}

func loadTestBundle(t *testing.T) *Bundle {
	t.Helper()
	bundle, err := LoadBundle(repositoryBundleRoot(t), testCaseName)
	if err != nil {
		t.Fatalf("load repository bundle: %v", err)
	}
	return bundle
}

func repositoryBundleRoot(t *testing.T) string {
	t.Helper()
	root, err := filepath.Abs(filepath.Join("..", "..", "..", "testdata", "terminal-golden-streams"))
	if err != nil {
		t.Fatalf("resolve testdata root: %v", err)
	}
	return root
}
