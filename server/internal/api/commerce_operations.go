package api

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type commerceReportingStore interface {
	GetCommerceCatalogDraft(context.Context, uuid.UUID) (*store.CommerceCatalogDraft, error)
	ListCommerceOrderReport(context.Context, uuid.UUID, store.CommerceOrderReportFilter) (store.CommerceOrderReportPage, error)
	ExportCommerceOrders(context.Context, uuid.UUID, store.CommerceOrderExportFilter) (store.CommerceOrderExport, error)
	ListCommerceConsistencyDetails(context.Context, uuid.UUID, store.CommerceConsistencyFilter) (store.CommerceConsistencyPage, error)
}

func (a *API) reportingStore() commerceReportingStore {
	if a.commerceReporting != nil {
		return a.commerceReporting
	}
	if a.Store == nil {
		return nil
	}
	return a.Store
}

func (a *API) commerceReportingActor(w http.ResponseWriter, r *http.Request) (commerceReportingStore, uuid.UUID, bool) {
	repository := a.reportingStore()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_REPORTING_UNAVAILABLE", errors.New("commerce reporting store is unavailable"))
		return nil, uuid.Nil, false
	}
	return repository, principal(r).UserID, true
}

// commerceHighRiskOperation protects existing handlers that predate the
// commerce step-up policy. The wrapped handler retains its own database role
// check; only successful responses receive the additional reason/key audit.
func (a *API) commerceHighRiskOperation(action, targetType string, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		actorID, ok := a.requireCommerceStepUp(w, r)
		if !ok {
			return
		}
		capture := &captureWriter{ResponseWriter: w, status: http.StatusOK}
		next.ServeHTTP(capture, r)
		if capture.status >= 200 && capture.status < 300 {
			a.auditCommerceOperationSuccess(r, actorID, action, targetType, uuid.Nil)
		}
	})
}

func (a *API) getCommerceCatalogDraft(w http.ResponseWriter, r *http.Request) {
	repository, actorID, ok := a.commerceReportingActor(w, r)
	if !ok {
		return
	}
	draft, err := repository.GetCommerceCatalogDraft(r.Context(), actorID)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"draft": commerceCatalogDraftView(draft)})
}

func (a *API) listCommerceOrderReport(w http.ResponseWriter, r *http.Request) {
	repository, actorID, ok := a.commerceReportingActor(w, r)
	if !ok {
		return
	}
	filter, err := commerceOrderReportFilter(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_REPORT_FILTER", err)
		return
	}
	page, err := repository.ListCommerceOrderReport(r.Context(), actorID, filter)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, commerceOrderReportPageView(page))
}

type commerceOrderExportRequest struct {
	OrderNo     string     `json:"order_no,omitempty"`
	AccountID   *uuid.UUID `json:"account_id,omitempty"`
	Provider    string     `json:"provider,omitempty"`
	Status      string     `json:"status,omitempty"`
	CreatedFrom *time.Time `json:"created_from"`
	CreatedTo   *time.Time `json:"created_to"`
	Limit       int        `json:"limit,omitempty"`
}

func (a *API) exportCommerceOrders(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	repository := a.reportingStore()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "COMMERCE_REPORTING_UNAVAILABLE", errors.New("commerce reporting store is unavailable"))
		return
	}
	var input commerceOrderExportRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_EXPORT_FILTER", err)
		return
	}
	if err := validateCommerceOrderExportRequest(input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ORDER_EXPORT_FILTER", err)
		return
	}
	result, err := repository.ExportCommerceOrders(r.Context(), actorID, store.CommerceOrderExportFilter{
		OrderNo: input.OrderNo, AccountID: input.AccountID, Provider: input.Provider,
		Status: input.Status, CreatedFrom: input.CreatedFrom, CreatedTo: input.CreatedTo, Limit: input.Limit,
	})
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	a.auditCommerceOperationSuccess(r, actorID, "commerce.order.export", "order_export", uuid.Nil)
	w.Header().Set("Content-Disposition", `attachment; filename="termlink-order-export.json"`)
	writeJSON(w, http.StatusOK, commerceOrderExportView(result))
}

