package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"slices"
	"testing"
	"time"

	"github.com/google/uuid"
)

// TestDeviceAuthorizationMatrix exercises the production PostgreSQL
// authorization predicates directly. It is intentionally kept as a database
// integration test because the important behavior is the interaction between
// trusted devices, grants, PC ownership, and revocation transactions.
func TestDeviceAuthorizationMatrix(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}
	ctx := context.Background()
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	userID := uuid.New()
	otherUserID := uuid.New()
	t.Cleanup(func() {
		_, _ = repository.Pool.Exec(ctx, `
			DELETE FROM outbox
			WHERE payload->>'user_id'=ANY($1::text[])
			   OR payload->>'account_id'=ANY($1::text[])`,
			[]string{userID.String(), otherUserID.String()})
		_, _ = repository.Pool.Exec(ctx, `DELETE FROM audit_events WHERE user_id=ANY($1::uuid[])`, []uuid.UUID{userID, otherUserID})
		for _, id := range []uuid.UUID{userID, otherUserID} {
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM users WHERE id=$1`, id)
		}
		repository.Close()
	})
	if err := repository.VerifySchema(ctx); err != nil {
		t.Fatal(err)
	}
	insertMatrixUser(t, repository, ctx, userID, "device-authorization-matrix")
	insertMatrixUser(t, repository, ctx, otherUserID, "device-authorization-cross-account")

	// The bootstrap PC owns pcOwner. The other PCs are trusted devices approved
	// by that bootstrap PC, matching the production bootstrap exception.
	bootstrapPC := uuid.New()
	ownerPC2 := uuid.New()
	ownerPC3 := uuid.New()
	insertMatrixDevice(t, repository, ctx, userID, bootstrapPC, "WINDOWS_PC", "Bootstrap PC", "TRUSTED", nil)
	insertMatrixDevice(t, repository, ctx, userID, ownerPC2, "WINDOWS_PC", "Second PC", "TRUSTED", &bootstrapPC)
	insertMatrixDevice(t, repository, ctx, userID, ownerPC3, "WINDOWS_PC", "Third PC", "TRUSTED", &bootstrapPC)

	pcOwner := uuid.New()
	pcTwo := uuid.New()
	pcThree := uuid.New()
	insertMatrixPC(t, repository, ctx, userID, pcOwner, bootstrapPC, "Owner route", 7)
	insertMatrixPC(t, repository, ctx, userID, pcTwo, ownerPC2, "Second route", 11)
	insertMatrixPC(t, repository, ctx, userID, pcThree, ownerPC3, "Third route", 13)

	grantTarget := uuid.New()
	manageTarget := uuid.New()
	viewTarget := uuid.New()
	controlTarget := uuid.New()
	expiredManageTarget := uuid.New()
	pendingTarget := uuid.New()
	revokedTarget := uuid.New()
	multiPCTarget := uuid.New()
	selfTarget := uuid.New()
	manageActor := uuid.New()
	viewController := uuid.New()
	controlController := uuid.New()
	expiredManager := uuid.New()
	partialManager := uuid.New()
	selfActor := selfTarget
	for _, device := range []struct {
		id    uuid.UUID
		name  string
		state string
	}{
		{grantTarget, "Grant target", "TRUSTED"},
		{manageTarget, "MANAGE target", "TRUSTED"},
		{viewTarget, "VIEW target", "TRUSTED"},
		{controlTarget, "CONTROL target", "TRUSTED"},
		{expiredManageTarget, "Expired MANAGE target", "TRUSTED"},
		{pendingTarget, "Pending target", "PENDING"},
		{revokedTarget, "Revoked target", "REVOKED"},
		{multiPCTarget, "Multi-PC target", "TRUSTED"},
		{selfTarget, "Self revoke target", "TRUSTED"},
		{manageActor, "MANAGE actor", "TRUSTED"},
		{viewController, "VIEW controller", "TRUSTED"},
		{controlController, "CONTROL controller", "TRUSTED"},
		{expiredManager, "Expired MANAGE actor", "TRUSTED"},
		{partialManager, "Partial MANAGE actor", "TRUSTED"},
	} {
		insertMatrixDevice(t, repository, ctx, userID, device.id, "ANDROID", device.name, device.state, nil)
	}
	crossAccountTarget := uuid.New()
	insertMatrixDevice(t, repository, ctx, otherUserID, crossAccountTarget, "ANDROID", "Cross-account target", "TRUSTED", nil)

	// A PC owner can grant any trusted target, and a trusted MANAGE delegate
	// can grant on that same route.
	if err := repository.PutGrant(ctx, userID, bootstrapPC, grantTarget, pcOwner, "VIEW", nil, "matrix-owner-grant"); err != nil {
		t.Fatalf("PC owner PutGrant: %v", err)
	}
	var permission string
	if err := repository.Pool.QueryRow(ctx, `SELECT permission FROM device_grants WHERE device_id=$1 AND pc_id=$2`, grantTarget, pcOwner).Scan(&permission); err != nil {
		t.Fatal(err)
	}
	if permission != "VIEW" {
		t.Fatalf("owner grant permission=%q, want VIEW", permission)
	}
	insertMatrixGrant(t, repository, ctx, manageActor, pcOwner, "MANAGE", nil, bootstrapPC)
	if err := repository.PutGrant(ctx, userID, manageActor, manageTarget, pcOwner, "CONTROL", nil, "matrix-manager-grant"); err != nil {
		t.Fatalf("valid MANAGE PutGrant: %v", err)
	}
	grantLeaseSession := uuid.New()
	grantLeaseID := uuid.New()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows)
		VALUES($1,$2,'pwsh','RUNNING',80,24)`, grantLeaseSession, pcOwner); err != nil {
		t.Fatal(err)
	}
	if err := repository.SaveLease(ctx, userID, manageTarget, grantLeaseID, grantLeaseSession, manageTarget, time.Now().UTC().Add(30*time.Second), 1, "lease.acquire", "matrix-lease-acquire"); err != nil {
		t.Fatal(err)
	}
	replacementLeaseID := uuid.New()
	replacementExpiry := time.Now().UTC().Add(45 * time.Second)
	if err := repository.SaveLease(ctx, userID, manageTarget, replacementLeaseID, grantLeaseSession, manageTarget, replacementExpiry, 2, "", "matrix-lease-replacement"); err != nil {
		t.Fatal(err)
	}
	if err := repository.SaveLease(ctx, userID, manageTarget, grantLeaseID, grantLeaseSession, manageTarget, time.Now().UTC().Add(time.Minute), 1, "", "matrix-stale-lease"); !errors.Is(err, ErrConflict) {
		t.Fatalf("stale SaveLease error=%v, want ErrConflict", err)
	}
	if err := repository.SaveLease(ctx, userID, manageTarget, replacementLeaseID, grantLeaseSession, manageTarget, replacementExpiry.Add(-time.Second), 2, "", "matrix-stale-renew"); err != nil {
		t.Fatalf("same-lease stale renewal error=%v", err)
	}
	var durableLeaseID uuid.UUID
	var durableVersion int64
	var durableExpiry time.Time
	if err := repository.Pool.QueryRow(ctx, `SELECT lease_id,version,expires_at FROM control_leases WHERE session_id=$1`, grantLeaseSession).Scan(&durableLeaseID, &durableVersion, &durableExpiry); err != nil {
		t.Fatal(err)
	}
	if durableLeaseID != replacementLeaseID || durableVersion != 2 || durableExpiry.Before(replacementExpiry.Add(-time.Millisecond)) {
		t.Fatalf("durable lease=%s version=%d expiry=%s", durableLeaseID, durableVersion, durableExpiry)
	}

	// A force release must carry the exact cache lease as a fencing token. If a
	// replacement is persisted before the durable half runs, the old request
	// must not delete that replacement.
	fenceSessionID := uuid.New()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows)
		VALUES($1,$2,'pwsh','RUNNING',80,24)`, fenceSessionID, pcOwner); err != nil {
		t.Fatal(err)
	}
	oldFenceLeaseID := uuid.New()
	newFenceLeaseID := uuid.New()
	if err := repository.SaveLease(ctx, userID, manageTarget, oldFenceLeaseID, fenceSessionID, manageTarget, time.Now().UTC().Add(20*time.Second), 10, "", "matrix-force-old"); err != nil {
		t.Fatal(err)
	}
	if err := repository.SaveLease(ctx, userID, manageTarget, newFenceLeaseID, fenceSessionID, manageTarget, time.Now().UTC().Add(30*time.Second), 11, "", "matrix-force-new"); err != nil {
		t.Fatal(err)
	}
	if err := repository.ForceDeleteLease(ctx, userID, manageActor, fenceSessionID, oldFenceLeaseID, 10, "lease.force_release", "matrix-force-stale"); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT lease_id,version FROM control_leases WHERE session_id=$1`, fenceSessionID).Scan(&durableLeaseID, &durableVersion); err != nil {
		t.Fatal(err)
	}
	if durableLeaseID != newFenceLeaseID || durableVersion != 11 {
		t.Fatalf("stale force release removed replacement lease=%s version=%d", durableLeaseID, durableVersion)
	}
	if err := repository.ForceDeleteLease(ctx, userID, manageActor, fenceSessionID, newFenceLeaseID, 11, "lease.force_release", "matrix-force-current"); err != nil {
		t.Fatal(err)
	}
	assertMatrixLeaseRemoved(t, repository, ctx, fenceSessionID)

	// Valkey is reconstructible and its version counter can restart at one.
	// An expired durable fence must therefore allow the live cache lease to
	// recover even when its version is numerically lower.
	recoverySessionID := uuid.New()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows)
		VALUES($1,$2,'pwsh','RUNNING',80,24)`, recoverySessionID, pcOwner); err != nil {
		t.Fatal(err)
	}
	expiredLeaseID := uuid.New()
	recoveredLeaseID := uuid.New()
	if err := repository.SaveLease(ctx, userID, manageTarget, expiredLeaseID, recoverySessionID, manageTarget, time.Now().UTC().Add(-time.Minute), 99, "", "matrix-expired-fence"); err != nil {
		t.Fatal(err)
	}
	if err := repository.SaveLease(ctx, userID, manageTarget, recoveredLeaseID, recoverySessionID, manageTarget, time.Now().UTC().Add(30*time.Second), 1, "", "matrix-recovered-fence"); err != nil {
		t.Fatalf("recover lease after Valkey rebuild: %v", err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT lease_id,version FROM control_leases WHERE session_id=$1`, recoverySessionID).Scan(&durableLeaseID, &durableVersion); err != nil {
		t.Fatal(err)
	}
	if durableLeaseID != recoveredLeaseID || durableVersion != 1 {
		t.Fatalf("recovered durable lease=%s version=%d", durableLeaseID, durableVersion)
	}
	if err := repository.PutGrant(ctx, userID, manageActor, manageTarget, pcOwner, "VIEW", nil, "matrix-manager-downgrade-grant"); err != nil {
		t.Fatalf("valid MANAGE PutGrant downgrade: %v", err)
	}
	assertMatrixLeaseRetained(t, repository, ctx, grantLeaseSession, replacementLeaseID, 2)
	assertMatrixLeaseRevocationOutbox(t, repository, ctx, grantLeaseSession, manageTarget, replacementLeaseID, 2, 0)
	assertMatrixOutboxEvent(t, repository, ctx, "grant.updated", manageTarget, 2)

	// Recreate a CONTROL grant and advance the durable fence. DeleteGrant must
	// return affected sessions for participant cleanup without deleting the
	// account-shared durable lease.
	if err := repository.PutGrant(ctx, userID, manageActor, manageTarget, pcOwner, "CONTROL", nil, "matrix-manager-restore-grant"); err != nil {
		t.Fatalf("valid MANAGE PutGrant restore: %v", err)
	}
	deleteLeaseID := uuid.New()
	if err := repository.SaveLease(ctx, userID, manageTarget, deleteLeaseID, grantLeaseSession, manageTarget, time.Now().UTC().Add(30*time.Second), 3, "", "matrix-delete-lease"); err != nil {
		t.Fatal(err)
	}
	var epochBeforeGrantDelete int64
	if err := repository.Pool.QueryRow(ctx, `SELECT key_epoch FROM pcs WHERE id=$1`, pcOwner).Scan(&epochBeforeGrantDelete); err != nil {
		t.Fatal(err)
	}
	deletedGrant, err := repository.DeleteGrant(ctx, userID, manageActor, manageTarget, pcOwner, "matrix-manager-delete-grant")
	if err != nil {
		t.Fatalf("valid MANAGE DeleteGrant: %v", err)
	}
	if deletedGrant.KeyEpoch != epochBeforeGrantDelete+1 || !slices.Contains(deletedGrant.LeaseSessionIDs, grantLeaseSession) {
		t.Fatalf("DeleteGrant result=%+v", deletedGrant)
	}
	var remainingGrant, remainingLease int
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM device_grants WHERE device_id=$1 AND pc_id=$2`, manageTarget, pcOwner).Scan(&remainingGrant); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM control_leases WHERE session_id=$1`, grantLeaseSession).Scan(&remainingLease); err != nil {
		t.Fatal(err)
	}
	if remainingGrant != 0 || remainingLease != 1 {
		t.Fatalf("DeleteGrant retained grant=%d lease=%d", remainingGrant, remainingLease)
	}
	assertMatrixLeaseRetained(t, repository, ctx, grantLeaseSession, deleteLeaseID, 3)
	assertMatrixLeaseRevocationOutbox(t, repository, ctx, grantLeaseSession, manageTarget, deleteLeaseID, 3, 0)
	assertMatrixOutboxEvent(t, repository, ctx, "grant.revoked", manageTarget, 1)
	if _, err := repository.DeleteGrant(ctx, userID, manageActor, manageTarget, pcOwner, "matrix-repeat-delete-grant"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("repeat DeleteGrant error=%v, want ErrNotFound", err)
	}

	// VIEW, CONTROL, and expired MANAGE delegates cannot create or update a
	// grant, even when both actor and target are otherwise trusted.
	insertMatrixGrant(t, repository, ctx, viewController, pcOwner, "VIEW", nil, bootstrapPC)
	insertMatrixGrant(t, repository, ctx, controlController, pcOwner, "CONTROL", nil, bootstrapPC)
	expiredAt := time.Now().UTC().Add(-time.Minute)
	insertMatrixGrant(t, repository, ctx, expiredManager, pcOwner, "MANAGE", &expiredAt, bootstrapPC)
	assertMatrixDenied(t, repository.PutGrant(ctx, userID, viewController, viewTarget, pcOwner, "VIEW", nil, "matrix-denied-view"), "VIEW PutGrant")
	assertMatrixDenied(t, repository.PutGrant(ctx, userID, controlController, controlTarget, pcOwner, "VIEW", nil, "matrix-denied-control"), "CONTROL PutGrant")
	assertMatrixDenied(t, repository.PutGrant(ctx, userID, expiredManager, expiredManageTarget, pcOwner, "VIEW", nil, "matrix-denied-expired"), "expired MANAGE PutGrant")

	// A controller cannot use its own CONTROL grant to elevate itself to
	// MANAGE. The target trust-state and account checks are covered alongside
	// that self-elevation attempt.
	assertMatrixDenied(t, repository.PutGrant(ctx, userID, controlController, controlController, pcOwner, "MANAGE", nil, "matrix-denied-self-elevate"), "controller self-elevation")
	assertMatrixDenied(t, repository.PutGrant(ctx, userID, bootstrapPC, pendingTarget, pcOwner, "VIEW", nil, "matrix-denied-pending"), "pending target")
	assertMatrixDenied(t, repository.PutGrant(ctx, userID, bootstrapPC, revokedTarget, pcOwner, "VIEW", nil, "matrix-denied-revoked"), "revoked target")
	assertMatrixDenied(t, repository.PutGrant(ctx, userID, bootstrapPC, crossAccountTarget, pcOwner, "VIEW", nil, "matrix-denied-cross-account"), "cross-account target")

	// A target with grants on multiple PCs cannot be revoked by an actor that
	// manages only a subset of those PCs. Once the actor manages every affected
	// PC, the same revoke is allowed and advances every affected key epoch.
	insertMatrixGrant(t, repository, ctx, multiPCTarget, pcTwo, "VIEW", nil, ownerPC2)
	insertMatrixGrant(t, repository, ctx, multiPCTarget, pcThree, "CONTROL", nil, ownerPC3)
	insertMatrixGrant(t, repository, ctx, partialManager, pcTwo, "MANAGE", nil, ownerPC2)
	assertMatrixDenied(t, repository.RevokeDevice(ctx, userID, partialManager, multiPCTarget, "matrix-partial-revoke"), "partial global revoke")
	insertMatrixGrant(t, repository, ctx, partialManager, pcThree, "MANAGE", nil, ownerPC3)
	if err := repository.RevokeDevice(ctx, userID, partialManager, multiPCTarget, "matrix-global-revoke"); err != nil {
		t.Fatalf("global revoke with all MANAGE grants: %v", err)
	}

	// Self-revocation is always allowed for a trusted actor. Give the device a
	// route grant and live durable lease so the revoke exercises grant cleanup
	// and epoch work without treating the compatibility holder as exclusive
	// ownership of an account-shared lease.
	insertMatrixGrant(t, repository, ctx, selfTarget, pcOwner, "CONTROL", nil, bootstrapPC)
	selfLeaseID := uuid.New()
	if err := repository.SaveLease(ctx, userID, selfTarget, selfLeaseID, grantLeaseSession, selfTarget, time.Now().UTC().Add(30*time.Second), 4, "", "matrix-self-revoke-lease"); err != nil {
		t.Fatal(err)
	}
	if err := repository.RevokeDevice(ctx, userID, selfActor, selfTarget, "matrix-self-revoke"); err != nil {
		t.Fatalf("self revoke: %v", err)
	}
	assertMatrixLeaseRetained(t, repository, ctx, grantLeaseSession, selfLeaseID, 4)
	assertMatrixLeaseRevocationOutbox(t, repository, ctx, grantLeaseSession, selfTarget, selfLeaseID, 4, 0)
	assertMatrixOutboxEvent(t, repository, ctx, "device.revoked", selfTarget, 1)

	// The bootstrap PC may revoke a later PC. The PC route is disabled and its
	// key epoch advances atomically with the device transition.
	var beforeEpoch int64
	var beforeRoute bool
	if err := repository.Pool.QueryRow(ctx, `SELECT key_epoch,remote_routing_enabled FROM pcs WHERE id=$1`, pcTwo).Scan(&beforeEpoch, &beforeRoute); err != nil {
		t.Fatal(err)
	}
	if !beforeRoute {
		t.Fatal("PC route unexpectedly disabled before revoke")
	}
	if err := repository.RevokeDevice(ctx, userID, bootstrapPC, ownerPC2, "matrix-bootstrap-pc-revoke"); err != nil {
		t.Fatalf("bootstrap PC revoke: %v", err)
	}
	var afterEpoch int64
	var afterRoute bool
	if err := repository.Pool.QueryRow(ctx, `SELECT key_epoch,remote_routing_enabled FROM pcs WHERE id=$1`, pcTwo).Scan(&afterEpoch, &afterRoute); err != nil {
		t.Fatal(err)
	}
	if afterRoute {
		t.Fatal("revoked PC route remains enabled")
	}
	if afterEpoch != beforeEpoch+1 {
		t.Fatalf("revoked PC key epoch=%d, want %d", afterEpoch, beforeEpoch+1)
	}

	// Locking the owning account invalidates already-authenticated Realtime
	// authority checks; existing sockets cannot keep refreshing Presence or
	// routing frames until an administrator restores the account.
	if _, err := repository.Pool.Exec(ctx, `UPDATE users SET status='LOCKED' WHERE id=$1`, userID); err != nil {
		t.Fatal(err)
	}
	assertMatrixDenied(t, repository.TouchPCPresence(ctx, userID, bootstrapPC), "locked account PC presence")
	assertMatrixDenied(t, repository.TouchDevicePresence(ctx, userID, manageActor), "locked account Android presence")
	assertMatrixDenied(t, func() error {
		_, err := repository.SessionPermission(ctx, userID, bootstrapPC, grantLeaseSession)
		return err
	}(), "locked account session permission")
	if _, err := repository.SessionPCDevice(ctx, userID, grantLeaseSession); !errors.Is(err, ErrNotFound) {
		t.Fatalf("locked account SessionPCDevice error=%v, want ErrNotFound", err)
	}
}

