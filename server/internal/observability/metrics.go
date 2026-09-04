// Package observability exposes low-cardinality operational metrics without
// terminal content, ciphertext, credentials, public keys, or raw identifiers.
package observability

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/collectors"
	"github.com/prometheus/client_golang/prometheus/promhttp"
)

var httpDurationBuckets = []float64{0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5}
var routeDurationBuckets = []float64{0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1}

// Metrics owns an isolated registry. It deliberately does not use the global
// Prometheus registry, which keeps tests deterministic and prevents accidental
// third-party metrics from widening the published data surface.
type Metrics struct {
	registry *prometheus.Registry
	pool     *pgxpool.Pool

	httpRequests     *prometheus.CounterVec
	httpDuration     *prometheus.HistogramVec
	connections      *prometheus.GaugeVec
	subscriptions    prometheus.Gauge
	envelopes        *prometheus.CounterVec
	envelopeBytes    *prometheus.CounterVec
	routeDuration    *prometheus.HistogramVec
	slowConsumers    prometheus.Counter
	leaseRequests    *prometheus.CounterVec
	registrationHTTP *prometheus.CounterVec
	postgresPool     *prometheus.GaugeVec
}

// New creates all collectors and optionally binds PostgreSQL pool statistics.
func New(pool *pgxpool.Pool) *Metrics {
	m := &Metrics{
		registry: prometheus.NewRegistry(),
		pool:     pool,
		httpRequests: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "termlink", Subsystem: "http", Name: "requests_total",
			Help: "HTTP requests by bounded route, method, and status class.",
		}, []string{"method", "route", "status_class"}),
		httpDuration: prometheus.NewHistogramVec(prometheus.HistogramOpts{
			Namespace: "termlink", Subsystem: "http", Name: "request_duration_seconds",
			Help: "HTTP request duration by bounded route and method.", Buckets: httpDurationBuckets,
		}, []string{"method", "route"}),
		connections: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "realtime", Name: "connections",
			Help: "Current authenticated realtime connections by role.",
		}, []string{"role"}),
		subscriptions: prometheus.NewGauge(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "realtime", Name: "subscriptions",
			Help: "Current authorized realtime session subscriptions.",
		}),
		envelopes: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "termlink", Subsystem: "realtime", Name: "envelopes_total",
			Help: "Production envelope routing decisions without payload or identifiers.",
		}, []string{"direction", "message_type", "result"}),
		envelopeBytes: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "termlink", Subsystem: "realtime", Name: "envelope_bytes_total",
			Help: "Opaque production envelope bytes accepted for routing.",
		}, []string{"direction"}),
		routeDuration: prometheus.NewHistogramVec(prometheus.HistogramOpts{
			Namespace: "termlink", Subsystem: "realtime", Name: "route_duration_seconds",
			Help: "Production envelope authorization and routing duration.", Buckets: routeDurationBuckets,
		}, []string{"direction", "result"}),
		slowConsumers: prometheus.NewCounter(prometheus.CounterOpts{
			Namespace: "termlink", Subsystem: "realtime", Name: "slow_consumer_disconnects_total",
			Help: "Realtime connections closed because their bounded send queue was full.",
		}),
		leaseRequests: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "termlink", Subsystem: "lease", Name: "requests_total",
			Help: "Lease HTTP operations by method and coarse result.",
		}, []string{"method", "result"}),
		registrationHTTP: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "termlink", Subsystem: "registration", Name: "http_operations_total",
			Help: "Registration and invitation HTTP operations by bounded operation and coarse result.",
		}, []string{"operation", "result"}),
		postgresPool: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "postgres", Name: "pool_connections",
			Help: "PostgreSQL pool connections by state.",
		}, []string{"state"}),
	}
	m.registry.MustRegister(
		collectors.NewGoCollector(),
		collectors.NewProcessCollector(collectors.ProcessCollectorOpts{}),
		m.httpRequests,
		m.httpDuration,
		m.connections,
		m.subscriptions,
		m.envelopes,
		m.envelopeBytes,
		m.routeDuration,
		m.slowConsumers,
		m.leaseRequests,
		m.registrationHTTP,
		m.postgresPool,
	)
	return m
}

