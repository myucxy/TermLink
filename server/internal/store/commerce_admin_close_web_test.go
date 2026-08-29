package store

import (
	"crypto/sha256"
	"testing"

	"github.com/google/uuid"
)

func TestAdminCloseWebAndDeviceInputsHaveExplicitTrustBoundaries(t *testing.T) {
	input := AdminCloseOrderInput{
		ActorID: uuid.New(), OrderID: uuid.New(), ReasonCode: "USER_REQUEST",
		IdempotencyKey: "web-close-key-0001", RequestHash: sha256.Sum256([]byte("request")),
		TraceID: "trace-close-0001",
	}
	if validAdminCloseInput(input, true) {
		t.Fatal("device API input accepted without a trusted device identifier")
	}
	if !validAdminCloseInput(input, false) {
		t.Fatal("strong Web input was rejected solely because browsers have no TermLink device identifier")
	}
	input.ActorDeviceID = uuid.New()
	if !validAdminCloseInput(input, true) {
		t.Fatal("device API input with a device identifier was rejected before the database trust check")
	}
	input.TraceID = ""
	if validAdminCloseInput(input, false) {
		t.Fatal("Web input accepted without a trace identifier")
	}
}
