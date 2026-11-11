package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"testing"
	"time"

	"github.com/google/uuid"
)

type disabledRealtimeFixture struct {
	portableDBFixture
	session uuid.UUID
	foreign uuid.UUID
}

func newDisabledRealtimeFixture(t *testing.T, role string, auto bool) disabledRealtimeFixture {
	t.Helper()
	base := portablePlatformDB(t, PCArchitectureArm64, role)
	base.store.AutoTrustDevices = auto
	f := disabledRealtimeFixture{portableDBFixture: base, session: uuid.New(), foreign: uuid.New()}
	hash := sha256.Sum256([]byte(f.foreign.String()))
	if _, err := f.store.Pool.Exec(f.ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, f.foreign, hash[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Pool.Exec(f.ctx, `INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows) VALUES($1,$2,'zsh','RUNNING',80,24)`, f.session, f.pc); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Pool.Exec(f.ctx, `INSERT INTO device_grants(pc_id,device_id,permission,approved_by) VALUES($1,$2,'MANAGE',$3)`, f.pc, f.mobile, f.agent); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_, _ = f.store.Pool.Exec(ctx, `DELETE FROM outbox WHERE payload->>'user_id'=$1`, f.user.String())
		_, _ = f.store.Pool.Exec(ctx, `DELETE FROM users WHERE id=$1`, f.foreign)
	})
	return f
}

func assertDisabledRealtimeAuthority(t *testing.T, f disabledRealtimeFixture, allowed, ownerAllowed bool) {
	t.Helper()
	route, err := f.store.SessionRoute(f.ctx, f.user, f.mobile, f.session)
	if allowed {
		if err != nil || route.PCDeviceID != f.agent || route.Permission != "MANAGE" {
			t.Fatalf("active route = %+v, err=%v", route, err)
		}
	} else if !errors.Is(err, ErrDenied) {
		t.Errorf("inactive single route err=%v; want denied", err)
	}
	batch, err := f.store.SessionRoutes(f.ctx, f.user, f.mobile, []uuid.UUID{f.session})
	if err != nil {
		t.Fatal(err)
	}
	if _, found := batch[f.session]; found != allowed {
		t.Errorf("batch route present=%v; want %v", found, allowed)
	}
	for name, read := range map[string]func(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) (uuid.UUID, error){
		"catalog": f.store.SessionCatalogDevice, "AI/manage": f.store.SessionCleanupDevice,
	} {
		owner, err := read(f.ctx, f.user, f.mobile, f.pc)
		if allowed && (err != nil || owner != f.agent) || !allowed && !errors.Is(err, ErrDenied) {
			t.Errorf("%s owner=%s err=%v; allowed=%v", name, owner, err, allowed)
		}
	}
	owner, err := f.store.SessionPCDevice(f.ctx, f.user, f.session)
	if ownerAllowed && (err != nil || owner != f.agent) || !ownerAllowed && !errors.Is(err, ErrNotFound) {
		t.Errorf("PC route owner=%s err=%v; allowed=%v", owner, err, ownerAllowed)
	}
}

