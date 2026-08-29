// Command termlink-lan-test is an explicitly non-production Golden Replay
// server. Keeping it outside the production command prevents test routes and
// fixtures from being linked into the release image.
package main

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/termlink/termlink/server/internal/lanreplay"
	"github.com/termlink/termlink/server/internal/phase1test"
)

func main() {
	if len(os.Args) > 1 && os.Args[1] == "healthcheck" {
		os.Exit(runHealthcheck())
	}
	if len(os.Args) > 1 && os.Args[1] != "serve" {
		logLANServerFailure(slog.Default(), "invalid_command", nil)
		os.Exit(1)
	}
	if err := run(); err != nil {
		logLANServerFailure(slog.Default(), "lan_test_server_failed", err)
		os.Exit(1)
	}
}

func logLANServerFailure(logger *slog.Logger, errorClass string, _ error) {
	logger.Error("LAN test server stopped", "error_class", errorClass)
}

func run() error {
	token := envOrDefault("TERMLINK_TEST_TOKEN", "termlink-lan-test")
	if len(token) < 12 {
		return errors.New("TERMLINK_TEST_TOKEN must contain at least 12 characters")
	}
	bundleRoot := envOrDefault("TERMLINK_TESTDATA_DIR", "/testdata/terminal-golden-streams")
	server := &http.Server{
		Addr: envOrDefault("TERMLINK_LISTEN_ADDR", ":8080"), Handler: newHandler(token, bundleRoot),
		ReadHeaderTimeout: 5 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 64 << 10,
	}
	shutdownContext, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	go func() {
		<-shutdownContext.Done()
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = server.Shutdown(ctx)
	}()
	slog.Info("TermLink LAN test server listening", "address", server.Addr)
	err := server.ListenAndServe()
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}

func newHandler(token, bundleRoot string) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", lanreplay.HealthHandler)
	mux.HandleFunc("GET /readyz", lanreplay.HealthHandler)
	mux.Handle("GET /v1/test/replay/", lanreplay.Handler{BundleRoot: bundleRoot, Token: token})
	mux.Handle("GET /v1/test/session/", phase1test.Handler{BundleRoot: bundleRoot, Token: token})
	return mux
}

func runHealthcheck() int {
	client := http.Client{Timeout: 2 * time.Second}
	response, err := client.Get("http://127.0.0.1:8080/readyz")
	if err != nil {
		return 1
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return 1
	}
	return 0
}

func envOrDefault(name, fallback string) string {
	if value := os.Getenv(name); value != "" {
		return value
	}
	return fallback
}
