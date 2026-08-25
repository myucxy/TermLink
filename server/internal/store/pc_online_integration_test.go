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

func TestListPCsReportsServerAuthoritativeOnline(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}

	userID, deviceID, pcID := uuid.New(), uuid.New(), uuid.New()
	subjectHash := sha256.Sum256([]byte("pc-online:" + userID.String()))
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupContext, `DELETE FROM users WHERE id=$1`, userID)
		repository.Close()
	})

	if _, err := repository.Pool.Exec(ctx,
		`INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subjectHash[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(
			id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,last_seen_at)
		VALUES($1,$2,'WINDOWS_PC','Online PC',decode(repeat('01',32),'hex'),
		       decode(repeat('02',32),'hex'),$3,'TRUSTED',now())`,
		deviceID, userID, "pc-online-"+deviceID.String()); err != nil {
		t.Fatal(err)
	}
	if err := repository.UpsertPC(ctx, userID, deviceID, PCRecord{
		ID:              pcID,
		Name:            "Online PC",
		AgentVersion:    "integration-test",
		KeyEpoch:        1,
		SupportedShells: []string{"command-prompt", "windows-powershell"},
	}); err != nil {
		t.Fatal(err)
	}

	pcs, err := repository.ListPCs(ctx, userID, deviceID)
	if err != nil {
		t.Fatal(err)
	}
	if len(pcs) != 1 || pcs[0].ID != pcID || !pcs[0].Online {
		t.Fatalf("fresh PCs=%+v, want PC %s online", pcs, pcID)
	}
	if got := pcs[0].SupportedShells; len(got) != 2 || got[0] != "command-prompt" || got[1] != "windows-powershell" {
		t.Fatalf("supported shells=%v, want Agent-reported ordered capabilities", got)
	}

	if _, err := repository.Pool.Exec(ctx,
		`UPDATE pcs SET last_seen_at=now()-interval '46 seconds' WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	pcs, err = repository.ListPCs(ctx, userID, deviceID)
	if err != nil {
		t.Fatal(err)
	}
	if len(pcs) != 1 || pcs[0].ID != pcID || pcs[0].Online {
		t.Fatalf("stale PCs=%+v, want PC %s offline", pcs, pcID)
	}
}
