package store

import (
	"context"
	"errors"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/pushnotify"
)

func pushFixture(t *testing.T) (portableDBFixture, *pushnotify.Codec, PushRegistration) {
	t.Helper()
	f := portablePlatformDB(t, "arm64", "IOS")
	codec, err := pushnotify.New([]byte(strings.Repeat("k", 32)))
	if err != nil {
		t.Fatal(err)
	}
	r, err := f.store.RegisterPushDevice(f.ctx, f.user, f.mobile, f.pushInput(t, codec, f.pushToken(), nil))
	if err != nil {
		t.Fatal(err)
	}
	return f, codec, r
}
func (f portableDBFixture) pushToken() string {
	return strings.ReplaceAll(f.mobile.String(), "-", "") + strings.ReplaceAll(f.user.String(), "-", "")
}

func (f portableDBFixture) pushInput(t *testing.T, codec *pushnotify.Codec, token string, generation *uuid.UUID) PushRegistrationInput {
	t.Helper()
	hash, err := codec.TokenHash(token, "dev.termlink.ios", "sandbox")
	if err != nil {
		t.Fatal(err)
	}
	cipher, err := codec.Seal(token, f.user, f.mobile, "dev.termlink.ios", "sandbox")
	if err != nil {
		t.Fatal(err)
	}
	return PushRegistrationInput{Topic: "dev.termlink.ios", Environment: "sandbox", TokenHash: hash, TokenCipher: cipher, ExpectedGeneration: generation}
}
func (f portableDBFixture) pushEvent() PushEventInput {
	return PushEventInput{EventID: uuid.New(), SessionID: uuid.New(), Kind: "ai.needs_input", Recipients: []uuid.UUID{f.mobile}, OccurredAt: time.Now().UTC()}
}
func claimPush(t *testing.T, f portableDBFixture) (uuid.UUID, uuid.UUID, *pushnotify.Delivery) {
	t.Helper()
	lease := uuid.New()
	ids, err := f.store.ClaimPushNotifications(f.ctx, lease, 10, 5*time.Minute)
	if err != nil || len(ids) != 1 {
		t.Fatalf("claim count=%d err=%v", len(ids), err)
	}
	d, err := f.store.PreparePushNotification(f.ctx, ids[0], lease)
	if err != nil || d == nil {
		t.Fatalf("prepare %+v err=%v", d, err)
	}
	return ids[0], lease, d
}

func TestPushRegistrationRotationRejectsStaleRequestsAndProtectsNewToken(t *testing.T) {
	f, codec, r := pushFixture(t)
	first := f.pushInput(t, codec, f.pushToken(), nil)
	var cipher []byte
	if err := f.store.Pool.QueryRow(f.ctx, `SELECT token_cipher FROM device_push_registrations WHERE id=$1`, r.ID).Scan(&cipher); err != nil {
		t.Fatal(err)
	}
	plain, err := codec.Open(cipher, f.user, f.mobile, "dev.termlink.ios", "sandbox")
	if err != nil || plain != f.pushToken() {
		t.Fatal("encrypted token cannot be reopened")
	}
	if strings.Contains(string(cipher), f.pushToken()) {
		t.Fatal("plaintext token stored")
	}
	again, err := f.store.RegisterPushDevice(f.ctx, f.user, f.mobile, first)
	if err != nil || again.Generation != r.Generation {
		t.Fatalf("retry changed token generation %v", err)
	}
	if _, err = f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, f.pushEvent()); err != nil {
		t.Fatal(err)
	}
	id, lease, _ := claimPush(t, f)
	rotated, err := f.store.RegisterPushDevice(f.ctx, f.user, f.mobile, f.pushInput(t, codec, strings.Repeat("cd", 48), &r.Generation))
	if err != nil {
		t.Fatal(err)
	}
	if rotated.Generation == r.Generation {
		t.Fatal("rotation kept generation")
	}
	if _, err = f.store.RegisterPushDevice(f.ctx, f.user, f.mobile, first); !errors.Is(err, ErrConflict) {
		t.Fatalf("delayed old token restored: %v", err)
	}
	if err = f.store.UnregisterPushDevice(f.ctx, f.user, f.mobile, r.Topic, r.Environment, r.Generation); !errors.Is(err, ErrConflict) {
		t.Fatalf("old unregister disabled new token: %v", err)
	}
	if err = f.store.FinishPushNotification(f.ctx, id, lease, pushnotify.Result{Status: pushnotify.InvalidToken, Code: "APNS_UNREGISTERED"}); err != nil {
		t.Fatal(err)
	}
	current, err := f.store.GetPushRegistration(f.ctx, f.user, f.mobile, r.Topic, r.Environment)
	if err != nil || current == nil || !current.Enabled || current.Generation != rotated.Generation {
		t.Fatal("stale invalidation disabled rotated token")
	}
	if err = f.store.UnregisterPushDevice(f.ctx, f.user, f.mobile, r.Topic, r.Environment, rotated.Generation); err != nil {
		t.Fatal(err)
	}
}

