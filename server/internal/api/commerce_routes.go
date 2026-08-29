package api

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"strconv"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
	"github.com/termlink/termlink/server/internal/commerce"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	maxCommerceWebhookBytes = 1 << 20
	maxGeneratedCodeCount   = 1000
	invitationCodeChars     = 12 // 60 bits from the 32-character alphabet.
	redemptionCodeChars     = 21 // 105 bits from the 32-character alphabet.
)

const commerceCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

const commerceStepUpMaximumAge = 10 * time.Minute
const commerceOperationReasonHeader = "X-TermLink-Operation-Reason"

type commerceOperationAuditKey struct{}

type commerceOperationAudit struct {
	reason string
}

type commerceSecurityStore interface {
	IsAdmin(context.Context, uuid.UUID) (bool, error)
	Audit(context.Context, uuid.UUID, uuid.UUID, string, string, uuid.UUID, string, string, string) error
	RecordSecurityEvent(context.Context, store.SecurityEvent) error
}

const (
	commerceRateInvitationValidate = "invitation-validate"
	commerceRateRedemptionPreview  = "redemption-preview"
	commerceRateRedemptionClaim    = "redemption-claim"
	commerceRateCodeGeneration     = "code-generation"
	commerceRateWebhookAlipay      = "webhook-alipay"
	commerceRateWebhookWeChat      = "webhook-wechat"
	commerceRateOrderQuery         = "order-query"
	commerceRateAdminOrderQuery    = "admin-order-query"
)

var commerceRateLimitScript = redis.NewScript(`
local current=redis.call('GET',KEYS[1])
if current and tonumber(current)>=tonumber(ARGV[1]) then return 0 end
current=redis.call('INCR',KEYS[1])
if current==1 then redis.call('PEXPIRE',KEYS[1],ARGV[2]) end
return 1`)

var errInvalidCodeGeneration = errors.New("invalid code generation request")

func commerceRateSpec(domain string) (int64, time.Duration, bool) {
	switch domain {
	case commerceRateInvitationValidate:
		return 12, time.Minute, true
	case commerceRateRedemptionPreview:
		return 20, time.Minute, true
	case commerceRateRedemptionClaim:
		return 10, time.Minute, true
	case commerceRateCodeGeneration:
		return 6, time.Minute, true
	case commerceRateWebhookAlipay, commerceRateWebhookWeChat:
		return 120, time.Minute, true
	case commerceRateOrderQuery:
		return 12, time.Minute, true
	case commerceRateAdminOrderQuery:
		return 30, time.Minute, true
	default:
		return 0, 0, false
	}
}

func commerceRateLimitScope(r *http.Request, domain string) (string, error) {
	if _, _, valid := commerceRateSpec(domain); !valid {
		return "", errors.New("unknown commerce rate-limit domain")
	}
	identity := accountRegistrationClientIdentity(r)
	if p := principal(r); p.UserID != uuid.Nil {
		identity += "\x00account:" + p.UserID.String() + "\x00device:" + p.Device.ID.String()
	}
	digest := sha256.Sum256([]byte("TermLink commerce rate limit v1\x00" + domain + "\x00" + identity))
	return "rate:commerce:" + domain + ":" + hex.EncodeToString(digest[:]), nil
}

func (a *API) consumeCommerceRateLimit(r *http.Request, domain string) (bool, error) {
	limit, window, valid := commerceRateSpec(domain)
	if !valid || a.Valkey == nil || a.Valkey.Client == nil {
		return false, errors.New("commerce rate limiter is unavailable")
	}
	key, err := commerceRateLimitScope(r, domain)
	if err != nil {
		return false, err
	}
	allowed, err := commerceRateLimitScript.Run(r.Context(), a.Valkey.Client, []string{key}, limit, window.Milliseconds()).Int64()
	return allowed == 1, err
}

func (a *API) securityStore() commerceSecurityStore {
	if a.commerceSecurity != nil {
		return a.commerceSecurity
	}
	if a.Store == nil {
		return nil
	}
	return a.Store
}

func commerceSecuritySourceHash(r *http.Request) []byte {
	digest := sha256.Sum256([]byte("TermLink security event source v1\x00" + accountRegistrationClientIdentity(r)))
	return digest[:]
}

func (a *API) recordCommerceSecurityEvent(r *http.Request, action, targetType, result string, metadata json.RawMessage) {
	repository := a.securityStore()
	if repository == nil {
		return
	}
	p := principal(r)
	event := store.SecurityEvent{
		Action: action, TargetType: targetType, Result: result,
		TraceID: traceID(r.Context()), SourceHash: commerceSecuritySourceHash(r), Metadata: metadata,
	}
	if p.UserID != uuid.Nil {
		event.UserID = &p.UserID
	}
	if p.Device.ID != uuid.Nil {
		event.ActorDeviceID = &p.Device.ID
	}
	if err := repository.RecordSecurityEvent(r.Context(), event); err != nil {
		logAPIFailure(r.Context(), slog.LevelError, "record commerce security event",
			"commerce_security_event_record_failed", err, slog.String("action", action))
	}
}

func commerceRateSecurityAction(domain string) (string, string) {
	switch domain {
	case commerceRateInvitationValidate:
		return "commerce.invitation.rate_limit.denied", "invitation"
	case commerceRateRedemptionPreview, commerceRateRedemptionClaim:
		return "commerce.redemption.rate_limit.denied", "redemption"
	case commerceRateOrderQuery, commerceRateAdminOrderQuery:
		return "commerce.payment.rate_limit.denied", "payment"
	case commerceRateCodeGeneration:
		return "commerce.code_generation.rate_limit.denied", "code_generation"
	case commerceRateWebhookAlipay, commerceRateWebhookWeChat:
		return "commerce.webhook.rate_limit.denied", "webhook"
	default:
		return "commerce.rate_limit.denied", "commerce"
	}
}

func (a *API) recordCommerceRateLimitFailure(r *http.Request, domain string, unavailable bool) {
	if _, _, valid := commerceRateSpec(domain); !valid {
		domain = "unknown"
	}
	action, targetType := commerceRateSecurityAction(domain)
	reason := "LIMIT_EXCEEDED"
	result := "DENIED"
	if unavailable {
		reason = "LIMITER_UNAVAILABLE"
		result = "FAILED"
	}
	metadata, _ := json.Marshal(map[string]string{"domain": domain, "reason": reason})
	a.recordCommerceSecurityEvent(r, action, targetType, result, metadata)
}

func (a *API) requireCommerce(w http.ResponseWriter, r *http.Request) bool {
	if a.Commerce == nil || a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce service is unavailable"))
		return false
	}
	return true
}

func commerceIdempotencyKey(r *http.Request) (string, error) {
	key := r.Header.Get("Idempotency-Key")
	if len(key) < 8 || len(key) > 128 || key != strings.TrimSpace(key) {
		return "", errors.New("idempotency key must contain 8 to 128 non-padding characters")
	}
	for _, character := range key {
		if character < 0x21 || character > 0x7e {
			return "", errors.New("idempotency key must be printable ASCII")
		}
	}
	return key, nil
}

func commerceLimit(r *http.Request) (int, error) {
	raw := strings.TrimSpace(r.URL.Query().Get("limit"))
	if raw == "" {
		return 50, nil
	}
	limit, err := strconv.Atoi(raw)
	if err != nil || limit < 1 || limit > 200 {
		return 0, errors.New("limit must be between 1 and 200")
	}
	return limit, nil
}

func commercePathUUID(r *http.Request, name string) (uuid.UUID, error) {
	value, err := uuid.Parse(r.PathValue(name))
	if err != nil || value == uuid.Nil {
		return uuid.Nil, errors.New("invalid UUID path value")
	}
	return value, nil
}

func decodeEmptyCommerceJSON(r *http.Request) error {
	if r.Body == nil {
		return nil
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, maxJSONBody+1))
	if err != nil || len(body) > maxJSONBody {
		return errors.New("request body exceeds limit")
	}
	if len(bytes.TrimSpace(body)) == 0 {
		return nil
	}
	clone := r.Clone(r.Context())
	clone.Body = io.NopCloser(bytes.NewReader(body))
	var input struct{}
	return decodeJSON(clone, &input)
}

