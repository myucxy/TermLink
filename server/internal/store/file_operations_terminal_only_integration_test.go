package store

import (
	"encoding/json"
	"errors"
	"testing"
	"time"

	"github.com/google/uuid"
)

var terminalOnlyCapabilities = []string{"realtime_terminal_v1", "terminal_commands_v1"}

func terminalOnlyFileFixture(t *testing.T) portableDBFixture {
	t.Helper()
	f := portablePlatformDB(t, "arm64", "IOS")
	f.store.AutoTrustDevices = false
	setFileFixtureCapabilities(t, f, terminalOnlyCapabilities)
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE pcs SET supported_shells=ARRAY['zsh']::text[],
		terminal_commands='[{"id":"zsh","display_name":"Z shell","shell_type":"zsh","source":"detected"}]'::jsonb WHERE id=$1`, f.pc); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Pool.Exec(f.ctx, `INSERT INTO device_grants(device_id,pc_id,permission,approved_by)
		VALUES($1,$2,'MANAGE',$3)`, f.mobile, f.pc, f.agent); err != nil {
		t.Fatal(err)
	}
	return f
}

func setFileFixtureCapabilities(t *testing.T, f portableDBFixture, capabilities []string) {
	t.Helper()
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE pcs SET capabilities=$2 WHERE id=$1`, f.pc, capabilities); err != nil {
		t.Fatal(err)
	}
}

func terminalOnlyInput() FileOperationCreateInput {
	return FileOperationCreateInput{Operation: FileOperationOpenTerminal, Path: "/tmp/终端 with spaces", ShellType: stringPointer("zsh"), ExpiresAt: time.Now().UTC().Add(time.Hour)}
}

func terminalOnlyResult() json.RawMessage {
	body, _ := json.Marshal(map[string]string{"session_id": uuid.NewString(), "terminal_command_id": "zsh", "shell_type": "zsh", "cwd": terminalOnlyInput().Path})
	return body
}

func TestFileOperationTerminalOnlyMacLifecycle(t *testing.T) {
	for _, arch := range []string{"arm64", "x64"} {
		t.Run(arch, func(t *testing.T) {
			f := terminalOnlyFileFixture(t)
			if _, err := f.store.Pool.Exec(f.ctx, `UPDATE pcs SET architecture=$2 WHERE id=$1`, f.pc, arch); err != nil {
				t.Fatal(err)
			}
			created, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, terminalOnlyInput())
			if err != nil {
				t.Fatal(err)
			}
			claimed, token, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute)
			if err != nil || claimed == nil || claimed.ID != created.ID {
				t.Fatalf("terminal-only claim item=%+v err=%v", claimed, err)
			}
			lease := fileOperationLeaseHash(t, token)
			if _, err = f.store.AgentFileOperation(f.ctx, f.user, f.agent, f.pc, created.ID, lease); err != nil {
				t.Fatal(err)
			}
			wrong := lease
			wrong[0]++
			if _, err = f.store.RenewFileOperation(f.ctx, f.user, f.agent, f.pc, created.ID, wrong, time.Minute); !errors.Is(err, ErrConflict) {
				t.Fatalf("foreign lease renewed: %v", err)
			}
			if _, err = f.store.RenewFileOperation(f.ctx, f.user, f.agent, f.pc, created.ID, lease, time.Minute); err != nil {
				t.Fatal(err)
			}
			input := FileOperationCompleteInput{Status: FileOperationSucceeded, Result: terminalOnlyResult()}
			completed, err := f.store.CompleteFileOperation(f.ctx, f.user, f.agent, f.pc, created.ID, lease, input)
			if err != nil || completed.Status != FileOperationSucceeded {
				t.Fatalf("terminal-only completion %s err=%v", completed.Status, err)
			}
			if _, err = f.store.CompleteFileOperation(f.ctx, f.user, f.agent, f.pc, created.ID, lease, input); err != nil {
				t.Fatalf("terminal-only completion retry: %v", err)
			}
			for _, operation := range []string{FileOperationList, FileOperationDownload} {
				if _, err = f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, FileOperationCreateInput{
					Operation: operation, Path: "/tmp/file", ExpiresAt: time.Now().Add(time.Hour),
				}); !errors.Is(err, ErrDenied) {
					t.Fatalf("terminal-only Mac created %s: %v", operation, err)
				}
			}
		})
	}
}

