package reconciliation

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	DiscrepancyStatusOpen         = "OPEN"
	DiscrepancyStatusAcknowledged = "ACKNOWLEDGED"
	DiscrepancyStatusResolved     = "RESOLVED"
	DiscrepancyStatusIgnored      = "IGNORED"

	DefaultOperationsPageSize = 50
	MaxOperationsPageSize     = 200
	operationsResponseSchema  = 1
)

var (
	ErrOperationsUnavailable = errors.New("reconciliation: operations unavailable")
	ErrInvalidOperation      = errors.New("reconciliation: invalid operation")
	ErrInvalidTransition     = errors.New("reconciliation: invalid discrepancy transition")
	ErrIdempotencyConflict   = errors.New("reconciliation: idempotency key conflict")
	ErrOperationInProgress   = errors.New("reconciliation: idempotent operation in progress")
	ErrOperationIntegrity    = errors.New("reconciliation: stored operation response failed validation")
)

// OperationalDiscrepancy is the only discrepancy shape exposed to callers.
// It deliberately omits discrepancy_key and external_reference_hmac, and has
// no field capable of carrying a raw provider reference or statement row.
type OperationalDiscrepancy struct {
	ID                    uuid.UUID  `json:"id"`
	RunID                 uuid.UUID  `json:"run_id"`
	Provider              string     `json:"provider"`
	PaymentChannelID      *uuid.UUID `json:"payment_channel_id,omitempty"`
	Category              string     `json:"category"`
	InternalOrderID       *uuid.UUID `json:"internal_order_id,omitempty"`
	InternalTransactionID *uuid.UUID `json:"internal_transaction_id,omitempty"`
	LocalStatus           *string    `json:"local_status,omitempty"`
	ProviderStatus        *string    `json:"provider_status,omitempty"`
	SummaryCode           string     `json:"summary_code"`
	LocalAmountMinor      *int64     `json:"local_amount_minor,omitempty"`
	ProviderAmountMinor   *int64     `json:"provider_amount_minor,omitempty"`
	LocalCurrency         *string    `json:"local_currency,omitempty"`
	ProviderCurrency      *string    `json:"provider_currency,omitempty"`
	Status                string     `json:"status"`
	Version               int64      `json:"version"`
	FirstSeenAt           time.Time  `json:"first_seen_at"`
	LastSeenAt            time.Time  `json:"last_seen_at"`
	AcknowledgedAt        *time.Time `json:"acknowledged_at,omitempty"`
	ResolvedAt            *time.Time `json:"resolved_at,omitempty"`
	ResolvedBy            *uuid.UUID `json:"resolved_by,omitempty"`
	ResolutionCode        *string    `json:"resolution_code,omitempty"`
	CreatedAt             time.Time  `json:"created_at"`
	UpdatedAt             time.Time  `json:"updated_at"`
}

type DiscrepancyCursor struct {
	LastSeenAt time.Time `json:"last_seen_at"`
	ID         uuid.UUID `json:"id"`
}

type DiscrepancyListRequest struct {
	ActorID  uuid.UUID
	Provider string
	Status   string
	RunID    uuid.UUID
	After    *DiscrepancyCursor
	Limit    int
}

type DiscrepancyPage struct {
	Items []OperationalDiscrepancy `json:"items"`
	Next  *DiscrepancyCursor       `json:"next_cursor,omitempty"`
}

type DiscrepancyTransitionRequest struct {
	ActorID         uuid.UUID
	DiscrepancyID   uuid.UUID
	TargetStatus    string
	ExpectedVersion int64
	ReasonCode      string
	TraceID         string
	IdempotencyKey  string
}

type DiscrepancyTransitionCommand struct {
	ActorID               uuid.UUID
	DiscrepancyID         uuid.UUID
	TargetStatus          string
	ExpectedVersion       int64
	ReasonCode            string
	TraceID               string
	IdempotencyStorageKey string
	RequestDigest         [sha256.Size]byte
}

type OperationsRepository interface {
	ListDiscrepancies(context.Context, DiscrepancyListRequest) (DiscrepancyPage, error)
	TransitionDiscrepancy(context.Context, DiscrepancyTransitionCommand) (OperationalDiscrepancy, error)
}

type OperationsOptions struct {
	DigestKey []byte
}

type Operations struct {
	repository OperationsRepository
	digestKey  []byte
}