// oneTimeCommerceIdempotency provides operation idempotency without retaining
// or replaying generated bearer secrets. A successful retry receives an
// explicit consumed response; failures remain replayable just like the normal
// API middleware.
func (a *API) oneTimeCommerceIdempotency(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		key, err := commerceIdempotencyKey(r)
		if err != nil {
			writeError(w, r, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", err)
			return
		}
		if a.Store == nil {
			writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
			return
		}
		body, err := io.ReadAll(io.LimitReader(r.Body, maxJSONBody+1))
		if err != nil || len(body) > maxJSONBody {
			writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", errors.New("request body exceeds limit"))
			return
		}
		r.Body = io.NopCloser(bytes.NewReader(body))
		digest := idempotencyRequestDigest(r, body)
		p := principal(r)
		result, err := a.Store.ClaimIdempotency(r.Context(), p.UserID, key, digest)
		if err != nil {
			writeStoreError(w, r, err)
			return
		}
		if !bytes.Equal(result.RequestHash[:], digest[:]) {
			writeError(w, r, http.StatusConflict, "IDEMPOTENCY_KEY_REUSED", store.ErrConflict)
			return
		}
		if !result.Claimed {
			if result.Status == nil {
				writeError(w, r, http.StatusConflict, "IDEMPOTENCY_IN_PROGRESS", store.ErrConflict)
				return
			}
			if *result.Status >= 200 && *result.Status < 300 {
				writeError(w, r, http.StatusConflict, "ONE_TIME_RESPONSE_CONSUMED", store.ErrConflict)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			w.Header().Set("Cache-Control", "no-store")
			w.Header().Set("Idempotency-Replayed", "true")
			w.WriteHeader(*result.Status)
			if len(result.Body) != 0 && !bytes.Equal(result.Body, []byte("null")) {
				_, _ = w.Write(result.Body)
			}
			return
		}
		capture := &captureWriter{ResponseWriter: w, status: http.StatusOK}
		next.ServeHTTP(capture, r)
		stored := json.RawMessage(capture.body.Bytes())
		if capture.status >= 200 && capture.status < 300 {
			stored = json.RawMessage(`{"generated":true,"plaintext_available":false}`)
		} else if len(stored) == 0 || !json.Valid(stored) {
			stored = json.RawMessage(`{"error":{"code":"NON_JSON_RESPONSE"}}`)
		}
		if err := a.Store.CompleteIdempotency(r.Context(), p.UserID, key, digest, capture.status, stored); err != nil {
			logAPIFailure(r.Context(), slog.LevelError, "complete one-time commerce idempotency",
				"commerce_idempotency_completion_failed", err)
		}
	})
}

func (a *API) handleAlipayWebhook(w http.ResponseWriter, r *http.Request) {
	scene := string(payment.FlowNative)
	if requested := strings.ToUpper(strings.TrimSpace(r.URL.Query().Get("scene"))); requested != "" {
		if requested != string(payment.FlowNative) && requested != string(payment.FlowWeb) {
			writeCommerceWebhookAcknowledgement(w, commerceWebhookFailure(string(payment.ProviderAlipay)))
			return
		}
		scene = requested
	}
	a.handleCommerceWebhook(w, r, string(payment.ProviderAlipay), scene)
}

func (a *API) handleWeChatWebhook(w http.ResponseWriter, r *http.Request) {
	a.handleCommerceWebhook(w, r, string(payment.ProviderWeChatPay), string(payment.FlowNative))
}

func (a *API) handleCommerceWebhook(w http.ResponseWriter, r *http.Request, provider, scene string) {
	failure := commerceWebhookFailure(provider)
	domain := commerceRateWebhookAlipay
	if provider == string(payment.ProviderWeChatPay) {
		domain = commerceRateWebhookWeChat
	}
	allowed, limitErr := a.consumeCommerceRateLimit(r, domain)
	if limitErr != nil || !allowed {
		a.recordCommerceRateLimitFailure(r, domain, limitErr != nil)
		failureClass := "RATE_LIMITED"
		if limitErr != nil {
			failureClass = "RATE_LIMIT_UNAVAILABLE"
		}
		slog.Warn("commerce webhook rejected", "trace_id", traceID(r.Context()), "provider", provider, "failure_class", failureClass)
		writeCommerceWebhookAcknowledgement(w, failure)
		return
	}
	if a.Commerce == nil {
		writeCommerceWebhookAcknowledgement(w, failure)
		return
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, maxCommerceWebhookBytes+1))
	if err != nil || len(body) > maxCommerceWebhookBytes {
		a.recordCommerceSecurityEvent(r, "commerce.webhook.denied", "webhook", "DENIED", json.RawMessage(`{"reason":"PAYLOAD_TOO_LARGE"}`))
		writeCommerceWebhookAcknowledgement(w, failure)
		return
	}
	acknowledgement, processErr := a.Commerce.HandleWebhook(r.Context(), provider, scene, r.Header.Clone(), body)
	if processErr != nil {
		failureClass := a.recordCommerceWebhookFailure(r, provider, processErr)
		slog.Warn("commerce webhook rejected", "trace_id", traceID(r.Context()), "provider", provider, "failure_class", failureClass)
	}
	writeCommerceWebhookAcknowledgement(w, acknowledgement)
}

func (a *API) recordCommerceWebhookFailure(r *http.Request, provider string, err error) string {
	failureClass := commerceWebhookFailureClass(err)
	metadata, _ := json.Marshal(map[string]string{"provider": boundedCommerceProvider(provider), "reason": failureClass})
	a.recordCommerceSecurityEvent(r, "commerce.webhook.denied", "webhook", "DENIED", metadata)
	return failureClass
}

func boundedCommerceProvider(provider string) string {
	switch provider {
	case string(payment.ProviderAlipay), string(payment.ProviderWeChatPay):
		return provider
	default:
		return "UNKNOWN"
	}
}

func commerceWebhookFailureClass(err error) string {
	switch {
	case errors.Is(err, payment.ErrInvalidSignature):
		return "INVALID_SIGNATURE"
	case errors.Is(err, payment.ErrReplay):
		return "REPLAY_WINDOW"
	case errors.Is(err, payment.ErrPayloadTooLarge):
		return "PAYLOAD_TOO_LARGE"
	case errors.Is(err, payment.ErrMalformedPayload), errors.Is(err, payment.ErrInvalidRequest), errors.Is(err, commerce.ErrInvalidRequest):
		return "MALFORMED_PAYLOAD"
	case errors.Is(err, commerce.ErrPaymentResultMismatch), errors.Is(err, store.ErrOrderAmountMismatch):
		return "PAYMENT_MISMATCH"
	case errors.Is(err, commerce.ErrPaymentStateNotProgressable), errors.Is(err, store.ErrOrderNotPayable):
		return "STATE_REJECTED"
	case errors.Is(err, commerce.ErrPaymentChannelUnavailable), errors.Is(err, store.ErrPaymentChannelUnavailable):
		return "CHANNEL_UNAVAILABLE"
	default:
		return "PROCESSING_FAILED"
	}
}

func commerceWebhookFailure(provider string) commerce.WebhookAcknowledgement {
	switch provider {
	case string(payment.ProviderAlipay):
		return commerce.WebhookAcknowledgement{Provider: payment.ProviderAlipay, StatusCode: http.StatusOK, ContentType: "text/plain; charset=utf-8", Body: []byte("failure")}
	case string(payment.ProviderWeChatPay):
		return commerce.WebhookAcknowledgement{Provider: payment.ProviderWeChatPay, StatusCode: http.StatusInternalServerError, ContentType: "application/json; charset=utf-8", Body: []byte(`{"code":"FAIL","message":"processing failed"}`)}
	default:
		return commerce.WebhookAcknowledgement{StatusCode: http.StatusBadRequest, ContentType: "text/plain; charset=utf-8", Body: []byte("failure")}
	}
}

func writeCommerceWebhookAcknowledgement(w http.ResponseWriter, acknowledgement commerce.WebhookAcknowledgement) {
	if acknowledgement.StatusCode < 100 || acknowledgement.StatusCode > 599 || acknowledgement.ContentType == "" || len(acknowledgement.Body) == 0 {
		acknowledgement = commerceWebhookFailure("")
	}
	w.Header().Set("Content-Type", acknowledgement.ContentType)
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(acknowledgement.StatusCode)
	_, _ = w.Write(acknowledgement.Body)
}

type redemptionClaimRequest struct {
	Code string `json:"code"`
}

func (a *API) previewRedemption(w http.ResponseWriter, r *http.Request) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return
	}
	allowed, err := a.consumeCommerceRateLimit(r, commerceRateRedemptionPreview)
	if err != nil {
		a.recordCommerceRateLimitFailure(r, commerceRateRedemptionPreview, true)
		writeError(w, r, http.StatusServiceUnavailable, "REDEMPTION_VALIDATION_UNAVAILABLE", err)
		return
	}
	if !allowed {
		a.recordCommerceRateLimitFailure(r, commerceRateRedemptionPreview, false)
		writeError(w, r, http.StatusTooManyRequests, "REDEMPTION_RATE_LIMITED", errors.New("redemption preview rate limit reached"))
		return
	}
	var input redemptionClaimRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	code := normalizeCommerceCode(input.Code)
	if !validCommerceCode(code, "TLR") {
		writeError(w, r, http.StatusUnprocessableEntity, "REDEMPTION_INVALID", store.ErrRedemptionInvalid)
		return
	}
	if len(a.commerceDigestKey()) < 32 {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce digest key is unavailable"))
		return
	}
	preview, err := a.Store.PreviewRedemption(r.Context(), principal(r).UserID, keyedCommerceDigest(a.commerceDigestKey(), "redemption-code", code))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"preview": preview, "inventory_reserved": false})
}

