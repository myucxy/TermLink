package kv

import (
	"context"
	"errors"
	"fmt"
	"math"
	"os"
	"strconv"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
	"github.com/termlink/termlink/server/internal/transport"
)

func TestDirectTransportStateIsSharedAndGenerationFenced(t *testing.T) {
	first, second, cleanup := directIntegrationStores(t)
	defer cleanup()
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	accountID, connectionID := uuid.New(), uuid.New()
	pcID, androidID := uuid.New(), uuid.New()
	reservationID := uuid.New()
	reservation := transport.DirectTicketReservation{
		AccountID: accountID, ConnectionID: connectionID, InitiatorID: pcID,
		ResponderID: androidID, ReservationID: reservationID,
	}

	const workers = 8
	start := make(chan struct{})
	results := make(chan transport.DirectTicketReserveResult, workers)
	errorsSeen := make(chan error, workers)
	var wait sync.WaitGroup
	for index := 0; index < workers; index++ {
		store := first
		if index%2 == 1 {
			store = second
		}
		wait.Add(1)
		go func() {
			defer wait.Done()
			<-start
			result, err := store.ReserveTicket(ctx, reservation)
			results <- result
			errorsSeen <- err
		}()
	}
	close(start)
	wait.Wait()
	close(results)
	close(errorsSeen)
	for err := range errorsSeen {
		if err != nil {
			t.Fatalf("ReserveTicket(): %v", err)
		}
	}
	accepted, replayed := 0, 0
	for result := range results {
		switch result.Status {
		case transport.DirectTicketReserveAccepted:
			accepted++
		case transport.DirectTicketReserveReplay:
			replayed++
		default:
			t.Fatalf("unexpected reserve status %v", result.Status)
		}
	}
	if accepted != 1 || replayed != workers-1 {
		t.Fatalf("accepted/replayed=%d/%d, want 1/%d", accepted, replayed, workers-1)
	}

	now := time.Now().UTC()
	finalization := transport.DirectTicketFinalization{
		Reservation: reservation, PathEpoch: 7, IssuedAt: now,
		ExpiresAt: now.Add(120 * time.Second), ReplayUntil: now.Add(10 * time.Minute),
	}
	if status, err := second.FinalizeTicket(ctx, finalization); err != nil || status != transport.DirectTicketFinalizeAccepted {
		t.Fatalf("cross-store FinalizeTicket() status=%v error=%v", status, err)
	}
	if removed, err := first.AbortTicket(ctx, reservation); err != nil || removed {
		t.Fatalf("AbortTicket() removed active route=%t error=%v", removed, err)
	}

	instanceID := uuid.New()
	oldMailbox := transport.DirectMailbox{InstanceID: instanceID, Generation: uuid.New()}
	newMailbox := transport.DirectMailbox{InstanceID: instanceID, Generation: uuid.New()}
	if result, err := first.RegisterMailbox(ctx, oldMailbox); err != nil || !result.Registered {
		t.Fatalf("register old mailbox result=%+v error=%v", result, err)
	}
	oldPresence := transport.DirectPresence{
		AccountID: accountID, DeviceID: pcID, Mailbox: oldMailbox, PeerGeneration: uuid.New(),
	}
	if result, err := first.RegisterPresence(ctx, oldPresence); err != nil || !result.Registered {
		t.Fatalf("register old presence result=%+v error=%v", result, err)
	}
	if result, err := second.RegisterMailbox(ctx, newMailbox); err != nil || !result.Registered || !result.Replaced {
		t.Fatalf("replace mailbox result=%+v error=%v", result, err)
	}
	newPresence := oldPresence
	newPresence.Mailbox = newMailbox
	newPresence.PeerGeneration = uuid.New()
	if result, err := second.RegisterPresence(ctx, newPresence); err != nil || !result.Registered || !result.Replaced {
		t.Fatalf("replace presence result=%+v error=%v", result, err)
	}
	if refreshed, err := first.RefreshMailbox(ctx, oldMailbox); err != nil || refreshed {
		t.Fatalf("old mailbox refresh=%t error=%v", refreshed, err)
	}
	if removed, err := first.RemoveMailbox(ctx, oldMailbox); err != nil || removed {
		t.Fatalf("old mailbox remove=%t error=%v", removed, err)
	}
	if refreshed, err := first.RefreshPresence(ctx, oldPresence); err != nil || refreshed {
		t.Fatalf("old presence refresh=%t error=%v", refreshed, err)
	}
	if removed, err := first.RemovePresence(ctx, oldPresence); err != nil || removed {
		t.Fatalf("old presence remove=%t error=%v", removed, err)
	}
	if refreshed, err := second.RefreshPresence(ctx, newPresence); err != nil || !refreshed {
		t.Fatalf("new presence refresh=%t error=%v", refreshed, err)
	}
	crossAccount := newPresence
	crossAccount.AccountID = uuid.New()
	crossAccount.PeerGeneration = uuid.New()
	if result, err := first.RegisterPresence(ctx, crossAccount); err != nil || result.Registered {
		t.Fatalf("cross-account presence result=%+v error=%v", result, err)
	}
	if refreshed, err := second.RefreshPresence(ctx, newPresence); err != nil || !refreshed {
		t.Fatalf("cross-account attempt replaced current presence: refresh=%t error=%v", refreshed, err)
	}
}