func NewOperations(repository OperationsRepository, options OperationsOptions) (*Operations, error) {
	if repository == nil || len(options.DigestKey) < sha256.Size {
		return nil, ErrOperationsUnavailable
	}
	return &Operations{repository: repository, digestKey: append([]byte(nil), options.DigestKey...)}, nil
}

func (operations *Operations) List(ctx context.Context, request DiscrepancyListRequest) (DiscrepancyPage, error) {
	if operations == nil || operations.repository == nil {
		return DiscrepancyPage{}, ErrOperationsUnavailable
	}
	request.Provider = strings.ToUpper(strings.TrimSpace(request.Provider))
	request.Status = strings.ToUpper(strings.TrimSpace(request.Status))
	if request.Limit < 1 {
		request.Limit = DefaultOperationsPageSize
	}
	if request.Limit > MaxOperationsPageSize || !validListRequest(request) {
		return DiscrepancyPage{}, ErrInvalidOperation
	}
	return operations.repository.ListDiscrepancies(ctx, request)
}

func (operations *Operations) Transition(ctx context.Context, request DiscrepancyTransitionRequest) (OperationalDiscrepancy, error) {
	if operations == nil || operations.repository == nil || len(operations.digestKey) < sha256.Size {
		return OperationalDiscrepancy{}, ErrOperationsUnavailable
	}
	request.TargetStatus = strings.ToUpper(strings.TrimSpace(request.TargetStatus))
	request.ReasonCode = strings.ToUpper(strings.TrimSpace(request.ReasonCode))
	request.TraceID = strings.TrimSpace(request.TraceID)
	request.IdempotencyKey = strings.TrimSpace(request.IdempotencyKey)
	if !validTransitionRequest(request) {
		return OperationalDiscrepancy{}, ErrInvalidOperation
	}
	storageDigest := operations.digest("idempotency-key", []byte(request.IdempotencyKey))
	command := DiscrepancyTransitionCommand{
		ActorID: request.ActorID, DiscrepancyID: request.DiscrepancyID, TargetStatus: request.TargetStatus,
		ExpectedVersion: request.ExpectedVersion, ReasonCode: request.ReasonCode, TraceID: request.TraceID,
		IdempotencyStorageKey: hex.EncodeToString(storageDigest[:]),
	}
	command.RequestDigest = operations.transitionDigest(command)
	return operations.repository.TransitionDiscrepancy(ctx, command)
}

func (operations *Operations) digest(purpose string, value []byte) [sha256.Size]byte {
	hash := hmac.New(sha256.New, operations.digestKey)
	writeOperationField(hash, []byte("termlink-reconciliation-operations-v1"))
	writeOperationField(hash, []byte(purpose))
	writeOperationField(hash, value)
	var result [sha256.Size]byte
	copy(result[:], hash.Sum(nil))
	return result
}

func (operations *Operations) transitionDigest(command DiscrepancyTransitionCommand) [sha256.Size]byte {
	hash := hmac.New(sha256.New, operations.digestKey)
	writeOperationField(hash, []byte("termlink-reconciliation-transition-v1"))
	writeOperationField(hash, command.ActorID[:])
	writeOperationField(hash, command.DiscrepancyID[:])
	writeOperationField(hash, []byte(command.TargetStatus))
	var version [8]byte
	binary.BigEndian.PutUint64(version[:], uint64(command.ExpectedVersion))
	writeOperationField(hash, version[:])
	writeOperationField(hash, []byte(command.ReasonCode))
	writeOperationField(hash, []byte(command.IdempotencyStorageKey))
	var result [sha256.Size]byte
	copy(result[:], hash.Sum(nil))
	return result
}

func writeOperationField(hash interface{ Write([]byte) (int, error) }, value []byte) {
	var size [4]byte
	binary.BigEndian.PutUint32(size[:], uint32(len(value)))
	_, _ = hash.Write(size[:])
	_, _ = hash.Write(value)
}

type PostgresOperationsRepository struct {
	pool *pgxpool.Pool
}

func NewPostgresOperationsRepository(repository *store.Postgres) (*PostgresOperationsRepository, error) {
	if repository == nil || repository.Pool == nil {
		return nil, ErrOperationsUnavailable
	}
	return &PostgresOperationsRepository{pool: repository.Pool}, nil
}