func (a *API) claimRedemption(w http.ResponseWriter, r *http.Request) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return
	}
	allowed, err := a.consumeCommerceRateLimit(r, commerceRateRedemptionClaim)
	if err != nil {
		a.recordCommerceRateLimitFailure(r, commerceRateRedemptionClaim, true)
		writeError(w, r, http.StatusServiceUnavailable, "REDEMPTION_VALIDATION_UNAVAILABLE", err)
		return
	}
	if !allowed {
		a.recordCommerceRateLimitFailure(r, commerceRateRedemptionClaim, false)
		writeError(w, r, http.StatusTooManyRequests, "REDEMPTION_RATE_LIMITED", errors.New("redemption claim rate limit reached"))
		return
	}
	key, err := commerceIdempotencyKey(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", err)
		return
	}
	var input redemptionClaimRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	code := normalizeCommerceCode(input.Code)
	if !validCommerceCode(code, "TLR") {
		writeError(w, r, http.StatusUnprocessableEntity, "REDEMPTION_INVALID", store.ErrRedemptionInvalid)
		return
	}
	if len(a.commerceDigestKey()) < 32 {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce digest key is unavailable"))
		return
	}
	p := principal(r)
	claim, entitlement, err := a.Store.ClaimRedemption(r.Context(), p.UserID, keyedCommerceDigest(a.commerceDigestKey(), "redemption-code", code), key)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"claim": redemptionClaimView(claim), "entitlement": entitlement})
}

func (a *API) listAccountRedemptions(w http.ResponseWriter, r *http.Request) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListAccountRedemptions(r.Context(), principal(r).UserID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": accountRedemptionViews(items)})
}

type commerceOrderRequest struct {
	PlanKey  string `json:"plan_key"`
	Provider string `json:"provider"`
	Scene    string `json:"scene"`
}

func (a *API) createCommerceOrder(w http.ResponseWriter, r *http.Request) {
	if !a.requireCommerce(w, r) {
		return
	}
	key, err := commerceIdempotencyKey(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", err)
		return
	}
	var input commerceOrderRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	p := principal(r)
	order, err := a.Commerce.CreateOrder(r.Context(), commerce.CreateOrderRequest{
		AccountID: p.UserID, PlanKey: input.PlanKey, Provider: input.Provider,
		Scene: input.Scene, IdempotencyKey: key, ExpiresAt: a.now().Add(15 * time.Minute),
	})
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"order": commerceOrderView(order)})
}

func (a *API) listCommerceOrders(w http.ResponseWriter, r *http.Request) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListOrders(r.Context(), principal(r).UserID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": commerceOrderViews(items)})
}

func (a *API) getCommerceOrder(w http.ResponseWriter, r *http.Request) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return
	}
	orderID, err := commercePathUUID(r, "orderId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_ID", err)
		return
	}
	p := principal(r)
	order, err := a.Store.GetOrder(r.Context(), p.UserID, orderID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	transactions, err := a.Store.ListOrderTransactions(r.Context(), p.UserID, orderID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"order": commerceOrderView(order), "transactions": commerceTransactionViews(transactions)})
}

func (a *API) payCommerceOrder(w http.ResponseWriter, r *http.Request) {
	if !a.requireCommerce(w, r) {
		return
	}
	if err := decodeEmptyCommerceJSON(r); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	orderID, err := commercePathUUID(r, "orderId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_ID", err)
		return
	}
	instruction, err := a.Commerce.StartPayment(r.Context(), principal(r).UserID, orderID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"payment": instruction})
}

func (a *API) queryCommerceOrder(w http.ResponseWriter, r *http.Request) {
	if !a.requireCommerce(w, r) {
		return
	}
	if err := decodeEmptyCommerceJSON(r); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	allowed, err := a.consumeCommerceRateLimit(r, commerceRateOrderQuery)
	if err != nil {
		a.recordCommerceRateLimitFailure(r, commerceRateOrderQuery, true)
		writeError(w, r, http.StatusServiceUnavailable, "PAYMENT_QUERY_UNAVAILABLE", err)
		return
	}
	if !allowed {
		a.recordCommerceRateLimitFailure(r, commerceRateOrderQuery, false)
		writeError(w, r, http.StatusTooManyRequests, "PAYMENT_QUERY_RATE_LIMITED", errors.New("payment query rate limit reached"))
		return
	}
	orderID, err := commercePathUUID(r, "orderId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_ID", err)
		return
	}
	outcome, err := a.Commerce.QueryAndFulfill(r.Context(), principal(r).UserID, orderID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, paymentOutcomeView(outcome))
}

func (a *API) cancelCommerceOrder(w http.ResponseWriter, r *http.Request) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return
	}
	if err := decodeEmptyCommerceJSON(r); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	orderID, err := commercePathUUID(r, "orderId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_ID", err)
		return
	}
	order, err := a.Store.CancelOrder(r.Context(), principal(r).UserID, orderID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"order": commerceOrderView(order)})
}

func (a *API) requireCommerceAdmin(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	repository := a.securityStore()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return uuid.Nil, false
	}
	p := principal(r)
	administrator, err := repository.IsAdmin(r.Context(), p.UserID)
	if err != nil {
		writeStoreError(w, r, err)
		return uuid.Nil, false
	}
	if !administrator {
		_ = repository.Audit(r.Context(), p.UserID, p.Device.ID, "commerce.admin.denied", "commerce", uuid.Nil, "DENIED", traceID(r.Context()), "{}")
		writeError(w, r, http.StatusForbidden, "ADMIN_REQUIRED", store.ErrDenied)
		return uuid.Nil, false
	}
	return p.UserID, true
}

func (a *API) requireCommerceStepUp(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	actorID, ok := a.requireCommerceAdmin(w, r)
	if !ok || !a.requireRecentStepUp(w, r, actorID) {
		return uuid.Nil, false
	}
	return actorID, true
}

func (a *API) requireRecentStepUp(w http.ResponseWriter, r *http.Request, actorID uuid.UUID) bool {
	identity := principal(r).Identity
	now := a.now()
	reason := "MFA_REQUIRED"
	valid := identity.MFA && !identity.AuthenticatedAt.IsZero()
	if valid {
		authenticatedAt := identity.AuthenticatedAt.UTC()
		age := now.Sub(authenticatedAt)
		valid = age >= 0 && age <= commerceStepUpMaximumAge
		if !valid {
			reason = "AUTHENTICATION_STALE"
		}
	}
	if valid {
		return a.requireCommerceOperationReason(w, r)
	}
	metadata, _ := json.Marshal(map[string]string{"reason": reason})
	a.recordCommerceSecurityEvent(r, "commerce.admin.step_up.denied", "commerce_admin", "DENIED", metadata)
	writeError(w, r, http.StatusForbidden, "STEP_UP_REQUIRED", store.ErrDenied)
	return false
}

func (a *API) requireCommerceOperationReason(w http.ResponseWriter, r *http.Request) bool {
	reason := strings.TrimSpace(r.Header.Get(commerceOperationReasonHeader))
	valid := utf8.ValidString(reason)
	runeCount := utf8.RuneCountInString(reason)
	valid = valid && runeCount >= 3 && runeCount <= 500
	if valid {
		for _, character := range reason {
			if unicode.IsControl(character) {
				valid = false
				break
			}
		}
	}
	if !valid {
		a.recordCommerceSecurityEvent(r, "commerce.admin.operation_reason.denied", "commerce_admin", "DENIED", json.RawMessage(`{"reason":"MISSING_OR_INVALID"}`))
		writeError(w, r, http.StatusBadRequest, "OPERATION_REASON_REQUIRED", errors.New("operation reason must contain 3 to 500 characters"))
		return false
	}
	*r = *r.WithContext(context.WithValue(r.Context(), commerceOperationAuditKey{}, commerceOperationAudit{reason: reason}))
	return true
}

