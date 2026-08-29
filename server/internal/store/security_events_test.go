package store

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
)

func TestSecurityEventValidationBoundsPersistedMaterial(t *testing.T) {
	repository := &Postgres{}
	tests := []struct {
		name  string
		event SecurityEvent
		want  string
	}{
		{"missing identity", SecurityEvent{}, "invalid security event identity"},
		{"invalid result", SecurityEvent{Action: "commerce.webhook.denied", TargetType: "webhook", Result: "SUCCEEDED", TraceID: "trace"}, "invalid security event result"},
		{"raw source", SecurityEvent{Action: "commerce.webhook.denied", TargetType: "webhook", Result: "DENIED", TraceID: "trace", SourceHash: []byte("203.0.113.1")}, "invalid security event source hash"},
		{"invalid metadata", SecurityEvent{Action: "commerce.webhook.denied", TargetType: "webhook", Result: "DENIED", TraceID: "trace", Metadata: json.RawMessage(`{"broken"`)}, "invalid security event metadata"},
		{"oversized metadata", SecurityEvent{Action: "commerce.webhook.denied", TargetType: "webhook", Result: "DENIED", TraceID: "trace", Metadata: json.RawMessage(`{"value":"` + strings.Repeat("x", maxSecurityEventMetadataBytes) + `"}`)}, "invalid security event metadata"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			err := repository.RecordSecurityEvent(context.Background(), test.event)
			if err == nil || !strings.Contains(err.Error(), test.want) {
				t.Fatalf("error = %v, want %q", err, test.want)
			}
		})
	}

	valid := SecurityEvent{
		Action: "commerce.webhook.denied", TargetType: "webhook", Result: "DENIED",
		TraceID: "trace", SourceHash: make([]byte, 32), Metadata: json.RawMessage(`{"reason":"INVALID_SIGNATURE"}`),
	}
	if err := repository.RecordSecurityEvent(context.Background(), valid); err == nil || !strings.Contains(err.Error(), "store is unavailable") {
		t.Fatalf("valid event reached unexpected result: %v", err)
	}
}
