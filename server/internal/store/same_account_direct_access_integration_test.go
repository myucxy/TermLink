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
)

// TestSameAccountDirectAccess verifies the production AutoTrustDevices policy
// without creating a pairing challenge or device grant. PostgreSQL remains the
// account boundary for recipient discovery, session routing, and durable lease
// acquisition.
func TestSameAccountDirectAccess(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	repository.AutoTrustDevices = true
	if err := repository.VerifySchema(ctx); err != nil {
		repository.Close()
		t.Fatal(err)
	}

	var userIDs []uuid.UUID
	var deviceIDs []uuid.UUID
	var aggregateIDs []uuid.UUID
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		for _, deviceID := range deviceIDs {
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM audit_events WHERE actor_device_id=$1 OR target_id=$1`, deviceID)
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=$1`, deviceID)
		}
		for _, aggregateID := range aggregateIDs {
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM audit_events WHERE target_id=$1`, aggregateID)
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=$1`, aggregateID)
		}
		for _, userID := range userIDs {
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM audit_events WHERE user_id=$1 OR target_id=$1`, userID)
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=$1`, userID)
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
		}
		repository.Close()
	})

	subject := sha256.Sum256([]byte("same-account-direct:" + uuid.NewString()))
	windowsID := uuid.New()
	userID, windows, bootstrap, err := repository.RegisterDevice(ctx, subject, sameAccountRegisterInput(windowsID, "WINDOWS_PC", "Direct PC"), "same-account-register-pc")
	if err != nil {
		t.Fatal(err)
	}
	userIDs = append(userIDs, userID)
	deviceIDs = append(deviceIDs, windows.ID)
	if !bootstrap || windows.TrustState != "TRUSTED" {
		t.Fatalf("Windows registration bootstrap=%v trust=%q, want true/TRUSTED", bootstrap, windows.TrustState)
	}

	androidID := uuid.New()
	registeredUserID, android, bootstrap, err := repository.RegisterDevice(ctx, subject, sameAccountRegisterInput(androidID, "ANDROID", "Direct Android"), "same-account-register-android")
	if err != nil {
		t.Fatal(err)
	}
	deviceIDs = append(deviceIDs, android.ID)
	if registeredUserID != userID {
		t.Fatalf("Android user=%s, want PC user=%s", registeredUserID, userID)
	}
	if bootstrap || android.TrustState != "TRUSTED" {
		t.Fatalf("Android registration bootstrap=%v trust=%q, want false/TRUSTED", bootstrap, android.TrustState)
	}

	pcID := uuid.New()
	aggregateIDs = append(aggregateIDs, pcID)
	const keyEpoch = int64(7)
	if err := repository.UpsertPC(ctx, userID, windows.ID, PCRecord{
		ID: pcID, DeviceID: windows.ID, Name: "Direct PC", AgentVersion: "same-account-test", KeyEpoch: keyEpoch,
	}); err != nil {
		t.Fatalf("upsert PC: %v", err)
	}

	sessionID := uuid.New()
	aggregateIDs = append(aggregateIDs, sessionID)
	if err := repository.UpsertSession(ctx, userID, windows.ID, SessionRecord{
		ID: sessionID, PCID: pcID, ShellType: "powershell", State: "RUNNING", Columns: 120, Rows: 40,
	}); err != nil {
		t.Fatalf("upsert session: %v", err)
	}

	var explicitGrants int
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM device_grants WHERE device_id=$1 AND pc_id=$2`, android.ID, pcID).Scan(&explicitGrants); err != nil {
		t.Fatal(err)
	}
	if explicitGrants != 0 {
		t.Fatalf("unexpected explicit grants=%d, want 0", explicitGrants)
	}

	gotEpoch, recipients, err := repository.ListPCGrants(ctx, userID, windows.ID, pcID)
	if err != nil {
		t.Fatalf("list implicit recipients: %v", err)
	}
	if gotEpoch != keyEpoch {
		t.Fatalf("key epoch=%d, want %d", gotEpoch, keyEpoch)
	}
	if len(recipients) != 1 {
		t.Fatalf("recipient count=%d, want 1: %+v", len(recipients), recipients)
	}
	recipient := recipients[0]
	if recipient.DeviceID != android.ID || recipient.DeviceType != "ANDROID" || recipient.Permission != "MANAGE" || recipient.ExpiresAt != nil {
		t.Fatalf("implicit recipient=%+v, want same-account Android MANAGE without expiry", recipient)
	}
	expectedAndroidSigning := sha256.Sum256([]byte("same-account-signing:" + android.ID.String()))
	expectedAndroidExchange := sha256.Sum256([]byte("same-account-exchange:" + android.ID.String()))
	if len(recipient.SigningPublicKey) != 32 || !bytes.Equal(recipient.SigningPublicKey, expectedAndroidSigning[:]) ||
		len(recipient.ExchangePublicKey) != 32 || !bytes.Equal(recipient.ExchangePublicKey, expectedAndroidExchange[:]) {
		t.Fatalf("implicit recipient public keys do not match device %s", android.ID)
	}
	discoveredPCs, err := repository.ListPCs(ctx, userID, android.ID)
	if err != nil || len(discoveredPCs) != 1 || discoveredPCs[0].ID != pcID {
		t.Fatalf("same-account PC discovery items=%+v err=%v", discoveredPCs, err)
	}
	discoveredSessions, err := repository.ListSessions(ctx, userID, android.ID, pcID)
	if err != nil || len(discoveredSessions) != 1 || discoveredSessions[0].ID != sessionID {
		t.Fatalf("same-account session discovery items=%+v err=%v", discoveredSessions, err)
	}

	route, err := repository.SessionRoute(ctx, userID, android.ID, sessionID)
	if err != nil {
		t.Fatalf("same-account session route: %v", err)
	}
	if route.Permission != "MANAGE" || route.PCDeviceID != windows.ID || route.GrantExpiresAt != nil {
		t.Fatalf("same-account route=%+v, want MANAGE through PC device %s without expiry", route, windows.ID)
	}
	policy, err := repository.SessionControlPolicy(ctx, userID, android.ID, sessionID)
	if err != nil {
		t.Fatalf("same-account control policy: %v", err)
	}
	if policy.Mode != ControlModeCollaborative || !policy.LocalPriority || policy.PolicyVersion != 1 ||
		policy.PCID != pcID || policy.PCDeviceID != windows.ID || policy.GrantExpiresAt != nil {
		t.Fatalf("same-account control policy=%+v, want default COLLABORATIVE policy for PC %s", policy, pcID)
	}
	routes, err := repository.SessionRoutes(ctx, userID, android.ID, []uuid.UUID{sessionID})
	if err != nil {
		t.Fatalf("same-account batch routes: %v", err)
	}
	batchRoute, ok := routes[sessionID]
	if !ok || batchRoute.Permission != "MANAGE" || batchRoute.PCDeviceID != windows.ID || batchRoute.GrantExpiresAt != nil {
		t.Fatalf("same-account batch route=%+v present=%v, want MANAGE through PC device %s without expiry", batchRoute, ok, windows.ID)
	}

	responderKeyID, err := repository.AuthorizeDirectTransportSignal(ctx, userID, windows.ID, android.ID, android.ID)
	if err != nil || responderKeyID != android.KeyID {
		t.Fatalf("PC-to-Android responder key=%q error=%v, want %q/nil", responderKeyID, err, android.KeyID)
	}
	responderKeyID, err = repository.AuthorizeDirectTransportSignal(ctx, userID, android.ID, windows.ID, android.ID)
	if err != nil || responderKeyID != android.KeyID {
		t.Fatalf("Android-to-PC responder key=%q error=%v, want original responder %q/nil", responderKeyID, err, android.KeyID)
	}
	if _, err := repository.AuthorizeDirectTransportSignal(ctx, userID, windows.ID, android.ID, uuid.New()); !errors.Is(err, ErrDenied) {
		t.Fatalf("non-route responder error=%v, want ErrDenied", err)
	}
	rotatedResponderKeyID := "same-account-rotated-" + uuid.NewString()
	if _, err := repository.Pool.Exec(ctx, `UPDATE devices SET key_id=$2 WHERE id=$1`, android.ID, rotatedResponderKeyID); err != nil {
		t.Fatal(err)
	}
	responderKeyID, err = repository.AuthorizeDirectTransportSignal(ctx, userID, windows.ID, android.ID, android.ID)
	if err != nil || responderKeyID != rotatedResponderKeyID {
		t.Fatalf("rotated responder key=%q error=%v, want %q/nil", responderKeyID, err, rotatedResponderKeyID)
	}

	leaseID := uuid.New()
	leaseExpiry := time.Now().UTC().Add(30 * time.Second)
	if err := repository.SaveLease(ctx, userID, android.ID, leaseID, sessionID, android.ID, leaseExpiry, 1, "lease.acquire", "same-account-acquire"); err != nil {
		t.Fatalf("same-account acquire durable lease: %v", err)
	}
	var storedHolder, storedLease uuid.UUID
	if err := repository.Pool.QueryRow(ctx, `SELECT holder_device_id,lease_id FROM control_leases WHERE session_id=$1`, sessionID).Scan(&storedHolder, &storedLease); err != nil {
		t.Fatal(err)
	}
	if storedHolder != android.ID || storedLease != leaseID {
		t.Fatalf("stored lease holder/id=%s/%s, want %s/%s", storedHolder, storedLease, android.ID, leaseID)
	}

	crossSubject := sha256.Sum256([]byte("same-account-cross:" + uuid.NewString()))
	crossID := uuid.New()
	crossUserID, crossAndroid, _, err := repository.RegisterDevice(ctx, crossSubject, sameAccountRegisterInput(crossID, "ANDROID", "Cross-account Android"), "same-account-register-cross")
	if err != nil {
		t.Fatal(err)
	}
	userIDs = append(userIDs, crossUserID)
	deviceIDs = append(deviceIDs, crossAndroid.ID)
	if crossAndroid.TrustState != "TRUSTED" {
		t.Fatalf("cross-account Android trust=%q, want TRUSTED within its own account", crossAndroid.TrustState)
	}

	if _, _, err := repository.ListPCGrants(ctx, userID, crossAndroid.ID, pcID); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account recipient discovery err=%v, want ErrDenied", err)
	}
	if _, err := repository.SessionRoute(ctx, userID, crossAndroid.ID, sessionID); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account session route err=%v, want ErrDenied", err)
	}
	if items, err := repository.ListPCs(ctx, userID, crossAndroid.ID); err != nil || len(items) != 0 {
		t.Fatalf("cross-account PC discovery items=%+v err=%v, want empty", items, err)
	}
	if items, err := repository.ListSessions(ctx, userID, crossAndroid.ID, pcID); err != nil || len(items) != 0 {
		t.Fatalf("cross-account session discovery items=%+v err=%v, want empty", items, err)
	}
	if err := repository.SaveLease(ctx, userID, crossAndroid.ID, uuid.New(), sessionID, crossAndroid.ID, leaseExpiry, 2, "lease.acquire", "same-account-cross-acquire"); err == nil {
		t.Fatal("cross-account durable lease acquisition unexpectedly succeeded")
	}
	if err := repository.SaveLease(ctx, userID, crossAndroid.ID, leaseID, sessionID, android.ID, leaseExpiry.Add(time.Second), 1, "", "same-account-cross-refresh"); err == nil {
		t.Fatal("cross-account actor unexpectedly refreshed the existing shared lease")
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT holder_device_id,lease_id FROM control_leases WHERE session_id=$1`, sessionID).Scan(&storedHolder, &storedLease); err != nil {
		t.Fatal(err)
	}
	if storedHolder != android.ID || storedLease != leaseID {
		t.Fatalf("cross-account attempt changed lease holder/id to %s/%s", storedHolder, storedLease)
	}
	if err := repository.RevokeDevice(ctx, userID, crossAndroid.ID, android.ID, "same-account-cross-revoke"); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account revoke err=%v, want ErrDenied", err)
	}

	// A second same-account Android has implicit MANAGE authority without a
	// grant. Revoking one Android rotates affected account PCs but must not
	// mistake the shared lease's compatibility holder for exclusive ownership.
	managerID := uuid.New()
	_, managerAndroid, _, err := repository.RegisterDevice(ctx, subject, sameAccountRegisterInput(managerID, "ANDROID", "Direct Manager Android"), "same-account-register-manager")
	if err != nil {
		t.Fatal(err)
	}
	deviceIDs = append(deviceIDs, managerAndroid.ID)
	if managerAndroid.TrustState != "TRUSTED" {
		t.Fatalf("manager Android trust=%q, want TRUSTED", managerAndroid.TrustState)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM device_grants WHERE device_id IN ($1,$2)`, android.ID, managerAndroid.ID).Scan(&explicitGrants); err != nil {
		t.Fatal(err)
	}
	if explicitGrants != 0 {
		t.Fatalf("automatic access created %d explicit grants, want 0", explicitGrants)
	}
	sharedExpiry := leaseExpiry.Add(10 * time.Second)
	if err := repository.SaveLease(ctx, userID, managerAndroid.ID, leaseID, sessionID, android.ID, sharedExpiry, 1, "", "same-account-shared-refresh"); err != nil {
		t.Fatalf("second same-account Android refresh shared lease: %v", err)
	}
	var storedExpiry time.Time
	if err := repository.Pool.QueryRow(ctx, `SELECT holder_device_id,lease_id,expires_at FROM control_leases WHERE session_id=$1`, sessionID).Scan(&storedHolder, &storedLease, &storedExpiry); err != nil {
		t.Fatal(err)
	}
	if storedHolder != android.ID || storedLease != leaseID || storedExpiry.Before(sharedExpiry.Add(-time.Millisecond)) {
		t.Fatalf("shared refresh changed fence or failed to extend expiry: holder/id/expiry=%s/%s/%s", storedHolder, storedLease, storedExpiry)
	}

	deleteSessionID := uuid.New()
	aggregateIDs = append(aggregateIDs, deleteSessionID)
	if err := repository.UpsertSession(ctx, userID, windows.ID, SessionRecord{
		ID: deleteSessionID, PCID: pcID, ShellType: "powershell", State: "EXITED", Columns: 120, Rows: 40,
	}); err != nil {
		t.Fatalf("upsert delete session: %v", err)
	}
	if err := repository.MarkSessionDeleted(ctx, userID, crossAndroid.ID, deleteSessionID, "same-account-cross-delete"); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account session delete err=%v, want ErrDenied", err)
	}
	if err := repository.MarkSessionDeleted(ctx, userID, managerAndroid.ID, deleteSessionID, "same-account-implicit-manager-delete"); err != nil {
		t.Fatalf("implicit MANAGE session delete: %v", err)
	}
	var deletedState string
	if err := repository.Pool.QueryRow(ctx, `SELECT state FROM sessions WHERE id=$1`, deleteSessionID).Scan(&deletedState); err != nil {
		t.Fatal(err)
	}
	if deletedState != "DELETED" {
		t.Fatalf("deleted session state=%q, want DELETED", deletedState)
	}

	forceSessionID := uuid.New()
	aggregateIDs = append(aggregateIDs, forceSessionID)
	var remainingLease int
	if err := repository.UpsertSession(ctx, userID, windows.ID, SessionRecord{
		ID: forceSessionID, PCID: pcID, ShellType: "powershell", State: "RUNNING", Columns: 120, Rows: 40,
	}); err != nil {
		t.Fatalf("upsert force-release session: %v", err)
	}
	forceLeaseID := uuid.New()
	if err := repository.SaveLease(ctx, userID, android.ID, forceLeaseID, forceSessionID, android.ID, leaseExpiry, 1, "", "same-account-force-lease"); err != nil {
		t.Fatalf("save force-release fixture: %v", err)
	}
	if err := repository.ForceDeleteLease(ctx, userID, crossAndroid.ID, forceSessionID, forceLeaseID, 1, "lease.force_release", "same-account-cross-force"); err != nil {
		t.Fatalf("cross-account force release returned an infrastructure error: %v", err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM control_leases WHERE session_id=$1`, forceSessionID).Scan(&remainingLease); err != nil {
		t.Fatal(err)
	}
	if remainingLease != 1 {
		t.Fatal("cross-account force release removed the durable lease")
	}
	if err := repository.ForceDeleteLease(ctx, userID, managerAndroid.ID, forceSessionID, forceLeaseID, 1, "lease.force_release", "same-account-implicit-manager-force"); err != nil {
		t.Fatalf("implicit MANAGE force release: %v", err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM control_leases WHERE session_id=$1`, forceSessionID).Scan(&remainingLease); err != nil {
		t.Fatal(err)
	}
	if remainingLease != 0 {
		t.Fatal("implicit MANAGE force release retained the durable lease")
	}

	var epochBeforeRevoke int64
	if err := repository.Pool.QueryRow(ctx, `SELECT key_epoch FROM pcs WHERE id=$1`, pcID).Scan(&epochBeforeRevoke); err != nil {
		t.Fatal(err)
	}
	if err := repository.RevokeDevice(ctx, userID, managerAndroid.ID, android.ID, "same-account-implicit-manager-revoke"); err != nil {
		t.Fatalf("implicit MANAGE revoke: %v", err)
	}

	var revokedState string
	var epochAfterRevoke int64
	if err := repository.Pool.QueryRow(ctx, `SELECT trust_state FROM devices WHERE id=$1`, android.ID).Scan(&revokedState); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT key_epoch FROM pcs WHERE id=$1`, pcID).Scan(&epochAfterRevoke); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM control_leases WHERE session_id=$1`, sessionID).Scan(&remainingLease); err != nil {
		t.Fatal(err)
	}
	if revokedState != "REVOKED" || epochAfterRevoke != epochBeforeRevoke+1 || remainingLease != 1 {
		t.Fatalf("revocation state/epoch/lease=%s/%d/%d, want REVOKED/%d/1", revokedState, epochAfterRevoke, remainingLease, epochBeforeRevoke+1)
	}

	gotEpoch, recipients, err = repository.ListPCGrants(ctx, userID, windows.ID, pcID)
	if err != nil {
		t.Fatalf("list recipients after revoke: %v", err)
	}
	if gotEpoch != epochAfterRevoke || len(recipients) != 1 || recipients[0].DeviceID != managerAndroid.ID || recipients[0].Permission != "MANAGE" {
		t.Fatalf("post-revoke recipient snapshot epoch/items=%d/%+v", gotEpoch, recipients)
	}
	expectedManagerSigning := sha256.Sum256([]byte("same-account-signing:" + managerAndroid.ID.String()))
	expectedManagerExchange := sha256.Sum256([]byte("same-account-exchange:" + managerAndroid.ID.String()))
	if len(recipients[0].SigningPublicKey) != 32 || !bytes.Equal(recipients[0].SigningPublicKey, expectedManagerSigning[:]) ||
		len(recipients[0].ExchangePublicKey) != 32 || !bytes.Equal(recipients[0].ExchangePublicKey, expectedManagerExchange[:]) {
		t.Fatalf("post-revoke recipient public keys do not match remaining device %s", managerAndroid.ID)
	}
	if _, err := repository.SessionRoute(ctx, userID, android.ID, sessionID); !errors.Is(err, ErrDenied) {
		t.Fatalf("revoked device route err=%v, want ErrDenied", err)
	}
	if items, err := repository.ListPCs(ctx, userID, android.ID); err != nil || len(items) != 0 {
		t.Fatalf("revoked-device PC discovery items=%+v err=%v, want empty", items, err)
	}
	if items, err := repository.ListSessions(ctx, userID, android.ID, pcID); err != nil || len(items) != 0 {
		t.Fatalf("revoked-device session discovery items=%+v err=%v, want empty", items, err)
	}
	if err := repository.MarkSessionDeleted(ctx, userID, android.ID, sessionID, "same-account-revoked-delete"); !errors.Is(err, ErrDenied) {
		t.Fatalf("revoked device session delete err=%v, want ErrDenied", err)
	}
	if err := repository.SaveLease(ctx, userID, android.ID, uuid.New(), sessionID, android.ID, leaseExpiry, 2, "lease.acquire", "same-account-revoked-acquire"); err == nil {
		t.Fatal("revoked device durable lease acquisition unexpectedly succeeded")
	}
	if err := repository.ForceDeleteLease(ctx, userID, managerAndroid.ID, sessionID, leaseID, 1, "lease.force_release", "same-account-shared-force-release"); err != nil {
		t.Fatalf("manager force release shared lease: %v", err)
	}
	replacementLeaseID := uuid.New()
	if err := repository.SaveLease(ctx, userID, managerAndroid.ID, replacementLeaseID, sessionID, managerAndroid.ID, leaseExpiry, 1, "", "same-account-revoked-force-fixture"); err != nil {
		t.Fatalf("save post-revoke lease fixture: %v", err)
	}
	if err := repository.ForceDeleteLease(ctx, userID, android.ID, sessionID, replacementLeaseID, 1, "lease.force_release", "same-account-revoked-force"); err != nil {
		t.Fatalf("revoked-device force release returned an infrastructure error: %v", err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM control_leases WHERE session_id=$1 AND lease_id=$2`, sessionID, replacementLeaseID).Scan(&remainingLease); err != nil {
		t.Fatal(err)
	}
	if remainingLease != 1 {
		t.Fatal("revoked device force release removed the replacement lease")
	}
	if err := repository.RevokeDevice(ctx, userID, managerAndroid.ID, android.ID, "same-account-repeat-revoke"); !errors.Is(err, ErrDenied) {
		t.Fatalf("repeat revoke err=%v, want ErrDenied", err)
	}
}

func sameAccountRegisterInput(id uuid.UUID, deviceType, name string) RegisterDeviceInput {
	signing := sha256.Sum256([]byte("same-account-signing:" + id.String()))
	exchange := sha256.Sum256([]byte("same-account-exchange:" + id.String()))
	return RegisterDeviceInput{
		ID:                id,
		Type:              deviceType,
		Name:              name,
		SigningPublicKey:  signing[:],
		ExchangePublicKey: exchange[:],
		KeyID:             "same-account-" + id.String(),
	}
}