func (a *API) auditCommerceOperationSuccess(r *http.Request, actorID uuid.UUID, action, targetType string, targetID uuid.UUID) {
	auditContext, ok := r.Context().Value(commerceOperationAuditKey{}).(commerceOperationAudit)
	if !ok || auditContext.reason == "" {
		slog.Error("commerce operation audit context missing", "trace_id", traceID(r.Context()), "action", action)
		return
	}
	repository := a.securityStore()
	if repository == nil {
		return
	}
	metadata, _ := json.Marshal(map[string]string{
		"operation_reason":     auditContext.reason,
		"idempotency_key_hash": a.commerceIdempotencyAuditHash(r.Header.Get("Idempotency-Key")),
	})
	if err := repository.Audit(r.Context(), actorID, principal(r).Device.ID, action, targetType, targetID, "SUCCEEDED", traceID(r.Context()), string(metadata)); err != nil {
		logAPIFailure(r.Context(), slog.LevelError, "record commerce operation audit",
			"commerce_operation_audit_failed", err, slog.String("action", action))
	}
}

func (a *API) commerceIdempotencyAuditHash(key string) string {
	key = strings.TrimSpace(key)
	if key == "" {
		return ""
	}
	digestKey := a.commerceDigestKey()
	if len(digestKey) >= 32 {
		digest := keyedCommerceDigest(digestKey, "idempotency-audit", key)
		return hex.EncodeToString(digest[:])
	}
	digest := sha256.Sum256([]byte("TermLink idempotency audit v1\x00" + key))
	return hex.EncodeToString(digest[:])
}

// commerceAdminReadActor deliberately does not trust token roles. Read
// methods below pass the actor into Store commerce queries, whose database
// role check admits SUPER_ADMIN, ADMIN, and AUDITOR while rejecting USER.
func (a *API) commerceAdminReadActor(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return uuid.Nil, false
	}
	return principal(r).UserID, true
}

func (a *API) adminGetRegistrationPolicy(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	// GetRegistrationPolicy is intentionally public for the registration
	// form, so probe a role-enforced commerce read before returning its admin
	// representation.
	if _, err := a.Store.ListPaymentChannels(r.Context(), actorID); err != nil {
		writeCommerceError(w, r, err)
		return
	}
	policy, err := a.Store.GetRegistrationPolicy(r.Context())
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"policy": registrationPolicyView(policy)})
}

type updateRegistrationPolicyRequest struct {
	Mode            string `json:"mode"`
	ExpectedVersion int64  `json:"expected_version"`
}

func (a *API) adminUpdateRegistrationPolicy(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	var input updateRegistrationPolicyRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	if input.ExpectedVersion < 1 {
		writeError(w, r, http.StatusBadRequest, "INVALID_POLICY_VERSION", errors.New("expected_version must be positive"))
		return
	}
	policy, err := a.Store.UpdateRegistrationPolicy(r.Context(), actorID, input.Mode, input.ExpectedVersion, traceID(r.Context()))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.registration_policy.update.context", "registration_policy", uuid.Nil)
	writeJSON(w, http.StatusOK, map[string]any{"policy": registrationPolicyView(policy)})
}

type invitationBatchRequest struct {
	Name             string     `json:"name"`
	Status           string     `json:"status,omitempty"`
	DefaultMaxUses   int        `json:"default_max_uses"`
	ValidFrom        *time.Time `json:"valid_from,omitempty"`
	ValidUntil       *time.Time `json:"valid_until,omitempty"`
	Email            string     `json:"email,omitempty"`
	EmailDomain      string     `json:"email_domain,omitempty"`
	GiftPlanKey      string     `json:"gift_plan_key,omitempty"`
	GiftDurationDays int        `json:"gift_duration_days,omitempty"`
	Note             string     `json:"note,omitempty"`
}

func (a *API) createInvitationBatch(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceAdmin(w, r)
	if !ok {
		return
	}
	var input invitationBatchRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	if strings.TrimSpace(input.Email) != "" && strings.TrimSpace(input.EmailDomain) != "" {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_SCOPE", errors.New("email and email_domain are mutually exclusive"))
		return
	}
	var emailHash *[32]byte
	if strings.TrimSpace(input.Email) != "" {
		if len(a.commerceDigestKey()) < 32 {
			writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce digest key is unavailable"))
			return
		}
		email, _, err := normalizedEmail(input.Email)
		if err != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_EMAIL", err)
			return
		}
		digest := keyedCommerceDigest(a.commerceDigestKey(), "registration-email", email)
		emailHash = &digest
	}
	batch, err := a.Store.CreateInvitationBatch(r.Context(), actorID, store.InvitationBatchInput{
		Name: input.Name, Status: input.Status, DefaultMaxUses: input.DefaultMaxUses,
		ValidFrom: input.ValidFrom, ValidUntil: input.ValidUntil, EmailHash: emailHash,
		EmailDomain: input.EmailDomain, GiftPlanKey: input.GiftPlanKey,
		GiftDurationDays: input.GiftDurationDays, Note: input.Note,
	}, traceID(r.Context()))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"batch": invitationBatchView(batch)})
}

func (a *API) listInvitationBatches(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListInvitationBatches(r.Context(), actorID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, invitationBatchView(item))
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": views})
}

func (a *API) listInvitationCodes(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	batchID, err := uuid.Parse(strings.TrimSpace(r.URL.Query().Get("batch_id")))
	if err != nil || batchID == uuid.Nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_BATCH_ID", errors.New("batch_id is required"))
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListInvitationCodes(r.Context(), actorID, batchID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": invitationCodeViews(items)})
}

func (a *API) disableInvitationBatch(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceAdmin(w, r)
	if !ok {
		return
	}
	batchID, err := commercePathUUID(r, "batchId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_BATCH_ID", err)
		return
	}
	if err := a.Store.SetInvitationBatchStatus(r.Context(), actorID, batchID, "DISABLED", traceID(r.Context())); err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"disabled": true, "batch_id": batchID})
}

func (a *API) disableInvitationCode(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceAdmin(w, r)
	if !ok {
		return
	}
	codeID, err := commercePathUUID(r, "codeId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_CODE_ID", err)
		return
	}
	if err := a.Store.SetInvitationCodeStatus(r.Context(), actorID, codeID, "DISABLED", traceID(r.Context())); err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"disabled": true, "code_id": codeID})
}

func (a *API) listInvitationRedemptions(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListInvitationRedemptions(r.Context(), actorID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": invitationRedemptionViews(items)})
}

type generateCodesRequest struct {
	Count      int        `json:"count"`
	MaxUses    int        `json:"max_uses"`
	ValidFrom  *time.Time `json:"valid_from,omitempty"`
	ValidUntil *time.Time `json:"valid_until,omitempty"`
}

func (a *API) generateInvitationCodes(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	allowed, err := a.consumeCommerceRateLimit(r, commerceRateCodeGeneration)
	if err != nil {
		a.recordCommerceRateLimitFailure(r, commerceRateCodeGeneration, true)
		writeError(w, r, http.StatusServiceUnavailable, "CODE_GENERATION_UNAVAILABLE", err)
		return
	}
	if !allowed {
		a.recordCommerceRateLimitFailure(r, commerceRateCodeGeneration, false)
		writeError(w, r, http.StatusTooManyRequests, "CODE_GENERATION_RATE_LIMITED", errors.New("code generation rate limit reached"))
		return
	}
	batchID, err := commercePathUUID(r, "batchId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_BATCH_ID", err)
		return
	}
	var input generateCodesRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	plaintext, records, err := generateCommerceCodes("TLI", "invitation-code", a.commerceDigestKey(), input)
	if err != nil {
		writeCodeGenerationError(w, r, err)
		return
	}
	created, err := a.Store.AddInvitationCodes(r.Context(), actorID, batchID, records, traceID(r.Context()))
	if err != nil {
		writeCodeGenerationError(w, r, err)
		return
	}
	if len(created) != len(plaintext) {
		writeError(w, r, http.StatusInternalServerError, "CODE_GENERATION_INCOMPLETE", errors.New("persisted code count mismatch"))
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.invitation.codes.generate.context", "invitation_batch", batchID)
	writeJSON(w, http.StatusCreated, map[string]any{
		"batch_id": batchID, "items": generatedCodeViews(plaintext, created),
		"plaintext_available_once": true,
	})
}

type redemptionBatchRequest struct {
	Name                string     `json:"name"`
	PlanKey             string     `json:"plan_key"`
	CatalogVersion      int64      `json:"catalog_version"`
	DurationDays        int64      `json:"duration_days,omitempty"`
	FixedExpiresAt      *time.Time `json:"fixed_expires_at,omitempty"`
	MaxClaimsPerAccount int        `json:"max_claims_per_account"`
	StackingRule        string     `json:"stacking_rule,omitempty"`
	Status              string     `json:"status,omitempty"`
	ValidFrom           *time.Time `json:"valid_from,omitempty"`
	ValidUntil          *time.Time `json:"valid_until,omitempty"`
	Note                string     `json:"note,omitempty"`
}

func (a *API) createRedemptionBatch(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceAdmin(w, r)
	if !ok {
		return
	}
	var input redemptionBatchRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	batch, err := a.Store.CreateRedemptionBatch(r.Context(), actorID, store.RedemptionBatchInput{
		Name: input.Name, PlanKey: input.PlanKey, CatalogVersion: input.CatalogVersion,
		DurationDays: input.DurationDays, FixedExpiresAt: input.FixedExpiresAt,
		MaxClaimsPerAccount: input.MaxClaimsPerAccount, StackingRule: input.StackingRule,
		Status: input.Status, ValidFrom: input.ValidFrom, ValidUntil: input.ValidUntil, Note: input.Note,
	}, traceID(r.Context()))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"batch": redemptionBatchView(batch)})
}