func insertMatrixUser(t *testing.T, repository *Postgres, ctx context.Context, userID uuid.UUID, label string) {
	t.Helper()
	subjectHash := sha256.Sum256([]byte(label + ":" + userID.String()))
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subjectHash[:]); err != nil {
		t.Fatal(err)
	}
}

func insertMatrixDevice(t *testing.T, repository *Postgres, ctx context.Context, userID, deviceID uuid.UUID, deviceType, name, trustState string, approvedBy *uuid.UUID) {
	t.Helper()
	signingKey, exchangeKey := matrixDeviceKeys(deviceID)
	var approver any
	if approvedBy != nil {
		approver = *approvedBy
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_by,approved_at)
		VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,CASE WHEN $8='TRUSTED' THEN now() ELSE NULL END)`,
		deviceID, userID, deviceType, name, signingKey, exchangeKey, "matrix-"+deviceID.String(), trustState, approver); err != nil {
		t.Fatal(err)
	}
}

func insertMatrixPC(t *testing.T, repository *Postgres, ctx context.Context, userID, pcID, deviceID uuid.UUID, name string, keyEpoch int64) {
	t.Helper()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch,remote_routing_enabled)
		VALUES($1,$2,$3,$4,'matrix-test',$5,true)`, pcID, userID, deviceID, name, keyEpoch); err != nil {
		t.Fatal(err)
	}
}