const operationalDiscrepancyColumns = `
d.id,d.run_id,d.provider,d.payment_channel_id,d.category,
d.internal_order_id,d.internal_transaction_id,d.local_status,d.provider_status,d.summary_code,
d.local_amount_minor,d.provider_amount_minor,d.local_currency,d.provider_currency,
d.status,d.version,d.first_seen_at,d.last_seen_at,d.acknowledged_at,d.resolved_at,
d.resolved_by,d.resolution_code,d.created_at,d.updated_at`

const listOperationalDiscrepanciesSQL = `
SELECT ` + operationalDiscrepancyColumns + `
FROM reconciliation_discrepancies d
WHERE ($1='' OR d.provider=$1)
  AND (($2='' AND d.status IN('OPEN','ACKNOWLEDGED')) OR ($2<>'' AND d.status=$2))
  AND ($3::uuid IS NULL OR d.run_id=$3)
  AND (NOT $4 OR (d.last_seen_at,d.id)<($5,$6))
ORDER BY d.last_seen_at DESC,d.id DESC
LIMIT $7`

const selectOperationalDiscrepancyForUpdateSQL = `
SELECT ` + operationalDiscrepancyColumns + `
FROM reconciliation_discrepancies d WHERE d.id=$1 FOR UPDATE`

const updateOperationalDiscrepancySQL = `
UPDATE reconciliation_discrepancies d SET
  status=$2,
  acknowledged_at=CASE WHEN $2='ACKNOWLEDGED' THEN now() ELSE d.acknowledged_at END,
  resolved_at=CASE WHEN $2 IN('RESOLVED','IGNORED') THEN now() ELSE NULL END,
  resolved_by=CASE WHEN $2 IN('RESOLVED','IGNORED') THEN $3::uuid ELSE NULL END,
  resolution_code=CASE WHEN $2 IN('RESOLVED','IGNORED') THEN $4 ELSE NULL END,
  version=d.version+1,updated_at=now()
WHERE d.id=$1 AND d.version=$5 AND d.status=$6
RETURNING ` + operationalDiscrepancyColumns

const claimOperationsIdempotencySQL = `
INSERT INTO idempotency_keys(user_id,idempotency_key,request_hash,expires_at)
VALUES($1,$2,$3,now()+interval '24 hours')
ON CONFLICT(user_id,idempotency_key) DO UPDATE SET
  request_hash=excluded.request_hash,response_status=NULL,response_body=NULL,
  expires_at=excluded.expires_at,created_at=now()
WHERE idempotency_keys.expires_at<now()`

const readOperationsIdempotencySQL = `
SELECT request_hash,response_status,response_body
FROM idempotency_keys WHERE user_id=$1 AND idempotency_key=$2`

const completeOperationsIdempotencySQL = `
UPDATE idempotency_keys SET response_status=200,response_body=$4::jsonb
WHERE user_id=$1 AND idempotency_key=$2 AND request_hash=$3 AND response_status IS NULL`

const auditOperationalDiscrepancySQL = `
INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
VALUES($1,$2,'reconciliation_discrepancy',$3,'SUCCEEDED',$4,
       jsonb_build_object('from_status',$5::text,'to_status',$6::text,'reason_code',$7::text,'version',$8::bigint))`

const outboxOperationalDiscrepancySQL = `
INSERT INTO outbox(event_type,aggregate_id,payload)
VALUES('payment.reconciliation.discrepancy.updated',$1,
       jsonb_build_object('discrepancy_id',$1::uuid::text,'provider',$2::text,
                          'category',$3::text,'status',$4::text,'version',$5::bigint))`