func (a *API) listRedemptionBatches(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListRedemptionBatches(r.Context(), actorID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": redemptionBatchViews(items)})
}

func (a *API) listRedemptionCodes(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	batchID, err := uuid.Parse(strings.TrimSpace(r.URL.Query().Get("batch_id")))
	if err != nil || batchID == uuid.Nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REDEMPTION_BATCH_ID", errors.New("batch_id is required"))
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListRedemptionCodes(r.Context(), actorID, batchID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": redemptionCodeViews(items)})
}

func (a *API) disableRedemptionBatch(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceAdmin(w, r)
	if !ok {
		return
	}
	batchID, err := commercePathUUID(r, "batchId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REDEMPTION_BATCH_ID", err)
		return
	}
	if err := a.Store.SetRedemptionBatchStatus(r.Context(), actorID, batchID, "DISABLED", traceID(r.Context())); err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"disabled": true, "batch_id": batchID})
}

func (a *API) disableRedemptionCode(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceAdmin(w, r)
	if !ok {
		return
	}
	codeID, err := commercePathUUID(r, "codeId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REDEMPTION_CODE_ID", err)
		return
	}
	if err := a.Store.SetRedemptionCodeStatus(r.Context(), actorID, codeID, "DISABLED", traceID(r.Context())); err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"disabled": true, "code_id": codeID})
}

func (a *API) listAdminRedemptionClaims(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListRedemptionClaims(r.Context(), actorID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": accountRedemptionViews(items)})
}

func (a *API) generateRedemptionCodes(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	allowed, err := a.consumeCommerceRateLimit(r, commerceRateCodeGeneration)
	if err != nil {
		a.recordCommerceRateLimitFailure(r, commerceRateCodeGeneration, true)
		writeError(w, r, http.StatusServiceUnavailable, "CODE_GENERATION_UNAVAILABLE", err)
		return
	}
	if !allowed {
		a.recordCommerceRateLimitFailure(r, commerceRateCodeGeneration, false)
		writeError(w, r, http.StatusTooManyRequests, "CODE_GENERATION_RATE_LIMITED", errors.New("code generation rate limit reached"))
		return
	}
	batchID, err := commercePathUUID(r, "batchId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REDEMPTION_BATCH_ID", err)
		return
	}
	var input generateCodesRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	if input.ValidFrom != nil || input.ValidUntil != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_CODE_GENERATION", errors.New("redemption validity is configured on its batch"))
		return
	}
	plaintext, records, err := generateCommerceCodes("TLR", "redemption-code", a.commerceDigestKey(), input)
	if err != nil {
		writeCodeGenerationError(w, r, err)
		return
	}
	created, err := a.Store.AddRedemptionCodes(r.Context(), actorID, batchID, records, traceID(r.Context()))
	if err != nil {
		writeCodeGenerationError(w, r, err)
		return
	}
	if len(created) != len(plaintext) {
		writeError(w, r, http.StatusInternalServerError, "CODE_GENERATION_INCOMPLETE", errors.New("persisted code count mismatch"))
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.redemption.codes.generate.context", "redemption_batch", batchID)
	writeJSON(w, http.StatusCreated, map[string]any{
		"batch_id": batchID, "items": generatedRedemptionCodeViews(plaintext, created),
		"plaintext_available_once": true,
	})
}

func generateCommerceCodes(prefix, digestPurpose string, key []byte, input generateCodesRequest) ([]string, []store.CodeDigestRecord, error) {
	validDomain := prefix == "TLI" && digestPurpose == "invitation-code" || prefix == "TLR" && digestPurpose == "redemption-code"
	if !validDomain || input.Count < 1 || input.Count > maxGeneratedCodeCount || input.MaxUses < 1 || input.MaxUses > 1_000_000 || len(key) < 32 {
		return nil, nil, errInvalidCodeGeneration
	}
	if input.ValidFrom != nil && input.ValidUntil != nil && !input.ValidUntil.After(*input.ValidFrom) {
		return nil, nil, errInvalidCodeGeneration
	}
	plaintext := make([]string, 0, input.Count)
	records := make([]store.CodeDigestRecord, 0, input.Count)
	seen := make(map[[32]byte]struct{}, input.Count)
	for len(records) < input.Count {
		normalized, display, err := newCommerceCode(prefix)
		if err != nil {
			return nil, nil, err
		}
		digest := keyedCommerceDigest(key, digestPurpose, normalized)
		if _, duplicate := seen[digest]; duplicate {
			continue
		}
		seen[digest] = struct{}{}
		plaintext = append(plaintext, display)
		records = append(records, store.CodeDigestRecord{
			ID: uuid.New(), Digest: digest, Prefix: normalized[:8], MaxUses: input.MaxUses,
			ValidFrom: input.ValidFrom, ValidUntil: input.ValidUntil,
		})
	}
	return plaintext, records, nil
}

func writeCodeGenerationError(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, errInvalidCodeGeneration):
		writeError(w, r, http.StatusBadRequest, "INVALID_CODE_GENERATION", err)
	case errors.Is(err, store.ErrNotFound):
		writeError(w, r, http.StatusNotFound, "CODE_BATCH_NOT_FOUND", err)
	case errors.Is(err, store.ErrConflict):
		writeError(w, r, http.StatusConflict, "CODE_GENERATION_CONFLICT", err)
	case errors.Is(err, store.ErrDenied):
		writeError(w, r, http.StatusForbidden, "CODE_GENERATION_FORBIDDEN", err)
	default:
		writeError(w, r, http.StatusServiceUnavailable, "CODE_GENERATION_UNAVAILABLE", err)
	}
}

func newCommerceCode(prefix string) (string, string, error) {
	randomCharacterCount := redemptionCodeChars
	groupSize := 7
	if prefix == "TLI" {
		randomCharacterCount = invitationCodeChars
		groupSize = 4
	}
	randomBytes := make([]byte, randomCharacterCount)
	if _, err := rand.Read(randomBytes); err != nil {
		return "", "", err
	}
	characters := make([]byte, randomCharacterCount)
	for index, value := range randomBytes {
		characters[index] = commerceCodeAlphabet[int(value)&31]
	}
	randomPart := string(characters)
	normalized := prefix + randomPart
	var display strings.Builder
	display.Grow(len(normalized) + len(randomPart)/groupSize + 1)
	display.WriteString(prefix)
	for index, character := range randomPart {
		if index%groupSize == 0 {
			display.WriteByte('-')
		}
		display.WriteRune(character)
	}
	return normalized, display.String(), nil
}

func generatedCodeViews(plaintext []string, stored []store.InvitationCode) []map[string]any {
	items := make([]map[string]any, 0, len(stored))
	for index, item := range stored {
		items = append(items, map[string]any{
			"id": item.ID, "code": plaintext[index], "prefix": item.Prefix,
			"max_uses": item.MaxUses, "valid_from": item.ValidFrom, "valid_until": item.ValidUntil,
		})
	}
	return items
}

func generatedRedemptionCodeViews(plaintext []string, stored []store.RedemptionCode) []map[string]any {
	items := make([]map[string]any, 0, len(stored))
	for index, item := range stored {
		items = append(items, map[string]any{
			"id": item.ID, "code": plaintext[index], "prefix": item.Prefix, "max_uses": item.MaxUses,
		})
	}
	return items
}

