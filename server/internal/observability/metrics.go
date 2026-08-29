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
	"sync"
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

	httpRequests                *prometheus.CounterVec
	httpDuration                *prometheus.HistogramVec
	connections                 *prometheus.GaugeVec
	subscriptions               prometheus.Gauge
	envelopes                   *prometheus.CounterVec
	envelopeBytes               *prometheus.CounterVec
	routeDuration               *prometheus.HistogramVec
	slowConsumers               prometheus.Counter
	leaseRequests               *prometheus.CounterVec
	commerceHTTP                *prometheus.CounterVec
	commerceState               *prometheus.GaugeVec
	commerceIssues              *prometheus.GaugeVec
	commerceSnapshotUp          prometheus.Gauge
	commerceSnapshotLastSuccess prometheus.Gauge
	paymentCertificateRemaining *prometheus.GaugeVec
	paymentCertificateActive    *prometheus.GaugeVec
	paymentCertificateRetiring  *prometheus.GaugeVec
	paymentCertificateRefreshUp *prometheus.GaugeVec
	postgresPool                *prometheus.GaugeVec
	commerceRefreshMu           sync.Mutex
	commerceRefreshedAt         time.Time
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
		commerceHTTP: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "termlink", Subsystem: "commerce", Name: "http_operations_total",
			Help: "Registration and commerce HTTP operations by bounded operation and coarse result.",
		}, []string{"operation", "result"}),
		commerceState: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "commerce", Name: "workflow_items",
			Help: "Current commercial workflow items by bounded workflow and state.",
		}, []string{"workflow", "state"}),
		commerceIssues: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "commerce", Name: "consistency_issues",
			Help: "Current commercial consistency and recovery issues by bounded check.",
		}, []string{"check"}),
		commerceSnapshotUp: prometheus.NewGauge(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "commerce", Name: "snapshot_up",
			Help: "Whether the latest bounded commercial workflow snapshot query succeeded.",
		}),
		commerceSnapshotLastSuccess: prometheus.NewGauge(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "commerce", Name: "snapshot_last_success_timestamp_seconds",
			Help: "Unix timestamp of the latest successful commercial workflow snapshot.",
		}),
		paymentCertificateRemaining: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "payment", Name: "certificate_remaining_days",
			Help: "Minimum remaining days for the active payment platform certificate trust set.",
		}, []string{"provider", "scene"}),
		paymentCertificateActive: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "payment", Name: "certificate_active",
			Help: "Active platform certificates by bounded provider and scene.",
		}, []string{"provider", "scene"}),
		paymentCertificateRetiring: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "payment", Name: "certificate_retiring",
			Help: "Retiring platform certificates retained during the verification grace period.",
		}, []string{"provider", "scene"}),
		paymentCertificateRefreshUp: prometheus.NewGaugeVec(prometheus.GaugeOpts{
			Namespace: "termlink", Subsystem: "payment", Name: "certificate_refresh_up",
			Help: "Whether the latest platform certificate refresh succeeded.",
		}, []string{"provider", "scene"}),
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
		m.commerceHTTP,
		m.commerceState,
		m.commerceIssues,
		m.commerceSnapshotUp,
		m.commerceSnapshotLastSuccess,
		m.paymentCertificateRemaining,
		m.paymentCertificateActive,
		m.paymentCertificateRetiring,
		m.paymentCertificateRefreshUp,
		m.postgresPool,
	)
	m.initializeCommerceSeries()
	return m
}

