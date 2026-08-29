package migrate

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestTerminalCommandFileOperationsMigrationContract(t *testing.T) {
	up, err := migrations.Files.ReadFile("000028_terminal_command_file_operations.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000028_terminal_command_file_operations.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	for _, expected := range []string{
		"DROP CONSTRAINT pc_file_operations_shell_type_check",
		"shell_type ~ '^[a-z][a-z0-9._-]{0,63}$'",
	} {
		if !strings.Contains(string(up), expected) {
			t.Errorf("migration 28 up omits %q", expected)
		}
	}
	for _, expected := range []string{
		"cannot be reversed while custom terminal-command operations exist",
		"shell_type NOT IN ('command-prompt', 'windows-powershell', 'powershell-7', 'wsl')",
		"shell_type IN ('command-prompt', 'windows-powershell', 'powershell-7', 'wsl')",
	} {
		if !strings.Contains(string(down), expected) {
			t.Errorf("migration 28 down omits %q", expected)
		}
	}
}

func TestTerminalCommandFileOperationsMigrationUpDownFailsClosed(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "migration_28_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err := admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cleanupCancel()
		if _, cleanupErr := admin.Exec(cleanupContext, "DROP SCHEMA "+quotedSchema+" CASCADE"); cleanupErr != nil {
			t.Errorf("drop isolated migration schema: %v", cleanupErr)
		}
	}()
	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	config.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	for version := 1; version <= 27; version++ {
		execMigrationVersion(t, ctx, pool, version, "up")
	}
	accountID, agentID, requesterID, pcID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, accountID, repeatedMigrationByte(0x51)); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES($1,$3,'WINDOWS_PC','Agent',$4,$5,'migration-28-agent','TRUSTED'),
		      ($2,$3,'ANDROID','Android',$6,$7,'migration-28-android','TRUSTED')`,
		agentID, requesterID, accountID,
		repeatedMigrationByte(0x52), repeatedMigrationByte(0x53),
		repeatedMigrationByte(0x54), repeatedMigrationByte(0x55)); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch,capabilities,terminal_commands)
		VALUES($1,$2,$3,'PC','test',1,ARRAY['file_operations_v1','terminal_commands_v1']::text[],
		       '[{"id":"custom-project","display_name":"Project CLI","shell_type":"custom","source":"manual"}]'::jsonb)`,
		pcID, accountID, agentID); err != nil {
		t.Fatal(err)
	}
	execMigrationVersion(t, ctx, pool, 28, "up")
	operationID := uuid.New()
	if _, err := pool.Exec(ctx, `
		INSERT INTO pc_file_operations(id,account_id,pc_id,requester_device_id,operation,path,shell_type,expires_at)
		VALUES($1,$2,$3,$4,'OPEN_TERMINAL','C:\\project','custom-project',now()+interval '1 hour')`,
		operationID, accountID, pcID, requesterID); err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000028_terminal_command_file_operations.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, string(down)); err == nil || !strings.Contains(err.Error(), "cannot be reversed while custom terminal-command operations exist") {
		t.Fatalf("migration 28 unsafe rollback error=%v", err)
	}
	if _, err := pool.Exec(ctx, `DELETE FROM pc_file_operations WHERE id=$1`, operationID); err != nil {
		t.Fatal(err)
	}
	execMigrationVersion(t, ctx, pool, 28, "down")
	if _, err := pool.Exec(ctx, `
		INSERT INTO pc_file_operations(id,account_id,pc_id,requester_device_id,operation,path,shell_type,expires_at)
		VALUES($1,$2,$3,$4,'OPEN_TERMINAL','C:\\project','custom-project',now()+interval '1 hour')`,
		uuid.New(), accountID, pcID, requesterID); err == nil {
		t.Fatal("migration 28 rollback retained custom terminal-command acceptance")
	}
}