func TestDirectTransportStatePublishesByteIdenticallyAcrossStores(t *testing.T) {
	first, second, cleanup := directIntegrationStores(t)
	defer cleanup()
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	accountID, connectionID := uuid.New(), uuid.New()
	senderID, recipientID := uuid.New(), uuid.New()
	reservation := transport.DirectTicketReservation{
		AccountID: accountID, ConnectionID: connectionID, InitiatorID: senderID,
		ResponderID: recipientID, ReservationID: uuid.New(),
	}
	if result, err := first.ReserveTicket(ctx, reservation); err != nil || result.Status != transport.DirectTicketReserveAccepted {
		t.Fatalf("ReserveTicket() result=%+v error=%v", result, err)
	}
	now := time.Now().UTC()
	if status, err := second.FinalizeTicket(ctx, transport.DirectTicketFinalization{
		Reservation: reservation, PathEpoch: 9, IssuedAt: now,
		ExpiresAt: now.Add(120 * time.Second), ReplayUntil: now.Add(10 * time.Minute),
	}); err != nil || status != transport.DirectTicketFinalizeAccepted {
		t.Fatalf("FinalizeTicket() status=%v error=%v", status, err)
	}

	senderMailbox := transport.DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}
	recipientMailbox := transport.DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}
	if _, err := first.RegisterMailbox(ctx, senderMailbox); err != nil {
		t.Fatal(err)
	}
	subscription, err := second.SubscribeMailbox(ctx, recipientMailbox)
	if err != nil {
		t.Fatal(err)
	}
	defer subscription.Close()
	if _, err := second.RegisterMailbox(ctx, recipientMailbox); err != nil {
		t.Fatal(err)
	}
	sender := transport.DirectPresence{
		AccountID: accountID, DeviceID: senderID, Mailbox: senderMailbox, PeerGeneration: uuid.New(),
	}
	recipient := transport.DirectPresence{
		AccountID: accountID, DeviceID: recipientID, Mailbox: recipientMailbox, PeerGeneration: uuid.New(),
	}
	if _, err := first.RegisterPresence(ctx, sender); err != nil {
		t.Fatal(err)
	}
	if _, err := second.RegisterPresence(ctx, recipient); err != nil {
		t.Fatal(err)
	}
	if status, err := first.ConsumeSignalBudget(ctx, transport.DirectSignalBudgetRequest{Sender: sender}); err != nil || status != transport.DirectSignalBudgetAccepted {
		t.Fatalf("ConsumeSignalBudget() status=%v error=%v", status, err)
	}

	frame := []byte{0, 1, 2, 3, 0xff, 0, 0x80}
	request := transport.DirectSignalPublishRequest{
		AccountID: accountID, ConnectionID: connectionID, Sender: sender, RecipientID: recipientID,
		PathEpoch: 9, SignalSequence: 1, SignalType: transport.SignalPathOffer,
		SentAt: time.Now().UTC(), Frame: frame,
	}
	result, err := first.AuthorizeAndPublish(ctx, request)
	if err != nil || result.Status != transport.DirectSignalPublishAccepted || result.SubscriberCount != 1 {
		t.Fatalf("AuthorizeAndPublish() result=%+v error=%v", result, err)
	}
	delivery, err := subscription.Receive(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if delivery.RecipientID != recipientID || delivery.PeerGeneration != recipient.PeerGeneration || string(delivery.Frame) != string(frame) {
		t.Fatalf("delivery=%+v, want recipient=%s peer=%s frame=%x", delivery, recipientID, recipient.PeerGeneration, frame)
	}

	request.SignalSequence = 3
	request.Nonce[0] = 1
	if replay, err := second.AuthorizeAndPublish(ctx, request); err != nil || replay.Status != transport.DirectSignalPublishReplay {
		t.Fatalf("sequence gap result=%+v error=%v", replay, err)
	}
	request.SignalSequence = 2
	request.Nonce[0] = 0
	if replay, err := second.AuthorizeAndPublish(ctx, request); err != nil || replay.Status != transport.DirectSignalPublishReplay {
		t.Fatalf("nonce replay result=%+v error=%v", replay, err)
	}
	request.SignalSequence = 2
	request.Nonce[0] = 1
	if next, err := second.AuthorizeAndPublish(ctx, request); err != nil || next.Status != transport.DirectSignalPublishAccepted || next.SubscriberCount != 1 {
		t.Fatalf("contiguous sequence result=%+v error=%v", next, err)
	}
	if err := subscription.Close(); err != nil {
		t.Fatal(err)
	}
	waitDirectMailboxSubscriberCount(t, ctx, first, recipientMailbox, 0)

	request.SignalSequence = 3
	request.Nonce[0] = 2
	if missing, err := first.AuthorizeAndPublish(ctx, request); err != nil || missing.Status != transport.DirectSignalPublishDenied || missing.SubscriberCount != 0 {
		t.Fatalf("no-subscriber result=%+v error=%v, want denied without consuming sequence", missing, err)
	}
	recovered, err := second.SubscribeMailbox(ctx, recipientMailbox)
	if err != nil {
		t.Fatal(err)
	}
	defer recovered.Close()
	if retried, err := first.AuthorizeAndPublish(ctx, request); err != nil || retried.Status != transport.DirectSignalPublishAccepted || retried.SubscriberCount != 1 {
		t.Fatalf("same signal after subscriber recovery result=%+v error=%v, want accepted", retried, err)
	}
	if _, err := recovered.Receive(ctx); err != nil {
		t.Fatal(err)
	}

	duplicate, err := second.SubscribeMailbox(ctx, recipientMailbox)
	if err != nil {
		t.Fatal(err)
	}
	waitDirectMailboxSubscriberCount(t, ctx, first, recipientMailbox, 2)
	request.SignalSequence = 4
	request.Nonce[0] = 3
	if ambiguous, err := first.AuthorizeAndPublish(ctx, request); err != nil || ambiguous.Status != transport.DirectSignalPublishDenied || ambiguous.SubscriberCount != 2 {
		t.Fatalf("multiple-subscriber result=%+v error=%v, want denied without consuming sequence", ambiguous, err)
	}
	if err := duplicate.Close(); err != nil {
		t.Fatal(err)
	}
	waitDirectMailboxSubscriberCount(t, ctx, first, recipientMailbox, 1)
	if retried, err := first.AuthorizeAndPublish(ctx, request); err != nil || retried.Status != transport.DirectSignalPublishAccepted || retried.SubscriberCount != 1 {
		t.Fatalf("same signal after duplicate subscriber retired result=%+v error=%v, want accepted", retried, err)
	}
}

