package api

import (
	"crypto/sha256"
	"errors"
	"net/http"
	"strings"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type adminCloseCommerceOrderRequest struct {
	ReasonCode string `json:"reason_code"`
}

// closeAdminCommerceOrder owns durable idempotency inside the Store
// transaction. It must be registered as an exact authenticated route outside
// the generic API idempotency wrapper; otherwise the outer wrapper would claim
// the same key before the atomic order transition can claim it.
func (a *API) closeAdminCommerceOrder(w http.ResponseWriter, r *http.Request) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_UNAVAILABLE", errors.New("commerce store is unavailable"))
		return
	}
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	device := principal(r).Device
	if device.ID == uuid.Nil || device.TrustState != "TRUSTED" {
		writeError(w, r, http.StatusForbidden, "TRUSTED_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	orderID, err := commercePathUUID(r, "orderId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_ID", err)
		return
	}
	key, err := commerceIdempotencyKey(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", err)
		return
	}
	var input adminCloseCommerceOrderRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ADMIN_ORDER_CLOSE", err)
		return
	}
	reasonCode := strings.ToUpper(strings.TrimSpace(input.ReasonCode))
	if !store.ValidAdminOrderCloseReasonCode(reasonCode) {
		writeError(w, r, http.StatusBadRequest, "INVALID_ADMIN_ORDER_CLOSE_REASON", store.ErrDenied)
		return
	}
	requestHash := adminCloseOrderRequestHash(actorID, orderID, reasonCode, strings.TrimSpace(r.Header.Get(commerceOperationReasonHeader)))
	result, err := a.Store.AdminCloseUnpaidOrder(r.Context(), store.AdminCloseOrderInput{
		ActorID: actorID, ActorDeviceID: device.ID, OrderID: orderID, ReasonCode: reasonCode,
		IdempotencyKey: key, RequestHash: requestHash, TraceID: traceID(r.Context()),
	})
	if err != nil {
		switch {
		case errors.Is(err, store.ErrAdminOrderNotClosable):
			writeError(w, r, http.StatusConflict, "ORDER_NOT_CLOSABLE", err)
		case errors.Is(err, store.ErrAdminCloseIdempotencyConflict):
			writeError(w, r, http.StatusConflict, "IDEMPOTENCY_KEY_REUSED", err)
		default:
			writeCommerceError(w, r, err)
		}
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"order": commerceOrderView(result.Order), "closed": true, "replayed": result.Replayed,
	})
}

func adminCloseOrderRequestHash(actorID, orderID uuid.UUID, reasonCode, operationReason string) [32]byte {
	return sha256.Sum256([]byte("TermLink admin order close v1\x00" + actorID.String() + "\x00" +
		orderID.String() + "\x00" + reasonCode + "\x00" + operationReason))
}
