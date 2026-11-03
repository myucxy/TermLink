package migrate

import (
	"context"
	"encoding/json"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestPOSIXShellMetadataMigrationRoundTrip(t *testing.T) {
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
	schema := "migration_52_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err = admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanup, stop := context.WithTimeout(context.Background(), 15*time.Second)
		defer stop()
		if _, err := admin.Exec(cleanup, "DROP SCHEMA "+quotedSchema+" CASCADE"); err != nil {
			t.Errorf("cleanup: %v", err)
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
	for version := 1; version <= 51; version++ {
		execMigrationVersion(t, ctx, pool, version, "up")
	}
	accountID, agentID, requesterID, pcID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, accountID, repeatedMigrationByte(0x61)); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES($1,$3,'PC_AGENT','Host',$4,$5,'posix-host','TRUSTED'),
		      ($2,$3,'ANDROID','Phone',$6,$7,'posix-phone','TRUSTED')`,
		agentID, requesterID, accountID, repeatedMigrationByte(0x62), repeatedMigrationByte(0x63),
		repeatedMigrationByte(0x64), repeatedMigrationByte(0x65)); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,key_epoch)
		VALUES($1,$2,$3,'Host','test',1)`, pcID, accountID, agentID); err != nil {
		t.Fatal(err)
	}
	execMigrationVersion(t, ctx, pool, 52, "up")
	for _, shell := range []string{"bash", "zsh", "sh", "fish"} {
		if _, err := pool.Exec(ctx, `UPDATE pcs SET supported_shells=ARRAY[$2]::text[] WHERE id=$1`, pcID, shell); err != nil {
			t.Fatalf("%s metadata: %v", shell, err)
		}
		result, _ := json.Marshal(map[string]string{
			"session_id": uuid.NewString(), "terminal_command_id": shell, "shell_type": shell, "cwd": "/tmp/project",
		})
		if _, err := pool.Exec(ctx, `INSERT INTO pc_file_operations
			(id,account_id,pc_id,requester_device_id,operation,path,shell_type,status,attempt_count,result,expires_at,completed_at)
			VALUES($1,$2,$3,$4,'OPEN_TERMINAL','/tmp/project',$5,'SUCCEEDED',1,$6,now()+interval '1 hour',now())`,
			uuid.New(), accountID, pcID, requesterID, shell, result); err != nil {
			t.Fatalf("%s result: %v", shell, err)
		}
		wrongShell := "bash"
		if shell == wrongShell {
			wrongShell = "zsh"
		}
		wrongResult, _ := json.Marshal(map[string]string{
			"session_id": uuid.NewString(), "terminal_command_id": shell, "shell_type": wrongShell, "cwd": "/tmp/project",
		})
		_, err = pool.Exec(ctx, `INSERT INTO pc_file_operations
			(id,account_id,pc_id,requester_device_id,operation,path,shell_type,status,attempt_count,result,expires_at,completed_at)
			VALUES($1,$2,$3,$4,'OPEN_TERMINAL','/tmp/project',$5,'SUCCEEDED',1,$6,now()+interval '1 hour',now())`,
			uuid.New(), accountID, pcID, requesterID, shell, wrongResult)
		assertMigration25PostgresCode(t, err, "23514")
	}
	_, err = pool.Exec(ctx, `UPDATE pcs SET supported_shells=ARRAY['unknown-shell']::text[] WHERE id=$1`, pcID)
	assertMigration25PostgresCode(t, err, "23514")
	down, err := migrations.Files.ReadFile("000052_posix_shell_metadata.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	assertRollbackBlocked := func() {
		t.Helper()
		if _, err := pool.Exec(ctx, string(down)); err == nil || !strings.Contains(err.Error(), "Cannot remove POSIX shell support") {
			t.Fatalf("unsafe rollback: %v", err)
		}
	}
	assertRollbackBlocked()
	if _, err := pool.Exec(ctx, `UPDATE pcs SET supported_shells='{}' WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	assertRollbackBlocked() // Completed receipts also prevent a destructive downgrade.
	if _, err := pool.Exec(ctx, `DELETE FROM pc_file_operations WHERE pc_id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `UPDATE pcs SET terminal_commands='[{"id":"zsh","shell_type":"zsh","source":"detected","display_name":"Zsh"}]' WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	assertRollbackBlocked()
	if _, err := pool.Exec(ctx, `UPDATE pcs SET terminal_commands='[]' WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	execMigrationVersion(t, ctx, pool, 52, "down")
	_, err = pool.Exec(ctx, `UPDATE pcs SET supported_shells=ARRAY['zsh']::text[] WHERE id=$1`, pcID)
	assertMigration25PostgresCode(t, err, "23514")
	execMigrationVersion(t, ctx, pool, 52, "up")
}
