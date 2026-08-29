package webportal

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/csv"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
	"unicode"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	commerceReportTimeLayout        = "2006-01-02T15:04"
	commerceReportDefaultLimit      = 50
	commerceReportMaximumLimit      = 200
	commerceReportMaximumRange      = 93 * 24 * time.Hour
	commerceExportDefaultLimit      = 1000
	commerceExportMaximumRange      = 31 * 24 * time.Hour
	commerceConsistencyMaximumRange = 31 * 24 * time.Hour
	commerceCursorMaximumBytes      = 512
)

var errCommerceReportFilter = errors.New("invalid commerce report filter")

type commerceReportingRepository interface {
	GetCommerceCatalogDraft(context.Context, uuid.UUID) (*store.CommerceCatalogDraft, error)
	ListCommerceOrderReport(context.Context, uuid.UUID, store.CommerceOrderReportFilter) (store.CommerceOrderReportPage, error)
	ExportCommerceOrders(context.Context, uuid.UUID, store.CommerceOrderExportFilter) (store.CommerceOrderExport, error)
	ListCommerceConsistencyDetails(context.Context, uuid.UUID, store.CommerceConsistencyFilter) (store.CommerceConsistencyPage, error)
}

type commerceExportAuditRepository interface {
	Audit(context.Context, uuid.UUID, uuid.UUID, string, string, uuid.UUID, string, string, string) error
}

type commerceCatalogPlanView struct {
	ID               uuid.UUID
	Key              string
	DisplayName      string
	Description      string
	BillingPeriod    string
	MaxRemotePCs     int
	MaxLiveSessions  int
	SortOrder        int
	VisiblePlatforms string
	AvailableFrom    *time.Time
	AvailableUntil   *time.Time
	FallbackPlanKey  string
	PriceMinor       int64
	Currency         string
}

type commerceCatalogIssueView struct {
	Code    string
	Label   string
	PlanKey string
	Field   string
}

type commerceCatalogDraftView struct {
	Exists    bool
	ID        uuid.UUID
	Version   int64
	CreatedAt time.Time
	Valid     bool
	PlanCount int
	Plans     []commerceCatalogPlanView
	Issues    []commerceCatalogIssueView
}

type commerceOrderReportFilterView struct {
	OrderNo     string
	AccountID   string
	Provider    string
	Status      string
	CreatedFrom string
	CreatedTo   string
	Limit       int
}

type commerceConsistencyFilterView struct {
	AccountID   string
	Provider    string
	Category    string
	CreatedFrom string
	CreatedTo   string
	Limit       int
}

type commerceOrderCursorPayload struct {
	CreatedAt string `json:"created_at"`
	OrderID   string `json:"order_id"`
}

type commerceConsistencyCursorPayload struct {
	OccurredAt string `json:"occurred_at"`
	EntityID   string `json:"entity_id"`
	Category   string `json:"category"`
}

func newCommerceCatalogDraftView(draft *store.CommerceCatalogDraft) commerceCatalogDraftView {
	if draft == nil {
		return commerceCatalogDraftView{}
	}
	result := commerceCatalogDraftView{
		Exists: true, ID: draft.ID, Version: draft.Version, CreatedAt: draft.CreatedAt,
		Valid: draft.Validation.Valid, PlanCount: draft.Validation.PlanCount,
		Plans:  make([]commerceCatalogPlanView, 0, len(draft.Plans)),
		Issues: make([]commerceCatalogIssueView, 0, len(draft.Validation.Issues)),
	}
	for _, plan := range draft.Plans {
		// FeatureFlags and ChannelProducts intentionally do not cross this view
		// boundary: they can contain channel-specific configuration that is not
		// needed to review catalog shape and publishability.
		result.Plans = append(result.Plans, commerceCatalogPlanView{
			ID: plan.ID, Key: plan.Key, DisplayName: plan.DisplayName, Description: plan.Description,
			BillingPeriod: plan.BillingPeriod, MaxRemotePCs: plan.MaxRemotePCs,
			MaxLiveSessions: plan.MaxLiveSessions, SortOrder: plan.SortOrder,
			VisiblePlatforms: strings.Join(plan.VisiblePlatforms, " / "),
			AvailableFrom:    plan.AvailableFrom, AvailableUntil: plan.AvailableUntil,
			FallbackPlanKey: plan.FallbackPlanKey, PriceMinor: plan.PriceMinor, Currency: plan.Currency,
		})
	}
	for _, issue := range draft.Validation.Issues {
		result.Issues = append(result.Issues, commerceCatalogIssueView{
			Code: issue.Code, Label: commerceCatalogIssueLabel(issue.Code),
			PlanKey: issue.PlanKey, Field: issue.Field,
		})
	}
	return result
}

