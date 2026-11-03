package store

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"os"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/migrations"
)

type portableDBFixture struct {
	ctx                     context.Context
	store                   *Postgres
	user, agent, mobile, pc uuid.UUID
	arch                    string
}

func portablePlatformDB(t *testing.T, arch, role string) portableDBFixture {
	t.Helper()
	url := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if url == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	pool, err := pgxpool.New(ctx, url)
	if err != nil {
		cancel()
		t.Fatal(err)
	}
	if err := migrate.UpPool(ctx, pool); err != nil {
		pool.Close()
		cancel()
		t.Fatal(err)
	}
	f := portableDBFixture{ctx: ctx, store: &Postgres{Pool: pool, AutoTrustDevices: true}, user: uuid.New(), agent: uuid.New(), mobile: uuid.New(), pc: uuid.New(), arch: arch}
	t.Cleanup(func() {
		cleanup, c := context.WithTimeout(context.Background(), 5*time.Second)
		defer c()
		_, _ = pool.Exec(cleanup, `DELETE FROM device_grants WHERE pc_id=$1`, f.pc)
		_, _ = pool.Exec(cleanup, `DELETE FROM users WHERE id=$1`, f.user)
		pool.Close()
		cancel()
	})
	hash := sha256.Sum256([]byte(f.user.String()))
	if _, err = pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,windows_bootstrap_consumed_at) VALUES($1,$2,now())`, f.user, hash[:]); err != nil {
		t.Fatal(err)
	}
	for _, d := range []struct {
		id   uuid.UUID
		role string
	}{{f.agent, "PC_AGENT"}, {f.mobile, role}} {
		signing := sha256.Sum256([]byte("signing:" + d.id.String()))
		exchange := sha256.Sum256([]byte("exchange:" + d.id.String()))
		if _, err = pool.Exec(ctx, `INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_at)
		 VALUES($1,$2,$3,'Portable fixture',$5,$6,$4,'TRUSTED',now())`, d.id, f.user, d.role, uuid.NewString(), signing[:], exchange[:]); err != nil {
			t.Fatal(err)
		}
	}
	if err = f.store.UpsertPC(ctx, f.user, f.agent, PCRecord{ID: f.pc, Name: "Portable Mac", AgentVersion: "0.3.16.146", KeyEpoch: 1,
		Platform: PCPlatformMacOS, Architecture: arch, PlatformVersion: "macOS fixture", Capabilities: []string{PCCapabilityAIAgentV1, PCCapabilityRemoteAgentUpdateV1}}); err != nil {
		t.Fatal(err)
	}
	if _, err = pool.Exec(ctx, `UPDATE pcs SET remote_routing_enabled=true WHERE id=$1`, f.pc); err != nil {
		t.Fatal(err)
	}
	return f
}

func (f portableDBFixture) updateInput() AgentUpdateCreateInput {
	team := "ABCDE12345"
	hash := sha256.Sum256([]byte("signed notarized package fixture"))
	return AgentUpdateCreateInput{CurrentVersion: "0.3.16.146", CurrentBuild: 146, TargetVersion: "0.3.17", TargetBuild: 147,
		ArtifactURL: "https://updates.example/TermLink.pkg", ArtifactFilename: "TermLink.pkg", ArtifactSizeBytes: 123, ArtifactSHA256: hash,
		ArtifactPlatform: "macos", ArtifactArchitecture: f.arch, ArtifactType: "pkg", ArtifactAppleTeamID: &team, ExpiresAt: time.Now().UTC().Add(time.Hour)}
}

func TestPortableMacUpdateDatabaseLifecycleAndArchitectureFence(t *testing.T) {
	for _, arch := range []string{"arm64", "x64"} {
		t.Run(arch, func(t *testing.T) {
			f := portablePlatformDB(t, arch, "IOS")
			input := f.updateInput()
			wrong := input
			if arch == "arm64" {
				wrong.ArtifactArchitecture = "x64"
			} else {
				wrong.ArtifactArchitecture = "arm64"
			}
			if _, err := f.store.CreateAgentUpdate(f.ctx, f.user, f.mobile, f.pc, wrong); !errors.Is(err, ErrDenied) {
				t.Fatalf("cross-architecture create err=%v", err)
			}
			job, err := f.store.CreateAgentUpdate(f.ctx, f.user, f.mobile, f.pc, input)
			if err != nil {
				t.Fatal(err)
			}
			if job.ArtifactPlatform != "macos" || job.ArtifactArchitecture != arch || job.ArtifactType != "pkg" || job.ArtifactAppleTeamID == nil || *job.ArtifactAppleTeamID != "ABCDE12345" {
				t.Fatalf("artifact snapshot %+v", job)
			}
			claimed, token, err := f.store.ClaimAgentUpdate(f.ctx, f.user, f.agent, f.pc, time.Minute)
			if err != nil || claimed == nil || claimed.ID != job.ID {
				t.Fatalf("claim %+v %v", claimed, err)
			}
			b, err := base64.RawURLEncoding.DecodeString(token)
			if err != nil {
				t.Fatal(err)
			}
			lease := sha256.Sum256(b)
			for _, phase := range []string{AgentUpdatePrechecking, AgentUpdateDownloading, AgentUpdateVerified, AgentUpdateDrainingSessions, AgentUpdateInstalling, AgentUpdateStartingNewAgent, AgentUpdateVerifyingConnection} {
				if _, err = f.store.ProgressAgentUpdate(f.ctx, f.user, f.agent, f.pc, job.ID, lease, AgentUpdateProgressInput{Phase: phase, ProgressPercent: 50}); err != nil {
					t.Fatalf("phase %s: %v", phase, err)
				}
			}
			if _, err = f.store.RenewAgentUpdate(f.ctx, f.user, f.agent, f.pc, job.ID, lease, time.Minute); err != nil {
				t.Fatal(err)
			}
			if _, err = f.store.Pool.Exec(f.ctx, `UPDATE pcs SET agent_version='0.3.17.147' WHERE id=$1`, f.pc); err != nil {
				t.Fatal(err)
			}
			version, build := "0.3.17.147", int64(147)
			completed, err := f.store.CompleteAgentUpdate(f.ctx, f.user, f.agent, f.pc, job.ID, lease, AgentUpdateCompleteInput{Status: AgentUpdateSucceeded, ObservedAgentVersion: &version, ObservedAgentBuild: &build})
			if err != nil || completed.Status != AgentUpdateSucceeded {
				t.Fatalf("complete status=%s err=%v", completed.Status, err)
			}
			if _, err = f.store.ReconcileInstalledAgentUpdates(f.ctx, f.user, uuid.New(), f.pc, version, build, job.ID); !errors.Is(err, ErrDenied) {
				t.Fatalf("foreign Agent reconcile err=%v", err)
			}
		})
	}
}

func TestPortableUpdateRequiresManageAndRechecksBeforeInstall(t *testing.T) {
	f := portablePlatformDB(t, "arm64", "ANDROID")
	f.store.AutoTrustDevices = false
	for _, permission := range []string{"VIEW", "CONTROL", "MANAGE"} {
		if _, err := f.store.Pool.Exec(f.ctx, `INSERT INTO device_grants(device_id,pc_id,permission,approved_by) VALUES($1,$2,$3,$4)
		 ON CONFLICT(device_id,pc_id) DO UPDATE SET permission=excluded.permission,expires_at=NULL`, f.mobile, f.pc, permission, f.agent); err != nil {
			t.Fatal(err)
		}
		job, err := f.store.CreateAgentUpdate(f.ctx, f.user, f.mobile, f.pc, f.updateInput())
		if permission != "MANAGE" {
			if !errors.Is(err, ErrDenied) {
				t.Fatalf("%s created installation err=%v", permission, err)
			}
			continue
		}
		if err != nil {
			t.Fatal(err)
		}
		if _, err = f.store.Pool.Exec(f.ctx, `DELETE FROM device_grants WHERE device_id=$1 AND pc_id=$2`, f.mobile, f.pc); err != nil {
			t.Fatal(err)
		}
		claimed, _, err := f.store.ClaimAgentUpdate(f.ctx, f.user, f.agent, f.pc, time.Minute)
		if err != nil || claimed != nil {
			t.Fatalf("revoked requester claim %+v err=%v", claimed, err)
		}
		if _, err = f.store.CancelAgentUpdate(f.ctx, f.user, f.mobile, f.pc, job.ID); err != nil {
			t.Fatal(err)
		}
	}
}

func TestPortableUpdateManualNewerBuildReconcilesRealObservedVersion(t *testing.T) {
	f := portablePlatformDB(t, "arm64", "IOS")
	job, err := f.store.CreateAgentUpdate(f.ctx, f.user, f.mobile, f.pc, f.updateInput())
	if err != nil {
		t.Fatal(err)
	}
	if _, err = f.store.Pool.Exec(f.ctx, `UPDATE pcs SET agent_version='0.3.18.148' WHERE id=$1`, f.pc); err != nil {
		t.Fatal(err)
	}
	got, err := f.store.GetAgentUpdate(f.ctx, f.user, f.mobile, f.pc, job.ID)
	if err != nil || got.Status != AgentUpdateSucceeded || got.ObservedAgentBuild == nil || *got.ObservedAgentBuild != 148 || got.ObservedAgentVersion == nil || *got.ObservedAgentVersion != "0.3.18.148" {
		t.Fatalf("newer manual installation %+v err=%v", got, err)
	}
	items, err := f.store.ReconcileInstalledAgentUpdates(f.ctx, f.user, f.agent, f.pc, "0.3.18.148", 148, job.ID)
	if err != nil || len(items) != 1 {
		t.Fatalf("reconcile items=%d err=%v", len(items), err)
	}
}

func TestAgentRoutingDatabaseCASAuthorityAndTransactionalAudit(t *testing.T) {
	f := portablePlatformDB(t, "arm64", "IOS")
	p, err := f.store.GetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = f.store.SetAgentPCRemoteRouting(f.ctx, f.user, f.mobile, f.pc, false, p.PolicyVersion, "fixture"); !errors.Is(err, ErrDenied) {
		t.Fatalf("mobile routing authority %v", err)
	}
	next, err := f.store.SetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc, false, p.PolicyVersion, "fixture")
	if err != nil || next.RemoteRoutingEnabled || next.PolicyVersion != p.PolicyVersion+1 {
		t.Fatalf("disable %+v %v", next, err)
	}
	if _, err = f.store.SetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc, true, p.PolicyVersion, "fixture"); !errors.Is(err, ErrConflict) {
		t.Fatalf("stale enable err=%v", err)
	}
	var wg sync.WaitGroup
	results := make(chan error, 2)
	for i := 0; i < 2; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			_, err := f.store.SetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc, true, next.PolicyVersion, "fixture")
			results <- err
		}()
	}
	wg.Wait()
	close(results)
	success, conflict := 0, 0
	for e := range results {
		if e == nil {
			success++
		} else if errors.Is(e, ErrConflict) {
			conflict++
		} else {
			t.Fatal(e)
		}
	}
	if success != 1 || conflict != 1 {
		t.Fatalf("CAS success=%d conflict=%d", success, conflict)
	}
	var audits int
	if err = f.store.Pool.QueryRow(f.ctx, `SELECT count(*) FROM audit_events WHERE user_id=$1 AND action='agent.pc.remote_routing.update'`, f.user).Scan(&audits); err != nil || audits != 2 {
		t.Fatalf("audits=%d err=%v", audits, err)
	}
	var outbox int
	if err = f.store.Pool.QueryRow(f.ctx, `SELECT count(*) FROM outbox WHERE aggregate_id=$1 AND event_type='grant.updated'`, f.agent).Scan(&outbox); err != nil || outbox != 2 {
		t.Fatalf("outbox=%d err=%v", outbox, err)
	}
	if _, err = f.store.GetAgentPCRemoteRouting(f.ctx, uuid.New(), f.agent, f.pc); !errors.Is(err, ErrDenied) {
		t.Fatal("foreign account read allowed")
	}
}

func TestPortableShellDatabaseMetadataAndOpenTerminalResult(t *testing.T) {
	f := portablePlatformDB(t, "arm64", "IOS")
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE pcs SET capabilities=ARRAY['file_operations_v1','terminal_commands_v1','realtime_terminal_v1'],
	 supported_shells=ARRAY['bash','zsh','fish','pwsh','sh'],terminal_commands='[{"id":"zsh","display_name":"Z shell","shell_type":"zsh","source":"detected"}]' WHERE id=$1`, f.pc); err != nil {
		t.Fatal(err)
	}
	shell := "zsh"
	path := "/Users/test/项目"
	job, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, FileOperationCreateInput{Operation: FileOperationOpenTerminal, Path: path, ShellType: &shell, ExpiresAt: time.Now().UTC().Add(time.Hour)})
	if err != nil {
		t.Fatal(err)
	}
	claimed, token, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute)
	if err != nil || claimed == nil || claimed.ID != job.ID {
		t.Fatalf("file claim %+v %v", claimed, err)
	}
	b, err := base64.RawURLEncoding.DecodeString(token)
	if err != nil {
		t.Fatal(err)
	}
	lease := sha256.Sum256(b)
	result, _ := json.Marshal(map[string]any{"session_id": uuid.NewString(), "terminal_command_id": "zsh", "shell_type": "zsh", "cwd": path})
	if _, err = f.store.CompleteFileOperation(f.ctx, f.user, f.agent, f.pc, job.ID, lease, FileOperationCompleteInput{Status: FileOperationSucceeded, Result: result}); err != nil {
		t.Fatal(err)
	}
}