func (a *API) listAdminCommerceOrders(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListCommerceOrders(r.Context(), actorID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": commerceOrderViews(items)})
}

func (a *API) queryAdminCommerceOrder(w http.ResponseWriter, r *http.Request) {
	if !a.requireCommerce(w, r) {
		return
	}
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	if err := decodeEmptyCommerceJSON(r); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	allowed, err := a.consumeCommerceRateLimit(r, commerceRateAdminOrderQuery)
	if err != nil {
		a.recordCommerceRateLimitFailure(r, commerceRateAdminOrderQuery, true)
		writeError(w, r, http.StatusServiceUnavailable, "PAYMENT_QUERY_UNAVAILABLE", err)
		return
	}
	if !allowed {
		a.recordCommerceRateLimitFailure(r, commerceRateAdminOrderQuery, false)
		writeError(w, r, http.StatusTooManyRequests, "PAYMENT_QUERY_RATE_LIMITED", errors.New("admin payment query rate limit reached"))
		return
	}
	orderID, err := commercePathUUID(r, "orderId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_ID", err)
		return
	}
	order, err := a.Store.GetOrderByID(r.Context(), orderID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	outcome, err := a.Commerce.QueryAndFulfill(r.Context(), order.AccountID, order.ID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.order.query", "order", order.ID)
	writeJSON(w, http.StatusOK, paymentOutcomeView(outcome))
}

func (a *API) retryAdminOrderFulfillment(w http.ResponseWriter, r *http.Request) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return
	}
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	if err := decodeEmptyCommerceJSON(r); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	allowed, err := a.consumeCommerceRateLimit(r, commerceRateAdminOrderQuery)
	if err != nil {
		a.recordCommerceRateLimitFailure(r, commerceRateAdminOrderQuery, true)
		writeError(w, r, http.StatusServiceUnavailable, "FULFILLMENT_RETRY_UNAVAILABLE", err)
		return
	}
	if !allowed {
		a.recordCommerceRateLimitFailure(r, commerceRateAdminOrderQuery, false)
		writeError(w, r, http.StatusTooManyRequests, "FULFILLMENT_RETRY_RATE_LIMITED", errors.New("fulfillment retry rate limit reached"))
		return
	}
	orderID, err := commercePathUUID(r, "orderId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_ID", err)
		return
	}
	current, err := a.Store.GetOrderByID(r.Context(), orderID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	if current.Status != "PAID" && current.Status != "FULFILLING" && current.Status != "FULFILLED" {
		writeError(w, r, http.StatusConflict, "ORDER_NOT_READY_FOR_FULFILLMENT", store.ErrConflict)
		return
	}
	order, entitlement, err := a.Store.FulfillOrder(r.Context(), orderID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.order.fulfillment.retry", "order", order.ID)
	writeJSON(w, http.StatusOK, map[string]any{"order": commerceOrderView(order), "entitlement": entitlement, "fulfilled": order.Status == "FULFILLED"})
}

func (a *API) listAdminCommerceTransactions(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListCommerceTransactions(r.Context(), actorID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": commerceTransactionViews(items)})
}

type createRefundRequest struct {
	AmountMinor int64  `json:"amount_minor"`
	Reason      string `json:"reason"`
}

func (a *API) createCommerceRefund(w http.ResponseWriter, r *http.Request) {
	if !a.requireCommerce(w, r) {
		return
	}
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	orderID, err := commercePathUUID(r, "orderId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_ID", err)
		return
	}
	var input createRefundRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	outcome, err := a.Commerce.CreateRefund(r.Context(), commerce.CreateRefundRequest{
		ActorID: actorID, OrderID: orderID, AmountMinor: input.AmountMinor,
		Reason: input.Reason, TraceID: traceID(r.Context()),
	})
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.refund.create.context", "refund", outcome.Refund.ID)
	writeJSON(w, http.StatusAccepted, map[string]any{
		"refund": commerceRefundView(outcome.Refund), "approval_required": true,
	})
}

type confirmRefundRequest struct {
	ExpectedVersion int64 `json:"expected_version"`
}

func (a *API) confirmCommerceRefund(w http.ResponseWriter, r *http.Request) {
	if !a.requireCommerce(w, r) {
		return
	}
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	refundID, err := commercePathUUID(r, "refundId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REFUND_ID", err)
		return
	}
	var input confirmRefundRequest
	if err := decodeJSON(r, &input); err != nil || input.ExpectedVersion < 1 {
		if err == nil {
			err = errors.New("expected_version must be a positive integer")
		}
		writeError(w, r, http.StatusBadRequest, "INVALID_REFUND_CONFIRMATION", err)
		return
	}
	outcome, err := a.Commerce.ConfirmRefund(r.Context(), commerce.ConfirmRefundRequest{
		ActorID: actorID, RefundID: refundID, ExpectedVersion: input.ExpectedVersion, TraceID: traceID(r.Context()),
	})
	if err != nil {
		if outcome.Refund.ID == uuid.Nil {
			writeCommerceError(w, r, err)
			return
		}
		a.auditCommerceOperationSuccess(r, actorID, "commerce.refund.confirm.context", "refund", outcome.Refund.ID)
		status := http.StatusAccepted
		response := map[string]any{
			"refund": commerceRefundView(outcome.Refund), "provider_status": outcome.ProviderStatus,
			"provider_confirmation_pending": outcome.Refund.Status != "FAILED",
		}
		if outcome.Refund.Status == "FAILED" {
			status = http.StatusUnprocessableEntity
			response["error"] = map[string]string{
				"code": "REFUND_PROVIDER_REJECTED", "message_key": "errors.refund_provider_rejected", "trace_id": traceID(r.Context()),
			}
		}
		writeJSON(w, status, response)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.refund.confirm.context", "refund", outcome.Refund.ID)
	writeJSON(w, http.StatusOK, map[string]any{
		"refund": commerceRefundView(outcome.Refund), "provider_status": outcome.ProviderStatus,
		"provider_confirmation_pending": outcome.Refund.Status == "CREATED" || outcome.Refund.Status == "PENDING",
	})
}

func (a *API) listCommerceRefunds(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListRefunds(r.Context(), actorID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, commerceRefundView(item))
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": views})
}

func (a *API) listPaymentNotifications(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListPaymentNotifications(r.Context(), actorID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, paymentNotificationView(item))
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": views})
}

func (a *API) listReconciliationRuns(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := commerceLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListReconciliationRuns(r.Context(), actorID, limit)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": reconciliationRunViews(items)})
}

type reconciliationRunRequest struct {
	Provider    string    `json:"provider"`
	PeriodStart time.Time `json:"period_start"`
	PeriodEnd   time.Time `json:"period_end"`
}

func (a *API) createReconciliationRun(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	var input reconciliationRunRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	if input.PeriodStart.IsZero() || !input.PeriodEnd.After(input.PeriodStart) || input.PeriodEnd.Sub(input.PeriodStart) > 366*24*time.Hour {
		writeError(w, r, http.StatusBadRequest, "INVALID_RECONCILIATION_PERIOD", errors.New("invalid reconciliation period"))
		return
	}
	run, err := a.Store.CreateReconciliationRun(r.Context(), actorID, input.Provider, input.PeriodStart.UTC(), input.PeriodEnd.UTC(), traceID(r.Context()))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.reconciliation.create.context", "reconciliation", run.ID)
	writeJSON(w, http.StatusAccepted, map[string]any{"run": reconciliationRunView(run)})
}

type paymentChannelRequest struct {
	Provider string `json:"provider"`
	Scene    string `json:"scene"`
}

func (a *API) listPaymentChannels(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	items, err := a.Store.ListPaymentChannels(r.Context(), actorID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": paymentChannelViews(items)})
}

func (a *API) createPaymentChannel(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	var input paymentChannelRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	channel, err := a.Store.EnsurePaymentChannel(r.Context(), actorID, input.Provider, input.Scene, traceID(r.Context()))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.payment_channel.create.context", "payment_channel", channel.ID)
	writeJSON(w, http.StatusCreated, map[string]any{"channel": paymentChannelView(channel)})
}

func (a *API) listPaymentChannelVersions(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	channelID, err := commercePathUUID(r, "channelId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PAYMENT_CHANNEL_ID", err)
		return
	}
	items, err := a.Store.ListPaymentChannelVersions(r.Context(), actorID, channelID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, paymentChannelVersionView(item))
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": views})
}

type paymentChannelVersionRequest struct {
	Environment  string          `json:"environment"`
	PublicConfig json.RawMessage `json:"public_config"`
	SecretRefs   json.RawMessage `json:"secret_refs"`
}

func (a *API) createPaymentChannelVersion(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	channelID, err := commercePathUUID(r, "channelId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PAYMENT_CHANNEL_ID", err)
		return
	}
	var input paymentChannelVersionRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	version, err := a.Store.CreatePaymentChannelVersion(r.Context(), actorID, channelID, store.PaymentChannelVersionInput{
		Environment: input.Environment, PublicConfig: input.PublicConfig, SecretRefs: input.SecretRefs,
	}, traceID(r.Context()))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.payment_channel.version.create.context", "payment_channel_version", version.ID)
	writeJSON(w, http.StatusCreated, map[string]any{"version": paymentChannelVersionView(version)})
}

type validatePaymentChannelRequest struct {
	VersionID uuid.UUID `json:"version_id"`
}

