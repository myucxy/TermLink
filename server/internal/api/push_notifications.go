package api

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/pushnotify"
	"github.com/termlink/termlink/server/internal/store"
)

type pushNotificationStore interface {
	GetPushRegistration(context.Context, uuid.UUID, uuid.UUID, string, string) (*store.PushRegistration, error)
	RegisterPushDevice(context.Context, uuid.UUID, uuid.UUID, store.PushRegistrationInput) (store.PushRegistration, error)
	UnregisterPushDevice(context.Context, uuid.UUID, uuid.UUID, string, string, uuid.UUID) error
	EnqueuePushEvent(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, store.PushEventInput) (int, error)
}

type PushNotificationConfiguration struct {
	Enabled            bool
	Topic, Environment string
	Codec              *pushnotify.Codec
}

func (a *API) pushRepository() pushNotificationStore {
	if a.pushNotifications != nil {
		return a.pushNotifications
	}
	if a.Store != nil {
		return a.Store
	}
	return nil
}

func (a *API) devicePush(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodPut && r.Method != http.MethodDelete {
		w.Header().Set("Allow", "GET, PUT, DELETE")
		writeError(w, r, http.StatusMethodNotAllowed, "METHOD_NOT_ALLOWED", nil)
		return
	}
	p := principal(r)
	if p.Device.Type != "IOS" {
		writeError(w, r, http.StatusForbidden, "IOS_DEVICE_REQUIRED", store.ErrDenied)
		return
	}
	c := a.PushNotifications
	if r.Method != http.MethodDelete && (!c.Enabled || c.Codec == nil) {
		writeError(w, r, http.StatusServiceUnavailable, "PUSH_NOTIFICATIONS_DISABLED", nil)
		return
	}
	repository := a.pushRepository()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "PUSH_NOTIFICATIONS_UNAVAILABLE", nil)
		return
	}
	switch r.Method {
	case http.MethodGet:
		registration, err := repository.GetPushRegistration(r.Context(), p.UserID, p.Device.ID, c.Topic, c.Environment)
		if err != nil {
			writeStoreError(w, r, err)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"registration": registration})
	case http.MethodPut:
		var input struct {
			SoundEnabled       bool       `json:"sound_enabled"`
			Token              string     `json:"token"`
			ExpectedGeneration *uuid.UUID `json:"expected_generation"`
		}
		if err := decodeJSON(r, &input); err != nil || len(input.Token) > 1024 {
			writeError(w, r, 400, "INVALID_PUSH_REGISTRATION", nil)
			return
		}
		token := strings.ToLower(input.Token)
		hash, err := c.Codec.TokenHash(token, c.Topic, c.Environment)
		if err != nil {
			writeError(w, r, 400, "INVALID_PUSH_REGISTRATION", nil)
			return
		}
		cipher, err := c.Codec.Seal(token, p.UserID, p.Device.ID, c.Topic, c.Environment)
		if err != nil {
			writeError(w, r, 400, "INVALID_PUSH_REGISTRATION", nil)
			return
		}
		registration, err := repository.RegisterPushDevice(r.Context(), p.UserID, p.Device.ID, store.PushRegistrationInput{Topic: c.Topic, Environment: c.Environment, TokenHash: hash, TokenCipher: cipher, ExpectedGeneration: input.ExpectedGeneration, SoundEnabled: input.SoundEnabled})
		if err != nil {
			writeStoreError(w, r, err)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"registration": registration})
	case http.MethodDelete:
		var input struct {
			ExpectedGeneration uuid.UUID `json:"expected_generation"`
		}
		if err := decodeJSON(r, &input); err != nil || input.ExpectedGeneration == uuid.Nil {
			writeError(w, r, 400, "INVALID_PUSH_REGISTRATION", nil)
			return
		}
		if err := repository.UnregisterPushDevice(r.Context(), p.UserID, p.Device.ID, c.Topic, c.Environment, input.ExpectedGeneration); err != nil {
			writeStoreError(w, r, err)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	}
}

func (a *API) notificationEvent(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if !store.IsPCAgentDeviceType(p.Device.Type) {
		writeError(w, r, 403, "PC_AGENT_REQUIRED", store.ErrDenied)
		return
	}
	if !a.PushNotifications.Enabled {
		writeError(w, r, 503, "PUSH_NOTIFICATIONS_DISABLED", nil)
		return
	}
	pc, err := uuid.Parse(r.PathValue("pcId"))
	if err != nil || pc == uuid.Nil {
		writeError(w, r, 400, "INVALID_PC_ID", nil)
		return
	}
	var input struct {
		EventID    uuid.UUID   `json:"event_id"`
		Kind       string      `json:"kind"`
		SessionID  uuid.UUID   `json:"session_id"`
		Recipients []uuid.UUID `json:"recipient_device_ids"`
		OccurredAt time.Time   `json:"occurred_at"`
	}
	if err := decodeJSON(r, &input); err != nil || input.EventID == uuid.Nil || input.SessionID == uuid.Nil ||
		(input.Kind != "ai.completed" && input.Kind != "ai.needs_input") || len(input.Recipients) < 1 || len(input.Recipients) > 16 ||
		input.OccurredAt.Before(a.now().Add(-5*time.Minute)) || input.OccurredAt.After(a.now().Add(30*time.Second)) {
		writeError(w, r, 400, "INVALID_NOTIFICATION_EVENT", nil)
		return
	}
	repository := a.pushRepository()
	if repository == nil {
		writeError(w, r, 503, "PUSH_NOTIFICATIONS_UNAVAILABLE", nil)
		return
	}
	count, err := repository.EnqueuePushEvent(r.Context(), p.UserID, p.Device.ID, pc, store.PushEventInput{
		EventID: input.EventID, Kind: input.Kind, SessionID: input.SessionID, Recipients: input.Recipients, OccurredAt: input.OccurredAt})
	if errors.Is(err, store.ErrPushRateLimited) {
		w.Header().Set("Retry-After", "60")
		writeError(w, r, 429, "NOTIFICATION_RATE_LIMITED", nil)
		return
	}
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusAccepted, map[string]any{"accepted": true, "queued": count})
}
