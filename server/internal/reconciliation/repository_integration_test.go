package reconciliation

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

func TestPostgresRepositoryLeaseAndAtomicDiscrepancyUpsert(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the repository integration test")
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
	repository, err := NewPostgresRepository(&store.Postgres{Pool: pool})
	if err != nil {
		t.Fatal(err)
	}

	runOne, runTwo := uuid.New(), uuid.New()
	periodOne := time.Date(3000+int(binary.BigEndian.Uint16(runOne[:2])), time.Month(runOne[2]%12+1), int(runOne[3]%24+1), 0, 0, 0, 0, time.UTC)
	periodTwo := periodOne.AddDate(0, 0, 2)
	for _, run := range []struct {
		id    uuid.UUID
		start time.Time
	}{{runOne, periodOne}, {runTwo, periodTwo}} {
		if _, err := pool.Exec(ctx, `INSERT INTO reconciliation_runs(id,provider,period_start,period_end,status) VALUES($1,'ALIPAY',$2,$3,'PENDING')`, run.id, run.start, run.start.AddDate(0, 0, 1)); err != nil {
			t.Fatal(err)
		}
	}
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = pool.Exec(cleanupContext, `DELETE FROM reconciliation_discrepancies WHERE run_id=ANY($1::uuid[])`, []uuid.UUID{runOne, runTwo})
		_, _ = pool.Exec(cleanupContext, `DELETE FROM reconciliation_runs WHERE id=ANY($1::uuid[])`, []uuid.UUID{runOne, runTwo})
	})

	lease, acquired, err := repository.TryAcquireReconciliationRun(ctx, runOne)
	if err != nil || !acquired || lease == nil {
		t.Fatalf("first acquire: lease=%v acquired=%v err=%v", lease, acquired, err)
	}
	secondLease, secondAcquired, err := repository.TryAcquireReconciliationRun(ctx, runOne)
	if err != nil || secondAcquired || secondLease != nil {
		t.Fatalf("concurrent acquire: lease=%v acquired=%v err=%v", secondLease, secondAcquired, err)
	}
	lease.Release()

	key := [32]byte{1, 2, 3}
	externalHMAC := bytes.Repeat([]byte{4}, 32)
	discrepancy := Discrepancy{
		Key: key, Provider: string(payment.ProviderAlipay), Category: "MISSING_LOCAL",
		ExternalReferenceHMAC: externalHMAC, ProviderStatus: pointerString("SUCCEEDED"),
		SummaryCode: "PROVIDER_ENTRY_WITHOUT_LOCAL_RECORD", ProviderAmountMinor: pointerInt64(123), ProviderCurrency: pointerString("CNY"),
	}
	summary, _ := json.Marshal(Summary{SchemaVersion: 1, Provider: "ALIPAY", Result: "DIFFERENCES"})
	if _, err := repository.CompleteReconciliationRun(ctx, runOne, true, summary, []Discrepancy{discrepancy}); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `UPDATE reconciliation_discrepancies SET status='RESOLVED',resolved_at=now(),resolution_code='MANUAL_REVIEW' WHERE discrepancy_key=$1`, key[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.CompleteReconciliationRun(ctx, runTwo, true, summary, []Discrepancy{discrepancy}); err != nil {
		t.Fatal(err)
	}
	var runID uuid.UUID
	var status string
	var version int64
	var resolvedAt *time.Time
	var resolutionCode *string
	var storedExternal []byte
	if err := pool.QueryRow(ctx, `SELECT run_id,status,version,resolved_at,resolution_code,external_reference_hmac FROM reconciliation_discrepancies WHERE discrepancy_key=$1`, key[:]).Scan(
		&runID, &status, &version, &resolvedAt, &resolutionCode, &storedExternal); err != nil {
		t.Fatal(err)
	}
	if runID != runTwo || status != "OPEN" || version != 2 || resolvedAt != nil || resolutionCode != nil || !bytes.Equal(storedExternal, externalHMAC) {
		t.Fatalf("unexpected upsert: run=%v status=%s version=%d resolved=%v code=%v external=%x", runID, status, version, resolvedAt, resolutionCode, storedExternal)
	}
}

func pointerString(value string) *string { return &value }
func pointerInt64(value int64) *int64    { return &value }