func parseCommerceOrderReportQuery(values url.Values, now time.Time) (store.CommerceOrderReportFilter, commerceOrderReportFilterView, error) {
	allowed := map[string]bool{
		"section": true, "order_no": true, "account_id": true, "provider": true,
		"status": true, "from": true, "to": true, "limit": true, "cursor": true,
	}
	if err := validateCommerceFormValues(values, allowed); err != nil || values.Get("section") != "reports" {
		return store.CommerceOrderReportFilter{}, commerceOrderReportFilterView{}, errCommerceReportFilter
	}
	filter, view, err := parseCommerceOrderFilter(values, now, false, commerceReportDefaultLimit, commerceReportMaximumLimit, commerceReportMaximumRange)
	if err != nil {
		return store.CommerceOrderReportFilter{}, commerceOrderReportFilterView{}, err
	}
	if cursorText := values.Get("cursor"); cursorText != "" {
		cursor, cursorErr := decodeCommerceOrderCursor(cursorText)
		if cursorErr != nil || cursor.CreatedAt.Before(*filter.CreatedFrom) || !cursor.CreatedAt.Before(*filter.CreatedTo) {
			return store.CommerceOrderReportFilter{}, commerceOrderReportFilterView{}, errCommerceReportFilter
		}
		filter.AfterCreatedAt, filter.AfterID = &cursor.CreatedAt, &cursor.OrderID
	}
	return filter, view, nil
}

func parseCommerceOrderExportForm(values url.Values, now time.Time) (store.CommerceOrderExportFilter, commerceOrderReportFilterView, error) {
	allowed := map[string]bool{
		"csrf": true, "reason": true, "idempotency_key": true, "order_no": true,
		"account_id": true, "provider": true, "status": true, "from": true,
		"to": true, "limit": true,
	}
	if err := validateCommerceFormValues(values, allowed); err != nil {
		return store.CommerceOrderExportFilter{}, commerceOrderReportFilterView{}, errCommerceReportFilter
	}
	report, view, err := parseCommerceOrderFilter(values, now, true, commerceExportDefaultLimit, commerceExportDefaultLimit, commerceExportMaximumRange)
	if err != nil {
		return store.CommerceOrderExportFilter{}, commerceOrderReportFilterView{}, err
	}
	return store.CommerceOrderExportFilter{
		OrderNo: report.OrderNo, AccountID: report.AccountID, Provider: report.Provider,
		Status: report.Status, CreatedFrom: report.CreatedFrom, CreatedTo: report.CreatedTo,
		Limit: report.Limit,
	}, view, nil
}

func parseCommerceOrderFilter(values url.Values, now time.Time, datesRequired bool, defaultLimit, maximumLimit int, maximumRange time.Duration) (store.CommerceOrderReportFilter, commerceOrderReportFilterView, error) {
	orderNo := strings.TrimSpace(values.Get("order_no"))
	if orderNo != "" && !validCommerceReportOrderNo(orderNo) {
		return store.CommerceOrderReportFilter{}, commerceOrderReportFilterView{}, errCommerceReportFilter
	}
	provider := strings.ToUpper(strings.TrimSpace(values.Get("provider")))
	if provider != "" && provider != "ALIPAY" && provider != "WECHAT_PAY" {
		return store.CommerceOrderReportFilter{}, commerceOrderReportFilterView{}, errCommerceReportFilter
	}
	status := strings.ToUpper(strings.TrimSpace(values.Get("status")))
	if status != "" && !validCommerceOrderReportStatus(status) {
		return store.CommerceOrderReportFilter{}, commerceOrderReportFilterView{}, errCommerceReportFilter
	}
	accountID, accountText, err := parseOptionalCommerceAccountID(values.Get("account_id"))
	if err != nil {
		return store.CommerceOrderReportFilter{}, commerceOrderReportFilterView{}, err
	}
	from, to, err := parseCommerceReportRange(values.Get("from"), values.Get("to"), now, datesRequired, 30*24*time.Hour, maximumRange)
	if err != nil {
		return store.CommerceOrderReportFilter{}, commerceOrderReportFilterView{}, err
	}
	limit, err := parseCommerceReportLimit(values.Get("limit"), defaultLimit, maximumLimit)
	if err != nil {
		return store.CommerceOrderReportFilter{}, commerceOrderReportFilterView{}, err
	}
	return store.CommerceOrderReportFilter{
			OrderNo: orderNo, AccountID: accountID, Provider: provider, Status: status,
			CreatedFrom: &from, CreatedTo: &to, Limit: limit,
		}, commerceOrderReportFilterView{
			OrderNo: orderNo, AccountID: accountText, Provider: provider, Status: status,
			CreatedFrom: from.Format(commerceReportTimeLayout), CreatedTo: to.Format(commerceReportTimeLayout), Limit: limit,
		}, nil
}