func (a *API) listCommerceConsistency(w http.ResponseWriter, r *http.Request) {
	repository, actorID, ok := a.commerceReportingActor(w, r)
	if !ok {
		return
	}
	filter, err := commerceConsistencyFilter(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_CONSISTENCY_FILTER", err)
		return
	}
	page, err := repository.ListCommerceConsistencyDetails(r.Context(), actorID, filter)
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, commerceConsistencyPageView(page))
}

func commerceOrderReportFilter(r *http.Request) (store.CommerceOrderReportFilter, error) {
	accountID, err := optionalCommerceUUID(r, "account_id")
	if err != nil {
		return store.CommerceOrderReportFilter{}, err
	}
	createdFrom, err := optionalCommerceTime(r, "created_from")
	if err != nil {
		return store.CommerceOrderReportFilter{}, err
	}
	createdTo, err := optionalCommerceTime(r, "created_to")
	if err != nil {
		return store.CommerceOrderReportFilter{}, err
	}
	if err := validateCommerceTimeRange(createdFrom, createdTo, 93*24*time.Hour); err != nil {
		return store.CommerceOrderReportFilter{}, err
	}
	afterCreatedAt, err := optionalCommerceTime(r, "after_created_at")
	if err != nil {
		return store.CommerceOrderReportFilter{}, err
	}
	afterID, err := optionalCommerceUUID(r, "after_id")
	if err != nil {
		return store.CommerceOrderReportFilter{}, err
	}
	if (afterCreatedAt == nil) != (afterID == nil) {
		return store.CommerceOrderReportFilter{}, errors.New("complete order report cursor is required")
	}
	limit, err := optionalCommerceLimit(r, 200)
	if err != nil {
		return store.CommerceOrderReportFilter{}, err
	}
	return store.CommerceOrderReportFilter{
		OrderNo: r.URL.Query().Get("order_no"), AccountID: accountID,
		Provider: r.URL.Query().Get("provider"), Status: r.URL.Query().Get("status"),
		CreatedFrom: createdFrom, CreatedTo: createdTo, AfterCreatedAt: afterCreatedAt,
		AfterID: afterID, Limit: limit,
	}, nil
}

func commerceConsistencyFilter(r *http.Request) (store.CommerceConsistencyFilter, error) {
	accountID, err := optionalCommerceUUID(r, "account_id")
	if err != nil {
		return store.CommerceConsistencyFilter{}, err
	}
	createdFrom, err := optionalCommerceTime(r, "created_from")
	if err != nil {
		return store.CommerceConsistencyFilter{}, err
	}
	createdTo, err := optionalCommerceTime(r, "created_to")
	if err != nil {
		return store.CommerceConsistencyFilter{}, err
	}
	if err := validateCommerceTimeRange(createdFrom, createdTo, 31*24*time.Hour); err != nil {
		return store.CommerceConsistencyFilter{}, err
	}
	afterAt, err := optionalCommerceTime(r, "after_occurred_at")
	if err != nil {
		return store.CommerceConsistencyFilter{}, err
	}
	afterEntityID, err := optionalCommerceUUID(r, "after_entity_id")
	if err != nil {
		return store.CommerceConsistencyFilter{}, err
	}
	afterCategory := strings.TrimSpace(r.URL.Query().Get("after_category"))
	var cursor *store.CommerceConsistencyCursor
	if afterAt != nil || afterEntityID != nil || afterCategory != "" {
		if afterAt == nil || afterEntityID == nil || afterCategory == "" {
			return store.CommerceConsistencyFilter{}, errors.New("complete consistency cursor is required")
		}
		cursor = &store.CommerceConsistencyCursor{OccurredAt: *afterAt, EntityID: *afterEntityID, Category: afterCategory}
	}
	limit, err := optionalCommerceLimit(r, 200)
	if err != nil {
		return store.CommerceConsistencyFilter{}, err
	}
	return store.CommerceConsistencyFilter{
		AccountID: accountID, Provider: r.URL.Query().Get("provider"), Category: r.URL.Query().Get("category"),
		CreatedFrom: createdFrom, CreatedTo: createdTo, After: cursor, Limit: limit,
	}, nil
}

