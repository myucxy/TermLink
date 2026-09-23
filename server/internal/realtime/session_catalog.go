package realtime

import (
	"context"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"github.com/termlink/termlink/server/internal/store"
)

type pcCatalogAuthorityStore interface {
	SessionCatalogDevice(context.Context, uuid.UUID, uuid.UUID, uuid.UUID) (uuid.UUID, error)
}

func isSessionCatalogMessage(kind realtimewire.MessageType) bool {
	return kind == realtimewire.SessionCatalogRequest || kind == realtimewire.SessionCatalogResponse
}

// Catalogs never use terminal subscriptions, terminal ACKs or stored lifecycle.
func (p *peer) routeSessionCatalog(envelope realtimewire.Envelope, pcID uuid.UUID, frame []byte) error {
	if !p.hub.isCurrent(p) || envelope.ACK != 0 || !p.capabilities["host_session_catalog_v1"] {
		return store.ErrDenied
	}
	recipient, err := uuid.FromBytes(envelope.RecipientDeviceID)
	if err != nil || recipient == p.principal.Device.ID {
		return store.ErrDenied
	}
	authority, ok := p.hub.store.(pcCatalogAuthorityStore)
	if !ok {
		return store.ErrDenied
	}
	androidID, pcDeviceID := p.principal.Device.ID, recipient
	if p.role == "pc" {
		androidID, pcDeviceID = recipient, p.principal.Device.ID
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	authorizedPC, err := authority.SessionCatalogDevice(ctx, p.principal.UserID, androidID, pcID)
	if err != nil {
		return err
	}
	if authorizedPC != pcDeviceID || !p.hub.isCurrent(p) {
		return store.ErrDenied
	}
	p.hub.mu.RLock()
	target := p.hub.peers[recipient]
	valid := target != nil && target.principal.UserID == p.principal.UserID &&
		target.role != p.role && target.capabilities["host_session_catalog_v1"]
	p.hub.mu.RUnlock()
	if !valid || !target.enqueue(frame) || !p.hub.isCurrent(target) {
		return store.ErrDenied
	}
	return nil
}