func TestPushNotificationDedupeLeaseRetryAndTimestampInvalidation(t *testing.T) {
	f, _, r := pushFixture(t)
	event := f.pushEvent()
	if n, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, event); err != nil || n != 1 {
		t.Fatalf("enqueue %d %v", n, err)
	}
	if n, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, event); err != nil || n != 0 {
		t.Fatalf("dedupe %d %v", n, err)
	}
	changed := event
	changed.Kind = "ai.completed"
	if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, changed); !errors.Is(err, ErrConflict) {
		t.Fatal("event id content changed")
	}
	id, lease, d := claimPush(t, f)
	if d.SessionID != event.SessionID || d.PCID != f.pc || d.Kind != event.Kind || d.Attempt != 1 {
		t.Fatalf("delivery metadata %+v", d)
	}
	if err := f.store.FinishPushNotification(f.ctx, id, uuid.New(), pushnotify.Result{Status: pushnotify.Sent, Code: "PUSH_SENT"}); err != nil {
		t.Fatal(err)
	}
	var state string
	_ = f.store.Pool.QueryRow(f.ctx, `SELECT state FROM device_push_jobs WHERE id=$1`, id).Scan(&state)
	if state != "PROCESSING" {
		t.Fatal("foreign lease completed job")
	}
	if err := f.store.FinishPushNotification(f.ctx, id, lease, pushnotify.Result{Status: pushnotify.Retry, Code: "APNS_RATE_LIMITED", RetryAfter: 30 * time.Minute}); err != nil {
		t.Fatal(err)
	}
	var wait time.Time
	if err := f.store.Pool.QueryRow(f.ctx, `SELECT next_attempt_at FROM device_push_jobs WHERE id=$1`, id).Scan(&wait); err != nil || time.Until(wait) < 29*time.Minute {
		t.Fatal("retry delay lost")
	}
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE device_push_jobs SET next_attempt_at=now() WHERE id=$1`, id); err != nil {
		t.Fatal(err)
	}
	id, lease, _ = claimPush(t, f)
	old := time.Now().Add(-time.Hour)
	if err := f.store.FinishPushNotification(f.ctx, id, lease, pushnotify.Result{Status: pushnotify.InvalidToken, Code: "APNS_UNREGISTERED", InvalidatedAt: &old}); err != nil {
		t.Fatal(err)
	}
	p, err := f.store.GetPushRegistration(f.ctx, f.user, f.mobile, r.Topic, r.Environment)
	if err != nil || !p.Enabled {
		t.Fatal("old APNs timestamp disabled new registration")
	}
}

func TestPushAuthorityRecheckedAfterQueueing(t *testing.T) {
	for _, mode := range []string{"grant", "routing", "recipient-disabled", "sender-revoked", "account", "token-unregistered"} {
		t.Run(mode, func(t *testing.T) {
			f, _, r := pushFixture(t)
			f.store.AutoTrustDevices = false
			if _, err := f.store.Pool.Exec(f.ctx, `INSERT INTO device_grants(device_id,pc_id,permission,approved_by) VALUES($1,$2,'MANAGE',$3)`, f.mobile, f.pc, f.agent); err != nil {
				t.Fatal(err)
			}
			if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, f.pushEvent()); err != nil {
				t.Fatal(err)
			}
			lease := uuid.New()
			ids, err := f.store.ClaimPushNotifications(f.ctx, lease, 10, 5*time.Minute)
			if err != nil || len(ids) != 1 {
				t.Fatal(err)
			}
			switch mode {
			case "grant":
				_, err = f.store.Pool.Exec(f.ctx, `UPDATE device_grants SET permission='VIEW' WHERE device_id=$1 AND pc_id=$2`, f.mobile, f.pc)
			case "routing":
				_, err = f.store.Pool.Exec(f.ctx, `UPDATE pcs SET remote_routing_enabled=false WHERE id=$1`, f.pc)
			case "recipient-disabled":
				_, err = f.store.Pool.Exec(f.ctx, `UPDATE devices SET disabled_at=now() WHERE id=$1`, f.mobile)
			case "sender-revoked":
				_, err = f.store.Pool.Exec(f.ctx, `UPDATE devices SET trust_state='REVOKED',revoked_at=now() WHERE id=$1`, f.agent)
			case "account":
				_, err = f.store.Pool.Exec(f.ctx, `UPDATE users SET status='LOCKED' WHERE id=$1`, f.user)
			case "token-unregistered":
				err = f.store.UnregisterPushDevice(f.ctx, f.user, f.mobile, r.Topic, r.Environment, r.Generation)
			}
			if err != nil {
				t.Fatal(err)
			}
			d, err := f.store.PreparePushNotification(f.ctx, ids[0], lease)
			if err != nil || d != nil {
				t.Fatalf("unauthorized delivery %+v %v", d, err)
			}
		})
	}
}

func TestPushEventsRejectForeignRecipientsAndLimitedPermission(t *testing.T) {
	f, _, _ := pushFixture(t)
	event := f.pushEvent()
	if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.mobile, f.pc, event); !errors.Is(err, ErrDenied) {
		t.Fatal("mobile forged PC event")
	}
	if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, uuid.New(), event); !errors.Is(err, ErrDenied) {
		t.Fatal("foreign PC event")
	}
	event.Recipients = []uuid.UUID{uuid.New()}
	if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, event); !errors.Is(err, ErrDenied) {
		t.Fatal("foreign recipient accepted")
	}
	f.store.AutoTrustDevices = false
	event = f.pushEvent()
	if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, event); !errors.Is(err, ErrDenied) {
		t.Fatal("ungranted notification")
	}
	if _, err := f.store.Pool.Exec(f.ctx, `INSERT INTO device_grants(device_id,pc_id,permission,approved_by) VALUES($1,$2,'CONTROL',$3)`, f.mobile, f.pc, f.agent); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, event); !errors.Is(err, ErrDenied) {
		t.Fatal("CONTROL notification accepted")
	}
}

func TestPushQueueConcurrentClaimExpiresAndBoundsEvents(t *testing.T) {
	f, _, _ := pushFixture(t)
	for i := 0; i < 60; i++ {
		if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, f.pushEvent()); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, f.pushEvent()); !errors.Is(err, ErrPushRateLimited) {
		t.Fatalf("event rate not bounded: %v", err)
	}
	results := make(chan []uuid.UUID, 2)
	errs := make(chan error, 2)
	var wg sync.WaitGroup
	for i := 0; i < 2; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			ids, err := f.store.ClaimPushNotifications(f.ctx, uuid.New(), 10, 5*time.Minute)
			results <- ids
			errs <- err
		}()
	}
	wg.Wait()
	close(results)
	close(errs)
	seen := map[uuid.UUID]bool{}
	for err := range errs {
		if err != nil {
			t.Fatal(err)
		}
	}
	for ids := range results {
		for _, id := range ids {
			if seen[id] {
				t.Fatal("same notification leased twice")
			}
			seen[id] = true
		}
	}
	if len(seen) != 20 {
		t.Fatalf("concurrent claims %d", len(seen))
	}
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE device_push_jobs SET expires_at=now()-interval '1 second' WHERE account_id=$1`, f.user); err != nil {
		t.Fatal(err)
	}
	if ids, err := f.store.ClaimPushNotifications(f.ctx, uuid.New(), 10, 5*time.Minute); err != nil || len(ids) != 0 {
		t.Fatal("expired events delivered")
	}
}