func TestPortableUpdateRevocationStopsPreinstallButAllowsInstallerRecovery(t *testing.T) {
	f := portablePlatformDB(t, "arm64", "IOS")
	f.store.AutoTrustDevices = false
	grant := func() {
		t.Helper()
		if _, err := f.store.Pool.Exec(f.ctx, `INSERT INTO device_grants(device_id,pc_id,permission,approved_by) VALUES($1,$2,'MANAGE',$3)`, f.mobile, f.pc, f.agent); err != nil {
			t.Fatal(err)
		}
	}
	revoke := func() {
		t.Helper()
		if _, err := f.store.Pool.Exec(f.ctx, `DELETE FROM device_grants WHERE device_id=$1 AND pc_id=$2`, f.mobile, f.pc); err != nil {
			t.Fatal(err)
		}
	}
	grant()
	job, err := f.store.CreateAgentUpdate(f.ctx, f.user, f.mobile, f.pc, f.updateInput())
	if err != nil {
		t.Fatal(err)
	}
	claimed, token, err := f.store.ClaimAgentUpdate(f.ctx, f.user, f.agent, f.pc, time.Minute)
	if err != nil || claimed == nil {
		t.Fatal(err)
	}
	b, _ := base64.RawURLEncoding.DecodeString(token)
	lease := sha256.Sum256(b)
	revoke()
	if _, err = f.store.ProgressAgentUpdate(f.ctx, f.user, f.agent, f.pc, job.ID, lease, AgentUpdateProgressInput{Phase: AgentUpdatePrechecking, ProgressPercent: 1}); !errors.Is(err, ErrNotFound) {
		t.Fatalf("revoked preinstall lease continued: %v", err)
	}
	grant()
	for _, phase := range []string{AgentUpdatePrechecking, AgentUpdateDownloading, AgentUpdateVerified, AgentUpdateDrainingSessions, AgentUpdateInstalling} {
		if _, err = f.store.ProgressAgentUpdate(f.ctx, f.user, f.agent, f.pc, job.ID, lease, AgentUpdateProgressInput{Phase: phase, ProgressPercent: 50}); err != nil {
			t.Fatal(err)
		}
	}
	revoke()
	if _, err = f.store.RenewAgentUpdate(f.ctx, f.user, f.agent, f.pc, job.ID, lease, time.Minute); err != nil {
		t.Fatalf("installed recovery cannot renew: %v", err)
	}
	if _, _, err = f.store.ClaimAgentUpdate(f.ctx, f.user, f.agent, f.pc, time.Minute); err != nil {
		t.Fatalf("installer recovery claim: %v", err)
	}
}