func TestRealtimeAuthorityRejectsDisabledAndRevokedDevices(t *testing.T) {
	for _, auto := range []bool{false, true} {
		for _, role := range []string{"IOS", "ANDROID"} {
			for _, change := range []struct {
				name, sql    string
				target       string
				ownerAllowed bool
			}{
				{"actor_disabled", `UPDATE devices SET disabled_at=now() WHERE id=$1`, "actor", true},
				{"actor_revoked_timestamp", `UPDATE devices SET revoked_at=now() WHERE id=$1`, "actor", true},
				{"owner_disabled", `UPDATE devices SET disabled_at=now() WHERE id=$1`, "owner", false},
				{"owner_revoked_timestamp", `UPDATE devices SET revoked_at=now() WHERE id=$1`, "owner", false},
				{"actor_untrusted", `UPDATE devices SET trust_state='PENDING' WHERE id=$1`, "actor", true},
				{"owner_untrusted", `UPDATE devices SET trust_state='PENDING' WHERE id=$1`, "owner", false},
				{"actor_account_mismatch", `UPDATE devices SET user_id=$2 WHERE id=$1`, "actor", true},
				{"owner_account_mismatch", `UPDATE devices SET user_id=$2 WHERE id=$1`, "owner", false},
				{"pc_owner_mismatch", `UPDATE pcs SET owner_user_id=$2 WHERE id=$1`, "pc", false},
			} {
				t.Run(fmt.Sprintf("auto_%t/%s/%s", auto, role, change.name), func(t *testing.T) {
					f := newDisabledRealtimeFixture(t, role, auto)
					assertDisabledRealtimeAuthority(t, f, true, true)
					device := f.mobile
					if change.target == "owner" {
						device = f.agent
					} else if change.target == "pc" {
						device = f.pc
					}
					var err error
					if change.name == "actor_account_mismatch" || change.name == "owner_account_mismatch" || change.name == "pc_owner_mismatch" {
						_, err = f.store.Pool.Exec(f.ctx, change.sql, device, f.foreign)
					} else {
						_, err = f.store.Pool.Exec(f.ctx, change.sql, device)
					}
					if err != nil {
						t.Fatal(err)
					}
					assertDisabledRealtimeAuthority(t, f, false, change.ownerAllowed)
				})
			}
		}
	}
}

func TestRealtimeAuthorityPreservesGrantsExpiryAndOwnerAccess(t *testing.T) {
	for _, auto := range []bool{false, true} {
		t.Run(fmt.Sprintf("auto_%t", auto), func(t *testing.T) {
			f := newDisabledRealtimeFixture(t, "IOS", auto)
			for _, permission := range []string{"VIEW", "CONTROL", "MANAGE"} {
				if _, err := f.store.Pool.Exec(f.ctx, `UPDATE device_grants SET permission=$3 WHERE pc_id=$1 AND device_id=$2`, f.pc, f.mobile, permission); err != nil {
					t.Fatal(err)
				}
				route, err := f.store.SessionRoute(f.ctx, f.user, f.mobile, f.session)
				want := permission
				if auto {
					want = "MANAGE"
				}
				if err != nil || route.Permission != want {
					t.Fatalf("permission=%s route=%+v err=%v", permission, route, err)
				}
				if _, err := f.store.SessionCatalogDevice(f.ctx, f.user, f.mobile, f.pc); err != nil {
					t.Fatal("view-capable catalog denied", err)
				}
				_, err = f.store.SessionCleanupDevice(f.ctx, f.user, f.mobile, f.pc)
				if (auto || permission == "MANAGE") && err != nil || !auto && permission != "MANAGE" && !errors.Is(err, ErrDenied) {
					t.Fatalf("manage route ignored permission=%s auto=%v err=%v", permission, auto, err)
				}
			}
			if _, err := f.store.Pool.Exec(f.ctx, `UPDATE device_grants SET expires_at=now()-interval '1 second' WHERE pc_id=$1 AND device_id=$2`, f.pc, f.mobile); err != nil {
				t.Fatal(err)
			}
			assertDisabledRealtimeAuthority(t, f, auto, true)
			if _, err := f.store.Pool.Exec(f.ctx, `DELETE FROM device_grants WHERE pc_id=$1 AND device_id=$2`, f.pc, f.mobile); err != nil {
				t.Fatal(err)
			}
			assertDisabledRealtimeAuthority(t, f, auto, true)
			owner, err := f.store.SessionRoute(f.ctx, f.user, f.agent, f.session)
			if err != nil || owner.Permission != "MANAGE" {
				t.Fatal("owning Agent lost its original self-MANAGE route", err)
			}
			batch, err := f.store.SessionRoutes(f.ctx, f.user, f.agent, []uuid.UUID{f.session})
			if err != nil || batch[f.session].Permission != "MANAGE" {
				t.Fatal("owning Agent lost its batch self-MANAGE route", err)
			}
		})
	}
}