func insertMatrixGrant(t *testing.T, repository *Postgres, ctx context.Context, deviceID, pcID uuid.UUID, permission string, expiresAt *time.Time, approvedBy uuid.UUID) {
	t.Helper()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO device_grants(device_id,pc_id,permission,approved_by,expires_at)
		VALUES($1,$2,$3,$4,$5)`, deviceID, pcID, permission, approvedBy, expiresAt); err != nil {
		t.Fatal(err)
	}
}

func assertMatrixDenied(t *testing.T, err error, operation string) {
	t.Helper()
	if !errors.Is(err, ErrDenied) {
		t.Errorf("%s error=%v, want ErrDenied", operation, err)
	}
}

func assertMatrixLeaseRemoved(t *testing.T, repository *Postgres, ctx context.Context, sessionID uuid.UUID) {
	t.Helper()
	var count int
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM control_leases WHERE session_id=$1`, sessionID).Scan(&count); err != nil {
		t.Fatal(err)
	}
	if count != 0 {
		t.Fatalf("session %s retained %d durable leases", sessionID, count)
	}
}

func assertMatrixLeaseRetained(t *testing.T, repository *Postgres, ctx context.Context, sessionID, leaseID uuid.UUID, version int64) {
	t.Helper()
	var storedLeaseID uuid.UUID
	var storedVersion int64
	if err := repository.Pool.QueryRow(ctx, `SELECT lease_id,version FROM control_leases WHERE session_id=$1`, sessionID).Scan(&storedLeaseID, &storedVersion); err != nil {
		t.Fatal(err)
	}
	if storedLeaseID != leaseID || storedVersion != version {
		t.Fatalf("session %s lease/version=%s/%d, want %s/%d", sessionID, storedLeaseID, storedVersion, leaseID, version)
	}
}

