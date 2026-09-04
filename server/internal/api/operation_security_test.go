package api

import (
	"context"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type memoryOperationSecurityStore struct {
	admin      bool
	roleChecks int
	audits     []operationAuditRecord
	events     []store.SecurityEvent
}

type operationAuditRecord struct {
	action, targetType, result, traceID, metadata string
	targetID                                      uuid.UUID
}

func (s *memoryOperationSecurityStore) IsAdmin(context.Context, uuid.UUID) (bool, error) {
	s.roleChecks++
	return s.admin, nil
}

func (s *memoryOperationSecurityStore) Audit(_ context.Context, _, _ uuid.UUID, action, targetType string, targetID uuid.UUID, result, traceID, metadata string) error {
	s.audits = append(s.audits, operationAuditRecord{action: action, targetType: targetType, targetID: targetID, result: result, traceID: traceID, metadata: metadata})
	return nil
}

func (s *memoryOperationSecurityStore) RecordSecurityEvent(_ context.Context, event store.SecurityEvent) error {
	s.events = append(s.events, event)
	return nil
}
