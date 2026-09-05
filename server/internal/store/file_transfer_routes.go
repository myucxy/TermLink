package store

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

type FileTransferRouteAuthorization struct {
	TransferID       uuid.UUID
	RequesterDevice  uuid.UUID
	PCDevice         uuid.UUID
	Status           string
	TransferProtocol *string
	ExpiresAt        time.Time
}

// FileTransferRoute authorizes only the two devices bound to one active
// DOWNLOAD operation. It returns metadata, never file content or file keys.
func (s *Postgres) FileTransferRoute(
	ctx context.Context,
	accountID, transferID uuid.UUID,
) (FileTransferRouteAuthorization, error) {
	var result FileTransferRouteAuthorization
	err := s.Pool.QueryRow(ctx, `
		SELECT operation.id,operation.requester_device_id,pc.device_id,
		       operation.status,operation.transfer_protocol,operation.expires_at
		FROM pc_file_operations operation
		JOIN pcs pc ON pc.id=operation.pc_id AND pc.owner_user_id=operation.account_id
		JOIN devices requester ON requester.id=operation.requester_device_id
		  AND requester.user_id=operation.account_id
		  AND requester.device_type='ANDROID' AND requester.trust_state='TRUSTED'
		JOIN devices agent ON agent.id=pc.device_id
		  AND agent.user_id=operation.account_id
		  AND agent.device_type IN ('WINDOWS_PC','PC_AGENT') AND agent.trust_state='TRUSTED'
		WHERE operation.id=$1 AND operation.account_id=$2
		  AND operation.operation='DOWNLOAD'`, transferID, accountID).Scan(
		&result.TransferID,
		&result.RequesterDevice,
		&result.PCDevice,
		&result.Status,
		&result.TransferProtocol,
		&result.ExpiresAt,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return FileTransferRouteAuthorization{}, ErrDenied
	}
	if err != nil {
		return FileTransferRouteAuthorization{}, err
	}
	return result, nil
}