func TestPushUpgradeNotificationIsTransactionalAndCannotBeForgedByAgentEvent(t *testing.T) {
	f, _, _ := pushFixture(t)
	job, err := f.store.CreateAgentUpdate(f.ctx, f.user, f.mobile, f.pc, f.updateInput())
	if err != nil {
		t.Fatal(err)
	}
	event := f.pushEvent()
	event.EventID = job.ID
	if _, err = f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, event); err != nil {
		t.Fatal(err)
	}
	// Advance the real server update task, then reconcile a manually installed
	// newer Agent. The trigger creates a separate server namespace event.
	if _, err = f.store.Pool.Exec(f.ctx, `UPDATE pcs SET agent_version='0.3.18.148' WHERE id=$1`, f.pc); err != nil {
		t.Fatal(err)
	}
	if _, err = f.store.GetAgentUpdate(f.ctx, f.user, f.mobile, f.pc, job.ID); err != nil {
		t.Fatal(err)
	}
	lease := uuid.New()
	ids, err := f.store.ClaimPushNotifications(f.ctx, lease, 10, 5*time.Minute)
	if err != nil || len(ids) != 2 {
		t.Fatalf("upgrade notification missing ids=%d err=%v", len(ids), err)
	}
	found := false
	for _, id := range ids {
		d, err := f.store.PreparePushNotification(f.ctx, id, lease)
		if err != nil || d == nil {
			t.Fatal(err)
		}
		if d.Kind == "upgrade.succeeded" {
			found = true
			if d.SessionID != uuid.Nil {
				t.Fatal("upgrade leaked unrelated session")
			}
		}
	}
	if !found {
		t.Fatal("agent event hid trusted upgrade result")
	}
	if _, err = f.store.GetAgentUpdate(f.ctx, f.user, f.mobile, f.pc, job.ID); err != nil {
		t.Fatal(err)
	}
	var n int
	if err = f.store.Pool.QueryRow(f.ctx, `SELECT count(*) FROM device_push_jobs WHERE pc_id=$1 AND event_source='server'`, f.pc).Scan(&n); err != nil || n != 1 {
		t.Fatal("upgrade repeated notification")
	}
}