func waitDirectMailboxSubscriberCount(
	t *testing.T,
	ctx context.Context,
	state *DirectTransportState,
	mailbox transport.DirectMailbox,
	want int64,
) {
	t.Helper()
	channel := state.mailboxChannel(mailbox)
	ticker := time.NewTicker(10 * time.Millisecond)
	defer ticker.Stop()
	for {
		counts, err := state.client.PubSubNumSub(ctx, channel).Result()
		if err != nil {
			t.Fatalf("query Direct mailbox subscriber count: %v", err)
		}
		if counts[channel] == want {
			return
		}
		select {
		case <-ctx.Done():
			t.Fatalf("Direct mailbox subscriber count=%d, want %d: %v", counts[channel], want, ctx.Err())
		case <-ticker.C:
		}
	}
}

func TestDirectTransportMultipairPublishRequiresBothSharedPresences(t *testing.T) {
	first, second, cleanup := directIntegrationStores(t)
	defer cleanup()
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	accountID, connectionID := uuid.New(), uuid.New()
	senderID, recipientID := uuid.New(), uuid.New()
	reservation := transport.DirectTicketReservation{
		AccountID: accountID, ConnectionID: connectionID, InitiatorID: senderID,
		ResponderID: recipientID, ReservationID: uuid.New(),
	}
	if result, err := first.ReserveTicket(ctx, reservation); err != nil || result.Status != transport.DirectTicketReserveAccepted {
		t.Fatalf("ReserveTicket() result=%+v error=%v", result, err)
	}
	now := time.Now().UTC()
	if status, err := second.FinalizeTicket(ctx, transport.DirectTicketFinalization{
		Reservation: reservation, PathEpoch: 3, IssuedAt: now,
		ExpiresAt: now.Add(120 * time.Second), ReplayUntil: now.Add(10 * time.Minute),
	}); err != nil || status != transport.DirectTicketFinalizeAccepted {
		t.Fatalf("FinalizeTicket() status=%v error=%v", status, err)
	}

	senderMailbox := transport.DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}
	recipientMailbox := transport.DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}
	if _, err := first.RegisterMailbox(ctx, senderMailbox); err != nil {
		t.Fatal(err)
	}
	subscription, err := second.SubscribeMailbox(ctx, recipientMailbox)
	if err != nil {
		t.Fatal(err)
	}
	defer subscription.Close()
	if _, err := second.RegisterMailbox(ctx, recipientMailbox); err != nil {
		t.Fatal(err)
	}
	sender := transport.DirectPresence{
		AccountID: accountID, DeviceID: senderID, Mailbox: senderMailbox, PeerGeneration: uuid.New(),
	}
	recipient := transport.DirectPresence{
		AccountID: accountID, DeviceID: recipientID, Mailbox: recipientMailbox,
		PeerGeneration: uuid.New(), SupportsMultipairV2: true,
	}
	if _, err := first.RegisterPresence(ctx, sender); err != nil {
		t.Fatal(err)
	}
	if _, err := second.RegisterPresence(ctx, recipient); err != nil {
		t.Fatal(err)
	}

	request := transport.DirectSignalPublishRequest{
		AccountID: accountID, ConnectionID: connectionID, Sender: sender,
		RecipientID: recipientID, PathEpoch: 3, SignalSequence: 1,
		SignalType: transport.SignalCheckBatchV2, SentAt: now, Frame: []byte{1, 2, 3},
	}
	request.Sender.SupportsMultipairV2 = true
	if result, err := first.AuthorizeAndPublish(ctx, request); err != nil || result.Status != transport.DirectSignalPublishStalePresence {
		t.Fatalf("forged sender capability result=%+v error=%v, want stale presence", result, err)
	}

	sender.PeerGeneration = uuid.New()
	sender.SupportsMultipairV2 = true
	if result, err := first.RegisterPresence(ctx, sender); err != nil || !result.Registered || !result.Replaced {
		t.Fatalf("upgrade sender presence result=%+v error=%v", result, err)
	}
	request.Sender = sender
	recipient.PeerGeneration = uuid.New()
	recipient.SupportsMultipairV2 = false
	if result, err := second.RegisterPresence(ctx, recipient); err != nil || !result.Registered || !result.Replaced {
		t.Fatalf("downgrade recipient presence result=%+v error=%v", result, err)
	}
	if result, err := first.AuthorizeAndPublish(ctx, request); err != nil || result.Status != transport.DirectSignalPublishDenied || result.SubscriberCount != 0 {
		t.Fatalf("unsupported recipient result=%+v error=%v, want denied", result, err)
	}

	recipient.PeerGeneration = uuid.New()
	recipient.SupportsMultipairV2 = true
	if result, err := second.RegisterPresence(ctx, recipient); err != nil || !result.Registered || !result.Replaced {
		t.Fatalf("upgrade recipient presence result=%+v error=%v", result, err)
	}
	// A rolling-deployment legacy writer can set schema 1 without deleting an
	// unknown multipair_v2 hash field. The schema fence must prevent that stale
	// value from granting v2 while preserving v1 delivery.
	if err := second.client.HSet(ctx, second.presenceKey(recipientID), "schema", "1").Err(); err != nil {
		t.Fatal(err)
	}
	if result, err := first.AuthorizeAndPublish(ctx, request); err != nil || result.Status != transport.DirectSignalPublishDenied || result.SubscriberCount != 0 {
		t.Fatalf("legacy recipient with stale capability result=%+v error=%v, want denied", result, err)
	}
	request.SignalType = transport.SignalPathOffer
	request.Frame = []byte{4, 5, 6}
	result, err := first.AuthorizeAndPublish(ctx, request)
	if err != nil || result.Status != transport.DirectSignalPublishAccepted || result.SubscriberCount != 1 {
		t.Fatalf("legacy recipient v1 result=%+v error=%v", result, err)
	}
	delivery, err := subscription.Receive(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if delivery.RecipientID != recipientID || delivery.PeerGeneration != recipient.PeerGeneration || string(delivery.Frame) != string(request.Frame) {
		t.Fatalf("v1 delivery=%+v, want recipient=%s peer=%s frame=%x", delivery, recipientID, recipient.PeerGeneration, request.Frame)
	}

	recipient.PeerGeneration = uuid.New()
	if result, err := second.RegisterPresence(ctx, recipient); err != nil || !result.Registered || !result.Replaced {
		t.Fatalf("restore recipient v2 presence result=%+v error=%v", result, err)
	}
	request.SignalSequence = 2
	request.Nonce[0] = 1
	request.SignalType = transport.SignalCheckBatchV2
	request.Frame = []byte{1, 2, 3}
	result, err = first.AuthorizeAndPublish(ctx, request)
	if err != nil || result.Status != transport.DirectSignalPublishAccepted || result.SubscriberCount != 1 {
		t.Fatalf("bilateral multipair result=%+v error=%v", result, err)
	}
	delivery, err = subscription.Receive(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if delivery.RecipientID != recipientID || delivery.PeerGeneration != recipient.PeerGeneration || string(delivery.Frame) != string(request.Frame) {
		t.Fatalf("delivery=%+v, want recipient=%s peer=%s frame=%x", delivery, recipientID, recipient.PeerGeneration, request.Frame)
	}
}

func TestDirectTransportValkeySequenceMatchesLocalPolicy(t *testing.T) {
	store, _, cleanup := directIntegrationStores(t)
	defer cleanup()
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	accountID, connectionID := uuid.New(), uuid.New()
	senderID, recipientID := uuid.New(), uuid.New()
	reservation := transport.DirectTicketReservation{
		AccountID: accountID, ConnectionID: connectionID, InitiatorID: senderID,
		ResponderID: recipientID, ReservationID: uuid.New(),
	}
	if result, err := store.ReserveTicket(ctx, reservation); err != nil || result.Status != transport.DirectTicketReserveAccepted {
		t.Fatalf("ReserveTicket() result=%+v error=%v", result, err)
	}
	now := time.Now().UTC()
	if status, err := store.FinalizeTicket(ctx, transport.DirectTicketFinalization{
		Reservation: reservation, PathEpoch: 1, IssuedAt: now,
		ExpiresAt: now.Add(120 * time.Second), ReplayUntil: now.Add(10 * time.Minute),
	}); err != nil || status != transport.DirectTicketFinalizeAccepted {
		t.Fatalf("FinalizeTicket() status=%v error=%v", status, err)
	}
	mailbox := transport.DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}
	recipientMailbox := transport.DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()}
	subscription, err := store.SubscribeMailbox(ctx, recipientMailbox)
	if err != nil {
		t.Fatal(err)
	}
	defer subscription.Close()
	if _, err := store.RegisterMailbox(ctx, mailbox); err != nil {
		t.Fatal(err)
	}
	if _, err := store.RegisterMailbox(ctx, recipientMailbox); err != nil {
		t.Fatal(err)
	}
	sender := transport.DirectPresence{
		AccountID: accountID, DeviceID: senderID, Mailbox: mailbox, PeerGeneration: uuid.New(),
	}
	recipient := transport.DirectPresence{
		AccountID: accountID, DeviceID: recipientID, Mailbox: recipientMailbox, PeerGeneration: uuid.New(),
	}
	if _, err := store.RegisterPresence(ctx, sender); err != nil {
		t.Fatal(err)
	}
	if _, err := store.RegisterPresence(ctx, recipient); err != nil {
		t.Fatal(err)
	}

	maximum := uint64(math.MaxInt64)
	sequenceField := "last_seq:" + senderID.String()
	for index, test := range []struct {
		name           string
		previous, next uint64
	}{
		{name: "first", previous: 0, next: 1},
		{name: "first gap", previous: 0, next: 2},
		{name: "contiguous", previous: 1, next: 2},
		{name: "gap", previous: 1, next: 3},
		{name: "duplicate", previous: 2, next: 2},
		{name: "regression", previous: 2, next: 1},
		{name: "maximum", previous: maximum - 1, next: maximum},
		{name: "maximum exhausted", previous: maximum, next: maximum},
		{name: "overflow", previous: maximum, next: maximum + 1},
	} {
		t.Run(test.name, func(t *testing.T) {
			routeKey := store.routeKey(accountID, connectionID)
			var err error
			if test.previous == 0 {
				err = store.client.HDel(ctx, routeKey, sequenceField).Err()
			} else {
				err = store.client.HSet(ctx, routeKey, sequenceField, strconv.FormatUint(test.previous, 10)).Err()
			}
			if err != nil {
				t.Fatal(err)
			}
			request := transport.DirectSignalPublishRequest{
				AccountID: accountID, ConnectionID: connectionID, Sender: sender, RecipientID: recipientID,
				PathEpoch: 1, SignalSequence: test.next, SignalType: transport.SignalPathClose,
				SentAt: now, Frame: []byte{1},
			}
			request.Nonce[0] = byte(index + 1)
			result, publishErr := store.AuthorizeAndPublish(ctx, request)
			if transport.IsNextSignalSequence(test.previous, test.next) {
				if publishErr != nil || result.Status != transport.DirectSignalPublishAccepted {
					t.Fatalf("Valkey result=%+v error=%v, local policy accepts", result, publishErr)
				}
				return
			}
			if test.next > maximum {
				if !errors.Is(publishErr, transport.ErrDirectStateInvalid) {
					t.Fatalf("overflow result=%+v error=%v, want invalid request", result, publishErr)
				}
				return
			}
			if publishErr != nil || result.Status != transport.DirectSignalPublishReplay {
				t.Fatalf("Valkey result=%+v error=%v, local policy rejects", result, publishErr)
			}
		})
	}

	routeKey := store.routeKey(accountID, connectionID)
	if err := store.client.HSet(ctx, routeKey, sequenceField, "01").Err(); err != nil {
		t.Fatal(err)
	}
	request := transport.DirectSignalPublishRequest{
		AccountID: accountID, ConnectionID: connectionID, Sender: sender, RecipientID: recipientID,
		PathEpoch: 1, SignalSequence: 2, SignalType: transport.SignalPathClose,
		SentAt: now, Frame: []byte{1},
	}
	request.Nonce[0] = 0xff
	if result, err := store.AuthorizeAndPublish(ctx, request); err != nil || result.Status != transport.DirectSignalPublishReplay {
		t.Fatalf("non-canonical stored sequence result=%+v error=%v", result, err)
	}
}

