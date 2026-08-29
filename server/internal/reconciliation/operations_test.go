package reconciliation

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

type recordingOperationsRepository struct {
	listRequests       []DiscrepancyListRequest
	transitionCommands []DiscrepancyTransitionCommand
	listResult         DiscrepancyPage
	transitionResult   OperationalDiscrepancy
	err                error
}

func (repository *recordingOperationsRepository) ListDiscrepancies(_ context.Context, request DiscrepancyListRequest) (DiscrepancyPage, error) {
	repository.listRequests = append(repository.listRequests, request)
	return repository.listResult, repository.err
}

func (repository *recordingOperationsRepository) TransitionDiscrepancy(_ context.Context, command DiscrepancyTransitionCommand) (OperationalDiscrepancy, error) {
	repository.transitionCommands = append(repository.transitionCommands, command)
	return repository.transitionResult, repository.err
}

func operationsTestKey() []byte {
	return bytes.Repeat([]byte{0x5a}, sha256.Size)
}

func TestNewOperationsRequiresRepositoryAndSecret(t *testing.T) {
	repository := &recordingOperationsRepository{}
	if _, err := NewOperations(nil, OperationsOptions{DigestKey: operationsTestKey()}); !errors.Is(err, ErrOperationsUnavailable) {
		t.Fatalf("nil repository: %v", err)
	}
	if _, err := NewOperations(repository, OperationsOptions{DigestKey: bytes.Repeat([]byte{1}, sha256.Size-1)}); !errors.Is(err, ErrOperationsUnavailable) {
		t.Fatalf("short digest key: %v", err)
	}

	key := operationsTestKey()
	operations, err := NewOperations(repository, OperationsOptions{DigestKey: key})
	if err != nil {
		t.Fatal(err)
	}
	key[0] ^= 0xff
	if bytes.Equal(operations.digestKey, key) {
		t.Fatal("operations retained the caller's mutable digest-key backing array")
	}
}

func TestOperationsListNormalizesAndBoundsRequest(t *testing.T) {
	repository := &recordingOperationsRepository{listResult: DiscrepancyPage{Items: []OperationalDiscrepancy{}}}
	operations, err := NewOperations(repository, OperationsOptions{DigestKey: operationsTestKey()})
	if err != nil {
		t.Fatal(err)
	}
	actorID := uuid.New()
	runID := uuid.New()
	page, err := operations.List(context.Background(), DiscrepancyListRequest{
		ActorID: actorID, Provider: " alipay ", Status: " open ", RunID: runID,
	})
	if err != nil {
		t.Fatal(err)
	}
	if page.Items == nil || len(repository.listRequests) != 1 {
		t.Fatalf("unexpected list result or calls: %#v %#v", page, repository.listRequests)
	}
	got := repository.listRequests[0]
	if got.ActorID != actorID || got.RunID != runID || got.Provider != string(payment.ProviderAlipay) ||
		got.Status != DiscrepancyStatusOpen || got.Limit != DefaultOperationsPageSize {
		t.Fatalf("request was not normalized: %#v", got)
	}
}

func TestOperationsListRejectsInvalidFiltersAndCursor(t *testing.T) {
	repository := &recordingOperationsRepository{}
	operations, err := NewOperations(repository, OperationsOptions{DigestKey: operationsTestKey()})
	if err != nil {
		t.Fatal(err)
	}
	valid := DiscrepancyListRequest{ActorID: uuid.New(), Limit: 1}
	invalid := []DiscrepancyListRequest{
		{Limit: 1},
		{ActorID: valid.ActorID, Provider: "STRIPE", Limit: 1},
		{ActorID: valid.ActorID, Status: "PENDING", Limit: 1},
		{ActorID: valid.ActorID, Limit: MaxOperationsPageSize + 1},
		{ActorID: valid.ActorID, Limit: 1, After: &DiscrepancyCursor{ID: uuid.New()}},
		{ActorID: valid.ActorID, Limit: 1, After: &DiscrepancyCursor{LastSeenAt: time.Now()}},
	}
	for index, request := range invalid {
		if _, err := operations.List(context.Background(), request); !errors.Is(err, ErrInvalidOperation) {
			t.Fatalf("case %d: expected invalid operation, got %v", index, err)
		}
	}
	if len(repository.listRequests) != 0 {
		t.Fatalf("invalid requests reached repository: %#v", repository.listRequests)
	}
}