func TestPushUnregisteredTokenCanMoveAccountsWithoutStealingActiveEndpoint(t *testing.T) {
	f, codec, r := pushFixture(t)
	other := portablePlatformDB(t, "arm64", "IOS")
	input := other.pushInput(t, codec, f.pushToken(), nil)
	if _, err := other.store.RegisterPushDevice(other.ctx, other.user, other.mobile, input); !errors.Is(err, ErrConflict) {
		t.Fatal("stole active endpoint")
	}
	if err := f.store.UnregisterPushDevice(f.ctx, f.user, f.mobile, r.Topic, r.Environment, r.Generation); err != nil {
		t.Fatal(err)
	}
	if _, err := other.store.RegisterPushDevice(other.ctx, other.user, other.mobile, input); err != nil {
		t.Fatal(err)
	}
	if p, err := f.store.GetPushRegistration(context.Background(), f.user, f.mobile, r.Topic, r.Environment); err != nil || p != nil {
		t.Fatal("old account retained transferred endpoint")
	}
}

func TestPushTokenRotationAndInvalidationFinishUseConsistentLockOrder(t *testing.T) {
	for _, action := range []string{"rotate", "unregister"} {
		t.Run(action, func(t *testing.T) {
			f, codec, r := pushFixture(t)
			if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, f.pushEvent()); err != nil {
				t.Fatal(err)
			}
			id, lease, _ := claimPush(t, f)
			start := make(chan struct{})
			errs := make(chan error, 2)
			go func() {
				<-start
				errs <- f.store.FinishPushNotification(f.ctx, id, lease, pushnotify.Result{Status: pushnotify.InvalidToken, Code: "APNS_BAD_DEVICE_TOKEN"})
			}()
			go func() {
				<-start
				if action == "rotate" {
					_, err := f.store.RegisterPushDevice(f.ctx, f.user, f.mobile, f.pushInput(t, codec, strings.Repeat("ef", 48), &r.Generation))
					errs <- err
				} else {
					errs <- f.store.UnregisterPushDevice(f.ctx, f.user, f.mobile, r.Topic, r.Environment, r.Generation)
				}
			}()
			close(start)
			for i := 0; i < 2; i++ {
				if err := <-errs; err != nil {
					t.Fatal(err)
				}
			}
			current, err := f.store.GetPushRegistration(f.ctx, f.user, f.mobile, r.Topic, r.Environment)
			if err != nil {
				t.Fatal(err)
			}
			if action == "rotate" && (!current.Enabled || current.Generation == r.Generation) {
				t.Fatal("concurrent old invalidation disabled new token")
			}
			if action == "unregister" && current.Enabled {
				t.Fatal("unregistered endpoint stayed enabled")
			}
		})
	}
}

