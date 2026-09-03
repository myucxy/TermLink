package store

import (
	"bytes"
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestStablePCMachineIdentityUniqueViolationIsConflict(t *testing.T) {
	err := &pgconn.PgError{Code: "23505", ConstraintName: "pcs_owner_machine_identity_unique"}
	if !isStablePCMachineIdentityConflict(err) {
		t.Fatal("stable machine identity unique violation was not classified as a conflict")
	}
	for _, other := range []*pgconn.PgError{
		{Code: "23505", ConstraintName: "another_constraint"},
		{Code: "40001", ConstraintName: "pcs_owner_machine_identity_unique"},
	} {
		if isStablePCMachineIdentityConflict(other) {
			t.Fatalf("unrelated PostgreSQL error was classified as a stable identity conflict: %#v", other)
		}
	}
}

func TestStablePCMachineIdentityReusesRouteAndRotatesDeviceKey(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the PC machine identity integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	if err := migrate.UpPool(ctx, pool); err != nil {
		t.Fatal(err)
	}
	repository := &Postgres{Pool: pool, AutoTrustDevices: true}
	userID, firstDeviceID, rotatedDeviceID, staleDeviceID, dependentDeviceID, pcID :=
		uuid.New(), uuid.New(), uuid.New(), uuid.New(), uuid.New(), uuid.New()
	machineID := sha256.Sum256([]byte("stable-pc-machine:" + pcID.String()))
	subject := sha256.Sum256([]byte("stable-pc-subject:" + userID.String()))
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=ANY($1::uuid[])`, []uuid.UUID{firstDeviceID, rotatedDeviceID})
		_, _ = pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
	}()
	if _, err := pool.Exec(ctx, `
		INSERT INTO users(id,oidc_subject_hash,windows_bootstrap_consumed_at) VALUES($1,$2,now())`,
		userID, subject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO devices(
			id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,
			trust_state,approved_by,approved_at)
		VALUES
			($1,$2,'PC_AGENT','Stable PC',decode(repeat('61',32),'hex'),decode(repeat('62',32),'hex'),$3,'TRUSTED',NULL,now()),
			($4,$2,'PC_AGENT','Stable PC',decode(repeat('63',32),'hex'),decode(repeat('64',32),'hex'),$5,'TRUSTED',$1,now()),
			($6,$2,'PC_AGENT','Stale contender',decode(repeat('67',32),'hex'),decode(repeat('68',32),'hex'),$7,'TRUSTED',$1,now()),
			($8,$2,'PC_AGENT','Dependent PC',decode(repeat('65',32),'hex'),decode(repeat('66',32),'hex'),$9,'TRUSTED',$1,now())`,
		firstDeviceID, userID, "stable-first-"+firstDeviceID.String(),
		rotatedDeviceID, "stable-rotated-"+rotatedDeviceID.String(),
		staleDeviceID, "stable-stale-"+staleDeviceID.String(),
		dependentDeviceID, "stable-dependent-"+dependentDeviceID.String()); err != nil {
		t.Fatal(err)
	}
	fixture := PCRecord{
		ID: pcID, Name: "Stable PC", AgentVersion: "0.4.0", KeyEpoch: 7,
		Platform: PCPlatformWindows, Architecture: PCArchitectureX64, PlatformVersion: "Windows",
		Capabilities:  []string{PCCapabilityRealtimeTerminalV1},
		MachineIDHash: machineID[:],
	}
	if _, err := repository.UpsertPCResolved(ctx, userID, firstDeviceID, fixture, "stable-pc-bind"); err != nil {
		t.Fatal(err)
	}
	legacyFixture := fixture
	legacyFixture.MachineIDHash = nil
	if _, err := repository.UpsertPCResolved(ctx, userID, firstDeviceID, legacyFixture, "stable-pc-legacy-heartbeat"); err != nil {
		t.Fatalf("authenticated legacy Agent could not update its bound PC: %v", err)
	}
	var retainedMachine []byte
	if err := pool.QueryRow(ctx, `SELECT machine_id_hash FROM pcs WHERE id=$1`, pcID).Scan(&retainedMachine); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(retainedMachine, machineID[:]) {
		t.Fatalf("legacy Agent cleared stable machine identity: %x", retainedMachine)
	}
	resolved, found, err := repository.ResolvePCMachine(ctx, subject, machineID[:])
	if err != nil || !found || resolved.ID != pcID || resolved.KeyEpoch != 7 || resolved.Name != "Stable PC" {
		t.Fatalf("ResolvePCMachine() = %#v, %t, %v", resolved, found, err)
	}
	result, err := repository.UpsertPCResolved(ctx, userID, rotatedDeviceID, fixture, "stable-pc-rotate")
	if err != nil {
		t.Fatal(err)
	}
	if result.ID != pcID || result.ReplacedDeviceID == nil || *result.ReplacedDeviceID != firstDeviceID {
		t.Fatalf("rotation result = %#v", result)
	}
	if _, err := repository.UpsertPCResolved(ctx, userID, staleDeviceID, fixture, "stable-pc-stale"); !errors.Is(err, ErrConflict) {
		t.Fatalf("stale replacement result = %v, want ErrConflict", err)
	}
	var storedDevice uuid.UUID
	var storedMachine []byte
	var storedEpoch int64
	if err := pool.QueryRow(ctx, `SELECT device_id,machine_id_hash,key_epoch FROM pcs WHERE id=$1`, pcID).
		Scan(&storedDevice, &storedMachine, &storedEpoch); err != nil {
		t.Fatal(err)
	}
	if storedDevice != rotatedDeviceID || !bytes.Equal(storedMachine, machineID[:]) || storedEpoch != 8 {
		t.Fatalf("rotated PC state = %s/%x/%d", storedDevice, storedMachine, storedEpoch)
	}
	var firstTrust string
	var rotatedApprover, dependentApprover *uuid.UUID
	if err := pool.QueryRow(ctx, `SELECT trust_state FROM devices WHERE id=$1`, firstDeviceID).Scan(&firstTrust); err != nil {
		t.Fatal(err)
	}
	if err := pool.QueryRow(ctx, `SELECT approved_by FROM devices WHERE id=$1`, rotatedDeviceID).Scan(&rotatedApprover); err != nil {
		t.Fatal(err)
	}
	if err := pool.QueryRow(ctx, `SELECT approved_by FROM devices WHERE id=$1`, dependentDeviceID).Scan(&dependentApprover); err != nil {
		t.Fatal(err)
	}
	if firstTrust != "REVOKED" || rotatedApprover != nil ||
		dependentApprover == nil || *dependentApprover != rotatedDeviceID {
		t.Fatalf("bootstrap rotation state = %q/%v/%v", firstTrust, rotatedApprover, dependentApprover)
	}
	var outboxCount, auditCount int
	if err := pool.QueryRow(ctx, `
		SELECT
			(SELECT count(*) FROM outbox WHERE event_type='device.revoked' AND aggregate_id=$1),
			(SELECT count(*) FROM audit_events WHERE user_id=$2 AND action='pc.machine_identity.rotate' AND target_id=$3)`,
		firstDeviceID, userID, pcID).Scan(&outboxCount, &auditCount); err != nil {
		t.Fatal(err)
	}
	if outboxCount != 1 || auditCount != 1 {
		t.Fatalf("rotation outbox/audit = %d/%d", outboxCount, auditCount)
	}

	concurrentPCID := uuid.New()
	concurrentOldDeviceID, contenderAID, contenderBID := uuid.New(), uuid.New(), uuid.New()
	concurrentMachineID := sha256.Sum256([]byte("stable-pc-concurrent:" + concurrentPCID.String()))
	if _, err := pool.Exec(ctx, `
		INSERT INTO devices(
			id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,
			trust_state,approved_by,approved_at)
		VALUES
			($1,$2,'PC_AGENT','Concurrent old',decode(repeat('71',32),'hex'),decode(repeat('72',32),'hex'),$3,'TRUSTED',$4,now()),
			($5,$2,'PC_AGENT','Concurrent A',decode(repeat('73',32),'hex'),decode(repeat('74',32),'hex'),$6,'TRUSTED',$4,now()),
			($7,$2,'PC_AGENT','Concurrent B',decode(repeat('75',32),'hex'),decode(repeat('76',32),'hex'),$8,'TRUSTED',$4,now())`,
		concurrentOldDeviceID, userID, "concurrent-old-"+concurrentOldDeviceID.String(), rotatedDeviceID,
		contenderAID, "concurrent-a-"+contenderAID.String(),
		contenderBID, "concurrent-b-"+contenderBID.String()); err != nil {
		t.Fatal(err)
	}
	concurrentFixture := fixture
	concurrentFixture.ID = concurrentPCID
	concurrentFixture.Name = "Concurrent stable PC"
	concurrentFixture.KeyEpoch = 11
	concurrentFixture.MachineIDHash = concurrentMachineID[:]
	if _, err := repository.UpsertPCResolved(
		ctx, userID, concurrentOldDeviceID, concurrentFixture, "stable-pc-concurrent-bind"); err != nil {
		t.Fatal(err)
	}

	start := make(chan struct{})
	results := make(chan error, 2)
	for _, contenderID := range []uuid.UUID{contenderAID, contenderBID} {
		go func(deviceID uuid.UUID) {
			<-start
			_, contenderErr := repository.UpsertPCResolved(
				ctx, userID, deviceID, concurrentFixture, "stable-pc-concurrent-rotate")
			results <- contenderErr
		}(contenderID)
	}
	close(start)
	var succeeded, conflicted int
	for range 2 {
		switch contenderErr := <-results; {
		case contenderErr == nil:
			succeeded++
		case errors.Is(contenderErr, ErrConflict):
			conflicted++
		default:
			t.Fatalf("concurrent replacement returned unexpected error: %v", contenderErr)
		}
	}
	if succeeded != 1 || conflicted != 1 {
		t.Fatalf("concurrent replacement success/conflict = %d/%d, want 1/1", succeeded, conflicted)
	}
	var concurrentDeviceID uuid.UUID
	var concurrentEpoch int64
	if err := pool.QueryRow(ctx, `SELECT device_id,key_epoch FROM pcs WHERE id=$1`, concurrentPCID).
		Scan(&concurrentDeviceID, &concurrentEpoch); err != nil {
		t.Fatal(err)
	}
	if (concurrentDeviceID != contenderAID && concurrentDeviceID != contenderBID) || concurrentEpoch != 12 {
		t.Fatalf("concurrent replacement state = %s/%d, want one contender at epoch 12",
			concurrentDeviceID, concurrentEpoch)
	}
	var concurrentAuditCount int
	if err := pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events
		WHERE user_id=$1 AND action='pc.machine_identity.rotate' AND target_id=$2`,
		userID, concurrentPCID).Scan(&concurrentAuditCount); err != nil {
		t.Fatal(err)
	}
	if concurrentAuditCount != 1 {
		t.Fatalf("concurrent replacement audit count = %d, want 1", concurrentAuditCount)
	}
}
