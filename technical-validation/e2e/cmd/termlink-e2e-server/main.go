package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/myucxy/TermLink/technical-validation/e2e/internal/e2eserver"
)

func main() {
	if len(os.Args) > 1 && os.Args[1] != "serve" {
		if os.Args[1] == "healthcheck" {
			return
		}
		_, _ = fmt.Fprintf(os.Stderr, "unknown command %q (use serve)\n", os.Args[1])
		os.Exit(2)
	}
	if err := run(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		slog.Error("E2E server stopped", "error", err)
		os.Exit(1)
	}
}

func run() error {
	addr := os.Getenv("TERMLINK_E2E_ADDR")
	if addr == "" {
		addr = "127.0.0.1:8080"
	}
	token := os.Getenv("TERMLINK_E2E_TOKEN")
	if token == "" {
		token = e2eserver.DefaultToken
	}
	if strings.TrimSpace(token) == "" {
		return errors.New("TERMLINK_E2E_TOKEN must not be empty")
	}
	if host, _, err := net.SplitHostPort(addr); err == nil &&
		host != "" && host != "localhost" && host != "127.0.0.1" && host != "::1" && token == e2eserver.DefaultToken {
		return errors.New("refusing the default E2E token on a non-loopback address; set TERMLINK_E2E_TOKEN")
	}
	loginTTL, err := developmentLoginTTL()
	if err != nil {
		return err
	}

	application := e2eserver.New(e2eserver.Config{
		Token:                   token,
		DevelopmentUsername:     envOrDefault("TERMLINK_E2E_LOGIN_USERNAME", e2eserver.DefaultDevelopmentUsername),
		DevelopmentPassword:     envOrDefault("TERMLINK_E2E_LOGIN_PASSWORD", e2eserver.DefaultDevelopmentPassword),
		DevelopmentAccountLabel: envOrDefault("TERMLINK_E2E_ACCOUNT_LABEL", e2eserver.DefaultDevelopmentAccountLabel),
		DevelopmentTokenTTL:     loginTTL,
	})
	server := &http.Server{Addr: addr, Handler: application.Handler(), ReadHeaderTimeout: 10 * time.Second}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go func() {
		<-ctx.Done()
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = application.Close(shutdownCtx)
		_ = server.Shutdown(shutdownCtx)
	}()
	slog.Info("TermLink E2E-only server starting", "address", addr, "identity_mode", "development_shared_token", "login_enabled", true)
	return server.ListenAndServe()
}

func envOrDefault(name, fallback string) string {
	if value, ok := os.LookupEnv(name); ok {
		return value
	}
	return fallback
}

func developmentLoginTTL() (time.Duration, error) {
	value, ok := os.LookupEnv("TERMLINK_E2E_LOGIN_TOKEN_TTL")
	if !ok || strings.TrimSpace(value) == "" {
		return e2eserver.DefaultDevelopmentTokenTTL, nil
	}
	ttl, err := time.ParseDuration(strings.TrimSpace(value))
	if err != nil || ttl <= 0 {
		return 0, fmt.Errorf("TERMLINK_E2E_LOGIN_TOKEN_TTL must be a positive duration (for example 1h): %q", value)
	}
	return ttl, nil
}
