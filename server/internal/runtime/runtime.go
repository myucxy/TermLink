// Package runtime owns the HTTP server lifecycle shared by the production
// command and focused integration tests.
package runtime

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"sync"
	"time"

	"github.com/termlink/termlink/server/internal/config"
)

const (
	defaultReadHeaderTimeout = 5 * time.Second
	defaultIdleTimeout       = 60 * time.Second
	defaultShutdownTimeout   = 10 * time.Second
)

// Runtime wraps an HTTP server and makes shutdown behavior explicit. HTTP is
// exported so callers can register additional observability hooks before
// calling Serve.
type Runtime struct {
	Config config.Config
	HTTP   *http.Server

	mu       sync.RWMutex
	listener net.Listener
}

// New creates a production-configured HTTP runtime. A nil handler serves 404
// for application routes while retaining the health endpoint.
func New(cfg config.Config, handler http.Handler) *Runtime {
	if handler == nil {
		handler = http.NotFoundHandler()
	}
	server := NewHTTPServer(cfg, handler)
	return &Runtime{Config: cfg, HTTP: server}
}

// NewChecked validates an explicitly assembled configuration before creating
// the runtime. Configs returned by config.Load or config.New are already
// validated, so New remains the lightweight constructor used by startup code.
func NewChecked(cfg config.Config, handler http.Handler) (*Runtime, error) {
	if err := cfg.Validate(); err != nil {
		return nil, err
	}
	return New(cfg, handler), nil
}

// NewHTTPServer creates a configured net/http server without starting it.
func NewHTTPServer(cfg config.Config, handler http.Handler) *http.Server {
	if handler == nil {
		handler = http.NotFoundHandler()
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", HealthHandler)
	mux.Handle("/", handler)
	addr := cfg.ListenAddr
	if addr == "" {
		addr = ":8080"
	}
	return &http.Server{
		Addr:              addr,
		Handler:           mux,
		ReadHeaderTimeout: defaultReadHeaderTimeout,
		IdleTimeout:       defaultIdleTimeout,
		MaxHeaderBytes:    1 << 20,
	}
}

// HealthHandler is intentionally dependency-free so edge proxies and Docker
// healthchecks can use it before application dependencies are connected.
func HealthHandler(w http.ResponseWriter, _ *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(struct {
		Status string `json:"status"`
	}{Status: "ok"})
}

// Serve starts listening and blocks until the context is canceled or the
// server exits. Context cancellation performs a bounded graceful shutdown.
func (r *Runtime) Serve(ctx context.Context) error {
	if r == nil || r.HTTP == nil {
		return errors.New("runtime is not initialized")
	}
	if ctx == nil {
		ctx = context.Background()
	}
	listener, err := net.Listen("tcp", r.HTTP.Addr)
	if err != nil {
		return fmt.Errorf("listen: %w", err)
	}
	r.mu.Lock()
	r.listener = listener
	r.mu.Unlock()
	defer func() {
		r.mu.Lock()
		r.listener = nil
		r.mu.Unlock()
	}()

	serveErr := make(chan error, 1)
	go func() { serveErr <- r.HTTP.Serve(listener) }()
	select {
	case err := <-serveErr:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	case <-ctx.Done():
		shutdownCtx, cancel := context.WithTimeout(context.Background(), defaultShutdownTimeout)
		shutdownErr := r.HTTP.Shutdown(shutdownCtx)
		cancel()
		serveErrValue := <-serveErr
		if shutdownErr != nil {
			return fmt.Errorf("shutdown: %w", shutdownErr)
		}
		if serveErrValue != nil && !errors.Is(serveErrValue, http.ErrServerClosed) {
			return serveErrValue
		}
		return nil
	}
}

// Run is a semantic alias for Serve for callers that model the runtime as a
// long-lived process.
func (r *Runtime) Run(ctx context.Context) error { return r.Serve(ctx) }

// Shutdown requests a bounded graceful shutdown using the caller's context.
func (r *Runtime) Shutdown(ctx context.Context) error {
	if r == nil || r.HTTP == nil {
		return nil
	}
	if ctx == nil {
		ctx = context.Background()
	}
	return r.HTTP.Shutdown(ctx)
}

// ListenerAddr reports the bound address after Serve has started. It returns
// nil before startup and after shutdown.
func (r *Runtime) ListenerAddr() net.Addr {
	if r == nil {
		return nil
	}
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.listenerAddrLocked()
}

// Addr is a short alias for ListenerAddr.
func (r *Runtime) Addr() net.Addr { return r.ListenerAddr() }

func (r *Runtime) listenerAddrLocked() net.Addr {
	if r.listener == nil {
		return nil
	}
	return r.listener.Addr()
}
