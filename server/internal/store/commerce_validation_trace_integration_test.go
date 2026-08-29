package store

import (
	"context"
	"crypto/sha256"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestListPaymentChannelValidationTracesReturnsLatestSafeTrace(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()
	actorID, channelID := uuid.New(), uuid.New()
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupContext, `DELETE FROM audit_events WHERE user_id=$1 OR target_id=$2`, actorID, channelID)
		_, _ = repository.Pool.Exec(cleanupContext, `DELETE FROM users WHERE id=$1`, actorID)
	})
	subject := sha256.Sum256([]byte("channel-validation-trace:" + actorID.String()))
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,role) VALUES($1,$2,'AUDITOR')`, actorID, subject[:]); err != nil {
		t.Fatal(err)
	}
	for _, event := range []struct {
		version int
		trace   string
	}{{1, "trace-v1-old"}, {2, "trace-v2"}, {1, "trace-v1-latest"}} {
		if _, err := repository.Pool.Exec(ctx, `
			INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
			VALUES($1,'payment.channel.version.validate','payment_channel',$2,'SUCCEEDED',$3,
			       jsonb_build_object('version',$4::bigint,'valid',false))`, actorID, channelID, event.trace, event.version); err != nil {
			t.Fatal(err)
		}
	}
	traces, err := repository.ListPaymentChannelValidationTraces(ctx, actorID, channelID)
	if err != nil {
		t.Fatal(err)
	}
	got := make(map[int64]string, len(traces))
	for _, trace := range traces {
		got[trace.Version] = trace.TraceID
	}
	if got[1] != "trace-v1-latest" || got[2] != "trace-v2" || len(got) != 2 {
		t.Fatalf("validation traces = %#v", got)
	}
}
