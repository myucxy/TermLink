package runtime

import (
	"context"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/termlink/termlink/server/internal/config"
)

func TestHealthHandler(t *testing.T) {
	r := New(config.Config{ListenAddr: "127.0.0.1:0"}, nil)
	request := httptestRequest(t, r.HTTP.Handler)
	if request.status != http.StatusOK {
		t.Fatalf("status = %d, want 200", request.status)
	}
	if request.body != "{\"status\":\"ok\"}\n" {
		t.Fatalf("body = %q", request.body)
	}
	if request.contentType != "application/json" {
		t.Fatalf("Content-Type = %q", request.contentType)
	}
}

func TestServeGracefullyStopsOnContextCancellation(t *testing.T) {
	cfg := config.Config{ListenAddr: "127.0.0.1:0"}
	r := New(cfg, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusNoContent)
	}))
	ctx, cancel := context.WithCancel(context.Background())
	result := make(chan error, 1)
	go func() { result <- r.Serve(ctx) }()

	var address string
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if addr := r.ListenerAddr(); addr != nil {
			address = addr.String()
			break
		}
		time.Sleep(5 * time.Millisecond)
	}
	if address == "" {
		t.Fatal("runtime did not bind a listener")
	}
	response, err := http.Get("http://" + address + "/healthz")
	if err != nil {
		t.Fatalf("health request: %v", err)
	}
	response.Body.Close()
	cancel()
	select {
	case err := <-result:
		if err != nil {
			t.Fatalf("Serve() error = %v", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Serve() did not stop after cancellation")
	}
	if r.ListenerAddr() != nil {
		t.Fatal("listener remained published after shutdown")
	}
}

func TestNewHTTPServerUsesConfiguredAddressAndRouteHandler(t *testing.T) {
	called := false
	handler := http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		called = req.URL.Path == "/v1/test"
		w.WriteHeader(http.StatusAccepted)
	})
	server := NewHTTPServer(config.Config{ListenAddr: "127.0.0.1:9191"}, handler)
	if server.Addr != "127.0.0.1:9191" {
		t.Fatalf("Addr = %q", server.Addr)
	}
	request := httptestRequestFor(server.Handler, "/v1/test")
	if request.status != http.StatusAccepted || !called {
		t.Fatalf("route result = %#v called=%t", request, called)
	}
}

type requestResult struct {
	status      int
	body        string
	contentType string
}

func httptestRequest(t *testing.T, handler http.Handler) requestResult {
	t.Helper()
	return httptestRequestFor(handler, "/healthz")
}

func httptestRequestFor(handler http.Handler, path string) requestResult {
	request, err := http.NewRequest(http.MethodGet, "http://example.test"+path, nil)
	if err != nil {
		panic(err)
	}
	recorder := &recordingResponseWriter{header: make(http.Header)}
	handler.ServeHTTP(recorder, request)
	return requestResult{status: recorder.status, body: recorder.body.String(), contentType: recorder.header.Get("Content-Type")}
}

type recordingResponseWriter struct {
	header http.Header
	status int
	body   strings.Builder
}

func (w *recordingResponseWriter) Header() http.Header    { return w.header }
func (w *recordingResponseWriter) WriteHeader(status int) { w.status = status }
func (w *recordingResponseWriter) Write(data []byte) (int, error) {
	if w.status == 0 {
		w.status = http.StatusOK
	}
	return w.body.Write(data)
}

var _ io.Writer = (*recordingResponseWriter)(nil)
