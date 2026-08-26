package migrate

import (
	"context"
	"crypto/sha256"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestGenericPCAgentMigrationRoundTripPreservesIdentityAndLegacyFallback(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the migration integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	if err := UpPool(ctx, pool); err != nil {
		t.Fatal(err)
	}

	userID, deviceID, pcID := uuid.New(), uuid.New(), uuid.New()
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
	}()
	subject := sha256.Sum256([]byte("pc-agent-migration:" + userID.String()))
	if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,windows_bootstrap_consumed_at) VALUES($1,$2,now())`, userID, subject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_at)
		VALUES($1,$2,'PC_AGENT','Migration Linux Agent',decode(repeat('31',32),'hex'),decode(repeat('32',32),'hex'),$3,'TRUSTED',now())`,
		deviceID, userID, "migration23-"+deviceID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch,platform,architecture,platform_version,capabilities)
		VALUES($1,$2,$3,'Migration Linux Agent','0.3.0.50',1,'linux','arm64','Ubuntu 24.04',
		       ARRAY['realtime_terminal_v1','file_operations_v1']::text[])`, pcID, userID, deviceID); err != nil {
		t.Fatal(err)
	}

	down, err := migrations.Files.ReadFile("000023_generic_pc_agent.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, string(down)); err != nil {
		t.Fatalf("roll back migration 23: %v", err)
	}
	var rolledBackType string
	var rolledBackPCID, rolledBackDeviceID uuid.UUID
	if err := pool.QueryRow(ctx, `
		SELECT d.device_type,p.id,p.device_id FROM devices d JOIN pcs p ON p.device_id=d.id
		WHERE d.id=$1 AND p.id=$2`, deviceID, pcID).Scan(&rolledBackType, &rolledBackPCID, &rolledBackDeviceID); err != nil {
		t.Fatal(err)
	}
	if rolledBackType != "WINDOWS_PC" || rolledBackPCID != pcID || rolledBackDeviceID != deviceID {
		t.Fatalf("rollback changed PC identity: type=%q pc=%s device=%s", rolledBackType, rolledBackPCID, rolledBackDeviceID)
	}
	if _, err := pool.Exec(ctx, `SELECT capabilities FROM pcs WHERE id=$1`, pcID); err == nil {
		t.Fatal("rollback retained a P5-only capabilities column")
	}

	up, err := migrations.Files.ReadFile("000023_generic_pc_agent.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, string(up)); err != nil {
		t.Fatalf("reapply migration 23: %v", err)
	}
	var reappliedType, platform, architecture string
	var capabilities []string
	if err := pool.QueryRow(ctx, `
		SELECT d.device_type,p.platform,p.architecture,p.capabilities
		FROM devices d JOIN pcs p ON p.device_id=d.id WHERE d.id=$1 AND p.id=$2`, deviceID, pcID).
		Scan(&reappliedType, &platform, &architecture, &capabilities); err != nil {
		t.Fatal(err)
	}
	if reappliedType != "WINDOWS_PC" || platform != "windows" || architecture != "unknown" || len(capabilities) < 2 {
		t.Fatalf("reapplied compatibility state is invalid: type=%q platform=%q arch=%q capabilities=%#v",
			reappliedType, platform, architecture, capabilities)
	}
}