func (a *API) validatePaymentChannelVersion(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	channelID, err := commercePathUUID(r, "channelId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PAYMENT_CHANNEL_ID", err)
		return
	}
	var input validatePaymentChannelRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	if input.VersionID == uuid.Nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PAYMENT_CHANNEL_VERSION_ID", errors.New("version_id is required"))
		return
	}
	if a.Commerce == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce service is unavailable"))
		return
	}
	channels, err := a.Store.ListPaymentChannels(r.Context(), actorID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	var channel store.PaymentChannel
	for _, candidate := range channels {
		if candidate.ID == channelID {
			channel = candidate
			break
		}
	}
	if channel.ID == uuid.Nil {
		writeError(w, r, http.StatusNotFound, "PAYMENT_CHANNEL_NOT_FOUND", store.ErrNotFound)
		return
	}
	versions, err := a.Store.ListPaymentChannelVersions(r.Context(), actorID, channelID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	var draft store.PaymentChannelVersion
	for _, candidate := range versions {
		if candidate.ID == input.VersionID {
			draft = candidate
			break
		}
	}
	if draft.ID == uuid.Nil || draft.ChannelID != channelID {
		writeError(w, r, http.StatusNotFound, "PAYMENT_CHANNEL_VERSION_NOT_FOUND", store.ErrNotFound)
		return
	}
	if draft.Status != "DRAFT" {
		writeError(w, r, http.StatusConflict, "PAYMENT_CHANNEL_VERSION_NOT_DRAFT", store.ErrConflict)
		return
	}
	// Authorize the SUPER_ADMIN-only transition in the database before the
	// service resolves any secret reference. This prevents lower roles from
	// using validation timing as a Docker Secret existence oracle.
	if _, err := a.Store.ValidatePaymentChannelVersion(r.Context(), actorID, draft.ID, false, "LOCAL_VALIDATION_PENDING", traceID(r.Context())); err != nil {
		writeCommerceError(w, r, err)
		return
	}
	if err := a.Commerce.ValidatePaymentChannelVersion(r.Context(), channel, draft); err != nil {
		// Persist only a stable result category. Never store a secret reference,
		// parser detail, certificate body, or provider response in the summary.
		_, recordErr := a.Store.ValidatePaymentChannelVersion(r.Context(), actorID, draft.ID, false, "LOCAL_CONFIGURATION_INVALID", traceID(r.Context()))
		if recordErr != nil {
			logAPIFailure(r.Context(), slog.LevelError, "record failed payment channel validation",
				"payment_channel_validation_result_record_failed", recordErr, slog.String("version_id", draft.ID.String()))
		}
		writeError(w, r, http.StatusUnprocessableEntity, "PAYMENT_CHANNEL_VALIDATION_FAILED", err)
		return
	}
	if err := a.Commerce.ProbePaymentChannelVersion(r.Context(), channel, draft); err != nil {
		_, recordErr := a.Store.ValidatePaymentChannelVersion(r.Context(), actorID, draft.ID, false, "PROVIDER_PROBE_FAILED", traceID(r.Context()))
		if recordErr != nil {
			logAPIFailure(r.Context(), slog.LevelError, "record failed payment channel probe",
				"payment_channel_probe_result_record_failed", recordErr, slog.String("version_id", draft.ID.String()))
		}
		writeError(w, r, http.StatusBadGateway, "PAYMENT_CHANNEL_PROBE_FAILED", err)
		return
	}
	version, err := a.Store.ValidatePaymentChannelVersion(r.Context(), actorID, draft.ID, true, "LOCAL_CONFIGURATION_AND_PROVIDER_PROBE_VALID", traceID(r.Context()))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.payment_channel.version.validate.context", "payment_channel_version", version.ID)
	writeJSON(w, http.StatusOK, map[string]any{"version": paymentChannelVersionView(version)})
}

type activatePaymentChannelRequest struct {
	VersionID uuid.UUID `json:"version_id"`
}

func (a *API) activatePaymentChannelVersion(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	channelID, err := commercePathUUID(r, "channelId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PAYMENT_CHANNEL_ID", err)
		return
	}
	var input activatePaymentChannelRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	if input.VersionID == uuid.Nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PAYMENT_CHANNEL_VERSION_ID", errors.New("version_id is required"))
		return
	}
	channel, err := a.Store.ActivatePaymentChannelVersion(r.Context(), actorID, channelID, input.VersionID, traceID(r.Context()))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.payment_channel.version.activate.context", "payment_channel", channel.ID)
	writeJSON(w, http.StatusOK, map[string]any{"channel": paymentChannelView(channel)})
}

func (a *API) disablePaymentChannel(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	channelID, err := commercePathUUID(r, "channelId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PAYMENT_CHANNEL_ID", err)
		return
	}
	channel, err := a.Store.DisablePaymentChannel(r.Context(), actorID, channelID, traceID(r.Context()))
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.payment_channel.disable.context", "payment_channel", channel.ID)
	writeJSON(w, http.StatusOK, map[string]any{"channel": paymentChannelView(channel)})
}

func registrationPolicyView(item store.RegistrationPolicy) map[string]any {
	return map[string]any{
		"mode": item.Mode, "version": item.Version, "updated_by": item.UpdatedBy, "updated_at": item.UpdatedAt,
	}
}

func redemptionClaimView(item store.RedemptionClaim) map[string]any {
	return map[string]any{
		"id": item.ID, "code_id": item.CodeID, "account_id": item.AccountID,
		"grant_id": item.GrantID, "claimed_at": item.ClaimedAt,
	}
}

func accountRedemptionView(item store.AccountRedemption) map[string]any {
	return map[string]any{
		"claim_id": item.ClaimID, "code_id": item.CodeID, "grant_id": item.GrantID, "account_id": item.AccountID,
		"code_prefix": item.CodePrefix, "plan_key": item.PlanKey,
		"catalog_version": item.CatalogVersion, "claimed_at": item.ClaimedAt,
		"starts_at": item.StartsAt, "ends_at": item.EndsAt, "status": item.Status,
	}
}

func accountRedemptionViews(items []store.AccountRedemption) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, accountRedemptionView(item))
	}
	return views
}

func invitationRedemptionView(item store.InvitationRedemption) map[string]any {
	return map[string]any{
		"id": item.ID, "code_id": item.CodeID, "registration_attempt_id": item.AttemptID,
		"user_id": item.UserID, "state": item.State, "reserved_until": item.ReservedUntil,
		"provider_accepted_at": item.ProviderAcceptedAt, "redeemed_at": item.RedeemedAt,
		"released_at": item.ReleasedAt, "created_at": item.CreatedAt,
	}
}

func invitationRedemptionViews(items []store.InvitationRedemption) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, invitationRedemptionView(item))
	}
	return views
}

func paymentOutcomeView(outcome commerce.PaymentOutcome) map[string]any {
	return map[string]any{
		"order": commerceOrderView(outcome.Order), "provider_status": outcome.ProviderStatus,
		"fulfilled": outcome.Fulfilled,
	}
}

func invitationBatchView(item store.InvitationBatch) map[string]any {
	return map[string]any{
		"id": item.ID, "name": item.Name, "status": item.Status,
		"default_max_uses": item.DefaultMaxUses, "valid_from": item.ValidFrom,
		"valid_until": item.ValidUntil, "email_domain": item.EmailDomain,
		"email_restricted": len(item.EmailHash) != 0, "gift_plan_key": item.GiftPlanKey,
		"gift_duration_days": item.GiftDurationDays, "note": item.Note,
		"created_by": item.CreatedBy, "created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
}

func invitationCodeView(item store.InvitationCode) map[string]any {
	return map[string]any{
		"id": item.ID, "batch_id": item.BatchID, "prefix": item.Prefix,
		"status": item.Status, "max_uses": item.MaxUses, "used_count": item.UsedCount,
		"valid_from": item.ValidFrom, "valid_until": item.ValidUntil,
		"created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
}

func invitationCodeViews(items []store.InvitationCode) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, invitationCodeView(item))
	}
	return views
}

func redemptionBatchView(item store.RedemptionBatch) map[string]any {
	return map[string]any{
		"id": item.ID, "name": item.Name, "plan_key": item.PlanKey,
		"catalog_version": item.CatalogVersion, "duration_days": item.DurationDays,
		"fixed_expires_at": item.FixedExpiresAt, "max_claims_per_account": item.MaxClaimsPerAccount,
		"stacking_rule": item.StackingRule, "status": item.Status,
		"valid_from": item.ValidFrom, "valid_until": item.ValidUntil, "note": item.Note,
		"created_by": item.CreatedBy, "created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
}

func redemptionBatchViews(items []store.RedemptionBatch) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, redemptionBatchView(item))
	}
	return views
}

func redemptionCodeView(item store.RedemptionCode) map[string]any {
	return map[string]any{
		"id": item.ID, "batch_id": item.BatchID, "prefix": item.Prefix,
		"status": item.Status, "max_uses": item.MaxUses, "used_count": item.UsedCount,
		"created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
}

func redemptionCodeViews(items []store.RedemptionCode) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, redemptionCodeView(item))
	}
	return views
}

