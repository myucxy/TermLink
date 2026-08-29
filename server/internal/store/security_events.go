package store

import (
	"context"
	"encoding/json"
	"errors"
	"strings"

	"github.com/google/uuid"
)

const maxSecurityEventMetadataBytes = 4096

// SecurityEvent records a bounded, privacy-preserving security decision. The
// caller must use stable action and metadata values rather than request data.
type SecurityEvent struct {
	UserID        *uuid.UUID
	ActorDeviceID *uuid.UUID
	Action        string
	TargetType    string
	Result        string
	TraceID       string
	SourceHash    []byte
	Metadata      json.RawMessage
}

func (s *Postgres) RecordSecurityEvent(ctx context.Context, event SecurityEvent) error {
	event.Action = strings.TrimSpace(event.Action)
	event.TargetType = strings.TrimSpace(event.TargetType)
	event.TraceID = strings.TrimSpace(event.TraceID)
	if event.Action == "" || len(event.Action) > 128 || event.TargetType == "" || len(event.TargetType) > 64 || event.TraceID == "" || len(event.TraceID) > 128 {
		return errors.New("invalid security event identity")
	}
	if event.Result != "DENIED" && event.Result != "FAILED" {
		return errors.New("invalid security event result")
	}
	if len(event.SourceHash) != 0 && len(event.SourceHash) != 32 {
		return errors.New("invalid security event source hash")
	}
	if len(event.Metadata) == 0 {
		event.Metadata = json.RawMessage(`{}`)
	}
	if len(event.Metadata) > maxSecurityEventMetadataBytes || !json.Valid(event.Metadata) {
		return errors.New("invalid security event metadata")
	}
	if s == nil || s.Pool == nil {
		return errors.New("security event store is unavailable")
	}
	_, err := s.Pool.Exec(ctx, `
		INSERT INTO audit_events(
			user_id,actor_device_id,action,target_type,target_id,result,ip_hash,trace_id,metadata
		) VALUES($1,$2,$3,$4,NULL,$5,$6,$7,$8::jsonb)`,
		event.UserID, event.ActorDeviceID, event.Action, event.TargetType,
		event.Result, nullableBytes(event.SourceHash), event.TraceID, event.Metadata,
	)
	return err
}

func nullableBytes(value []byte) any {
	if len(value) == 0 {
		return nil
	}
	return value
}
