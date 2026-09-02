package transport

import (
	"errors"
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestDirectStateInputsRejectInvalidAndUnboundedValues(t *testing.T) {
	reservation := DirectTicketReservation{
		AccountID: uuid.New(), ConnectionID: uuid.New(), InitiatorID: uuid.New(),
		ResponderID: uuid.New(), ReservationID: uuid.New(),
	}
	if err := reservation.Validate(); err != nil {
		t.Fatalf("valid reservation: %v", err)
	}
	invalid := reservation
	invalid.ResponderID = invalid.InitiatorID
	if err := invalid.Validate(); !errors.Is(err, ErrDirectStateInvalid) {
		t.Fatalf("same-device reservation error=%v, want invalid", err)
	}

	now := time.Now().UTC()
	finalization := DirectTicketFinalization{
		Reservation: reservation, PathEpoch: 1, IssuedAt: now,
		ExpiresAt: now.Add(120 * time.Second), ReplayUntil: now.Add(10 * time.Minute),
	}
	if err := finalization.Validate(); err != nil {
		t.Fatalf("valid finalization: %v", err)
	}
	finalization.ExpiresAt = now.Add(120*time.Second + time.Millisecond)
	if err := finalization.Validate(); !errors.Is(err, ErrDirectStateInvalid) {
		t.Fatalf("unbounded ticket error=%v, want invalid", err)
	}
}

func TestDirectSignalPublishValidationPreservesWireLimits(t *testing.T) {
	mailbox := DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}
	presence := DirectPresence{
		AccountID: uuid.New(), DeviceID: uuid.New(), Mailbox: mailbox, PeerGeneration: uuid.New(),
	}
	request := DirectSignalPublishRequest{
		AccountID: presence.AccountID, ConnectionID: uuid.New(), Sender: presence,
		RecipientID: uuid.New(), PathEpoch: 1, SignalSequence: 1,
		SignalType: SignalPathOffer, SentAt: time.Now().UTC(), Frame: []byte{1},
	}
	if err := request.Validate(); err != nil {
		t.Fatalf("valid publish request: %v", err)
	}
	request.Frame = make([]byte, MaxSignalBytes+1)
	if err := request.Validate(); !errors.Is(err, ErrDirectStateInvalid) {
		t.Fatalf("oversize publish error=%v, want invalid", err)
	}
	request.Frame = []byte{1}
	request.SignalType = SignalCheckBatchV2
	if err := request.Validate(); !errors.Is(err, ErrDirectStateInvalid) {
		t.Fatalf("unnegotiated multipair publish error=%v, want invalid", err)
	}
	request.Sender.SupportsMultipairV2 = true
	if err := request.Validate(); err != nil {
		t.Fatalf("negotiated multipair publish: %v", err)
	}
}