func commerceOrderView(item store.Order) map[string]any {
	return map[string]any{
		"id": item.ID, "order_no": item.OrderNo, "account_id": item.AccountID,
		"plan_key": item.PlanKey, "catalog_version": item.CatalogVersion,
		"provider": item.Provider, "payment_scene": item.PaymentScene,
		"amount_minor": item.AmountMinor, "currency": item.Currency, "status": item.Status,
		"expires_at": item.ExpiresAt, "paid_at": item.PaidAt, "fulfilled_at": item.FulfilledAt,
		"closed_at": item.ClosedAt, "created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
}

func commerceOrderViews(items []store.Order) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, commerceOrderView(item))
	}
	return views
}

func commerceTransactionView(item store.PaymentTransaction) map[string]any {
	return map[string]any{
		"id": item.ID, "order_id": item.OrderID, "provider": item.Provider,
		"status": item.Status, "amount_minor": item.AmountMinor, "currency": item.Currency,
		"failure_code": item.FailureCode, "requested_at": item.RequestedAt,
		"paid_at": item.PaidAt, "updated_at": item.UpdatedAt,
	}
}

func commerceTransactionViews(items []store.PaymentTransaction) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, commerceTransactionView(item))
	}
	return views
}

func commerceRefundView(item store.Refund) map[string]any {
	return map[string]any{
		"id": item.ID, "refund_no": item.RefundNo, "order_id": item.OrderID,
		"transaction_id": item.TransactionID, "amount_minor": item.AmountMinor,
		"reason": item.Reason, "status": item.Status, "failure_code": item.FailureCode, "requested_by": item.RequestedBy,
		"version": item.Version, "approved_by": item.ApprovedBy, "approved_at": item.ApprovedAt,
		"requested_at": item.RequestedAt, "approval_expires_at": item.ApprovalExpiresAt,
		"completed_at": item.CompletedAt, "updated_at": item.UpdatedAt,
	}
}

func paymentNotificationView(item store.PaymentNotification) map[string]any {
	return map[string]any{
		"id": item.ID, "provider": item.Provider, "order_no": item.OrderNo,
		"signature_valid": item.SignatureValid, "processing_state": item.ProcessingState,
		"failure_code": item.FailureCode, "received_at": item.ReceivedAt, "processed_at": item.ProcessedAt,
	}
}

func reconciliationRunView(item store.ReconciliationRun) map[string]any {
	return map[string]any{
		"id": item.ID, "provider": item.Provider, "period_start": item.PeriodStart,
		"period_end": item.PeriodEnd, "status": item.Status, "result_summary": safeReconciliationSummaryView(item.ResultSummary),
		"requested_by": item.RequestedBy, "started_at": item.StartedAt,
		"completed_at": item.CompletedAt, "created_at": item.CreatedAt,
	}
}

func reconciliationRunViews(items []store.ReconciliationRun) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, reconciliationRunView(item))
	}
	return views
}

func paymentChannelView(item store.PaymentChannel) map[string]any {
	return map[string]any{
		"id": item.ID, "provider": item.Provider, "scene": item.Scene, "enabled": item.Enabled,
		"active_version_id": item.ActiveVersionID, "health_status": item.HealthStatus,
		"last_webhook_at": item.LastWebhookAt, "last_error_code": item.LastErrorCode,
		"created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
}

func paymentChannelViews(items []store.PaymentChannel) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, paymentChannelView(item))
	}
	return views
}

func paymentChannelVersionView(item store.PaymentChannelVersion) map[string]any {
	return map[string]any{
		"id": item.ID, "channel_id": item.ChannelID, "version": item.Version,
		"environment": item.Environment, "public_config": item.PublicConfig,
		"secret_refs_configured": len(bytes.TrimSpace(item.SecretRefs)) > 2,
		"status":                 item.Status, "validated_at": item.ValidatedAt,
		"validation_summary": item.ValidationSummary, "created_by": item.CreatedBy,
		"created_at": item.CreatedAt,
	}
}

func writeCommerceError(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, store.ErrInvitationRequired):
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_REQUIRED", err)
	case errors.Is(err, store.ErrInvitationInvalid):
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_INVALID", err)
	case errors.Is(err, store.ErrInvitationExpired):
		writeError(w, r, http.StatusGone, "INVITATION_EXPIRED", err)
	case errors.Is(err, store.ErrInvitationExhausted):
		writeError(w, r, http.StatusConflict, "INVITATION_EXHAUSTED", err)
	case errors.Is(err, store.ErrInvitationEmailMismatch):
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_EMAIL_MISMATCH", err)
	case errors.Is(err, store.ErrRedemptionInvalid):
		writeError(w, r, http.StatusUnprocessableEntity, "REDEMPTION_INVALID", err)
	case errors.Is(err, store.ErrRedemptionExpired):
		writeError(w, r, http.StatusGone, "REDEMPTION_EXPIRED", err)
	case errors.Is(err, store.ErrRedemptionExhausted):
		writeError(w, r, http.StatusConflict, "REDEMPTION_EXHAUSTED", err)
	case errors.Is(err, store.ErrRedemptionAlreadyUsed):
		writeError(w, r, http.StatusConflict, "REDEMPTION_ALREADY_USED", err)
	case errors.Is(err, store.ErrRedemptionNotApplicable):
		writeError(w, r, http.StatusUnprocessableEntity, "REDEMPTION_NOT_APPLICABLE", err)
	case errors.Is(err, store.ErrRedemptionConflict):
		writeError(w, r, http.StatusConflict, "REDEMPTION_CONFLICT", err)
	case errors.Is(err, store.ErrRefundApprovalExpired):
		writeError(w, r, http.StatusGone, "REFUND_APPROVAL_EXPIRED", err)
	case errors.Is(err, store.ErrRefundVersionConflict):
		writeError(w, r, http.StatusConflict, "REFUND_VERSION_CONFLICT", err)
	case errors.Is(err, commerce.ErrInvalidRequest), errors.Is(err, payment.ErrInvalidRequest):
		writeError(w, r, http.StatusBadRequest, "INVALID_COMMERCE_REQUEST", err)
	case errors.Is(err, commerce.ErrInvalidChannelConfig), errors.Is(err, commerce.ErrInvalidSecret),
		errors.Is(err, commerce.ErrUnsafeSecretReference), errors.Is(err, commerce.ErrSecretTooLarge):
		writeError(w, r, http.StatusServiceUnavailable, "PAYMENT_CHANNEL_CONFIGURATION_INVALID", err)
	case errors.Is(err, commerce.ErrGatewayValidationUnavailable):
		writeError(w, r, http.StatusServiceUnavailable, "PAYMENT_CHANNEL_VALIDATION_UNAVAILABLE", err)
	case errors.Is(err, commerce.ErrPaymentChannelUnavailable), errors.Is(err, store.ErrPaymentChannelUnavailable):
		writeError(w, r, http.StatusServiceUnavailable, "PAYMENT_CHANNEL_UNAVAILABLE", err)
	case errors.Is(err, commerce.ErrPaymentResultMismatch), errors.Is(err, store.ErrOrderAmountMismatch):
		writeError(w, r, http.StatusConflict, "PAYMENT_RESULT_MISMATCH", err)
	case errors.Is(err, store.ErrOrderSettlementMismatch):
		writeError(w, r, http.StatusConflict, "PAYMENT_SETTLEMENT_CONFLICT", err)
	case errors.Is(err, commerce.ErrPaymentTransactionMissing):
		writeError(w, r, http.StatusConflict, "PAYMENT_TRANSACTION_MISSING", err)
	case errors.Is(err, commerce.ErrPaymentStateNotProgressable), errors.Is(err, store.ErrOrderNotPayable):
		writeError(w, r, http.StatusConflict, "ORDER_NOT_PAYABLE", err)
	case errors.Is(err, payment.ErrPayloadTooLarge):
		writeError(w, r, http.StatusRequestEntityTooLarge, "PAYMENT_PAYLOAD_TOO_LARGE", err)
	case errors.Is(err, payment.ErrInvalidSignature), errors.Is(err, payment.ErrReplay), errors.Is(err, payment.ErrMalformedPayload):
		writeError(w, r, http.StatusUnprocessableEntity, "PAYMENT_PROVIDER_RESPONSE_INVALID", err)
	case errors.Is(err, payment.ErrProviderRejected):
		writeError(w, r, http.StatusBadGateway, "PAYMENT_PROVIDER_REJECTED", err)
	case errors.Is(err, payment.ErrUnsupported):
		writeError(w, r, http.StatusUnprocessableEntity, "PAYMENT_OPERATION_UNSUPPORTED", err)
	default:
		writeStoreError(w, r, err)
	}
}