func optionalCommerceUUID(r *http.Request, name string) (*uuid.UUID, error) {
	raw := strings.TrimSpace(r.URL.Query().Get(name))
	if raw == "" {
		return nil, nil
	}
	value, err := uuid.Parse(raw)
	if err != nil || value == uuid.Nil {
		return nil, errors.New("invalid " + name)
	}
	return &value, nil
}

func optionalCommerceTime(r *http.Request, name string) (*time.Time, error) {
	raw := strings.TrimSpace(r.URL.Query().Get(name))
	if raw == "" {
		return nil, nil
	}
	value, err := time.Parse(time.RFC3339Nano, raw)
	if err != nil {
		return nil, errors.New("invalid " + name)
	}
	value = value.UTC()
	return &value, nil
}

func optionalCommerceLimit(r *http.Request, maximum int) (int, error) {
	raw := strings.TrimSpace(r.URL.Query().Get("limit"))
	if raw == "" {
		return 0, nil
	}
	value, err := strconv.Atoi(raw)
	if err != nil || value < 1 || value > maximum {
		return 0, errors.New("invalid limit")
	}
	return value, nil
}

func validateCommerceTimeRange(from, to *time.Time, maximum time.Duration) error {
	if (from == nil) != (to == nil) {
		return errors.New("created_from and created_to must be supplied together")
	}
	if from == nil {
		return nil
	}
	if from.IsZero() || to.IsZero() || !to.After(*from) || to.Sub(*from) > maximum {
		return errors.New("invalid commerce time range")
	}
	return nil
}

func validateCommerceOrderExportRequest(input commerceOrderExportRequest) error {
	if err := validateCommerceTimeRange(input.CreatedFrom, input.CreatedTo, 31*24*time.Hour); err != nil {
		return err
	}
	if input.AccountID != nil && *input.AccountID == uuid.Nil {
		return errors.New("invalid account_id")
	}
	if input.Limit < 0 || input.Limit > 1000 {
		return errors.New("invalid limit")
	}
	return nil
}

func commerceCatalogDraftView(draft *store.CommerceCatalogDraft) any {
	if draft == nil {
		return nil
	}
	issues := make([]map[string]any, 0, len(draft.Validation.Issues))
	for _, issue := range draft.Validation.Issues {
		issues = append(issues, map[string]any{
			"code": issue.Code, "plan_id": issue.PlanID, "plan_key": issue.PlanKey, "field": issue.Field,
		})
	}
	return map[string]any{
		"id": draft.ID, "version": draft.Version, "created_at": draft.CreatedAt, "plans": draft.Plans,
		"validation": map[string]any{"valid": draft.Validation.Valid, "plan_count": draft.Validation.PlanCount, "issues": issues},
	}
}

func commerceOrderReportPageView(page store.CommerceOrderReportPage) map[string]any {
	items := make([]map[string]any, 0, len(page.Rows))
	for _, item := range page.Rows {
		items = append(items, map[string]any{
			"order_id": item.OrderID, "order_no": item.OrderNo, "account_id": item.AccountID,
			"plan_key": item.PlanKey, "catalog_version": item.CatalogVersion,
			"provider": item.Provider, "payment_scene": item.PaymentScene,
			"amount_minor": item.AmountMinor, "currency": item.Currency, "status": item.Status,
			"expires_at": item.ExpiresAt, "paid_at": item.PaidAt, "fulfilled_at": item.FulfilledAt,
			"closed_at": item.ClosedAt, "created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
		})
	}
	var next any
	if page.NextCursor != nil {
		next = map[string]any{"after_created_at": page.NextCursor.CreatedAt, "after_id": page.NextCursor.OrderID}
	}
	return map[string]any{"items": items, "next_cursor": next}
}