// ObservePaymentCertificate records only bounded provider/scene labels and
// low-cardinality health numbers. Certificate serials and merchant identity
// never enter metrics.
func (m *Metrics) ObservePaymentCertificate(provider, scene, _ string, remainingDays, active, retiring int, refreshOK bool) {
	if m == nil || (provider != "WECHAT_PAY" && provider != "ALIPAY") || (scene != "NATIVE" && scene != "WEB") {
		return
	}
	m.paymentCertificateRemaining.WithLabelValues(provider, scene).Set(float64(remainingDays))
	m.paymentCertificateActive.WithLabelValues(provider, scene).Set(float64(active))
	m.paymentCertificateRetiring.WithLabelValues(provider, scene).Set(float64(retiring))
	if refreshOK {
		m.paymentCertificateRefreshUp.WithLabelValues(provider, scene).Set(1)
	} else {
		m.paymentCertificateRefreshUp.WithLabelValues(provider, scene).Set(0)
	}
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
		m.refreshCommerceSnapshot()
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
			if operation := boundedCommerceOperation(route); operation != "" {
				m.commerceHTTP.WithLabelValues(operation, coarseResult(status)).Inc()
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

var commerceWorkflowStates = map[string][]string{
	"invitation":     {"RESERVED", "PROVIDER_ACCEPTED", "REDEEMED", "RELEASED"},
	"redemption":     {"CLAIMED"},
	"order":          {"CREATED", "PENDING_PAYMENT", "PAID", "FULFILLING", "FULFILLED", "CLOSED", "REFUNDING", "REFUNDED", "FAILED"},
	"payment":        {"INITIATED", "PENDING", "SUCCEEDED", "FAILED", "CLOSED", "REFUNDED"},
	"webhook":        {"RECEIVED", "PROCESSED", "REJECTED", "RETRY"},
	"refund":         {"CREATED", "PENDING", "SUCCEEDED", "FAILED", "CLOSED"},
	"reconciliation": {"PENDING", "RUNNING", "SUCCEEDED", "FAILED"},
	"entitlement":    {"PENDING", "ACTIVE", "EXPIRED", "REVOKED"},
}

var commerceConsistencyChecks = []string{
	"stalled_fulfillment",
	"fulfilled_order_without_grant",
	"redemption_claim_without_grant",
	"refunded_order_with_active_grant",
	"processed_webhook_without_order",
	"materialized_entitlement_mismatch",
}

const commerceWorkflowSnapshotSQL = `
SELECT workflow,state,total FROM (
  SELECT 'invitation'::text AS workflow,state,count(*)::bigint AS total FROM invitation_redemptions GROUP BY state
  UNION ALL
  SELECT 'redemption','CLAIMED',count(*)::bigint FROM redemption_claims
  UNION ALL
  SELECT 'order',status,count(*)::bigint FROM orders GROUP BY status
  UNION ALL
  SELECT 'payment',status,count(*)::bigint FROM payment_transactions GROUP BY status
  UNION ALL
  SELECT 'webhook',processing_state,count(*)::bigint FROM payment_notifications GROUP BY processing_state
  UNION ALL
  SELECT 'refund',status,count(*)::bigint FROM refunds GROUP BY status
  UNION ALL
  SELECT 'reconciliation',status,count(*)::bigint FROM reconciliation_runs GROUP BY status
  UNION ALL
  SELECT 'entitlement',status,count(*)::bigint FROM entitlement_grants GROUP BY status
) bounded_commerce_state`

const commerceConsistencySnapshotSQL = `
WITH effective_grant AS (
  SELECT DISTINCT ON (g.account_id)
         g.account_id,g.plan_key,g.catalog_version,g.source_type
  FROM entitlement_grants g
  JOIN catalog_versions cv ON cv.version=g.catalog_version
  JOIN plans p ON p.catalog_version_id=cv.id AND p.plan_key=g.plan_key
  WHERE g.status='ACTIVE' AND g.starts_at<=now()
    AND (g.ends_at IS NULL OR g.ends_at>now())
  ORDER BY g.account_id,p.price_minor DESC,p.sort_order DESC,
           (g.source_type='DEFAULT') ASC,g.ends_at DESC NULLS FIRST,g.created_at DESC
)
SELECT check_name,total FROM (
  SELECT 'stalled_fulfillment'::text AS check_name,count(*)::bigint AS total
  FROM orders WHERE status IN ('PAID','FULFILLING','FAILED') AND updated_at<now()-interval '5 minutes'
  UNION ALL
  SELECT 'fulfilled_order_without_grant',count(*)::bigint
  FROM orders o WHERE o.status='FULFILLED' AND NOT EXISTS (
    SELECT 1 FROM entitlement_grants g
    WHERE g.source_id=o.id AND g.source_type=o.provider
  )
  UNION ALL
  SELECT 'redemption_claim_without_grant',count(*)::bigint
  FROM redemption_claims c LEFT JOIN entitlement_grants g ON g.id=c.grant_id
  WHERE g.id IS NULL
  UNION ALL
  SELECT 'refunded_order_with_active_grant',count(*)::bigint
  FROM orders o WHERE o.status='REFUNDED' AND EXISTS (
    SELECT 1 FROM entitlement_grants g
    WHERE g.source_id=o.id AND g.source_type=o.provider AND g.status='ACTIVE'
  )
  UNION ALL
  SELECT 'processed_webhook_without_order',count(*)::bigint
  FROM payment_notifications n LEFT JOIN orders o ON o.order_no=n.order_no
  WHERE n.processing_state='PROCESSED' AND n.order_no IS NOT NULL AND o.id IS NULL
  UNION ALL
  SELECT 'materialized_entitlement_mismatch',count(*)::bigint
  FROM effective_grant g LEFT JOIN entitlements e ON e.account_id=g.account_id
  WHERE e.account_id IS NULL OR e.plan_key<>g.plan_key OR e.catalog_version<>g.catalog_version OR e.source<>g.source_type
) bounded_commerce_consistency`

func (m *Metrics) initializeCommerceSeries() {
	for workflow, states := range commerceWorkflowStates {
		for _, state := range states {
			m.commerceState.WithLabelValues(workflow, state).Set(0)
		}
	}
	for _, check := range commerceConsistencyChecks {
		m.commerceIssues.WithLabelValues(check).Set(0)
	}
}

func validCommerceWorkflowState(workflow, state string) bool {
	for _, allowed := range commerceWorkflowStates[workflow] {
		if state == allowed {
			return true
		}
	}
	return false
}

func validCommerceConsistencyCheck(check string) bool {
	for _, allowed := range commerceConsistencyChecks {
		if check == allowed {
			return true
		}
	}
	return false
}

func (m *Metrics) refreshCommerceSnapshot() {
	if m.pool == nil {
		return
	}
	m.commerceRefreshMu.Lock()
	defer m.commerceRefreshMu.Unlock()
	if time.Since(m.commerceRefreshedAt) < 30*time.Second {
		return
	}
	m.commerceRefreshedAt = time.Now()
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()

	workflowValues, err := m.queryCommerceWorkflowSnapshot(ctx)
	if err != nil {
		m.commerceSnapshotUp.Set(0)
		return
	}
	issueValues, err := m.queryCommerceConsistencySnapshot(ctx)
	if err != nil {
		m.commerceSnapshotUp.Set(0)
		return
	}
	m.initializeCommerceSeries()
	for labels, total := range workflowValues {
		m.commerceState.WithLabelValues(labels.workflow, labels.state).Set(float64(total))
	}
	for check, total := range issueValues {
		m.commerceIssues.WithLabelValues(check).Set(float64(total))
	}
	m.commerceSnapshotUp.Set(1)
	m.commerceSnapshotLastSuccess.SetToCurrentTime()
}

type commerceWorkflowLabels struct {
	workflow string
	state    string
}

func (m *Metrics) queryCommerceWorkflowSnapshot(ctx context.Context) (map[commerceWorkflowLabels]int64, error) {
	rows, err := m.pool.Query(ctx, commerceWorkflowSnapshotSQL)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	values := make(map[commerceWorkflowLabels]int64)
	for rows.Next() {
		var workflow, state string
		var total int64
		if err := rows.Scan(&workflow, &state, &total); err != nil {
			return nil, err
		}
		if !validCommerceWorkflowState(workflow, state) || total < 0 {
			return nil, errors.New("commercial workflow snapshot returned an unbounded value")
		}
		values[commerceWorkflowLabels{workflow: workflow, state: state}] = total
	}
	return values, rows.Err()
}

func (m *Metrics) queryCommerceConsistencySnapshot(ctx context.Context) (map[string]int64, error) {
	rows, err := m.pool.Query(ctx, commerceConsistencySnapshotSQL)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	values := make(map[string]int64)
	for rows.Next() {
		var check string
		var total int64
		if err := rows.Scan(&check, &total); err != nil {
			return nil, err
		}
		if !validCommerceConsistencyCheck(check) || total < 0 {
			return nil, errors.New("commercial consistency snapshot returned an unbounded value")
		}
		values[check] = total
	}
	return values, rows.Err()
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

var commerceRouteOperations = map[string]string{
	"POST /v1/auth/register":                                    "registration",
	"GET /v1/registration-policy":                               "registration_policy_read",
	"POST /v1/auth/invitations/validate":                        "invitation_validation",
	"GET /v1/account/entitlements":                              "entitlement_read",
	"GET /v1/billing/plans":                                     "catalog_read",
	"POST /v1/billing/google/verify":                            "payment_verify_google",
	"POST /v1/billing/restore":                                  "payment_restore",
	"POST /v1/billing/web/checkout":                             "payment_checkout_legacy",
	"POST /v1/billing/webhooks/alipay":                          "webhook_alipay",
	"POST /v1/billing/webhooks/wechat-pay":                      "webhook_wechat_pay",
	"POST /v1/billing/redemptions/preview":                      "redemption_preview",
	"POST /v1/billing/redemptions/claim":                        "redemption_claim",
	"GET /v1/account/redemptions":                               "redemption_history",
	"POST /v1/billing/orders":                                   "order_create",
	"GET /v1/billing/orders":                                    "order_read",
	"GET /v1/billing/orders/{orderId}":                          "order_read",
	"POST /v1/billing/orders/{orderId}/pay":                     "payment_start",
	"POST /v1/billing/orders/{orderId}/query":                   "payment_recovery",
	"POST /v1/billing/orders/{orderId}/cancel":                  "order_cancel",
	"GET /v1/admin/registration-policy":                         "registration_policy_admin",
	"PATCH /v1/admin/registration-policy":                       "registration_policy_admin",
	"GET /v1/admin/invitation-batches":                          "invitation_admin",
	"POST /v1/admin/invitation-batches":                         "invitation_admin",
	"POST /v1/admin/invitation-batches/{batchId}/generate":      "invitation_generate",
	"POST /v1/admin/invitation-batches/{batchId}/disable":       "invitation_admin",
	"GET /v1/admin/invitation-codes":                            "invitation_admin",
	"POST /v1/admin/invitation-codes/{codeId}/disable":          "invitation_admin",
	"GET /v1/admin/invitation-redemptions":                      "invitation_history_admin",
	"GET /v1/admin/redemption-batches":                          "redemption_admin",
	"POST /v1/admin/redemption-batches":                         "redemption_admin",
	"POST /v1/admin/redemption-batches/{batchId}/generate":      "redemption_generate",
	"POST /v1/admin/redemption-batches/{batchId}/disable":       "redemption_admin",
	"GET /v1/admin/redemption-codes":                            "redemption_admin",
	"POST /v1/admin/redemption-codes/{codeId}/disable":          "redemption_admin",
	"GET /v1/admin/redemption-claims":                           "redemption_history_admin",
	"GET /v1/admin/billing/orders":                              "order_read_admin",
	"POST /v1/admin/billing/orders/{orderId}/query":             "payment_recovery_admin",
	"POST /v1/admin/billing/orders/{orderId}/retry-fulfillment": "fulfillment_recovery",
	"GET /v1/admin/billing/transactions":                        "payment_read_admin",
	"POST /v1/admin/billing/orders/{orderId}/refunds":           "refund_create",
	"GET /v1/admin/billing/refunds":                             "refund_read",
	"GET /v1/admin/billing/notifications":                       "webhook_read",
	"GET /v1/admin/billing/reconciliation-runs":                 "reconciliation_read",
	"POST /v1/admin/billing/reconciliation-runs":                "reconciliation_create",
	"GET /v1/admin/payment-channels":                            "payment_channel_read",
	"POST /v1/admin/payment-channels":                           "payment_channel_admin",
	"GET /v1/admin/payment-channels/{channelId}/versions":       "payment_channel_read",
	"POST /v1/admin/payment-channels/{channelId}/versions":      "payment_channel_admin",
	"POST /v1/admin/payment-channels/{channelId}/validate":      "payment_channel_validate",
	"POST /v1/admin/payment-channels/{channelId}/activate":      "payment_channel_admin",
	"POST /v1/admin/payment-channels/{channelId}/disable":       "payment_channel_admin",
	"POST /v1/admin/billing/plans":                              "catalog_admin",
	"PUT /v1/admin/billing/plans/{id}":                          "catalog_admin",
	"POST /v1/admin/billing/catalog/publish":                    "catalog_publish",
	"GET /commerce":                                             "commerce_read",
	"POST /commerce/redemptions":                                "redemption_claim",
	"POST /commerce/orders":                                     "order_create",
	"POST /commerce/orders/{id}/pay":                            "payment_start",
	"POST /commerce/orders/{id}/query":                          "payment_recovery",
	"POST /commerce/orders/{id}/cancel":                         "order_cancel",
	"GET /admin/commerce":                                       "commerce_read_admin",
	"POST /admin/commerce/registration-policy":                  "registration_policy_admin",
	"POST /admin/commerce/invitations/batches":                  "invitation_admin",
	"POST /admin/commerce/invitations/batches/{id}/codes":       "invitation_generate",
	"POST /admin/commerce/invitations/batches/{id}/status":      "invitation_admin",
	"POST /admin/commerce/invitations/codes/{id}/status":        "invitation_admin",
	"POST /admin/commerce/redemptions/batches":                  "redemption_admin",
	"POST /admin/commerce/redemptions/batches/{id}/codes":       "redemption_generate",
	"POST /admin/commerce/redemptions/batches/{id}/status":      "redemption_admin",
	"POST /admin/commerce/redemptions/codes/{id}/status":        "redemption_admin",
	"POST /admin/commerce/refunds":                              "refund_create",
	"POST /admin/commerce/reconciliation":                       "reconciliation_create",
	"POST /admin/commerce/payment-channels":                     "payment_channel_admin",
	"POST /admin/commerce/payment-channels/{id}/versions":       "payment_channel_admin",
	"POST /admin/commerce/payment-channels/{id}/activate":       "payment_channel_admin",
	"POST /admin/commerce/payment-channels/{id}/disable":        "payment_channel_admin",
}

func boundedCommerceOperation(route string) string {
	return commerceRouteOperations[route]
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