func TestPushTenthInFlightAttemptSurvivesConcurrentClaimCleanup(t *testing.T) {
	f, _, _ := pushFixture(t)
	if _, err := f.store.EnqueuePushEvent(f.ctx, f.user, f.agent, f.pc, f.pushEvent()); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Pool.Exec(f.ctx, `UPDATE device_push_jobs SET attempts=9 WHERE account_id=$1`, f.user); err != nil {
		t.Fatal(err)
	}
	id, lease, d := claimPush(t, f)
	if d.Attempt != 10 {
		t.Fatalf("attempt=%d", d.Attempt)
	}
	if ids, err := f.store.ClaimPushNotifications(f.ctx, uuid.New(), 10, 5*time.Minute); err != nil || len(ids) != 0 {
		t.Fatal("claimed active tenth delivery")
	}
	if err := f.store.FinishPushNotification(f.ctx, id, lease, pushnotify.Result{Status: pushnotify.Sent, Code: "APNS_SENT"}); err != nil {
		t.Fatal(err)
	}
	var state string
	if err := f.store.Pool.QueryRow(f.ctx, `SELECT state FROM device_push_jobs WHERE id=$1`, id).Scan(&state); err != nil || state != "SENT" {
		t.Fatalf("active final attempt cancelled %s %v", state, err)
	}
}

func TestPushUpgradeStatusRollsBackIfNotificationInsertFails(t *testing.T) {
	f, _, _ := pushFixture(t)
	job, err := f.store.CreateAgentUpdate(f.ctx, f.user, f.mobile, f.pc, f.updateInput())
	if err != nil {
		t.Fatal(err)
	}
	name := "push_fail_" + strings.ReplaceAll(f.user.String(), "-", "")
	if _, err = f.store.Pool.Exec(f.ctx, `CREATE FUNCTION `+name+`() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture push write failure'; END $$;
        CREATE TRIGGER `+name+` BEFORE INSERT ON device_push_jobs FOR EACH ROW WHEN (NEW.account_id='`+f.user.String()+`') EXECUTE FUNCTION `+name+`()`); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_, _ = f.store.Pool.Exec(ctx, `DROP TRIGGER IF EXISTS `+name+` ON device_push_jobs;DROP FUNCTION IF EXISTS `+name+`()`)
	})
	if _, err = f.store.Pool.Exec(f.ctx, `UPDATE pcs SET agent_version='0.3.18.148' WHERE id=$1`, f.pc); err != nil {
		t.Fatal(err)
	}
	if _, err = f.store.GetAgentUpdate(f.ctx, f.user, f.mobile, f.pc, job.ID); err == nil {
		t.Fatal("push failure ignored")
	}
	var state string
	if err = f.store.Pool.QueryRow(f.ctx, `SELECT status FROM agent_update_jobs WHERE id=$1`, job.ID).Scan(&state); err != nil || state != "PENDING" {
		t.Fatal("status committed without notification")
	}
	var events int
	if err = f.store.Pool.QueryRow(f.ctx, `SELECT count(*) FROM device_push_events WHERE pc_id=$1 AND source='server'`, f.pc).Scan(&events); err != nil || events != 0 {
		t.Fatal("notification event partly committed")
	}
}