func TestDirectTransportSignalBudgetIsGlobalAcrossStores(t *testing.T) {
	first, second, cleanup := directIntegrationStores(t)
	defer cleanup()
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	presence := transport.DirectPresence{
		AccountID: uuid.New(), DeviceID: uuid.New(),
		Mailbox:        transport.DirectMailbox{InstanceID: uuid.New(), Generation: uuid.New()},
		PeerGeneration: uuid.New(),
	}
	if _, err := first.RegisterMailbox(ctx, presence.Mailbox); err != nil {
		t.Fatal(err)
	}
	if _, err := first.RegisterPresence(ctx, presence); err != nil {
		t.Fatal(err)
	}
	request := transport.DirectSignalBudgetRequest{Sender: presence}
	for count := 0; count < transport.DirectSignalDeviceLimit; count++ {
		store := first
		if count%2 == 1 {
			store = second
		}
		if status, err := store.ConsumeSignalBudget(ctx, request); err != nil || status != transport.DirectSignalBudgetAccepted {
			t.Fatalf("budget %d status=%v error=%v", count, status, err)
		}
	}
	if status, err := second.ConsumeSignalBudget(ctx, request); err != nil || status != transport.DirectSignalBudgetRateLimited {
		t.Fatalf("global rate status=%v error=%v", status, err)
	}
}

