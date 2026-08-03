package store

import (
	"context"
	"os"
	"testing"

	"github.com/google/uuid"
)

// This test is skipped unless the integration PostgreSQL URL is configured;
// the production integration Compose job supplies that variable.
func TestUpsertPCClassifiesDeniedAndQuota(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}
	ctx := context.Background()
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()
	if err := repository.VerifySchema(ctx); err != nil {
		t.Fatal(err)
	}
	userID, deviceID := uuid.New(), uuid.New()
	defer func() { _, _ = repository.Pool.Exec(ctx, `DELETE FROM users WHERE id=$1`, userID) }()
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, []byte(uuid.NewString() + "-item6")[:32]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state) VALUES($1,$2,'WINDOWS_PC','Quota PC',decode(repeat('01',32),'hex'),decode(repeat('02',32),'hex'),$3,'TRUSTED')`, deviceID, userID, "item6-"+deviceID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `INSERT INTO entitlements(account_id,plan_key,catalog_version,max_remote_pcs,max_live_sessions,feature_flags,version,effective_at) VALUES($1,'free',1,0,1,'{}',1,now())`, userID); err != nil {
		t.Fatal(err)
	}
	if err := repository.UpsertPC(ctx, userID, uuid.New(), PCRecord{ID: uuid.New(), Name: "wrong device", AgentVersion: "test", KeyEpoch: 1}); err != ErrDenied {
		t.Fatalf("wrong device error = %v, want ErrDenied", err)
	}
	if err := repository.UpsertPC(ctx, userID, deviceID, PCRecord{ID: uuid.New(), Name: "quota", AgentVersion: "test", KeyEpoch: 1}); err != ErrEntitlement {
		t.Fatalf("quota error = %v, want ErrEntitlement", err)
	}
}
