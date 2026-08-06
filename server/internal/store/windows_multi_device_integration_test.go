package store

import (
	"context"
	"crypto/sha256"
	"os"
	"testing"

	"github.com/google/uuid"
)

// TestWindowsAutoTrustMultipleDevices verifies that the production
// AutoTrustDevices policy can register more than one Windows PC for an
// account without violating the bootstrap partial unique index.
func TestWindowsAutoTrustMultipleDevices(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}

	ctx := context.Background()
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	repository.AutoTrustDevices = true
	if err := repository.VerifySchema(ctx); err != nil {
		repository.Close()
		t.Fatal(err)
	}

	subject := sha256.Sum256([]byte("windows-multi-device:" + uuid.NewString()))
	var userID uuid.UUID
	var deviceIDs []uuid.UUID
	t.Cleanup(func() {
		for _, deviceID := range deviceIDs {
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM audit_events WHERE actor_device_id=$1 OR target_id=$1`, deviceID)
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM outbox WHERE aggregate_id=$1`, deviceID)
		}
		if userID != uuid.Nil {
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM audit_events WHERE user_id=$1`, userID)
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM outbox WHERE aggregate_id=$1`, userID)
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM users WHERE id=$1`, userID)
		}
		repository.Close()
	})

	firstID := uuid.New()
	userID, first, bootstrap, err := repository.RegisterDevice(ctx, subject, windowsRegisterInput(firstID, "Bootstrap PC"), "windows-multi-device-first")
	if err != nil {
		t.Fatal(err)
	}
	deviceIDs = append(deviceIDs, first.ID)
	if !bootstrap || first.TrustState != "TRUSTED" || first.ApprovedBy != nil {
		t.Fatalf("first Windows registration bootstrap=%v record=%+v", bootstrap, first)
	}

	secondID := uuid.New()
	_, second, bootstrap, err := repository.RegisterDevice(ctx, subject, windowsRegisterInput(secondID, "Second PC"), "windows-multi-device-second")
	if err != nil {
		t.Fatal(err)
	}
	deviceIDs = append(deviceIDs, second.ID)
	if bootstrap || second.TrustState != "TRUSTED" {
		t.Fatalf("second Windows registration bootstrap=%v record=%+v", bootstrap, second)
	}
	if second.ApprovedBy == nil || *second.ApprovedBy != first.ID {
		t.Fatalf("second Windows approved_by=%v, want bootstrap %s", second.ApprovedBy, first.ID)
	}

	var trustedBootstrapCount int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM devices
		WHERE user_id=$1 AND device_type='WINDOWS_PC' AND approved_by IS NULL AND trust_state='TRUSTED'`, userID).Scan(&trustedBootstrapCount); err != nil {
		t.Fatal(err)
	}
	if trustedBootstrapCount != 1 {
		t.Fatalf("trusted nil-approved Windows devices=%d, want 1", trustedBootstrapCount)
	}
}

func windowsRegisterInput(id uuid.UUID, name string) RegisterDeviceInput {
	signing := sha256.Sum256([]byte("windows-multi-device-signing:" + id.String()))
	exchange := sha256.Sum256([]byte("windows-multi-device-exchange:" + id.String()))
	return RegisterDeviceInput{
		ID:                id,
		Type:              "WINDOWS_PC",
		Name:              name,
		SigningPublicKey:  signing[:],
		ExchangePublicKey: exchange[:],
		KeyID:             "windows-multi-device-" + id.String(),
	}
}