func (repository *PostgresOperationsRepository) ListDiscrepancies(ctx context.Context, request DiscrepancyListRequest) (DiscrepancyPage, error) {
	if repository == nil || repository.pool == nil || !validListRequest(request) {
		return DiscrepancyPage{}, ErrInvalidOperation
	}
	tx, err := repository.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.ReadCommitted, AccessMode: pgx.ReadOnly})
	if err != nil {
		return DiscrepancyPage{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireOperationsRole(ctx, tx, request.ActorID, false); err != nil {
		return DiscrepancyPage{}, err
	}
	var runID any
	if request.RunID != uuid.Nil {
		runID = request.RunID
	}
	var afterAt time.Time
	var afterID uuid.UUID
	afterValid := request.After != nil
	if afterValid {
		afterAt, afterID = request.After.LastSeenAt.UTC(), request.After.ID
	}
	rows, err := tx.Query(ctx, listOperationalDiscrepanciesSQL,
		request.Provider, request.Status, runID, afterValid, afterAt, afterID, request.Limit+1)
	if err != nil {
		return DiscrepancyPage{}, err
	}
	defer rows.Close()
	items := make([]OperationalDiscrepancy, 0, request.Limit+1)
	for rows.Next() {
		item, err := scanOperationalDiscrepancy(rows)
		if err != nil {
			return DiscrepancyPage{}, err
		}
		if !validOperationalDiscrepancy(item) {
			return DiscrepancyPage{}, ErrOperationIntegrity
		}
		items = append(items, item)
	}
	if err := rows.Err(); err != nil {
		return DiscrepancyPage{}, err
	}
	page := DiscrepancyPage{Items: items}
	if len(items) > request.Limit {
		page.Items = items[:request.Limit]
		last := page.Items[len(page.Items)-1]
		page.Next = &DiscrepancyCursor{LastSeenAt: last.LastSeenAt, ID: last.ID}
	}
	if err := tx.Commit(ctx); err != nil {
		return DiscrepancyPage{}, err
	}
	return page, nil
}

func (repository *PostgresOperationsRepository) TransitionDiscrepancy(ctx context.Context, command DiscrepancyTransitionCommand) (OperationalDiscrepancy, error) {
	if repository == nil || repository.pool == nil || !validTransitionCommand(command) {
		return OperationalDiscrepancy{}, ErrInvalidOperation
	}
	tx, err := repository.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable, AccessMode: pgx.ReadWrite})
	if err != nil {
		return OperationalDiscrepancy{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := requireOperationsRole(ctx, tx, command.ActorID, true); err != nil {
		return OperationalDiscrepancy{}, err
	}
	claim, err := tx.Exec(ctx, claimOperationsIdempotencySQL,
		command.ActorID, command.IdempotencyStorageKey, command.RequestDigest[:])
	if err != nil {
		return OperationalDiscrepancy{}, err
	}
	if claim.RowsAffected() == 0 {
		return replayOperationalDiscrepancy(ctx, tx, command)
	}

	current, err := scanOperationalDiscrepancy(tx.QueryRow(ctx, selectOperationalDiscrepancyForUpdateSQL, command.DiscrepancyID))
	if errors.Is(err, pgx.ErrNoRows) {
		return OperationalDiscrepancy{}, store.ErrNotFound
	}
	if err != nil {
		return OperationalDiscrepancy{}, err
	}
	if !validOperationalDiscrepancy(current) {
		return OperationalDiscrepancy{}, ErrOperationIntegrity
	}
	if current.Version != command.ExpectedVersion {
		return OperationalDiscrepancy{}, errors.Join(store.ErrConflict, ErrInvalidTransition)
	}
	if !allowedDiscrepancyTransition(current.Status, command.TargetStatus) {
		return OperationalDiscrepancy{}, errors.Join(store.ErrConflict, ErrInvalidTransition)
	}
	updated, err := scanOperationalDiscrepancy(tx.QueryRow(ctx, updateOperationalDiscrepancySQL,
		command.DiscrepancyID, command.TargetStatus, command.ActorID, command.ReasonCode,
		command.ExpectedVersion, current.Status))
	if errors.Is(err, pgx.ErrNoRows) {
		return OperationalDiscrepancy{}, errors.Join(store.ErrConflict, ErrInvalidTransition)
	}
	if err != nil {
		return OperationalDiscrepancy{}, err
	}
	if !validOperationalDiscrepancy(updated) {
		return OperationalDiscrepancy{}, ErrOperationIntegrity
	}
	action := "payment.reconciliation.discrepancy." + strings.ToLower(command.TargetStatus)
	if _, err := tx.Exec(ctx, auditOperationalDiscrepancySQL,
		command.ActorID, action, updated.ID, command.TraceID, current.Status,
		updated.Status, command.ReasonCode, updated.Version); err != nil {
		return OperationalDiscrepancy{}, err
	}
	if _, err := tx.Exec(ctx, outboxOperationalDiscrepancySQL,
		updated.ID, updated.Provider, updated.Category, updated.Status, updated.Version); err != nil {
		return OperationalDiscrepancy{}, err
	}
	payload, err := json.Marshal(operationsResponse{SchemaVersion: operationsResponseSchema, Discrepancy: updated})
	if err != nil {
		return OperationalDiscrepancy{}, err
	}
	completed, err := tx.Exec(ctx, completeOperationsIdempotencySQL,
		command.ActorID, command.IdempotencyStorageKey, command.RequestDigest[:], payload)
	if err != nil {
		return OperationalDiscrepancy{}, err
	}
	if completed.RowsAffected() != 1 {
		return OperationalDiscrepancy{}, ErrOperationIntegrity
	}
	if err := tx.Commit(ctx); err != nil {
		return OperationalDiscrepancy{}, err
	}
	return updated, nil
}

type operationsResponse struct {
	SchemaVersion int                    `json:"schema_version"`
	Discrepancy   OperationalDiscrepancy `json:"discrepancy"`
}

func replayOperationalDiscrepancy(ctx context.Context, tx pgx.Tx, command DiscrepancyTransitionCommand) (OperationalDiscrepancy, error) {
	var storedDigest []byte
	var responseStatus *int
	var responseBody json.RawMessage
	if err := tx.QueryRow(ctx, readOperationsIdempotencySQL,
		command.ActorID, command.IdempotencyStorageKey).Scan(&storedDigest, &responseStatus, &responseBody); err != nil {
		return OperationalDiscrepancy{}, err
	}
	if len(storedDigest) != sha256.Size || subtle.ConstantTimeCompare(storedDigest, command.RequestDigest[:]) != 1 {
		return OperationalDiscrepancy{}, errors.Join(store.ErrConflict, ErrIdempotencyConflict)
	}
	if responseStatus == nil {
		return OperationalDiscrepancy{}, ErrOperationInProgress
	}
	if *responseStatus != 200 {
		return OperationalDiscrepancy{}, ErrOperationIntegrity
	}
	var response operationsResponse
	if err := json.Unmarshal(responseBody, &response); err != nil || response.SchemaVersion != operationsResponseSchema ||
		!validOperationalDiscrepancy(response.Discrepancy) || response.Discrepancy.ID != command.DiscrepancyID {
		return OperationalDiscrepancy{}, ErrOperationIntegrity
	}
	if err := tx.Commit(ctx); err != nil {
		return OperationalDiscrepancy{}, err
	}
	return response.Discrepancy, nil
}

func requireOperationsRole(ctx context.Context, tx pgx.Tx, actorID uuid.UUID, write bool) error {
	if actorID == uuid.Nil {
		return store.ErrDenied
	}
	query := `SELECT role,status FROM users WHERE id=$1`
	if write {
		query += ` FOR UPDATE`
	}
	var role store.Role
	var status string
	if err := tx.QueryRow(ctx, query, actorID).Scan(&role, &status); err != nil || status != "ACTIVE" {
		return store.ErrDenied
	}
	if write && !role.IsAdministrator() {
		return store.ErrDenied
	}
	if !write && !role.CanOpenConsole() {
		return store.ErrDenied
	}
	return nil
}

func scanOperationalDiscrepancy(row pgx.Row) (OperationalDiscrepancy, error) {
	var item OperationalDiscrepancy
	err := row.Scan(
		&item.ID, &item.RunID, &item.Provider, &item.PaymentChannelID, &item.Category,
		&item.InternalOrderID, &item.InternalTransactionID, &item.LocalStatus, &item.ProviderStatus, &item.SummaryCode,
		&item.LocalAmountMinor, &item.ProviderAmountMinor, &item.LocalCurrency, &item.ProviderCurrency,
		&item.Status, &item.Version, &item.FirstSeenAt, &item.LastSeenAt, &item.AcknowledgedAt, &item.ResolvedAt,
		&item.ResolvedBy, &item.ResolutionCode, &item.CreatedAt, &item.UpdatedAt,
	)
	return item, err
}

func validListRequest(request DiscrepancyListRequest) bool {
	if request.ActorID == uuid.Nil || request.Limit < 1 || request.Limit > MaxOperationsPageSize {
		return false
	}
	if request.Provider != "" && request.Provider != string(payment.ProviderAlipay) && request.Provider != string(payment.ProviderWeChatPay) {
		return false
	}
	if request.Status != "" && !validDiscrepancyStatus(request.Status) {
		return false
	}
	return request.After == nil || (!request.After.LastSeenAt.IsZero() && request.After.ID != uuid.Nil)
}

func validTransitionRequest(request DiscrepancyTransitionRequest) bool {
	return request.ActorID != uuid.Nil && request.DiscrepancyID != uuid.Nil && request.ExpectedVersion > 0 &&
		validTransitionTarget(request.TargetStatus) && validReasonCode(request.ReasonCode) &&
		validTraceID(request.TraceID) && validIdempotencyKey(request.IdempotencyKey)
}

func validTransitionCommand(command DiscrepancyTransitionCommand) bool {
	decodedKey, err := hex.DecodeString(command.IdempotencyStorageKey)
	return command.ActorID != uuid.Nil && command.DiscrepancyID != uuid.Nil && command.ExpectedVersion > 0 &&
		validTransitionTarget(command.TargetStatus) && validReasonCode(command.ReasonCode) && validTraceID(command.TraceID) &&
		err == nil && len(decodedKey) == sha256.Size && command.RequestDigest != [sha256.Size]byte{}
}

func validTransitionTarget(status string) bool {
	return status == DiscrepancyStatusAcknowledged || status == DiscrepancyStatusResolved || status == DiscrepancyStatusIgnored
}

func validDiscrepancyStatus(status string) bool {
	return status == DiscrepancyStatusOpen || validTransitionTarget(status)
}

func allowedDiscrepancyTransition(from, to string) bool {
	switch from {
	case DiscrepancyStatusOpen:
		return to == DiscrepancyStatusAcknowledged
	case DiscrepancyStatusAcknowledged:
		return to == DiscrepancyStatusResolved || to == DiscrepancyStatusIgnored
	default:
		return false
	}
}

func validReasonCode(value string) bool {
	return len(value) >= 3 && len(value) <= 128 && validUpperCode(value, 128)
}

func validTraceID(value string) bool {
	if len(value) < 1 || len(value) > 256 || !utf8.ValidString(value) {
		return false
	}
	for _, character := range value {
		if unicode.IsControl(character) {
			return false
		}
	}
	return true
}

func validIdempotencyKey(value string) bool {
	if len(value) < 8 || len(value) > 128 {
		return false
	}
	for _, character := range value {
		if character < 0x21 || character > 0x7e {
			return false
		}
	}
	return true
}

func validOperationalDiscrepancy(item OperationalDiscrepancy) bool {
	if item.ID == uuid.Nil || item.RunID == uuid.Nil || item.Version < 1 ||
		(item.Provider != string(payment.ProviderAlipay) && item.Provider != string(payment.ProviderWeChatPay)) ||
		!validDiscrepancyCategory(item.Category) || !validUpperCode(item.SummaryCode, 64) || !validDiscrepancyStatus(item.Status) ||
		item.FirstSeenAt.IsZero() || item.LastSeenAt.Before(item.FirstSeenAt) || item.CreatedAt.IsZero() || item.UpdatedAt.Before(item.CreatedAt) {
		return false
	}
	for _, value := range []*string{item.LocalStatus, item.ProviderStatus} {
		if value != nil && !validUpperCode(*value, 32) {
			return false
		}
	}
	for _, value := range []*string{item.LocalCurrency, item.ProviderCurrency} {
		if value != nil && !validCurrency(*value) {
			return false
		}
	}
	if (item.LocalAmountMinor != nil && *item.LocalAmountMinor < 0) || (item.ProviderAmountMinor != nil && *item.ProviderAmountMinor < 0) {
		return false
	}
	switch item.Status {
	case DiscrepancyStatusOpen:
		return item.ResolvedAt == nil && item.ResolvedBy == nil && item.ResolutionCode == nil
	case DiscrepancyStatusAcknowledged:
		return item.AcknowledgedAt != nil && item.ResolvedAt == nil && item.ResolvedBy == nil && item.ResolutionCode == nil
	case DiscrepancyStatusResolved, DiscrepancyStatusIgnored:
		// resolved_by is ON DELETE SET NULL. A historical discrepancy remains
		// valid after the operator account is deleted; the immutable audit event
		// is still the authority for who performed the transition at that time.
		return item.AcknowledgedAt != nil && item.ResolvedAt != nil && item.ResolutionCode != nil && validReasonCode(*item.ResolutionCode)
	default:
		return false
	}
}

func validDiscrepancyCategory(category string) bool {
	switch category {
	case "MISSING_LOCAL", "MISSING_PROVIDER", "AMOUNT_MISMATCH", "CURRENCY_MISMATCH", "STATUS_MISMATCH", "REFUND_MISMATCH", "DUPLICATE_PROVIDER_REFERENCE", "DUPLICATE_LOCAL_REFERENCE", "OTHER":
		return true
	default:
		return false
	}
}
