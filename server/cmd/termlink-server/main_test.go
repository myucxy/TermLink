package main

import (
	"bytes"
	"log/slog"
	"reflect"
	"strings"
	"testing"

	"github.com/google/uuid"
)

type processSensitiveLogError struct {
	text  string
	calls int
}

func (e *processSensitiveLogError) Error() string {
	e.calls++
	return e.text
}

func TestDeriveServerKeySeparatesSecurityDomains(t *testing.T) {
	master := "0123456789abcdef0123456789abcdef"
	subject := deriveServerKey(master, "oidc-subject-hash")
	token := deriveServerKey(master, "capability-token-signing")
	cookie := deriveServerKey(master, "web-cookie-master")
	registrationCode := deriveServerKey(master, "commerce-code-digest")
	registrationProof := deriveServerKey(master, "keycloak-registration-attempt-proof")
	keys := [][]byte{subject, token, cookie, registrationCode, registrationProof}
	for index, key := range keys {
		if len(key) != 32 {
			t.Fatalf("derived key %d length = %d, want 32", index, len(key))
		}
	}
	for left := range keys {
		for right := left + 1; right < len(keys); right++ {
			if bytes.Equal(keys[left], keys[right]) {
				t.Fatalf("security domains %d and %d produced the same derived key", left, right)
			}
		}
	}
	if !bytes.Equal(subject, deriveServerKey(master, "oidc-subject-hash")) {
		t.Fatal("server key derivation is not deterministic")
	}
}

type recordingAuthorityTarget struct {
	event string
	id    uuid.UUID
}

func (target *recordingAuthorityTarget) record(event string, id uuid.UUID) {
	target.event, target.id = event, id
}

func (target *recordingAuthorityTarget) Revoke(id uuid.UUID) { target.record("revoke", id) }
func (target *recordingAuthorityTarget) RefreshDevice(id uuid.UUID) {
	target.record("refresh-device", id)
}
func (target *recordingAuthorityTarget) DisableAccount(id uuid.UUID) {
	target.record("disable-account", id)
}
func (target *recordingAuthorityTarget) RefreshControlPolicy(id uuid.UUID) {
	target.record("refresh-control-policy", id)
}

func TestAuthorityChangeChannelsAndDispatch(t *testing.T) {
	wantChannels := []string{
		"device-revoked",
		"device-authorization-changed",
		"account-status-changed",
		"pc-control-policy-changed",
	}
	if got := authorityChangeChannels(); !reflect.DeepEqual(got, wantChannels) {
		t.Fatalf("authorityChangeChannels() = %v, want %v", got, wantChannels)
	}

	id := uuid.New()
	tests := map[string]string{
		"device-revoked":               "revoke",
		"device-authorization-changed": "refresh-device",
		"account-status-changed":       "disable-account",
		"pc-control-policy-changed":    "refresh-control-policy",
	}
	for channel, wantEvent := range tests {
		t.Run(channel, func(t *testing.T) {
			target := &recordingAuthorityTarget{}
			applyAuthorityChange(target, channel, id)
			if target.event != wantEvent || target.id != id {
				t.Fatalf("dispatch = %q/%s, want %q/%s", target.event, target.id, wantEvent, id)
			}
		})
	}

	target := &recordingAuthorityTarget{}
	applyAuthorityChange(target, "unknown", id)
	if target.event != "" || target.id != uuid.Nil {
		t.Fatalf("unknown event was dispatched: %#v", target)
	}
}

func TestProcessFailureLogDoesNotFormatErrorOrUnknownCommand(t *testing.T) {
	var output bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&output, nil))
	sensitiveErr := &processSensitiveLogError{text: "database_url=postgres://admin:secret@database/termlink"}
	unknownCommand := "secret-command-argument"

	logProcessFailure(logger, unknownCommand, sensitiveErr)

	logged := output.String()
	if sensitiveErr.calls != 0 || strings.Contains(logged, "postgres://") || strings.Contains(logged, "secret-command-argument") {
		t.Fatalf("process log formatted or exposed unsafe input: calls=%d log=%s", sensitiveErr.calls, logged)
	}
	if !strings.Contains(logged, `"error_class":"invalid_command"`) {
		t.Fatalf("process log is missing stable error class: %s", logged)
	}
}

func TestProcessFailureClassesAreStable(t *testing.T) {
	tests := map[string]string{
		"serve":       "production_server_failed",
		"migrate":     "database_migration_failed",
		"healthcheck": "healthcheck_failed",
	}
	for command, errorClass := range tests {
		t.Run(command, func(t *testing.T) {
			var output bytes.Buffer
			logProcessFailure(slog.New(slog.NewJSONHandler(&output, nil)), command, nil)
			if !strings.Contains(output.String(), `"error_class":"`+errorClass+`"`) {
				t.Fatalf("unexpected process failure log: %s", output.String())
			}
		})
	}
}
