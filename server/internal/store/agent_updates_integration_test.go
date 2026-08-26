package store

import (
	"context"
	"crypto/sha256"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestClaimAgentUpdateReturnsQualifiedJobColumns(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the Agent update integration test")
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
	userID, agentID, androidID, pcID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
	}()

	subject := sha256.Sum256([]byte("agent-update-claim:" + userID.String()))
	if _, err := pool.Exec(ctx, `
		INSERT INTO users(id,oidc_subject_hash,windows_bootstrap_consumed_at) VALUES($1,$2,now())`,
		userID, subject[:],
	); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_at)
		VALUES
		($1,$2,'WINDOWS_PC','Windows Agent',decode(repeat('61',32),'hex'),decode(repeat('62',32),'hex'),$3,'TRUSTED',now()),
		($4,$2,'ANDROID','Android requester',decode(repeat('63',32),'hex'),decode(repeat('64',32),'hex'),$5,'TRUSTED',now())`,
		agentID, userID, "agent-update-pc-"+agentID.String(), androidID, "agent-update-android-"+androidID.String(),
	); err != nil {
		t.Fatal(err)
	}
	const currentVersion = "0.2.4.45"
	if err := repository.UpsertPC(ctx, userID, agentID, PCRecord{
		ID: pcID, Name: "Windows Agent", AgentVersion: currentVersion, KeyEpoch: 1,
		Platform: PCPlatformWindows, Architecture: PCArchitectureX64, PlatformVersion: "Windows 11",
		Capabilities: []string{PCCapabilityRealtimeTerminalV1, PCCapabilityRemoteAgentUpdateV1},
	}); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `UPDATE pcs SET remote_routing_enabled=true WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}

	artifactHash := sha256.Sum256([]byte("signed installer"))
	created, err := repository.CreateAgentUpdate(ctx, userID, androidID, pcID, AgentUpdateCreateInput{
		CurrentVersion:    currentVersion,
		CurrentBuild:      45,
		TargetVersion:     "0.2.5",
		TargetBuild:       46,
		ArtifactURL:       "https://updates.example.test/TermLink-0.2.5.exe",
		ArtifactFilename:  "TermLink-0.2.5.exe",
		ArtifactSizeBytes: int64(len("signed installer")),
		ArtifactSHA256:    artifactHash,
		ExpiresAt:         time.Now().UTC().Add(15 * time.Minute),
	})
	if err != nil {
		t.Fatal(err)
	}

	claimed, leaseToken, err := repository.ClaimAgentUpdate(ctx, userID, agentID, pcID, 2*time.Minute)
	if err != nil {
		t.Fatalf("claim Agent update: %v", err)
	}
	if claimed == nil || claimed.ID != created.ID {
		t.Fatalf("claimed job=%#v, want id=%s", claimed, created.ID)
	}
	if claimed.Status != AgentUpdateProcessing || claimed.AttemptCount != 1 || leaseToken == "" {
		t.Fatalf("claim state status=%q attempts=%d lease_present=%t", claimed.Status, claimed.AttemptCount, leaseToken != "")
	}
}