func commerceOrderExportView(result store.CommerceOrderExport) map[string]any {
	items := make([]map[string]any, 0, len(result.Rows))
	for _, item := range result.Rows {
		items = append(items, map[string]any{
			"order_no": item.OrderNo, "account_id": item.AccountID, "plan_key": item.PlanKey,
			"catalog_version": item.CatalogVersion, "provider": item.Provider,
			"payment_scene": item.PaymentScene, "amount_minor": item.AmountMinor,
			"currency": item.Currency, "status": item.Status, "paid_at": item.PaidAt, "created_at": item.CreatedAt,
		})
	}
	return map[string]any{"items": items, "truncated": result.Truncated}
}

func commerceConsistencyPageView(page store.CommerceConsistencyPage) map[string]any {
	items := make([]map[string]any, 0, len(page.Rows))
	for _, item := range page.Rows {
		view := map[string]any{
			"category": item.Category, "severity": item.Severity, "recommended_action": item.RecommendedAction,
			"entity_id": item.EntityID, "order_id": item.OrderID, "order_no": item.OrderNo,
			"account_id": item.AccountID, "provider": item.Provider, "order_status": item.OrderStatus,
			"transaction_states": item.TransactionStates, "transaction_count": item.TransactionCount,
			"grant_states": item.GrantStates, "grant_count": item.GrantCount,
			"materialized_plan": item.MaterializedPlan, "expected_plan": item.ExpectedPlan,
			"occurred_at": item.OccurredAt, "repair": commerceConsistencyRepairView(item),
		}
		items = append(items, view)
	}
	var next any
	if page.NextCursor != nil {
		next = map[string]any{
			"after_occurred_at": page.NextCursor.OccurredAt,
			"after_entity_id":   page.NextCursor.EntityID,
			"after_category":    page.NextCursor.Category,
		}
	}
	return map[string]any{"items": items, "next_cursor": next}
}

func commerceConsistencyRepairView(item store.CommerceConsistencyDetail) any {
	if item.OrderID == nil || *item.OrderID == uuid.Nil {
		return nil
	}
	switch item.Category {
	case "ORDER_WITHOUT_COMPATIBLE_TRANSACTION", "SETTLED_TRANSACTION_ORDER_MISMATCH":
		return map[string]any{
			"operation": "QUERY_PROVIDER", "method": http.MethodPost,
			"path": "/v1/admin/billing/orders/" + item.OrderID.String() + "/query",
		}
	case "COMPLETED_ORDER_WITHOUT_GRANT":
		if item.OrderStatus == "FULFILLED" {
			return map[string]any{
				"operation": "RETRY_FULFILLMENT", "method": http.MethodPost,
				"path": "/v1/admin/billing/orders/" + item.OrderID.String() + "/retry-fulfillment",
			}
		}
	}
	return nil
}

type safeReconciliationDifferenceCounts struct {
	ProviderOnly        int `json:"provider_only"`
	LocalOnly           int `json:"local_only"`
	AmountMismatch      int `json:"amount_mismatch"`
	CurrencyMismatch    int `json:"currency_mismatch"`
	StatusMismatch      int `json:"status_mismatch"`
	TransactionMismatch int `json:"transaction_mismatch"`
	DuplicateProvider   int `json:"duplicate_provider"`
	DuplicateLocal      int `json:"duplicate_local"`
}

