package store

import (
	"crypto/sha256"
	"errors"
	"testing"
	"time"

	"github.com/google/uuid"
)

func fileTransferRouteFixture(t *testing.T) (portableDBFixture, FileOperationRecord, [32]byte) {
	t.Helper()
	f := terminalOnlyFileFixture(t)
	setFileFixtureCapabilities(t, f, []string{"realtime_terminal_v1", "terminal_commands_v1", "file_operations_v1"})
	job, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, FileOperationCreateInput{
		Operation: FileOperationDownload, Path: "/tmp/shared transfer.txt", ExpiresAt: time.Now().Add(time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	pending, err := f.store.FileTransferRoute(f.ctx, f.user, job.ID)
	if err != nil || pending.Status != FileOperationPending || pending.RequesterDevice != f.mobile || pending.PCDevice != f.agent {
		t.Fatalf("pending mobile route %+v %v", pending, err)
	}
	claimed, token, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute)
	if err != nil || claimed == nil || claimed.ID != job.ID {
		t.Fatalf("download claim %+v %v", claimed, err)
	}
	lease := fileOperationLeaseHash(t, token)
	digest := sha256.Sum256([]byte("test-only file metadata"))
	job, err = f.store.RecordFileOperationTransfer(f.ctx, f.user, f.agent, f.pc, job.ID, lease, FileOperationUploadInput{
		Filename: "shared transfer.txt", SizeBytes: 10, SHA256: digest[:],
	})
	if err != nil {
		t.Fatal(err)
	}
	route, err := f.store.FileTransferRoute(f.ctx, f.user, job.ID)
	if err != nil || route.TransferProtocol == nil || *route.TransferProtocol != FileTransferRealtimeV2 || route.Status != FileOperationProcessing {
		t.Fatalf("processing realtime route %+v %v", route, err)
	}
	return f, job, lease
}

func TestFileTransferRouteRechecksLiveAuthorityAndCapability(t *testing.T) {
	cases := []struct {
		name string
		sql  string
		args func(portableDBFixture, FileOperationRecord) []any
	}{
		{"grant_deleted", `DELETE FROM device_grants WHERE pc_id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.pc} }},
		{"grant_expired", `UPDATE device_grants SET expires_at=now()-interval '1 second' WHERE pc_id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.pc} }},
		{"grant_view", `UPDATE device_grants SET permission='VIEW' WHERE pc_id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.pc} }},
		{"grant_control", `UPDATE device_grants SET permission='CONTROL' WHERE pc_id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.pc} }},
		{"account_locked", `UPDATE users SET status='LOCKED' WHERE id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.user} }},
		{"routing_disabled", `UPDATE pcs SET remote_routing_enabled=false WHERE id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.pc} }},
		{"file_capability_removed", `UPDATE pcs SET capabilities=ARRAY['realtime_terminal_v1','terminal_commands_v1']::text[] WHERE id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.pc} }},
		{"requester_disabled", `UPDATE devices SET disabled_at=now() WHERE id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.mobile} }},
		{"requester_revoked", `UPDATE devices SET revoked_at=now() WHERE id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.mobile} }},
		{"requester_untrusted", `UPDATE devices SET trust_state='PENDING' WHERE id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.mobile} }},
		{"agent_disabled", `UPDATE devices SET disabled_at=now() WHERE id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.agent} }},
		{"agent_revoked", `UPDATE devices SET revoked_at=now() WHERE id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.agent} }},
		{"agent_untrusted", `UPDATE devices SET trust_state='PENDING' WHERE id=$1`, func(f portableDBFixture, _ FileOperationRecord) []any { return []any{f.agent} }},
		{"lease_expired", `UPDATE pc_file_operations SET lease_expires_at=now()-interval '1 second' WHERE id=$1`, func(_ portableDBFixture, job FileOperationRecord) []any { return []any{job.ID} }},
		{"job_expired", `UPDATE pc_file_operations SET created_at=now()-interval '2 hours',expires_at=now()-interval '1 hour',lease_expires_at=now()-interval '1 hour' WHERE id=$1`, func(_ portableDBFixture, job FileOperationRecord) []any { return []any{job.ID} }},
		{"job_cancelled", `UPDATE pc_file_operations SET status='CANCELLED',cancelled_at=now() WHERE id=$1`, func(_ portableDBFixture, job FileOperationRecord) []any { return []any{job.ID} }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f, job, _ := fileTransferRouteFixture(t)
			if _, err := f.store.Pool.Exec(f.ctx, tc.sql, tc.args(f, job)...); err != nil {
				t.Fatal(err)
			}
			if _, err := f.store.FileTransferRoute(f.ctx, f.user, job.ID); !errors.Is(err, ErrDenied) {
				t.Fatalf("lost authority still routed ciphertext: %v", err)
			}
		})
	}
}