func parseCommerceConsistencyQuery(values url.Values, now time.Time) (store.CommerceConsistencyFilter, commerceConsistencyFilterView, error) {
	allowed := map[string]bool{
		"section": true, "account_id": true, "provider": true, "category": true,
		"from": true, "to": true, "limit": true, "cursor": true,
	}
	if err := validateCommerceFormValues(values, allowed); err != nil || values.Get("section") != "consistency" {
		return store.CommerceConsistencyFilter{}, commerceConsistencyFilterView{}, errCommerceReportFilter
	}
	provider := strings.ToUpper(strings.TrimSpace(values.Get("provider")))
	if provider != "" && provider != "ALIPAY" && provider != "WECHAT_PAY" {
		return store.CommerceConsistencyFilter{}, commerceConsistencyFilterView{}, errCommerceReportFilter
	}
	category := strings.ToUpper(strings.TrimSpace(values.Get("category")))
	if category != "" && !validCommerceConsistencyCategory(category) {
		return store.CommerceConsistencyFilter{}, commerceConsistencyFilterView{}, errCommerceReportFilter
	}
	accountID, accountText, err := parseOptionalCommerceAccountID(values.Get("account_id"))
	if err != nil {
		return store.CommerceConsistencyFilter{}, commerceConsistencyFilterView{}, err
	}
	from, to, err := parseCommerceReportRange(values.Get("from"), values.Get("to"), now, false, 24*time.Hour, commerceConsistencyMaximumRange)
	if err != nil {
		return store.CommerceConsistencyFilter{}, commerceConsistencyFilterView{}, err
	}
	limit, err := parseCommerceReportLimit(values.Get("limit"), commerceReportDefaultLimit, commerceReportMaximumLimit)
	if err != nil {
		return store.CommerceConsistencyFilter{}, commerceConsistencyFilterView{}, err
	}
	filter := store.CommerceConsistencyFilter{
		AccountID: accountID, Provider: provider, Category: category,
		CreatedFrom: &from, CreatedTo: &to, Limit: limit,
	}
	if cursorText := values.Get("cursor"); cursorText != "" {
		cursor, cursorErr := decodeCommerceConsistencyCursor(cursorText)
		if cursorErr != nil || cursor.OccurredAt.Before(from) || !cursor.OccurredAt.Before(to) {
			return store.CommerceConsistencyFilter{}, commerceConsistencyFilterView{}, errCommerceReportFilter
		}
		filter.After = &cursor
	}
	return filter, commerceConsistencyFilterView{
		AccountID: accountText, Provider: provider, Category: category,
		CreatedFrom: from.Format(commerceReportTimeLayout), CreatedTo: to.Format(commerceReportTimeLayout), Limit: limit,
	}, nil
}

func validateCommerceFormValues(values url.Values, allowed map[string]bool) error {
	for key, items := range values {
		if !allowed[key] || len(items) != 1 {
			return errCommerceReportFilter
		}
	}
	return nil
}

func parseOptionalCommerceAccountID(value string) (*uuid.UUID, string, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return nil, "", nil
	}
	id, err := uuid.Parse(value)
	if err != nil || id == uuid.Nil || id.String() != strings.ToLower(value) {
		return nil, "", errCommerceReportFilter
	}
	return &id, id.String(), nil
}