func TestFileOperationTerminalOnlyMacMixedQueueFiltersFiles(t *testing.T) {
	f := terminalOnlyFileFixture(t)
	setFileFixtureCapabilities(t, f, append(append([]string{}, terminalOnlyCapabilities...), "file_operations_v1"))
	var files []FileOperationRecord
	for _, operation := range []string{FileOperationList, FileOperationDownload} {
		item, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, FileOperationCreateInput{
			Operation: operation, Path: "/tmp/retained-file", ExpiresAt: time.Now().Add(time.Hour),
		})
		if err != nil {
			t.Fatal(err)
		}
		files = append(files, item)
	}
	// Keep one former DOWNLOAD in PROCESSING with an owned lease. Capability
	// withdrawal must block read/renew/upload/complete as well as a new claim.
	if _, err := f.store.CancelFileOperation(f.ctx, f.user, f.mobile, f.pc, files[0].ID); err != nil {
		t.Fatal(err)
	}
	download, downloadToken, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute)
	if err != nil || download == nil || download.ID != files[1].ID {
		t.Fatalf("pre-withdrawal download claim: %+v %v", download, err)
	}
	downloadLease := fileOperationLeaseHash(t, downloadToken)
	// Restore LIST to pending and expire the DOWNLOAD processing lease. Both
	// are due to claim, while the old download token still cannot bypass the
	// operation-specific capability fence on read/update paths.
	if _, err = f.store.Pool.Exec(f.ctx, `UPDATE pc_file_operations SET status='PENDING',cancelled_at=NULL WHERE id=$1`, files[0].ID); err != nil {
		t.Fatal(err)
	}
	if _, err = f.store.Pool.Exec(f.ctx, `UPDATE pc_file_operations SET lease_expires_at=now()-interval '1 second' WHERE id=$1`, files[1].ID); err != nil {
		t.Fatal(err)
	}
	expired, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, FileOperationCreateInput{
		Operation: FileOperationList, Path: "/tmp/expired-file", ExpiresAt: time.Now().Add(time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE pc_file_operations SET created_at=now()-interval '2 hours',expires_at=now()-interval '1 hour' WHERE id=$1`, expired.ID); err != nil {
		t.Fatal(err)
	}
	terminal, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, terminalOnlyInput())
	if err != nil {
		t.Fatal(err)
	}
	setFileFixtureCapabilities(t, f, terminalOnlyCapabilities)
	claimed, token, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute)
	if err != nil || claimed == nil || claimed.ID != terminal.ID {
		t.Fatalf("mixed queue leaked a file job: %+v err=%v", claimed, err)
	}
	if another, _, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute); err != nil || another != nil {
		t.Fatalf("terminal-only poller claimed retained file work: %+v %v", another, err)
	}
	if _, err := f.store.AgentFileOperation(f.ctx, f.user, f.agent, f.pc, download.ID, downloadLease); !errors.Is(err, ErrNotFound) {
		t.Fatalf("old download lease bypassed read fence: %v", err)
	}
	if _, err := f.store.RenewFileOperation(f.ctx, f.user, f.agent, f.pc, download.ID, downloadLease, time.Minute); !errors.Is(err, ErrNotFound) {
		t.Fatalf("old download lease bypassed renew fence: %v", err)
	}
	if _, err := f.store.RecordFileOperationTransfer(f.ctx, f.user, f.agent, f.pc, download.ID, downloadLease,
		FileOperationUploadInput{Filename: "file", SizeBytes: 1, SHA256: make([]byte, 32)}); !errors.Is(err, ErrNotFound) {
		t.Fatalf("old download lease bypassed upload fence: %v", err)
	}
	if _, err := f.store.CompleteFileOperation(f.ctx, f.user, f.agent, f.pc, download.ID, downloadLease,
		FileOperationCompleteInput{Status: FileOperationFailed, ErrorMessage: stringPointer("stale")}); !errors.Is(err, ErrNotFound) {
		t.Fatalf("old download lease bypassed completion fence: %v", err)
	}
	files = append(files, expired)
	for _, item := range files {
		var attempts int
		var status string
		want := FileOperationPending
		if item.Operation == FileOperationDownload {
			want = FileOperationProcessing
		}
		if err := f.store.Pool.QueryRow(f.ctx, `SELECT status,attempt_count FROM pc_file_operations WHERE id=$1`, item.ID).Scan(&status, &attempts); err != nil || status != want {
			t.Fatalf("skipped file was mutated: %s %v", status, err)
		}
		if item.Operation == FileOperationList && attempts != 0 || item.Operation == FileOperationDownload && attempts != 1 {
			t.Fatal("terminal-only claim incremented skipped file attempts")
		}
	}
	if _, err := f.store.CompleteFileOperation(f.ctx, f.user, f.agent, f.pc, terminal.ID, fileOperationLeaseHash(t, token),
		FileOperationCompleteInput{Status: FileOperationSucceeded, Result: terminalOnlyResult()}); err != nil {
		t.Fatal(err)
	}
}

func TestFileOperationTerminalOnlyAuthorityRecheckedAtEveryStage(t *testing.T) {
	cases := []struct {
		name string
		sql  string
		args func(portableDBFixture) []any
	}{
		{"grant_deleted", `DELETE FROM device_grants WHERE pc_id=$1`, func(f portableDBFixture) []any { return []any{f.pc} }},
		{"grant_view", `UPDATE device_grants SET permission='VIEW' WHERE pc_id=$1`, func(f portableDBFixture) []any { return []any{f.pc} }},
		{"grant_control", `UPDATE device_grants SET permission='CONTROL' WHERE pc_id=$1`, func(f portableDBFixture) []any { return []any{f.pc} }},
		{"grant_expired", `UPDATE device_grants SET expires_at=now()-interval '1 second' WHERE pc_id=$1`, func(f portableDBFixture) []any { return []any{f.pc} }},
		{"requester_revoked", `UPDATE devices SET trust_state='REVOKED',revoked_at=now() WHERE id=$1`, func(f portableDBFixture) []any { return []any{f.mobile} }},
		{"requester_disabled", `UPDATE devices SET disabled_at=now() WHERE id=$1`, func(f portableDBFixture) []any { return []any{f.mobile} }},
		{"agent_revoked", `UPDATE devices SET trust_state='REVOKED',revoked_at=now() WHERE id=$1`, func(f portableDBFixture) []any { return []any{f.agent} }},
		{"agent_disabled", `UPDATE devices SET disabled_at=now() WHERE id=$1`, func(f portableDBFixture) []any { return []any{f.agent} }},
		{"account_locked", `UPDATE users SET status='LOCKED' WHERE id=$1`, func(f portableDBFixture) []any { return []any{f.user} }},
		{"routing_disabled", `UPDATE pcs SET remote_routing_enabled=false WHERE id=$1`, func(f portableDBFixture) []any { return []any{f.pc} }},
		{"terminal_capability_removed", `UPDATE pcs SET capabilities=ARRAY['terminal_commands_v1']::text[] WHERE id=$1`, func(f portableDBFixture) []any { return []any{f.pc} }},
		{"commands_capability_removed", `UPDATE pcs SET capabilities=ARRAY['realtime_terminal_v1']::text[] WHERE id=$1`, func(f portableDBFixture) []any { return []any{f.pc} }},
		{"shell_removed", `UPDATE pcs SET supported_shells='{}'::text[],terminal_commands='[]'::jsonb WHERE id=$1`, func(f portableDBFixture) []any { return []any{f.pc} }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := terminalOnlyFileFixture(t)
			processing, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, terminalOnlyInput())
			if err != nil {
				t.Fatal(err)
			}
			claimed, token, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute)
			if err != nil || claimed == nil || claimed.ID != processing.ID {
				t.Fatalf("fixture claim failed: %v", err)
			}
			lease := fileOperationLeaseHash(t, token)
			pending, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, terminalOnlyInput())
			if err != nil {
				t.Fatal(err)
			}
			if _, err := f.store.Pool.Exec(f.ctx, tc.sql, tc.args(f)...); err != nil {
				t.Fatal(err)
			}
			if _, err = f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, terminalOnlyInput()); !errors.Is(err, ErrDenied) {
				t.Fatalf("authority loss did not block create: %v", err)
			}
			if item, _, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute); item != nil || err != nil && !errors.Is(err, ErrDenied) {
				t.Fatalf("authority loss did not block claim: %+v %v", item, err)
			}
			if _, err = f.store.AgentFileOperation(f.ctx, f.user, f.agent, f.pc, processing.ID, lease); !errors.Is(err, ErrNotFound) {
				t.Fatalf("authority loss did not block agent read: %v", err)
			}
			if _, err = f.store.RenewFileOperation(f.ctx, f.user, f.agent, f.pc, processing.ID, lease, time.Minute); !errors.Is(err, ErrNotFound) {
				t.Fatalf("authority loss did not block renew: %v", err)
			}
			if _, err = f.store.CompleteFileOperation(f.ctx, f.user, f.agent, f.pc, processing.ID, lease,
				FileOperationCompleteInput{Status: FileOperationSucceeded, Result: terminalOnlyResult()}); !errors.Is(err, ErrNotFound) {
				t.Fatalf("authority loss did not block completion: %v", err)
			}
			var state string
			var attempts int
			if err := f.store.Pool.QueryRow(f.ctx, `SELECT status,attempt_count FROM pc_file_operations WHERE id=$1`, pending.ID).Scan(&state, &attempts); err != nil || state != FileOperationPending || attempts != 0 {
				t.Fatalf("denied job mutated state=%s attempts=%d err=%v", state, attempts, err)
			}
		})
	}
}

func TestFileOperationTerminalOnlyAutoTrustAndPlatformFence(t *testing.T) {
	f := terminalOnlyFileFixture(t)
	if _, err := f.store.Pool.Exec(f.ctx, `DELETE FROM device_grants WHERE pc_id=$1`, f.pc); err != nil {
		t.Fatal(err)
	}
	f.store.AutoTrustDevices = true
	job, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, terminalOnlyInput())
	if err != nil {
		t.Fatal(err)
	}
	claimed, token, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute)
	if err != nil || claimed == nil || claimed.ID != job.ID {
		t.Fatalf("same-account AutoTrust failed %+v %v", claimed, err)
	}
	if _, err = f.store.CompleteFileOperation(f.ctx, f.user, f.agent, f.pc, job.ID, fileOperationLeaseHash(t, token),
		FileOperationCompleteInput{Status: FileOperationSucceeded, Result: terminalOnlyResult()}); err != nil {
		t.Fatal(err)
	}
	for _, platform := range []string{"windows", "linux"} {
		if _, err := f.store.Pool.Exec(f.ctx, `UPDATE pcs SET platform=$2 WHERE id=$1`, f.pc, platform); err != nil {
			t.Fatal(err)
		}
		if _, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, terminalOnlyInput()); !errors.Is(err, ErrDenied) {
			t.Fatalf("Mac-only capability exception applied to %s: %v", platform, err)
		}
	}
	other := portablePlatformDB(t, "arm64", "IOS")
	if _, err = f.store.CreateFileOperation(f.ctx, other.user, other.mobile, f.pc, terminalOnlyInput()); !errors.Is(err, ErrDenied) {
		t.Fatalf("AutoTrust crossed account ownership: %v", err)
	}
	if _, _, err = f.store.ClaimFileOperation(f.ctx, f.user, other.agent, f.pc, time.Minute); !errors.Is(err, ErrDenied) {
		t.Fatalf("a foreign Agent claimed work: %v", err)
	}
}