func TestOperationsTransitionCanonicalizesAndHMACsIdempotency(t *testing.T) {
	repository := &recordingOperationsRepository{}
	operations, err := NewOperations(repository, OperationsOptions{DigestKey: operationsTestKey()})
	if err != nil {
		t.Fatal(err)
	}
	rawKey := "client-operation-key-0001"
	request := DiscrepancyTransitionRequest{
		ActorID: uuid.New(), DiscrepancyID: uuid.New(), TargetStatus: " acknowledged ",
		ExpectedVersion: 7, ReasonCode: " review_started ", TraceID: " trace-1 ", IdempotencyKey: " " + rawKey + " ",
	}
	if _, err := operations.Transition(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	if len(repository.transitionCommands) != 1 {
		t.Fatalf("transition calls=%d", len(repository.transitionCommands))
	}
	command := repository.transitionCommands[0]
	decoded, err := hex.DecodeString(command.IdempotencyStorageKey)
	if err != nil || len(decoded) != sha256.Size {
		t.Fatalf("invalid storage key %q: %v", command.IdempotencyStorageKey, err)
	}
	if command.IdempotencyStorageKey == rawKey || strings.Contains(command.IdempotencyStorageKey, rawKey) {
		t.Fatal("raw idempotency key leaked into persistence command")
	}
	if command.TargetStatus != DiscrepancyStatusAcknowledged || command.ReasonCode != "REVIEW_STARTED" ||
		command.TraceID != "trace-1" || command.RequestDigest == [sha256.Size]byte{} {
		t.Fatalf("transition was not canonicalized: %#v", command)
	}
}

func TestOperationsTransitionDigestIsStableAndTraceIndependent(t *testing.T) {
	repository := &recordingOperationsRepository{}
	operations, err := NewOperations(repository, OperationsOptions{DigestKey: operationsTestKey()})
	if err != nil {
		t.Fatal(err)
	}
	base := DiscrepancyTransitionRequest{
		ActorID: uuid.New(), DiscrepancyID: uuid.New(), TargetStatus: DiscrepancyStatusAcknowledged,
		ExpectedVersion: 3, ReasonCode: "MANUAL_REVIEW", TraceID: "trace-one", IdempotencyKey: "operation-key-1234",
	}
	requests := []DiscrepancyTransitionRequest{base, base, base, base}
	requests[1].TraceID = "trace-two"
	requests[2].ReasonCode = "REVIEW_STARTED"
	requests[3].TargetStatus = DiscrepancyStatusResolved
	for _, request := range requests {
		if _, err := operations.Transition(context.Background(), request); err != nil {
			t.Fatal(err)
		}
	}
	commands := repository.transitionCommands
	if commands[0].RequestDigest != commands[1].RequestDigest {
		t.Fatal("trace changes must not alter the idempotency request digest")
	}
	if commands[0].RequestDigest == commands[2].RequestDigest || commands[0].RequestDigest == commands[3].RequestDigest {
		t.Fatal("reason or target changes must alter the idempotency request digest")
	}
	if commands[0].IdempotencyStorageKey != commands[1].IdempotencyStorageKey ||
		commands[0].IdempotencyStorageKey != commands[2].IdempotencyStorageKey {
		t.Fatal("storage identity must depend only on the raw idempotency key")
	}
}

func TestOperationsTransitionValidation(t *testing.T) {
	repository := &recordingOperationsRepository{}
	operations, err := NewOperations(repository, OperationsOptions{DigestKey: operationsTestKey()})
	if err != nil {
		t.Fatal(err)
	}
	valid := DiscrepancyTransitionRequest{
		ActorID: uuid.New(), DiscrepancyID: uuid.New(), TargetStatus: DiscrepancyStatusAcknowledged,
		ExpectedVersion: 1, ReasonCode: "MANUAL_REVIEW", TraceID: "trace", IdempotencyKey: "operation-key",
	}
	invalid := []DiscrepancyTransitionRequest{
		func() DiscrepancyTransitionRequest { value := valid; value.ActorID = uuid.Nil; return value }(),
		func() DiscrepancyTransitionRequest { value := valid; value.DiscrepancyID = uuid.Nil; return value }(),
		func() DiscrepancyTransitionRequest { value := valid; value.ExpectedVersion = 0; return value }(),
		func() DiscrepancyTransitionRequest {
			value := valid
			value.TargetStatus = DiscrepancyStatusOpen
			return value
		}(),
		func() DiscrepancyTransitionRequest { value := valid; value.ReasonCode = "no spaces"; return value }(),
		func() DiscrepancyTransitionRequest { value := valid; value.TraceID = "bad\ntrace"; return value }(),
		func() DiscrepancyTransitionRequest { value := valid; value.IdempotencyKey = "short"; return value }(),
		func() DiscrepancyTransitionRequest {
			value := valid
			value.IdempotencyKey = "invalid key"
			return value
		}(),
	}
	for index, request := range invalid {
		if _, err := operations.Transition(context.Background(), request); !errors.Is(err, ErrInvalidOperation) {
			t.Fatalf("case %d: expected invalid operation, got %v", index, err)
		}
	}
	if len(repository.transitionCommands) != 0 {
		t.Fatalf("invalid requests reached repository: %#v", repository.transitionCommands)
	}
}

func TestAllowedDiscrepancyTransitionsAreStrict(t *testing.T) {
	statuses := []string{
		DiscrepancyStatusOpen, DiscrepancyStatusAcknowledged,
		DiscrepancyStatusResolved, DiscrepancyStatusIgnored,
	}
	allowed := map[string]bool{
		DiscrepancyStatusOpen + ">" + DiscrepancyStatusAcknowledged:     true,
		DiscrepancyStatusAcknowledged + ">" + DiscrepancyStatusResolved: true,
		DiscrepancyStatusAcknowledged + ">" + DiscrepancyStatusIgnored:  true,
	}
	for _, from := range statuses {
		for _, to := range statuses {
			key := from + ">" + to
			if got := allowedDiscrepancyTransition(from, to); got != allowed[key] {
				t.Fatalf("transition %s: got %v want %v", key, got, allowed[key])
			}
		}
	}
}

func TestOperationalDiscrepancyValidationAllowsDeletedResolver(t *testing.T) {
	now := time.Now().UTC()
	item := OperationalDiscrepancy{
		ID: uuid.New(), RunID: uuid.New(), Provider: string(payment.ProviderAlipay), Category: "OTHER",
		SummaryCode: "MANUAL_DIFFERENCE", Status: DiscrepancyStatusOpen, Version: 1,
		FirstSeenAt: now.Add(-3 * time.Hour), LastSeenAt: now.Add(-2 * time.Hour),
		CreatedAt: now.Add(-3 * time.Hour), UpdatedAt: now.Add(-time.Hour),
	}
	if !validOperationalDiscrepancy(item) {
		t.Fatal("valid OPEN discrepancy rejected")
	}
	acknowledgedAt := now.Add(-90 * time.Minute)
	item.Status = DiscrepancyStatusAcknowledged
	item.AcknowledgedAt = &acknowledgedAt
	if !validOperationalDiscrepancy(item) {
		t.Fatal("valid ACKNOWLEDGED discrepancy rejected")
	}
	resolvedAt := now.Add(-time.Hour)
	resolutionCode := "MANUAL_REVIEW"
	item.Status = DiscrepancyStatusResolved
	item.ResolvedAt = &resolvedAt
	item.ResolutionCode = &resolutionCode
	item.ResolvedBy = nil // users(id) is ON DELETE SET NULL.
	if !validOperationalDiscrepancy(item) {
		t.Fatal("historical resolution with a deleted actor was rejected")
	}
	item.AcknowledgedAt = nil
	if validOperationalDiscrepancy(item) {
		t.Fatal("resolution that bypassed acknowledgement was accepted")
	}
}

func TestOperationalDiscrepancyJSONIsRedacted(t *testing.T) {
	now := time.Now().UTC()
	item := OperationalDiscrepancy{
		ID: uuid.New(), RunID: uuid.New(), Provider: string(payment.ProviderWeChatPay), Category: "OTHER",
		SummaryCode: "REDACTED_DIFFERENCE", Status: DiscrepancyStatusOpen, Version: 1,
		FirstSeenAt: now, LastSeenAt: now, CreatedAt: now, UpdatedAt: now,
	}
	payload, err := json.Marshal(item)
	if err != nil {
		t.Fatal(err)
	}
	lower := strings.ToLower(string(payload))
	for _, forbidden := range []string{
		"discrepancy_key", "external_reference_hmac", "trade", "reference", "hash", "raw", "statement",
	} {
		if strings.Contains(lower, forbidden) {
			t.Fatalf("redacted DTO contains forbidden token %q: %s", forbidden, payload)
		}
	}
}

func TestOperationsSQLContracts(t *testing.T) {
	normalize := func(value string) string { return strings.ToLower(strings.Join(strings.Fields(value), " ")) }
	list := normalize(listOperationalDiscrepanciesSQL)
	for _, expected := range []string{
		"d.status in('open','acknowledged')", "(d.last_seen_at,d.id)<($5,$6)",
		"order by d.last_seen_at desc,d.id desc", "limit $7",
	} {
		if !strings.Contains(list, expected) {
			t.Fatalf("list SQL lacks %q: %s", expected, list)
		}
	}

	update := normalize(updateOperationalDiscrepancySQL)
	for _, expected := range []string{
		"where d.id=$1 and d.version=$5 and d.status=$6", "version=d.version+1", "returning",
	} {
		if !strings.Contains(update, expected) {
			t.Fatalf("update SQL lacks %q: %s", expected, update)
		}
	}

	claim := normalize(claimOperationsIdempotencySQL)
	complete := normalize(completeOperationsIdempotencySQL)
	if !strings.Contains(claim, "insert into idempotency_keys") ||
		!strings.Contains(claim, "on conflict(user_id,idempotency_key)") ||
		!strings.Contains(complete, "request_hash=$3 and response_status is null") {
		t.Fatalf("incomplete idempotency SQL contract: claim=%s complete=%s", claim, complete)
	}

	audit := normalize(auditOperationalDiscrepancySQL)
	for _, expected := range []string{
		"insert into audit_events", "user_id", "trace_id", "reason_code", "from_status", "to_status", "version",
	} {
		if !strings.Contains(audit, expected) {
			t.Fatalf("audit SQL lacks %q: %s", expected, audit)
		}
	}
	outbox := normalize(outboxOperationalDiscrepancySQL)
	for _, expected := range []string{
		"insert into outbox", "payment.reconciliation.discrepancy.updated", "discrepancy_id", "provider", "category", "status", "version",
	} {
		if !strings.Contains(outbox, expected) {
			t.Fatalf("outbox SQL lacks %q: %s", expected, outbox)
		}
	}
	for _, forbidden := range []string{"amount", "currency", "order", "transaction", "reference", "hmac", "statement"} {
		if strings.Contains(outbox, forbidden) {
			t.Fatalf("outbox leaks forbidden field %q: %s", forbidden, outbox)
		}
	}

	mutations := normalize(strings.Join([]string{
		claimOperationsIdempotencySQL, updateOperationalDiscrepancySQL, auditOperationalDiscrepancySQL,
		outboxOperationalDiscrepancySQL, completeOperationsIdempotencySQL,
	}, " "))
	for _, table := range []string{"orders", "payment_transactions", "refunds"} {
		pattern := regexp.MustCompile(`(?:update|insert into|delete from)\s+` + table + `\b`)
		if pattern.MatchString(mutations) {
			t.Fatalf("operational transition mutates commerce table %s: %s", table, mutations)
		}
	}
}

func TestPostgresOperationsStateMachineIdempotencyAndAudit(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the operations integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(pool.Close)
	if err := migrate.UpPool(ctx, pool); err != nil {
		t.Fatal(err)
	}

	actorID := uuid.New()
	runID := uuid.New()
	discrepancyID := uuid.New()
	subjectHash := sha256.Sum256(append([]byte("operations-integration-actor-v1"), actorID[:]...))
	discrepancyKey := sha256.Sum256(append([]byte("operations-integration-discrepancy-v1"), discrepancyID[:]...))
	externalHMAC := sha256.Sum256(append([]byte("operations-integration-external-v1"), discrepancyID[:]...))
	periodStart := time.Date(2600+int(runID[0]), time.Month(runID[1]%12+1), int(runID[2]%20+1), 0, 0, 0, 0, time.UTC)
	if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,role,is_admin) VALUES($1,$2,'ADMIN',true)`, actorID, subjectHash[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `INSERT INTO reconciliation_runs(id,provider,period_start,period_end,status) VALUES($1,'ALIPAY',$2,$3,'SUCCEEDED')`, runID, periodStart, periodStart.AddDate(0, 0, 1)); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO reconciliation_discrepancies(
			id,run_id,discrepancy_key,provider,category,external_reference_hmac,summary_code
		) VALUES($1,$2,$3,'ALIPAY','OTHER',$4,'MANUAL_DIFFERENCE')`, discrepancyID, runID, discrepancyKey[:], externalHMAC[:]); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = pool.Exec(cleanupContext, `DELETE FROM outbox WHERE aggregate_id=$1`, discrepancyID)
		_, _ = pool.Exec(cleanupContext, `DELETE FROM audit_events WHERE target_id=$1 OR user_id=$2`, discrepancyID, actorID)
		_, _ = pool.Exec(cleanupContext, `DELETE FROM idempotency_keys WHERE user_id=$1`, actorID)
		_, _ = pool.Exec(cleanupContext, `DELETE FROM reconciliation_discrepancies WHERE id=$1`, discrepancyID)
		_, _ = pool.Exec(cleanupContext, `DELETE FROM reconciliation_runs WHERE id=$1`, runID)
		_, _ = pool.Exec(cleanupContext, `DELETE FROM users WHERE id=$1`, actorID)
	})

	repository, err := NewPostgresOperationsRepository(&store.Postgres{Pool: pool})
	if err != nil {
		t.Fatal(err)
	}
	operations, err := NewOperations(repository, OperationsOptions{DigestKey: operationsTestKey()})
	if err != nil {
		t.Fatal(err)
	}
	acknowledge := DiscrepancyTransitionRequest{
		ActorID: actorID, DiscrepancyID: discrepancyID, TargetStatus: DiscrepancyStatusAcknowledged,
		ExpectedVersion: 1, ReasonCode: "MANUAL_REVIEW", TraceID: "integration-ack-1", IdempotencyKey: "integration-operation-ack",
	}
	acknowledged, err := operations.Transition(ctx, acknowledge)
	if err != nil {
		t.Fatal(err)
	}
	if acknowledged.Status != DiscrepancyStatusAcknowledged || acknowledged.Version != 2 || acknowledged.AcknowledgedAt == nil {
		t.Fatalf("unexpected acknowledge result: %#v", acknowledged)
	}
	acknowledge.TraceID = "integration-ack-retry"
	replayed, err := operations.Transition(ctx, acknowledge)
	if err != nil {
		t.Fatal(err)
	}
	if replayed.ID != acknowledged.ID || replayed.Status != acknowledged.Status || replayed.Version != acknowledged.Version ||
		!replayed.UpdatedAt.Equal(acknowledged.UpdatedAt) {
		t.Fatalf("idempotent replay changed the result: first=%#v replay=%#v", acknowledged, replayed)
	}
	conflict := acknowledge
	conflict.ReasonCode = "DIFFERENT_REASON"
	if _, err := operations.Transition(ctx, conflict); !errors.Is(err, ErrIdempotencyConflict) {
		t.Fatalf("same key with a different request: %v", err)
	}

	resolved, err := operations.Transition(ctx, DiscrepancyTransitionRequest{
		ActorID: actorID, DiscrepancyID: discrepancyID, TargetStatus: DiscrepancyStatusResolved,
		ExpectedVersion: 2, ReasonCode: "MANUAL_REVIEW", TraceID: "integration-resolve", IdempotencyKey: "integration-operation-resolve",
	})
	if err != nil {
		t.Fatal(err)
	}
	if resolved.Status != DiscrepancyStatusResolved || resolved.Version != 3 || resolved.ResolvedAt == nil ||
		resolved.ResolvedBy == nil || *resolved.ResolvedBy != actorID || resolved.ResolutionCode == nil || *resolved.ResolutionCode != "MANUAL_REVIEW" {
		t.Fatalf("unexpected resolve result: %#v", resolved)
	}

	defaultPage, err := operations.List(ctx, DiscrepancyListRequest{ActorID: actorID})
	if err != nil {
		t.Fatal(err)
	}
	if len(defaultPage.Items) != 0 {
		t.Fatalf("default actionable list includes resolved discrepancy: %#v", defaultPage.Items)
	}
	resolvedPage, err := operations.List(ctx, DiscrepancyListRequest{
		ActorID: actorID, Status: DiscrepancyStatusResolved, RunID: runID, Limit: 1,
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(resolvedPage.Items) != 1 || resolvedPage.Items[0].ID != discrepancyID {
		t.Fatalf("resolved filter failed: %#v", resolvedPage)
	}

	var auditCount, outboxCount int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM audit_events WHERE target_type='reconciliation_discrepancy' AND target_id=$1 AND user_id=$2`, discrepancyID, actorID).Scan(&auditCount); err != nil {
		t.Fatal(err)
	}
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM outbox WHERE event_type='payment.reconciliation.discrepancy.updated' AND aggregate_id=$1`, discrepancyID).Scan(&outboxCount); err != nil {
		t.Fatal(err)
	}
	if auditCount != 2 || outboxCount != 2 {
		t.Fatalf("audit/outbox were not exactly-once: audit=%d outbox=%d", auditCount, outboxCount)
	}
	var rawKeyCount int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM idempotency_keys WHERE user_id=$1 AND idempotency_key IN($2,$3)`, actorID, "integration-operation-ack", "integration-operation-resolve").Scan(&rawKeyCount); err != nil {
		t.Fatal(err)
	}
	if rawKeyCount != 0 {
		t.Fatal("raw idempotency key was persisted")
	}
}