func parseCommerceReportRange(fromText, toText string, now time.Time, required bool, defaultRange, maximumRange time.Duration) (time.Time, time.Time, error) {
	fromText, toText = strings.TrimSpace(fromText), strings.TrimSpace(toText)
	if (fromText == "") != (toText == "") || required && fromText == "" {
		return time.Time{}, time.Time{}, errCommerceReportFilter
	}
	now = now.UTC()
	if fromText == "" {
		to := now.Truncate(time.Minute)
		return to.Add(-defaultRange), to, nil
	}
	from, fromErr := time.ParseInLocation(commerceReportTimeLayout, fromText, time.UTC)
	to, toErr := time.ParseInLocation(commerceReportTimeLayout, toText, time.UTC)
	if fromErr != nil || toErr != nil || !to.After(from) || to.Sub(from) > maximumRange || to.After(now.Add(5*time.Minute)) {
		return time.Time{}, time.Time{}, errCommerceReportFilter
	}
	return from, to, nil
}

func parseCommerceReportLimit(value string, fallback, maximum int) (int, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return fallback, nil
	}
	limit, err := strconv.Atoi(value)
	if err != nil || limit < 1 || limit > maximum || strconv.Itoa(limit) != value {
		return 0, errCommerceReportFilter
	}
	return limit, nil
}

func validCommerceReportOrderNo(value string) bool {
	if len(value) < 16 || len(value) > 64 {
		return false
	}
	for _, character := range value {
		if character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
			character >= '0' && character <= '9' || character == '-' || character == '_' {
			continue
		}
		return false
	}
	return true
}

func validCommerceOrderReportStatus(value string) bool {
	switch value {
	case "CREATED", "PENDING_PAYMENT", "PAID", "FULFILLING", "FULFILLED", "CLOSED", "REFUNDING", "REFUNDED", "FAILED":
		return true
	default:
		return false
	}
}

func validCommerceConsistencyCategory(value string) bool {
	switch value {
	case "ORDER_WITHOUT_COMPATIBLE_TRANSACTION", "SETTLED_TRANSACTION_ORDER_MISMATCH",
		"COMPLETED_ORDER_WITHOUT_GRANT", "REFUNDED_ORDER_WITH_LIVE_GRANT",
		"MATERIALIZED_ENTITLEMENT_MISMATCH":
		return true
	default:
		return false
	}
}

func encodeCommerceCursor(value any) (string, error) {
	contents, err := json.Marshal(value)
	if err != nil || len(contents) > commerceCursorMaximumBytes {
		return "", errCommerceReportFilter
	}
	return base64.RawURLEncoding.EncodeToString(contents), nil
}

func decodeCommerceCursor(value string, target any) error {
	if value == "" || len(value) > base64.RawURLEncoding.EncodedLen(commerceCursorMaximumBytes) {
		return errCommerceReportFilter
	}
	contents, err := base64.RawURLEncoding.DecodeString(value)
	if err != nil || len(contents) > commerceCursorMaximumBytes {
		return errCommerceReportFilter
	}
	decoder := json.NewDecoder(bytes.NewReader(contents))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		return errCommerceReportFilter
	}
	var extra any
	if err := decoder.Decode(&extra); !errors.Is(err, io.EOF) {
		return errCommerceReportFilter
	}
	return nil
}

func decodeCommerceOrderCursor(value string) (store.CommerceOrderReportCursor, error) {
	var payload commerceOrderCursorPayload
	if err := decodeCommerceCursor(value, &payload); err != nil {
		return store.CommerceOrderReportCursor{}, err
	}
	createdAt, err := time.Parse(time.RFC3339Nano, payload.CreatedAt)
	orderID, idErr := uuid.Parse(payload.OrderID)
	if err != nil || idErr != nil || orderID == uuid.Nil || payload.OrderID != orderID.String() {
		return store.CommerceOrderReportCursor{}, errCommerceReportFilter
	}
	return store.CommerceOrderReportCursor{CreatedAt: createdAt.UTC(), OrderID: orderID}, nil
}

func decodeCommerceConsistencyCursor(value string) (store.CommerceConsistencyCursor, error) {
	var payload commerceConsistencyCursorPayload
	if err := decodeCommerceCursor(value, &payload); err != nil {
		return store.CommerceConsistencyCursor{}, err
	}
	occurredAt, err := time.Parse(time.RFC3339Nano, payload.OccurredAt)
	entityID, idErr := uuid.Parse(payload.EntityID)
	payload.Category = strings.ToUpper(strings.TrimSpace(payload.Category))
	if err != nil || idErr != nil || entityID == uuid.Nil || payload.EntityID != entityID.String() || !validCommerceConsistencyCategory(payload.Category) {
		return store.CommerceConsistencyCursor{}, errCommerceReportFilter
	}
	return store.CommerceConsistencyCursor{OccurredAt: occurredAt.UTC(), EntityID: entityID, Category: payload.Category}, nil
}