func assertMatrixLeaseRevocationOutbox(t *testing.T, repository *Postgres, ctx context.Context, sessionID, holderID, leaseID uuid.UUID, version int64, want int) {
	t.Helper()
	var count int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM outbox
		WHERE event_type='lease.revoked'
		  AND payload->>'session_id'=$1
		  AND payload->>'holder_device_id'=$2
		  AND payload->>'lease_id'=$3
		  AND (payload->>'version')::bigint=$4`,
		sessionID.String(), holderID.String(), leaseID.String(), version).Scan(&count); err != nil {
		t.Fatal(err)
	}
	if count != want {
		t.Fatalf("lease revocation outbox count=%d for session=%s lease=%s version=%d, want %d", count, sessionID, leaseID, version, want)
	}
}

func assertMatrixOutboxEvent(t *testing.T, repository *Postgres, ctx context.Context, eventType string, aggregateID uuid.UUID, want int) {
	t.Helper()
	var count int
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM outbox WHERE event_type=$1 AND aggregate_id=$2`, eventType, aggregateID).Scan(&count); err != nil {
		t.Fatal(err)
	}
	if count != want {
		t.Fatalf("outbox %s aggregate=%s count=%d, want %d", eventType, aggregateID, count, want)
	}
}

func matrixDeviceKeys(deviceID uuid.UUID) ([]byte, []byte) {
	signingInput := append([]byte("termlink-matrix-signing:"), deviceID[:]...)
	signingHash := sha256.Sum256(signingInput)
	exchangeInput := append([]byte("termlink-matrix-exchange:"), deviceID[:]...)
	exchangeHash := sha256.Sum256(exchangeInput)
	return signingHash[:], exchangeHash[:]
}
