package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestPCAgentPlatformAndFileCapabilityAreServerAuthoritative(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the PC Agent integration test")
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
	subject := sha256.Sum256([]byte("pc-agent-capabilities:" + userID.String()))
	if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,windows_bootstrap_consumed_at) VALUES($1,$2,now())`, userID, subject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_at)
		VALUES
		($1,$2,'PC_AGENT','Linux Agent',decode(repeat('51',32),'hex'),decode(repeat('52',32),'hex'),$3,'TRUSTED',now()),
		($4,$2,'ANDROID','Android requester',decode(repeat('53',32),'hex'),decode(repeat('54',32),'hex'),$5,'TRUSTED',now())`,
		agentID, userID, "pc-agent-"+agentID.String(), androidID, "pc-agent-android-"+androidID.String()); err != nil {
		t.Fatal(err)
	}
	if err := repository.UpsertPC(ctx, userID, agentID, PCRecord{
		ID: pcID, Name: "Linux Agent", AgentVersion: "0.3.0.50", KeyEpoch: 1,
		Platform: PCPlatformLinux, Architecture: PCArchitectureArm64, PlatformVersion: "Ubuntu 24.04",
		Capabilities: []string{PCCapabilityRealtimeTerminalV1, PCCapabilityFileOperationsV1},
	}); err != nil {
		t.Fatal(err)
	}
	pcs, err := repository.ListPCs(ctx, userID, androidID)
	if err != nil {
		t.Fatal(err)
	}
	if len(pcs) != 1 || pcs[0].ID != pcID || pcs[0].Platform != PCPlatformLinux ||
		pcs[0].Architecture != PCArchitectureArm64 || !HasPCCapability(pcs[0].Capabilities, PCCapabilityFileOperationsV1) {
		t.Fatalf("generic PC metadata did not round trip: %#v", pcs)
	}
	if _, err := repository.CreateFileOperation(ctx, userID, androidID, pcID, FileOperationCreateInput{
		Operation: FileOperationList, Path: "/tmp", ExpiresAt: time.Now().Add(time.Minute),
	}); err != nil {
		t.Fatalf("file operation with capability was denied: %v", err)
	}
	if _, err := pool.Exec(ctx, `UPDATE pcs SET capabilities=ARRAY['realtime_terminal_v1']::text[] WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.CreateFileOperation(ctx, userID, androidID, pcID, FileOperationCreateInput{
		Operation: FileOperationList, Path: "/tmp", ExpiresAt: time.Now().Add(time.Minute),
	}); !errors.Is(err, ErrDenied) {
		t.Fatalf("file operation without capability error=%v, want ErrDenied", err)
	}
}