func commerceOrderReportNextURL(view commerceOrderReportFilterView, cursor store.CommerceOrderReportCursor) (string, error) {
	encoded, err := encodeCommerceCursor(commerceOrderCursorPayload{
		CreatedAt: cursor.CreatedAt.UTC().Format(time.RFC3339Nano), OrderID: cursor.OrderID.String(),
	})
	if err != nil {
		return "", err
	}
	values := commerceOrderReportQueryValues(view)
	values.Set("cursor", encoded)
	return "/admin/commerce?" + values.Encode(), nil
}

func commerceConsistencyNextURL(view commerceConsistencyFilterView, cursor store.CommerceConsistencyCursor) (string, error) {
	encoded, err := encodeCommerceCursor(commerceConsistencyCursorPayload{
		OccurredAt: cursor.OccurredAt.UTC().Format(time.RFC3339Nano), EntityID: cursor.EntityID.String(), Category: cursor.Category,
	})
	if err != nil {
		return "", err
	}
	values := commerceConsistencyQueryValues(view)
	values.Set("cursor", encoded)
	return "/admin/commerce?" + values.Encode(), nil
}

func commerceOrderReportQueryValues(view commerceOrderReportFilterView) url.Values {
	values := url.Values{
		"section": {"reports"}, "from": {view.CreatedFrom}, "to": {view.CreatedTo},
		"limit": {strconv.Itoa(view.Limit)},
	}
	setOptionalCommerceQueryValue(values, "order_no", view.OrderNo)
	setOptionalCommerceQueryValue(values, "account_id", view.AccountID)
	setOptionalCommerceQueryValue(values, "provider", view.Provider)
	setOptionalCommerceQueryValue(values, "status", view.Status)
	return values
}

func commerceConsistencyQueryValues(view commerceConsistencyFilterView) url.Values {
	values := url.Values{
		"section": {"consistency"}, "from": {view.CreatedFrom}, "to": {view.CreatedTo},
		"limit": {strconv.Itoa(view.Limit)},
	}
	setOptionalCommerceQueryValue(values, "account_id", view.AccountID)
	setOptionalCommerceQueryValue(values, "provider", view.Provider)
	setOptionalCommerceQueryValue(values, "category", view.Category)
	return values
}

func setOptionalCommerceQueryValue(values url.Values, key, value string) {
	if value != "" {
		values.Set(key, value)
	}
}

func (p *Portal) adminCommerceExportOrders(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSensitiveReportingAction(w, r)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceReportingRepository)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "订单导出暂不可用", "服务端尚未完成商业化报表存储配置。")
		return
	}
	filter, _, err := parseCommerceOrderExportForm(r.Form, p.now())
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "导出条件无效", "导出必须使用有效的 UTC 时间范围、账号、渠道和状态筛选。")
		return
	}
	export, err := repository.ExportCommerceOrders(r.Context(), account.ID, filter)
	if err != nil {
		if errors.Is(err, store.ErrDenied) {
			p.renderError(w, http.StatusForbidden, "无权导出订单", "当前账号角色或导出范围不允许该操作。")
			return
		}
		p.internalError(w, err)
		return
	}
	contents, err := encodeCommerceOrderCSV(export)
	if err != nil {
		p.internalError(w, err)
		return
	}
	metadata, err := json.Marshal(map[string]any{
		"row_count": len(export.Rows), "truncated": export.Truncated,
		"reason": strings.TrimSpace(r.Form.Get("reason")),
	})
	if err != nil {
		p.internalError(w, err)
		return
	}
	auditRepository, available := p.config.Repository.(commerceExportAuditRepository)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "订单导出暂不可用", "服务端安全审计配置不完整。")
		return
	}
	auditContext, cancelAudit := context.WithTimeout(context.WithoutCancel(r.Context()), sensitiveAuditTimeout)
	defer cancelAudit()
	if err := auditRepository.Audit(
		auditContext, account.ID, uuid.Nil, "web.commerce.orders.export", "commerce_order_export",
		uuid.Nil, "SUCCEEDED", uuid.NewString(), string(metadata),
	); err != nil {
		p.internalError(w, err)
		return
	}
	w.Header().Set("Content-Type", "text/csv; charset=utf-8")
	w.Header().Set("Content-Disposition", fmt.Sprintf("attachment; filename=termlink-orders-%s.csv", p.now().UTC().Format("20060102-150405")))
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("X-TermLink-Export-Row-Count", strconv.Itoa(len(export.Rows)))
	w.Header().Set("X-TermLink-Export-Truncated", strconv.FormatBool(export.Truncated))
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(contents)
}

