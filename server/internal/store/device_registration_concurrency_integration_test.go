package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
)

// TestConcurrentIdempotentDeviceRegistration reproduces the Android startup
// pattern: several independent API calls verify the same already-registered
// device concurrently. Every loser must resolve as the stable conflict used by
// the client to verify /devices/self; a PostgreSQL 40001 must never escape as a
// transient HTTP 500.
func TestConcurrentIdempotentDeviceRegistration(t *testing.T) {
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

	subject := sha256.Sum256([]byte("concurrent-device-register:" + uuid.NewString()))
	deviceID := uuid.New()
	input := sameAccountRegisterInput(deviceID, "ANDROID", "Concurrent Android")
	userID, device, _, err := repository.RegisterDevice(ctx, subject, input, "concurrent-register-initial")
	if err != nil {
		repository.Close()
		t.Fatal(err)
	}
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM audit_events WHERE user_id=$1 OR actor_device_id=$2 OR target_id=$2`, userID, device.ID)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=$1 OR aggregate_id=$2`, userID, device.ID)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
		repository.Close()
	})

	const callers = 12
	start := make(chan struct{})
	errorsByCaller := make(chan error, callers)
	var wait sync.WaitGroup
	wait.Add(callers)
	for index := 0; index < callers; index++ {
		go func(index int) {
			defer wait.Done()
			<-start
			_, _, _, registerErr := repository.RegisterDevice(
				ctx,
				subject,
				input,
				"concurrent-register-"+uuid.NewSHA1(uuid.Nil, []byte{byte(index)}).String(),
			)
			errorsByCaller <- registerErr
		}(index)
	}
	close(start)
	wait.Wait()
	close(errorsByCaller)

	for registerErr := range errorsByCaller {
		if !errors.Is(registerErr, ErrConflict) {
			t.Fatalf("concurrent idempotent registration error = %v, want ErrConflict", registerErr)
		}
	}
}