func TestDirectTransportTicketBudgetIsGlobalAcrossStores(t *testing.T) {
	first, second, cleanup := directIntegrationStores(t)
	defer cleanup()
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	accountID, initiatorID, responderID := uuid.New(), uuid.New(), uuid.New()
	for count := 0; count < transport.DirectTicketDeviceLimit; count++ {
		store := first
		if count%2 == 1 {
			store = second
		}
		result, err := store.ReserveTicket(ctx, transport.DirectTicketReservation{
			AccountID: accountID, ConnectionID: uuid.New(), InitiatorID: initiatorID,
			ResponderID: responderID, ReservationID: uuid.New(),
		})
		if err != nil || result.Status != transport.DirectTicketReserveAccepted {
			t.Fatalf("ticket budget %d result=%+v error=%v", count, result, err)
		}
	}
	result, err := second.ReserveTicket(ctx, transport.DirectTicketReservation{
		AccountID: accountID, ConnectionID: uuid.New(), InitiatorID: initiatorID,
		ResponderID: responderID, ReservationID: uuid.New(),
	})
	if err != nil || result.Status != transport.DirectTicketReserveRateLimited {
		t.Fatalf("global ticket rate result=%+v error=%v", result, err)
	}
}

func directIntegrationStores(t *testing.T) (*DirectTransportState, *DirectTransportState, func()) {
	t.Helper()
	redisURL := os.Getenv("TERMLINK_INTEGRATION_REDIS_URL")
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the real Direct Valkey test")
	}
	firstOptions, err := redis.ParseURL(redisURL)
	if err != nil {
		t.Fatal(err)
	}
	secondOptions := *firstOptions
	firstOptions.MaxRetries = -1
	secondOptions.MaxRetries = -1
	firstClient := redis.NewClient(firstOptions)
	secondClient := redis.NewClient(&secondOptions)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := firstClient.Ping(ctx).Err(); err != nil {
		_ = firstClient.Close()
		_ = secondClient.Close()
		t.Fatalf("ping Direct Valkey: %v", err)
	}
	namespace := fmt.Sprintf("test:direct:%s", uuid.New().String())
	first, err := NewDirectTransportState(firstClient, namespace)
	if err != nil {
		t.Fatal(err)
	}
	second, err := NewDirectTransportState(secondClient, namespace)
	if err != nil {
		t.Fatal(err)
	}
	cleanup := func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		var cursor uint64
		for {
			keys, next, scanErr := firstClient.Scan(cleanupContext, cursor, namespace+":*", 128).Result()
			if scanErr != nil {
				break
			}
			if len(keys) > 0 {
				_ = firstClient.Unlink(cleanupContext, keys...).Err()
			}
			cursor = next
			if cursor == 0 {
				break
			}
		}
		_ = firstClient.Close()
		_ = secondClient.Close()
	}
	return first, second, cleanup
}