func encodeCommerceOrderCSV(export store.CommerceOrderExport) ([]byte, error) {
	var output bytes.Buffer
	writer := csv.NewWriter(&output)
	writer.UseCRLF = true
	if err := writer.Write([]string{
		"order_no", "account_id", "plan_key", "catalog_version", "provider", "payment_scene",
		"amount_minor", "currency", "status", "paid_at", "created_at",
	}); err != nil {
		return nil, err
	}
	for _, row := range export.Rows {
		paidAt := ""
		if row.PaidAt != nil {
			paidAt = row.PaidAt.UTC().Format(time.RFC3339Nano)
		}
		values := []string{
			row.OrderNo, row.AccountID.String(), row.PlanKey, strconv.FormatInt(row.CatalogVersion, 10),
			row.Provider, row.PaymentScene, strconv.FormatInt(row.AmountMinor, 10), row.Currency,
			row.Status, paidAt, row.CreatedAt.UTC().Format(time.RFC3339Nano),
		}
		for index := range values {
			values[index] = safeCommerceCSVCell(values[index])
		}
		if err := writer.Write(values); err != nil {
			return nil, err
		}
	}
	writer.Flush()
	if err := writer.Error(); err != nil {
		return nil, err
	}
	return output.Bytes(), nil
}

func safeCommerceCSVCell(value string) string {
	trimmed := strings.TrimLeftFunc(value, unicode.IsSpace)
	if trimmed != "" && strings.ContainsRune("=+-@", rune(trimmed[0])) {
		return "'" + value
	}
	return value
}

func commerceCatalogIssueLabel(code string) string {
	labels := map[string]string{
		store.CommerceCatalogIssueFreePlanRequired:        "缺少 free 基础套餐",
		store.CommerceCatalogIssueDuplicatePlanKey:        "套餐键重复",
		store.CommerceCatalogIssueCurrencyMustBeCNY:       "币种必须为 CNY",
		store.CommerceCatalogIssueFreePriceMustBeZero:     "免费套餐价格必须为零",
		store.CommerceCatalogIssuePaidPriceMustBePositive: "付费套餐价格必须大于零",
		store.CommerceCatalogIssueFallbackPlanNotFound:    "回退套餐不存在",
		store.CommerceCatalogIssueSaleWindowInvalid:       "可售时间窗口无效",
	}
	if label := labels[code]; label != "" {
		return label
	}
	return "未知校验问题"
}

func commerceConsistencyCategoryLabel(category string) string {
	labels := map[string]string{
		"ORDER_WITHOUT_COMPATIBLE_TRANSACTION": "订单缺少兼容的权威交易",
		"SETTLED_TRANSACTION_ORDER_MISMATCH":   "渠道结算与订单状态不一致",
		"COMPLETED_ORDER_WITHOUT_GRANT":        "已完成订单缺少权益授予",
		"REFUNDED_ORDER_WITH_LIVE_GRANT":       "已退款订单仍有有效权益",
		"MATERIALIZED_ENTITLEMENT_MISMATCH":    "当前权益与授予记录不一致",
	}
	if label := labels[category]; label != "" {
		return label
	}
	return "未知一致性问题"
}

func commerceConsistencyActionLabel(action string) string {
	labels := map[string]string{
		"QUERY_PROVIDER_THEN_REPLAY_VERIFIED_SETTLEMENT": "查询渠道后重放已验证结算",
		"QUERY_PROVIDER_AND_REPLAY_VERIFIED_SETTLEMENT":  "复核渠道并重放已验证结算",
		"RETRY_IDEMPOTENT_FULFILLMENT":                   "重试幂等履约",
		"REVIEW_REFUND_AND_REVOKE_ORDER_GRANT":           "复核退款并撤销订单权益",
		"RECOMPUTE_ENTITLEMENT_FROM_VERIFIED_GRANTS":     "从已验证授予记录重算权益",
	}
	if label := labels[action]; label != "" {
		return label
	}
	return "需要人工复核"
}

func commerceQuotaLabel(value int) string {
	if value < 0 {
		return "不限"
	}
	return strconv.Itoa(value)
}

func formatOptionalCommerceTime(value *time.Time) string {
	if value == nil {
		return "—"
	}
	return formatPortalTime(*value)
}
