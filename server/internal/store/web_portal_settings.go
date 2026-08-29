package store

import (
	"context"
	"errors"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

type WebPortalSettings struct {
	AdminRouteCiphertext []byte
	AdminRouteHMAC       []byte
	Version              int64
}

func (s *Postgres) GetWebPortalSettings(ctx context.Context) (WebPortalSettings, error) {
	var result WebPortalSettings
	err := s.Pool.QueryRow(ctx, `SELECT admin_route_ciphertext,admin_route_hmac,version FROM web_portal_settings WHERE singleton=true`).
		Scan(&result.AdminRouteCiphertext, &result.AdminRouteHMAC, &result.Version)
	return result, err
}

func (s *Postgres) SaveWebPortalAdminRoute(ctx context.Context, actorID uuid.UUID, ciphertext, routeHMAC []byte, expectedVersion int64) (WebPortalSettings, error) {
	if actorID == uuid.Nil || len(ciphertext) < 1 || len(routeHMAC) != 32 || expectedVersion < 1 {
		return WebPortalSettings{}, ErrDenied
	}
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return WebPortalSettings{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var role Role
	var status string
	if err = tx.QueryRow(ctx, `SELECT role,status FROM users WHERE id=$1 FOR UPDATE`, actorID).Scan(&role, &status); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return WebPortalSettings{}, ErrDenied
		}
		return WebPortalSettings{}, err
	}
	if role != RoleSuperAdmin || status != "ACTIVE" {
		return WebPortalSettings{}, ErrDenied
	}
	var result WebPortalSettings
	err = tx.QueryRow(ctx, `
		UPDATE web_portal_settings SET admin_route_ciphertext=$1,admin_route_hmac=$2,
		 version=version+1,updated_by=$3,updated_at=now()
		WHERE singleton=true AND version=$4
		RETURNING admin_route_ciphertext,admin_route_hmac,version`,
		ciphertext, routeHMAC, actorID, expectedVersion).
		Scan(&result.AdminRouteCiphertext, &result.AdminRouteHMAC, &result.Version)
	if errors.Is(err, pgx.ErrNoRows) {
		return WebPortalSettings{}, ErrConflict
	}
	if err != nil {
		return WebPortalSettings{}, err
	}
	if _, err = tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,'web.admin_route.update','system_settings',NULL,'SUCCEEDED','admin-route',
		 jsonb_build_object('version',$2::bigint,'custom_route_enabled',true))`, actorID, result.Version); err != nil {
		return WebPortalSettings{}, err
	}
	if err = tx.Commit(ctx); err != nil {
		return WebPortalSettings{}, err
	}
	return result, nil
}

// ResetWebPortalAdminRouteOffline is deliberately not exposed through HTTP.
// It exists for the local server command that recovers a lost custom route.
func (s *Postgres) ResetWebPortalAdminRouteOffline(ctx context.Context) error {
	tx, err := s.Pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.Serializable})
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	var version int64
	if err = tx.QueryRow(ctx, `UPDATE web_portal_settings SET admin_route_ciphertext=NULL,admin_route_hmac=NULL,
		version=version+1,updated_by=NULL,updated_at=now() WHERE singleton=true RETURNING version`).Scan(&version); err != nil {
		return err
	}
	if _, err = tx.Exec(ctx, `
		INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES(NULL,'system.admin_route.offline_reset','system_settings',NULL,'SUCCEEDED','local-recovery',
		 jsonb_build_object('version',$1::bigint,'custom_route_enabled',false))`, version); err != nil {
		return err
	}
	return tx.Commit(ctx)
}