func TestFileTransferRouteAutoTrustStaysSameAccountAndNeverBypassesDisabledOrCapability(t *testing.T) {
	f, job, _ := fileTransferRouteFixture(t)
	if _, err := f.store.Pool.Exec(f.ctx, `DELETE FROM device_grants WHERE pc_id=$1`, f.pc); err != nil {
		t.Fatal(err)
	}
	f.store.AutoTrustDevices = true
	if _, err := f.store.FileTransferRoute(f.ctx, f.user, job.ID); err != nil {
		t.Fatalf("same-account AutoTrust no longer permitted: %v", err)
	}
	if _, err := f.store.FileTransferRoute(f.ctx, uuid.New(), job.ID); !errors.Is(err, ErrDenied) {
		t.Fatalf("AutoTrust crossed account ownership: %v", err)
	}
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE devices SET disabled_at=now() WHERE id=$1`, f.mobile); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.FileTransferRoute(f.ctx, f.user, job.ID); !errors.Is(err, ErrDenied) {
		t.Fatalf("AutoTrust bypassed disabled endpoint: %v", err)
	}
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE devices SET disabled_at=NULL WHERE id=$1`, f.mobile); err != nil {
		t.Fatal(err)
	}
	setFileFixtureCapabilities(t, f, terminalOnlyCapabilities)
	if _, err := f.store.FileTransferRoute(f.ctx, f.user, job.ID); !errors.Is(err, ErrDenied) {
		t.Fatalf("terminal-only capabilities authorized file relay: %v", err)
	}
}

func TestFileTransferRouteReclaimedLeaseAndTerminalJobs(t *testing.T) {
	f, job, _ := fileTransferRouteFixture(t)
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE pc_file_operations SET lease_expires_at=now()-interval '1 second' WHERE id=$1`, job.ID); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.FileTransferRoute(f.ctx, f.user, job.ID); !errors.Is(err, ErrDenied) {
		t.Fatal("expired worker lease remained routed")
	}
	reclaimed, token, err := f.store.ClaimFileOperation(f.ctx, f.user, f.agent, f.pc, time.Minute)
	if err != nil || reclaimed == nil || reclaimed.ID != job.ID || reclaimed.AttemptCount != 2 {
		t.Fatalf("reclaim %+v %v", reclaimed, err)
	}
	if _, err := f.store.FileTransferRoute(f.ctx, f.user, job.ID); err != nil {
		t.Fatalf("fresh lease could not resume existing transfer: %v", err)
	}
	if _, err := f.store.CompleteFileOperation(f.ctx, f.user, f.agent, f.pc, job.ID, fileOperationLeaseHash(t, token),
		FileOperationCompleteInput{Status: FileOperationSucceeded}); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.FileTransferRoute(f.ctx, f.user, job.ID); !errors.Is(err, ErrDenied) {
		t.Fatalf("completed transfer retained a ciphertext route: %v", err)
	}
	if result, err := f.store.GetFileOperation(f.ctx, f.user, f.mobile, f.pc, job.ID); err != nil || result.Status != FileOperationSucceeded {
		t.Fatalf("HTTP completion polling no longer available: %s %v", result.Status, err)
	}
	terminal, err := f.store.CreateFileOperation(f.ctx, f.user, f.mobile, f.pc, terminalOnlyInput())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.FileTransferRoute(f.ctx, f.user, terminal.ID); !errors.Is(err, ErrDenied) {
		t.Fatalf("OPEN_TERMINAL exposed a file transfer route: %v", err)
	}
}
