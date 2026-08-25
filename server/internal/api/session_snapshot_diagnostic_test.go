package api

import "testing"

func TestValidateSessionSnapshotDiagnostic(t *testing.T) {
	value := func(text string) *string { return &text }
	valid := []struct {
		status *string
		reason *string
	}{
		{},
		{status: value("READY")},
		{status: value("PENDING"), reason: value("PARSER_BOUNDARY")},
		{status: value("UNSUPPORTED"), reason: value("CSI_PRIVATE_MODE_UNSUPPORTED")},
		{status: value("FAILED"), reason: value("CHECKPOINT_WRITE_FAILED")},
	}
	for _, item := range valid {
		if err := validateSessionSnapshotDiagnostic(item.status, item.reason); err != nil {
			t.Fatalf("valid diagnostic rejected: status=%v reason=%v err=%v", item.status, item.reason, err)
		}
	}
	invalid := []struct {
		status *string
		reason *string
	}{
		{reason: value("ORPHAN")},
		{status: value("READY"), reason: value("NOT_ALLOWED")},
		{status: value("UNSUPPORTED")},
		{status: value("UNKNOWN"), reason: value("UNKNOWN")},
		{status: value("FAILED"), reason: value("contains terminal text")},
		{status: value("FAILED"), reason: value("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")},
	}
	for _, item := range invalid {
		if err := validateSessionSnapshotDiagnostic(item.status, item.reason); err == nil {
			t.Fatalf("invalid diagnostic accepted: status=%v reason=%v", item.status, item.reason)
		}
	}
}
