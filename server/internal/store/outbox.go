package store

import (
	"context"
	"encoding/json"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

// OutboxEvent is a transactionally committed authority change awaiting
// idempotent delivery to the realtime invalidation plane.
type OutboxEvent struct {
	ID          int64
	EventType   string
	AggregateID *uuid.UUID
	Payload     json.RawMessage
	Attempts    int
}

const claimOutboxEventsSQL = `
		WITH candidates AS (
			SELECT id
			FROM outbox
			WHERE processed_at IS NULL AND available_at<=now()
			  AND event_type IN (
				'device.revoked','grant.revoked','grant.updated','lease.revoked',
				'account.status.changed','history.object.delete'
			  )
			ORDER BY id
			FOR UPDATE SKIP LOCKED
			LIMIT $1
		)
		UPDATE outbox event
		SET attempts=event.attempts+1,
			available_at=now()+interval '30 seconds',
			last_error=NULL
		FROM candidates
		WHERE event.id=candidates.id
		RETURNING event.id,event.event_type,event.aggregate_id,event.payload,event.attempts`

// ClaimOutboxEvents leases a bounded batch with SKIP LOCKED. If a process
// exits before acknowledging an event, available_at makes it claimable again.
func (s *Postgres) ClaimOutboxEvents(ctx context.Context, limit int) ([]OutboxEvent, error) {
	if limit < 1 || limit > 100 {
		limit = 50
	}
	rows, err := s.Pool.Query(ctx, claimOutboxEventsSQL, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]OutboxEvent, 0)
	for rows.Next() {
		var event OutboxEvent
		if err := rows.Scan(&event.ID, &event.EventType, &event.AggregateID, &event.Payload, &event.Attempts); err != nil {
			return nil, err
		}
		result = append(result, event)
	}
	return result, rows.Err()
}

func (s *Postgres) CompleteOutboxEvent(ctx context.Context, id int64) error {
	command, err := s.Pool.Exec(ctx, `
		UPDATE outbox SET processed_at=now(),last_error=NULL
		WHERE id=$1 AND processed_at IS NULL`, id)
	if err != nil {
		return err
	}
	if command.RowsAffected() == 0 {
		var processed bool
		err := s.Pool.QueryRow(ctx, `SELECT processed_at IS NOT NULL FROM outbox WHERE id=$1`, id).Scan(&processed)
		if errors.Is(err, pgx.ErrNoRows) || err == nil && processed {
			return nil
		}
		if err != nil {
			return err
		}
		return ErrConflict
	}
	return nil
}

// RetryOutboxEvent records only a fixed, non-sensitive category. Raw broker
// errors can contain endpoints or credentials and must never enter the DB.
func (s *Postgres) RetryOutboxEvent(ctx context.Context, id int64, delay time.Duration) error {
	if delay < time.Second {
		delay = time.Second
	}
	if delay > 5*time.Minute {
		delay = 5 * time.Minute
	}
	_, err := s.Pool.Exec(ctx, `
		UPDATE outbox
		SET available_at=now()+($2::bigint*interval '1 millisecond'),last_error='delivery_failed'
		WHERE id=$1 AND processed_at IS NULL`, id, delay.Milliseconds())
	return err
}