func TestAgentRoutingPortalDisableWinsAgainstDelayedEnable(t *testing.T) {
	f := portablePlatformDB(t, "arm64", "IOS")
	current, err := f.store.GetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc)
	if err != nil {
		t.Fatal(err)
	}
	results := make(chan error, 2)
	go func() {
		_, e := f.store.SetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc, true, current.PolicyVersion, "fixture")
		results <- e
	}()
	go func() {
		_, e := f.store.SetUserPortalPCRemoteRouting(f.ctx, f.user, f.pc, false, uuid.NewString(), "fixture")
		results <- e
	}()
	for i := 0; i < 2; i++ {
		if err := <-results; err != nil && !errors.Is(err, ErrConflict) {
			t.Fatal(err)
		}
	}
	final, err := f.store.GetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc)
	if err != nil || final.RemoteRoutingEnabled {
		t.Fatalf("portal disable overwritten %+v %v", final, err)
	}
	if _, err = f.store.SetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc, true, current.PolicyVersion, "fixture"); !errors.Is(err, ErrConflict) {
		t.Fatalf("delayed enable err=%v", err)
	}
}

func TestAgentRoutingRollsBackPolicyWhenAuditCannotPersist(t *testing.T) {
	f := portablePlatformDB(t, "arm64", "IOS")
	name := "routing_fixture_" + f.user.String()[:8]
	_, err := f.store.Pool.Exec(f.ctx, `CREATE FUNCTION `+name+`() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture audit failure'; END $$;
        CREATE TRIGGER `+name+` BEFORE INSERT ON audit_events FOR EACH ROW WHEN (NEW.user_id='`+f.user.String()+`') EXECUTE FUNCTION `+name+`()`)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_, _ = f.store.Pool.Exec(ctx, `DROP TRIGGER IF EXISTS `+name+` ON audit_events; DROP FUNCTION IF EXISTS `+name+`()`)
	})
	before, err := f.store.GetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = f.store.SetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc, false, before.PolicyVersion, "fixture"); err == nil {
		t.Fatal("audit failure was ignored")
	}
	after, err := f.store.GetAgentPCRemoteRouting(f.ctx, f.user, f.agent, f.pc)
	if err != nil || after.PolicyVersion != before.PolicyVersion || after.RemoteRoutingEnabled != before.RemoteRoutingEnabled {
		t.Fatalf("policy partly committed %+v err=%v", after, err)
	}
	var count int
	if err = f.store.Pool.QueryRow(f.ctx, `SELECT count(*) FROM outbox WHERE aggregate_id=$1`, f.agent).Scan(&count); err != nil || count != 0 {
		t.Fatalf("outbox partly committed %d err=%v", count, err)
	}
}

func TestPortableMigrationsRejectRollbackWhileNewRecordsExist(t *testing.T) {
	f := portablePlatformDB(t, "arm64", "IOS")
	if _, err := f.store.CreateAgentUpdate(f.ctx, f.user, f.mobile, f.pc, f.updateInput()); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE pcs SET supported_shells=ARRAY['pwsh'] WHERE id=$1`, f.pc); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"000054_portable_pwsh_metadata.down.sql", "000053_platform_agent_updates.down.sql"} {
		sql, err := migrations.Files.ReadFile(name)
		if err != nil {
			t.Fatal(err)
		}
		tx, err := f.store.Pool.Begin(f.ctx)
		if err != nil {
			t.Fatal(err)
		}
		_, err = tx.Exec(f.ctx, string(sql))
		_ = tx.Rollback(f.ctx)
		if err == nil {
			t.Fatalf("%s discarded new platform records", name)
		}
	}
}
