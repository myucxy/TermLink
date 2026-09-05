// Package outbox delivers transactionally committed authority changes to the
// ephemeral realtime invalidation plane with at-least-once semantics.
package outbox

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/store"
)

type Dispatcher struct {
	Store  *store.Postgres
	Valkey *kv.Valkey
	Logger *slog.Logger
}

func (d Dispatcher) Run(ctx context.Context) {
	if d.Store == nil {
		return
	}
	logger := d.Logger
	if logger == nil {
		logger = slog.Default()
	}
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		d.dispatchBatch(ctx, logger)
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func (d Dispatcher) dispatchBatch(ctx context.Context, logger *slog.Logger) {
	events, err := d.Store.ClaimOutboxEvents(ctx, 50)
	if err != nil {
		if !errors.Is(err, context.Canceled) {
			logger.Warn("authority outbox claim failed")
		}
		return
	}
	for _, event := range events {
		if ctx.Err() != nil {
			return
		}
		if err := d.deliver(ctx, event); err != nil {
			delay := time.Second << min(event.Attempts, 8)
			if retryErr := d.Store.RetryOutboxEvent(ctx, event.ID, delay); retryErr != nil && !errors.Is(retryErr, context.Canceled) {
				logger.Warn("authority outbox retry scheduling failed", "event_type", event.EventType)
			}
			continue
		}
		if err := d.Store.CompleteOutboxEvent(ctx, event.ID); err != nil && !errors.Is(err, context.Canceled) {
			logger.Warn("authority outbox completion failed", "event_type", event.EventType)
		}
	}
}

func (d Dispatcher) deliver(ctx context.Context, event store.OutboxEvent) error {
	switch event.EventType {
	case "lease.revoked":
		var payload struct {
			SessionID      string `json:"session_id"`
			HolderDeviceID string `json:"holder_device_id"`
			LeaseID        string `json:"lease_id"`
			Version        int64  `json:"version"`
		}
		if err := json.Unmarshal(event.Payload, &payload); err != nil {
			return err
		}
		sessionID, err := uuid.Parse(payload.SessionID)
		if err != nil {
			return err
		}
		holderID, err := uuid.Parse(payload.HolderDeviceID)
		if err != nil {
			return err
		}
		leaseID, err := uuid.Parse(payload.LeaseID)
		if err != nil || payload.Version < 1 {
			return errors.New("invalid lease revocation payload")
		}
		_, err = d.Valkey.Release(ctx, kv.Lease{
			SessionID: sessionID, HolderDeviceID: holderID, ID: leaseID, Version: payload.Version,
		})
		return err
	case "device.revoked":
		return d.publishAggregate(ctx, "device-revoked", event.AggregateID)
	case "grant.revoked", "grant.updated":
		return d.publishAggregate(ctx, "device-authorization-changed", event.AggregateID)
	case "account.status.changed":
		return d.publishAggregate(ctx, "account-status-changed", event.AggregateID)
	default:
		return errors.New("unsupported authority outbox event")
	}
}

func (d Dispatcher) publishAggregate(ctx context.Context, channel string, id *uuid.UUID) error {
	if id == nil || *id == uuid.Nil {
		return errors.New("authority outbox aggregate is missing")
	}
	if d.Valkey == nil {
		return errors.New("authority invalidation store is unavailable")
	}
	return d.Valkey.Client.Publish(ctx, channel, id.String()).Err()
}