func TestPortalDisableEmitsOneTransactionalDeviceRevocation(t *testing.T) {
	for _, auto := range []bool{false, true} {
		for _, role := range []string{"IOS", "ANDROID"} {
			t.Run(fmt.Sprintf("auto_%t/%s", auto, role), func(t *testing.T) {
				f := newDisabledRealtimeFixture(t, role, auto)
				first, err := f.store.DisableUserPortalDevice(f.ctx, f.user, f.mobile, "disable-first-operation", "test-disable")
				if err != nil || !first.Changed {
					t.Fatalf("initial disable %+v err=%v", first, err)
				}
				assertDisabledRealtimeAuthority(t, f, false, true)
				for _, key := range []string{"disable-first-operation", "disable-new-operation"} {
					result, err := f.store.DisableUserPortalDevice(f.ctx, f.user, f.mobile, key, "test-retry")
					if err != nil || key == "disable-first-operation" && !result.Changed || key != "disable-first-operation" && result.Changed {
						t.Fatalf("disable replay %+v err=%v", result, err)
					}
				}
				var events int
				var trust string
				var revoked *time.Time
				if err := f.store.Pool.QueryRow(f.ctx, `SELECT count(*) FROM outbox WHERE event_type='device.revoked' AND aggregate_id=$1 AND payload->>'user_id'=$2 AND payload->>'device_id'=$3`, f.mobile, f.user.String(), f.mobile.String()).Scan(&events); err != nil || events != 1 {
					t.Fatalf("revocation events=%d err=%v", events, err)
				}
				if err := f.store.Pool.QueryRow(f.ctx, `SELECT trust_state,revoked_at FROM devices WHERE id=$1`, f.mobile).Scan(&trust, &revoked); err != nil || trust != "TRUSTED" || revoked != nil {
					t.Fatal("disable unexpectedly changed revocation/trust semantics", err)
				}
			})
		}
	}
}

func TestPortalDisableRollsBackWhenRevocationCannotBeQueued(t *testing.T) {
	f := newDisabledRealtimeFixture(t, "IOS", true)
	name := "deny_disable_" + uuid.NewString()[:8]
	if _, err := f.store.Pool.Exec(f.ctx, `CREATE FUNCTION `+name+`() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture outbox failure'; END $$;
	 CREATE TRIGGER `+name+` BEFORE INSERT ON outbox FOR EACH ROW WHEN (NEW.aggregate_id='`+f.mobile.String()+`') EXECUTE FUNCTION `+name+`()`); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		_, _ = f.store.Pool.Exec(context.Background(), `DROP TRIGGER IF EXISTS `+name+` ON outbox; DROP FUNCTION IF EXISTS `+name+`()`)
	})
	if _, err := f.store.DisableUserPortalDevice(f.ctx, f.user, f.mobile, "disable-queue-failure", "test-rollback"); err == nil {
		t.Fatal("disable committed without its transport invalidation event")
	}
	var disabled *time.Time
	var grants, audit, receipt int
	if err := f.store.Pool.QueryRow(f.ctx, `SELECT disabled_at FROM devices WHERE id=$1`, f.mobile).Scan(&disabled); err != nil || disabled != nil {
		t.Fatal("failed disable retained disabled state", err)
	}
	if err := f.store.Pool.QueryRow(f.ctx, `SELECT count(*) FROM device_grants WHERE pc_id=$1 AND device_id=$2`, f.pc, f.mobile).Scan(&grants); err != nil || grants != 1 {
		t.Fatal("failed disable removed the grant", err)
	}
	if err := f.store.Pool.QueryRow(f.ctx, `SELECT count(*) FROM audit_events WHERE user_id=$1 AND action='user_portal.device.disable'`, f.user).Scan(&audit); err != nil || audit != 0 {
		t.Fatal("failed disable committed audit", err)
	}
	if err := f.store.Pool.QueryRow(f.ctx, `SELECT count(*) FROM idempotency_keys WHERE user_id=$1 AND idempotency_key='disable-queue-failure'`, f.user).Scan(&receipt); err != nil || receipt != 0 {
		t.Fatal("failed disable retained a success receipt", err)
	}
	assertDisabledRealtimeAuthority(t, f, true, true)
}
