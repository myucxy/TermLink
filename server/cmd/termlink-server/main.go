package main

import (
	"context"
	"errors"
	"fmt"
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
	if err := run(); err != nil {
		slog.Error("server stopped", "error", err)
		os.Exit(1)
	}
}

func run() error {
	token := envOrDefault("TERMLINK_TEST_TOKEN", "termlink-lan-test")
	if len(token) < 12 {
		return fmt.Errorf("TERMLINK_TEST_TOKEN must contain at least 12 characters")
	}

	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", lanreplay.HealthHandler)
	bundleRoot := envOrDefault("TERMLINK_TESTDATA_DIR", "/testdata/terminal-golden-streams")
	mux.Handle("GET /v1/test/replay/", lanreplay.Handler{
		BundleRoot: bundleRoot,
		Token:      token,
	})
	mux.Handle("GET /v1/test/session/", phase1test.Handler{
		BundleRoot: bundleRoot,
		Token:      token,
	})

	server := &http.Server{
		Addr:              envOrDefault("TERMLINK_LISTEN_ADDR", ":8080"),
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       30 * time.Second,
	}
	shutdownContext, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	go func() {
		<-shutdownContext.Done()
		context, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = server.Shutdown(context)
	}()

	slog.Info("TermLink LAN replay test server listening", "address", server.Addr)
	err := server.ListenAndServe()
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}

func runHealthcheck() int {
	client := http.Client{Timeout: 2 * time.Second}
	response, err := client.Get("http://127.0.0.1:8080/healthz")
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
