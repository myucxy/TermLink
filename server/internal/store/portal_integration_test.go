package store

import (
	"context"
	"errors"
	"os"
	"testing"

	"github.com/google/uuid"
)

func TestPortalRoleAuthorityProtectsLastSuperAdmin(t *testing.T) {
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

	firstID, secondID := uuid.New(), uuid.New()
	firstHash, secondHash := make([]byte, 32), make([]byte, 32)
	copy(firstHash, []byte(uuid.NewString()))
	copy(secondHash, []byte(uuid.NewString()))
	defer func() {
		_, _ = repository.Pool.Exec(ctx, `DELETE FROM audit_events WHERE user_id IN ($1,$2) OR target_id IN ($1,$2)`, firstID, secondID)
		_, _ = repository.Pool.Exec(ctx, `DELETE FROM users WHERE id IN ($1,$2)`, firstID, secondID)
	}()
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO users(id,oidc_subject_hash,role,is_admin) VALUES
		($1,$2,'SUPER_ADMIN',true),($3,$4,'USER',false)`, firstID, firstHash, secondID, secondHash); err != nil {
		t.Fatal(err)
	}

	dashboard, err := repository.PortalAdminDashboard(ctx, firstID)
	if err != nil {
		t.Fatal(err)
	}
	if dashboard.SchemaVersion != 6 || dashboard.Users < 2 {
		t.Fatalf("dashboard schema/users = %d/%d", dashboard.SchemaVersion, dashboard.Users)
	}
	if err := repository.UpdatePortalAccountRole(ctx, firstID, secondID, RoleSuperAdmin, "role-test-promote"); err != nil {
		t.Fatal(err)
	}
	if err := repository.UpdatePortalAccountRole(ctx, firstID, firstID, RoleAdmin, "role-test-demote-first"); err != nil {
		t.Fatal(err)
	}
	if err := repository.UpdatePortalAccountRole(ctx, secondID, secondID, RoleUser, "role-test-last-super"); !errors.Is(err, ErrLastSuperAdmin) {
		t.Fatalf("last SUPER_ADMIN demotion error = %v, want ErrLastSuperAdmin", err)
	}
	if _, err := repository.Pool.Exec(ctx, `UPDATE users SET status='DELETED' WHERE id=$1`, firstID); err != nil {
		t.Fatal(err)
	}
	if err := repository.UpdatePortalAccountRole(ctx, secondID, firstID, RoleUser, "role-test-deleted"); !errors.Is(err, ErrDenied) {
		t.Fatalf("deleted account role update error = %v, want ErrDenied", err)
	}
	if err := repository.UpdatePortalAccountStatus(ctx, secondID, firstID, "ACTIVE", "status-test-deleted"); !errors.Is(err, ErrDenied) {
		t.Fatalf("deleted account activation error = %v, want ErrDenied", err)
	}
}