// Handler serves only the Prometheus exposition. The caller must place it on
// the dedicated internal listener rather than the public application mux.
func (m *Metrics) Handler() http.Handler {
	metrics := promhttp.HandlerFor(m.registry, promhttp.HandlerOpts{EnableOpenMetrics: true})
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet || r.URL.Path != "/metrics" {
			http.NotFound(w, r)
			return
		}
		m.refreshPostgres()
		metrics.ServeHTTP(w, r)
	})
}

// HTTPMiddleware records route templates, never raw paths. This prevents UUIDs
// and attacker-controlled URL fragments from becoming metric labels.
func (m *Metrics) HTTPMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		started := time.Now()
		capture := &statusWriter{ResponseWriter: w}
		defer func() {
			method := boundedMethod(r.Method)
			route := boundedRoute(r.Pattern)
			status := capture.statusCode()
			statusClass := strconv.Itoa(status/100) + "xx"
			m.httpRequests.WithLabelValues(method, route, statusClass).Inc()
			m.httpDuration.WithLabelValues(method, route).Observe(time.Since(started).Seconds())
			if strings.Contains(route, "/lease") {
				m.leaseRequests.WithLabelValues(method, coarseResult(status)).Inc()
			}
			if operation := boundedRegistrationOperation(route); operation != "" {
				m.registrationHTTP.WithLabelValues(operation, coarseResult(status)).Inc()
			}
		}()
		next.ServeHTTP(capture, r)
	})
}

// ConnectionOpened and ConnectionClosed implement the realtime observer.
func (m *Metrics) ConnectionOpened(role string) {
	m.connections.WithLabelValues(boundedRole(role)).Inc()
}

func (m *Metrics) ConnectionClosed(role string) {
	m.connections.WithLabelValues(boundedRole(role)).Dec()
}

func (m *Metrics) SubscriptionsChanged(delta int) {
	m.subscriptions.Add(float64(delta))
}

func (m *Metrics) EnvelopeRouted(direction, messageType, result string, size int, duration time.Duration) {
	direction = boundedDirection(direction)
	messageType = boundedMessageType(messageType)
	result = boundedRouteResult(result)
	m.envelopes.WithLabelValues(direction, messageType, result).Inc()
	if size > 0 {
		m.envelopeBytes.WithLabelValues(direction).Add(float64(size))
	}
	m.routeDuration.WithLabelValues(direction, result).Observe(duration.Seconds())
}

func (m *Metrics) SlowConsumerDisconnected() { m.slowConsumers.Inc() }

func (m *Metrics) refreshPostgres() {
	if m.pool == nil {
		return
	}
	stats := m.pool.Stat()
	m.postgresPool.WithLabelValues("in_use").Set(float64(stats.AcquiredConns()))
	m.postgresPool.WithLabelValues("idle").Set(float64(stats.IdleConns()))
	m.postgresPool.WithLabelValues("total").Set(float64(stats.TotalConns()))
	m.postgresPool.WithLabelValues("max").Set(float64(stats.MaxConns()))
}

// Serve runs the internal metrics listener until cancellation. It has no
// public routes, permissive CORS behavior, or application credentials.
func (m *Metrics) Serve(ctx context.Context, addr string) error {
	server := &http.Server{
		Addr:              addr,
		Handler:           m.Handler(),
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       30 * time.Second,
		MaxHeaderBytes:    64 << 10,
	}
	result := make(chan error, 1)
	go func() { result <- server.ListenAndServe() }()
	select {
	case err := <-result:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	case <-ctx.Done():
		shutdown, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		if err := server.Shutdown(shutdown); err != nil {
			return fmt.Errorf("metrics shutdown: %w", err)
		}
		err := <-result
		if err != nil && !errors.Is(err, http.ErrServerClosed) {
			return err
		}
		return nil
	}
}

