package store

import (
	"context"
	"errors"
	"slices"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const allocateDirectTransportEpochSQL = `
	WITH authorized AS (
		SELECT 1
		FROM devices source
		JOIN users account ON account.id=$1 AND account.status='ACTIVE'
		JOIN devices target ON target.id=$3 AND target.user_id=$1
		JOIN pcs pc ON pc.owner_user_id=$1
		LEFT JOIN device_grants android_grant
		  ON android_grant.pc_id=pc.id
		 AND android_grant.device_id=CASE
		       WHEN source.device_type='ANDROID' THEN source.id
		       ELSE target.id
		     END
		 AND (android_grant.expires_at IS NULL OR android_grant.expires_at>now())
		 AND android_grant.permission IN ('VIEW','CONTROL','MANAGE')
		WHERE source.id=$2 AND source.user_id=$1
		  AND source.trust_state='TRUSTED' AND source.revoked_at IS NULL
		  AND target.trust_state='TRUSTED' AND target.revoked_at IS NULL
		  AND pc.remote_routing_enabled
		  AND (
		    (source.device_type='ANDROID' AND target.device_type IN ('WINDOWS_PC','PC_AGENT') AND pc.device_id=target.id)
		    OR
		    (target.device_type='ANDROID' AND source.device_type IN ('WINDOWS_PC','PC_AGENT') AND pc.device_id=source.id)
		  )
		  AND ($6 OR android_grant.device_id IS NOT NULL)
	), allocated AS (
		INSERT INTO direct_transport_pair_epochs(
			account_id,first_device_id,second_device_id,last_epoch)
		SELECT $1,$4,$5,1 FROM authorized
		ON CONFLICT (account_id,first_device_id,second_device_id) DO UPDATE
		SET last_epoch=direct_transport_pair_epochs.last_epoch+1,
		    updated_at=now()
		WHERE direct_transport_pair_epochs.last_epoch < 9223372036854775807
		RETURNING last_epoch
	)
	SELECT last_epoch FROM allocated`

const authorizeDirectTransportPairSQL = `
	SELECT EXISTS(
		SELECT 1
		FROM devices source
		JOIN users account ON account.id=$1 AND account.status='ACTIVE'
		JOIN devices target ON target.id=$3 AND target.user_id=$1
		JOIN pcs pc ON pc.owner_user_id=$1
		LEFT JOIN device_grants android_grant
		  ON android_grant.pc_id=pc.id
		 AND android_grant.device_id=CASE
		       WHEN source.device_type='ANDROID' THEN source.id
		       ELSE target.id
		     END
		 AND (android_grant.expires_at IS NULL OR android_grant.expires_at>now())
		 AND android_grant.permission IN ('VIEW','CONTROL','MANAGE')
		WHERE source.id=$2 AND source.user_id=$1
		  AND source.trust_state='TRUSTED' AND source.revoked_at IS NULL
		  AND target.trust_state='TRUSTED' AND target.revoked_at IS NULL
		  AND pc.remote_routing_enabled
		  AND (
		    (source.device_type='ANDROID' AND target.device_type IN ('WINDOWS_PC','PC_AGENT') AND pc.device_id=target.id)
		    OR
		    (target.device_type='ANDROID' AND source.device_type IN ('WINDOWS_PC','PC_AGENT') AND pc.device_id=source.id)
		  )
		  AND ($4 OR android_grant.device_id IS NOT NULL)
	)`

// AllocateDirectTransportEpoch authorizes one exact Android/PC pair and
// atomically advances its path epoch. Persisting the counter prevents a server
// restart or rolling deployment from reusing an epoch while a previous direct
// path is still retiring on either device.
func (s *Postgres) AllocateDirectTransportEpoch(
	ctx context.Context,
	accountID, sourceDeviceID, targetDeviceID uuid.UUID,
) (uint64, error) {
	first, second := canonicalDirectTransportPair(sourceDeviceID, targetDeviceID)
	var epoch int64
	err := s.Pool.QueryRow(ctx, allocateDirectTransportEpochSQL,
		accountID, sourceDeviceID, targetDeviceID, first, second, s.AutoTrustDevices,
	).Scan(&epoch)
	if errors.Is(err, pgx.ErrNoRows) {
		return 0, ErrDenied
	}
	if err != nil {
		return 0, err
	}
	if epoch <= 0 {
		return 0, ErrDenied
	}
	return uint64(epoch), nil
}

// AuthorizeDirectTransportPair rechecks current device trust, PC ownership,
// remote-routing policy and grant expiry for every signalling frame. Revoking
// either device or disabling remote routing therefore fences an in-flight
// ticket without waiting for its short TTL.
func (s *Postgres) AuthorizeDirectTransportPair(
	ctx context.Context,
	accountID, sourceDeviceID, targetDeviceID uuid.UUID,
) error {
	var allowed bool
	err := s.Pool.QueryRow(ctx, authorizeDirectTransportPairSQL,
		accountID, sourceDeviceID, targetDeviceID, s.AutoTrustDevices).Scan(&allowed)
	if err != nil {
		return err
	}
	if !allowed {
		return ErrDenied
	}
	return nil
}

func canonicalDirectTransportPair(first, second uuid.UUID) (uuid.UUID, uuid.UUID) {
	if slices.Compare(first[:], second[:]) <= 0 {
		return first, second
	}
	return second, first
}