type safeReconciliationSummary struct {
	SchemaVersion   int                                `json:"schema_version"`
	Provider        string                             `json:"provider"`
	PeriodStart     time.Time                          `json:"period_start"`
	PeriodEnd       time.Time                          `json:"period_end"`
	Result          string                             `json:"result"`
	Balanced        bool                               `json:"balanced"`
	ProviderEntries int                                `json:"provider_entries"`
	LocalEntries    int                                `json:"local_entries"`
	MatchedEntries  int                                `json:"matched_entries"`
	Differences     safeReconciliationDifferenceCounts `json:"differences"`
	CompletedAt     time.Time                          `json:"completed_at"`
	FailureCode     string                             `json:"failure_code,omitempty"`
}

func safeReconciliationSummaryView(raw json.RawMessage) any {
	if len(raw) == 0 {
		return nil
	}
	var summary safeReconciliationSummary
	if err := json.Unmarshal(raw, &summary); err != nil || summary.SchemaVersion != 1 {
		return map[string]any{"available": false}
	}
	provider := boundedCommerceProvider(summary.Provider)
	if provider == "UNKNOWN" {
		return map[string]any{"available": false}
	}
	switch summary.Result {
	case "BALANCED", "DIFFERENCES", "FAILED":
	default:
		return map[string]any{"available": false}
	}
	failureCode := boundedReconciliationFailureCode(summary.FailureCode)
	if !validSafeReconciliationSummary(summary, failureCode) {
		return map[string]any{"available": false}
	}
	return map[string]any{
		"schema_version": summary.SchemaVersion, "provider": provider,
		"period_start": summary.PeriodStart, "period_end": summary.PeriodEnd,
		"result": summary.Result, "balanced": summary.Balanced,
		"provider_entries": summary.ProviderEntries, "local_entries": summary.LocalEntries,
		"matched_entries": summary.MatchedEntries, "differences": summary.Differences,
		"completed_at": summary.CompletedAt, "failure_code": failureCode,
	}
}

func validSafeReconciliationSummary(summary safeReconciliationSummary, failureCode string) bool {
	if summary.PeriodStart.IsZero() || summary.PeriodEnd.IsZero() || summary.CompletedAt.IsZero() ||
		!summary.PeriodEnd.After(summary.PeriodStart) || summary.CompletedAt.Before(summary.PeriodEnd) {
		return false
	}
	counts := []int{
		summary.ProviderEntries, summary.LocalEntries, summary.MatchedEntries,
		summary.Differences.ProviderOnly, summary.Differences.LocalOnly,
		summary.Differences.AmountMismatch, summary.Differences.CurrencyMismatch,
		summary.Differences.StatusMismatch, summary.Differences.TransactionMismatch,
		summary.Differences.DuplicateProvider, summary.Differences.DuplicateLocal,
	}
	for _, count := range counts {
		if count < 0 {
			return false
		}
	}
	if summary.MatchedEntries > summary.ProviderEntries || summary.MatchedEntries > summary.LocalEntries {
		return false
	}
	differenceTotal := summary.Differences.ProviderOnly + summary.Differences.LocalOnly +
		summary.Differences.AmountMismatch + summary.Differences.CurrencyMismatch +
		summary.Differences.StatusMismatch + summary.Differences.TransactionMismatch +
		summary.Differences.DuplicateProvider + summary.Differences.DuplicateLocal
	switch summary.Result {
	case "BALANCED":
		return summary.Balanced && differenceTotal == 0 && failureCode == ""
	case "DIFFERENCES":
		return !summary.Balanced && differenceTotal > 0 && failureCode == ""
	case "FAILED":
		return !summary.Balanced && failureCode != ""
	default:
		return false
	}
}

func boundedReconciliationFailureCode(value string) string {
	value = strings.TrimSpace(value)
	if value == "" {
		return ""
	}
	if len(value) > 64 {
		return "PROCESSING_FAILED"
	}
	for _, character := range value {
		if character != '_' && (character < 'A' || character > 'Z') {
			return "PROCESSING_FAILED"
		}
	}
	return value
}