func boundedMethod(value string) string {
	switch value {
	case http.MethodGet, http.MethodPost, http.MethodPut, http.MethodDelete, http.MethodPatch, http.MethodHead, http.MethodOptions:
		return value
	default:
		return "OTHER"
	}
}

func boundedRoute(value string) string {
	if value == "" {
		return "unmatched"
	}
	if len(value) > 160 || strings.ContainsAny(value, "\r\n") {
		return "invalid_pattern"
	}
	return value
}

func boundedRole(value string) string {
	if value == "pc" || value == "android" {
		return value
	}
	return "unknown"
}

func boundedDirection(value string) string {
	if value == "pc_to_android" || value == "android_to_pc" {
		return value
	}
	return "unknown"
}

func boundedMessageType(value string) string {
	if len(value) > 3 {
		return "unknown"
	}
	parsed, err := strconv.ParseUint(value, 10, 32)
	if err != nil || parsed > 82 {
		return "unknown"
	}
	return value
}

func boundedRouteResult(value string) string {
	if value == "accepted" || value == "denied" {
		return value
	}
	return "error"
}

func coarseResult(status int) string {
	switch {
	case status >= 200 && status < 400:
		return "success"
	case status >= 400 && status < 500:
		return "denied"
	default:
		return "error"
	}
}

var registrationRouteOperations = map[string]string{
	"POST /v1/auth/register":                               "registration",
	"GET /v1/registration-policy":                          "registration_policy_read",
	"POST /v1/auth/invitations/validate":                   "invitation_validation",
	"GET /v1/admin/registration-policy":                    "registration_policy_admin",
	"PATCH /v1/admin/registration-policy":                  "registration_policy_admin",
	"GET /v1/admin/invitation-batches":                     "invitation_admin",
	"POST /v1/admin/invitation-batches":                    "invitation_admin",
	"POST /v1/admin/invitation-batches/{batchId}/generate": "invitation_generate",
	"POST /v1/admin/invitation-batches/{batchId}/disable":  "invitation_admin",
	"GET /v1/admin/invitation-codes":                       "invitation_admin",
	"POST /v1/admin/invitation-codes/{codeId}/disable":     "invitation_admin",
	"GET /v1/admin/invitation-redemptions":                 "invitation_history_admin",
}

func boundedRegistrationOperation(route string) string {
	return registrationRouteOperations[route]
}

// statusWriter preserves the interfaces required by Gorilla WebSocket while
// capturing the response class for metrics. A wrapper that drops Hijacker
// would break the production realtime upgrade path.
type statusWriter struct {
	http.ResponseWriter
	status int
}

func (w *statusWriter) WriteHeader(status int) {
	if w.status == 0 {
		w.status = status
	}
	w.ResponseWriter.WriteHeader(status)
}

func (w *statusWriter) Write(data []byte) (int, error) {
	if w.status == 0 {
		w.WriteHeader(http.StatusOK)
	}
	return w.ResponseWriter.Write(data)
}

func (w *statusWriter) Flush() {
	if w.status == 0 {
		w.WriteHeader(http.StatusOK)
	}
	if flusher, ok := w.ResponseWriter.(http.Flusher); ok {
		flusher.Flush()
	}
}

func (w *statusWriter) Hijack() (net.Conn, *bufio.ReadWriter, error) {
	hijacker, ok := w.ResponseWriter.(http.Hijacker)
	if !ok {
		return nil, nil, errors.New("response writer does not support hijacking")
	}
	connection, buffer, err := hijacker.Hijack()
	if err == nil && w.status == 0 {
		w.status = http.StatusSwitchingProtocols
	}
	return connection, buffer, err
}

func (w *statusWriter) Push(target string, options *http.PushOptions) error {
	if pusher, ok := w.ResponseWriter.(http.Pusher); ok {
		return pusher.Push(target, options)
	}
	return http.ErrNotSupported
}

func (w *statusWriter) Unwrap() http.ResponseWriter { return w.ResponseWriter }

func (w *statusWriter) statusCode() int {
	if w.status == 0 {
		return http.StatusOK
	}
	return w.status
}
