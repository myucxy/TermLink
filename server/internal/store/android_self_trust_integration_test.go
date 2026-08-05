package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
)

// TestAndroidSelfTrust exercises the database authority for the Android
// self-trust exception.  It is intentionally an integration test because the
// one-time JTI consumption, device transition, audit record, and bootstrap PC
// predicate are all enforced in PostgreSQL transactions.
func TestAndroidSelfTrust(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}

	ctx := context.Background()
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	if err := repository.VerifySchema(ctx); err != nil {
		repository.Close()
		t.Fatal(err)
	}

	var cleanupUsers []uuid.UUID
	var cleanupDevices []uuid.UUID
	t.Cleanup(func() {
		// Audit and outbox rows do not all cascade from users (audit rows use
		// SET NULL), so remove rows by both account and device before deleting
		// the accounts themselves.
		for _, id := range cleanupDevices {
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM audit_events WHERE actor_device_id=$1 OR target_id=$1`, id)
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM outbox WHERE aggregate_id=$1`, id)
		}
		for _, id := range cleanupUsers {
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM audit_events WHERE user_id=$1 OR target_id=$1`, id)
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM outbox WHERE aggregate_id=$1`, id)
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM users WHERE id=$1`, id)
		}
		repository.Close()
	})

	// Register Android first.  Registration creates the account and leaves the
	// Android device pending, even when there is no Windows PC yet.
	androidSubject := sha256.Sum256([]byte("android-self-trust:" + uuid.NewString()))
	androidID := uuid.New()
	userID, android, bootstrap, err := repository.RegisterDevice(ctx, androidSubject, selfTrustRegisterInput(androidID, "ANDROID", "Self-trust Android"), "android-self-trust-register")
	if err != nil {
		t.Fatal(err)
	}
	cleanupUsers = append(cleanupUsers, userID)
	cleanupDevices = append(cleanupDevices, android.ID)
	if bootstrap {
		t.Fatal("Android registration unexpectedly bootstrapped a PC")
	}
	if android.TrustState != "PENDING" {
		t.Fatalf("initial Android trust state=%q, want PENDING", android.TrustState)
	}

	var firstJTI [32]byte
	firstJTI = sha256.Sum256([]byte("android-self-trust-jti:" + uuid.NewString()))
	trusted, changed, err := repository.SelfTrustAndroidDevice(ctx, userID, android.ID, firstJTI, "android-self-trust-transition")
	if err != nil {
		t.Fatalf("first self-trust transition: %v", err)
	}
	if !changed {
		t.Fatal("first self-trust transition was not reported as changed")
	}
	if trusted.TrustState != "TRUSTED" {
		t.Fatalf("self-trusted Android state=%q, want TRUSTED", trusted.TrustState)
	}
	if trusted.ApprovedBy == nil || *trusted.ApprovedBy != android.ID {
		t.Fatalf("self-trusted Android approved_by=%v, want %s", trusted.ApprovedBy, android.ID)
	}
	if trusted.ApprovedAt == nil {
		t.Fatal("self-trusted Android has no approved_at timestamp")
	}

	// A trusted device can repeat the same request without consuming another
	// token or creating another audit event.
	repeated, changed, err := repository.SelfTrustAndroidDevice(ctx, userID, android.ID, firstJTI, "android-self-trust-repeat")
	if err != nil {
		t.Fatalf("repeated self-trust transition: %v", err)
	}
	if changed {
		t.Fatal("repeated self-trust transition was reported as changed")
	}
	if repeated.TrustState != "TRUSTED" || repeated.ApprovedBy == nil || *repeated.ApprovedBy != android.ID {
		t.Fatalf("repeated self-trust record=%+v", repeated)
	}

	var auditCount int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events
		WHERE user_id=$1 AND action='device.self_trust' AND target_id=$2`, userID, android.ID).Scan(&auditCount); err != nil {
		t.Fatal(err)
	}
	if auditCount != 1 {
		t.Fatalf("device.self_trust audit count=%d, want 1", auditCount)
	}
	var grants int
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM device_grants WHERE device_id=$1`, android.ID).Scan(&grants); err != nil {
		t.Fatal(err)
	}
	if grants != 0 {
		t.Fatalf("self-trust created %d device grants, want 0", grants)
	}

	// The same OIDC token identifier is single-use across pending devices.
	secondAndroidID := uuid.New()
	_, secondAndroid, _, err := repository.RegisterDevice(ctx, androidSubject, selfTrustRegisterInput(secondAndroidID, "ANDROID", "Second Android"), "android-self-trust-register-second")
	if err != nil {
		t.Fatal(err)
	}
	cleanupDevices = append(cleanupDevices, secondAndroid.ID)
	if secondAndroid.TrustState != "PENDING" {
		t.Fatalf("second Android trust state=%q, want PENDING", secondAndroid.TrustState)
	}
	if _, changed, err := repository.SelfTrustAndroidDevice(ctx, userID, secondAndroid.ID, firstJTI, "android-self-trust-reuse"); !errors.Is(err, ErrDenied) || changed {
		t.Fatalf("reused JTI result changed=%v err=%v, want ErrDenied and unchanged", changed, err)
	}
	var secondState string
	if err := repository.Pool.QueryRow(ctx, `SELECT trust_state FROM devices WHERE id=$1`, secondAndroid.ID).Scan(&secondState); err != nil {
		t.Fatal(err)
	}
	if secondState != "PENDING" {
		t.Fatalf("second Android state after JTI reuse=%q, want PENDING", secondState)
	}

	// Revoked Android devices and Windows devices cannot use the exception.
	revokedAndroidID := uuid.New()
	_, revokedAndroid, _, err := repository.RegisterDevice(ctx, androidSubject, selfTrustRegisterInput(revokedAndroidID, "ANDROID", "Revoked Android"), "android-self-trust-register-revoked")
	if err != nil {
		t.Fatal(err)
	}
	cleanupDevices = append(cleanupDevices, revokedAndroid.ID)
	if _, err := repository.Pool.Exec(ctx, `UPDATE devices SET trust_state='REVOKED',revoked_at=now() WHERE id=$1`, revokedAndroid.ID); err != nil {
		t.Fatal(err)
	}
	revokedJTI := sha256.Sum256([]byte("android-self-trust-revoked:" + uuid.NewString()))
	if _, changed, err := repository.SelfTrustAndroidDevice(ctx, userID, revokedAndroid.ID, revokedJTI, "android-self-trust-revoked"); !errors.Is(err, ErrDenied) || changed {
		t.Fatalf("revoked Android result changed=%v err=%v, want ErrDenied and unchanged", changed, err)
	}

	windowsID := uuid.New()
	_, windows, windowsBootstrap, err := repository.RegisterDevice(ctx, androidSubject, selfTrustRegisterInput(windowsID, "WINDOWS_PC", "Bootstrap Windows"), "android-self-trust-register-windows")
	if err != nil {
		t.Fatal(err)
	}
	cleanupDevices = append(cleanupDevices, windows.ID)
	if !windowsBootstrap || windows.TrustState != "TRUSTED" || windows.ApprovedBy != nil {
		t.Fatalf("first Windows registration bootstrap=%v record=%+v", windowsBootstrap, windows)
	}
	windowsJTI := sha256.Sum256([]byte("android-self-trust-windows:" + uuid.NewString()))
	if _, changed, err := repository.SelfTrustAndroidDevice(ctx, userID, windows.ID, windowsJTI, "android-self-trust-windows"); !errors.Is(err, ErrDenied) || changed {
		t.Fatalf("Windows self-trust result changed=%v err=%v, want ErrDenied and unchanged", changed, err)
	}
	if _, err := repository.Pool.Exec(ctx, `UPDATE devices SET trust_state='REVOKED',revoked_at=now() WHERE id=$1`, windows.ID); err != nil {
		t.Fatal(err)
	}
	replacementWindowsID := uuid.New()
	_, replacementWindows, replacementBootstrap, err := repository.RegisterDevice(ctx, androidSubject, selfTrustRegisterInput(replacementWindowsID, "WINDOWS_PC", "Replacement Windows"), "android-self-trust-register-replacement-windows")
	if err != nil {
		t.Fatal(err)
	}
	cleanupDevices = append(cleanupDevices, replacementWindows.ID)
	if replacementBootstrap || replacementWindows.TrustState != "PENDING" {
		t.Fatalf("replacement Windows registration bootstrap=%v record=%+v, want PENDING", replacementBootstrap, replacementWindows)
	}
	var bootstrapConsumedAt *time.Time
	if err := repository.Pool.QueryRow(ctx, `SELECT windows_bootstrap_consumed_at FROM users WHERE id=$1`, userID).Scan(&bootstrapConsumedAt); err != nil {
		t.Fatal(err)
	}
	if bootstrapConsumedAt == nil {
		t.Fatal("Windows bootstrap consumption was not persisted")
	}

	// A device from another account must not be addressable by this account's
	// user ID, even when it is a pending Android device.
	crossSubject := sha256.Sum256([]byte("android-self-trust-cross-account:" + uuid.NewString()))
	crossUserID, crossAndroid, _, err := repository.RegisterDevice(ctx, crossSubject, selfTrustRegisterInput(uuid.New(), "ANDROID", "Cross-account Android"), "android-self-trust-register-cross")
	if err != nil {
		t.Fatal(err)
	}
	cleanupUsers = append(cleanupUsers, crossUserID)
	cleanupDevices = append(cleanupDevices, crossAndroid.ID)
	crossJTI := sha256.Sum256([]byte("android-self-trust-cross:" + uuid.NewString()))
	if _, changed, err := repository.SelfTrustAndroidDevice(ctx, userID, crossAndroid.ID, crossJTI, "android-self-trust-cross"); !errors.Is(err, ErrDenied) || changed {
		t.Fatalf("cross-account self-trust result changed=%v err=%v, want ErrDenied and unchanged", changed, err)
	}
	var crossState string
	if err := repository.Pool.QueryRow(ctx, `SELECT trust_state FROM devices WHERE id=$1`, crossAndroid.ID).Scan(&crossState); err != nil {
		t.Fatal(err)
	}
	if crossState != "PENDING" {
		t.Fatalf("cross-account Android state=%q, want PENDING", crossState)
	}
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events
		WHERE user_id=$1 AND action='device.self_trust' AND target_id=$2`, userID, android.ID).Scan(&auditCount); err != nil {
		t.Fatal(err)
	}
	if auditCount != 1 {
		t.Fatalf("final device.self_trust audit count=%d, want 1", auditCount)
	}
}

func selfTrustRegisterInput(id uuid.UUID, deviceType, name string) RegisterDeviceInput {
	return RegisterDeviceInput{
		ID:                id,
		Type:              deviceType,
		Name:              name,
		SigningPublicKey:  selfTrustKeyBytes(id, "signing"),
		ExchangePublicKey: selfTrustKeyBytes(id, "exchange"),
		KeyID:             "self-trust-" + id.String(),
	}
}

func selfTrustKeyBytes(id uuid.UUID, label string) []byte {
	digest := sha256.Sum256([]byte("android-self-trust-key:" + label + ":" + id.String()))
	return digest[:]
}
