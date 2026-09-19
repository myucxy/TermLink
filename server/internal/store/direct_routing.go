package store

import (
	"context"
	"errors"
	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

// The direct transport data-plane routing policy is a runtime switch owned by
// the management portal. A missing row means routing is PERMITTED (the
// product default: prefer direct when it works, Relay always as the
// fallback); SUPER_ADMIN can disable it from the portal at runtime.
func (s *Postgres) GetDirectRoutingPolicy(ctx context.Context) (bool, error) {
	if s == nil || s.Pool == nil {
		return false, ErrDenied
	}
	var permitted bool
	err := s.Pool.QueryRow(ctx,
		`SELECT routing_permitted FROM direct_routing_policy WHERE singleton`).Scan(&permitted)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return true, nil
		}
		return false, err
	}
	return permitted, nil
}

func (s *Postgres) SetDirectRoutingPolicy(ctx context.Context, permitted bool, actorID uuid.UUID) error {
	if s == nil || s.Pool == nil {
		return ErrDenied
	}
	tag, err := s.Pool.Exec(ctx, `
		INSERT INTO direct_routing_policy(singleton, routing_permitted, updated_by, updated_at)
		VALUES(true, $1, NULLIF($2::uuid, '00000000-0000-0000-0000-000000000000'), now())
		ON CONFLICT(singleton) DO UPDATE
		SET routing_permitted=EXCLUDED.routing_permitted, updated_by=EXCLUDED.updated_by, updated_at=now()`,
		permitted, actorID)
	if err != nil {
		return err
	}
	if tag.RowsAffected() != 1 {
		return ErrDenied
	}
	return nil
}
