package presence

import (
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestRegistryKeepsMultipleConnectionsAndExpiresThem(t *testing.T) {
	now := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	registry := New(10 * time.Second)
	registry.clock = func() time.Time { return now }
	userID, deviceID := uuid.New(), uuid.New()
	first, second := uuid.New(), uuid.New()
	registry.Register(userID, deviceID, first, "android")
	registry.Register(userID, deviceID, second, "android")
	if _, ok := registry.Online(userID, deviceID); !ok {
		t.Fatal("device should be online with two connections")
	}
	registry.Remove(first)
	if _, ok := registry.Online(userID, deviceID); !ok {
		t.Fatal("removing one connection removed the other")
	}
	now = now.Add(11 * time.Second)
	if _, ok := registry.Online(userID, deviceID); ok {
		t.Fatal("expired connection remained online")
	}
}

func TestRegistryTouchRefreshesOnlyKnownConnection(t *testing.T) {
	registry := New(time.Second)
	if registry.Touch(uuid.New()) {
		t.Fatal("unknown connection was refreshed")
	}
	userID, deviceID, connectionID := uuid.New(), uuid.New(), uuid.New()
	registry.Register(userID, deviceID, connectionID, "pc")
	if !registry.Touch(connectionID) {
		t.Fatal("known connection was not refreshed")
	}
}

func TestRegistryCountsDistinctDevicesAcrossConnections(t *testing.T) {
	r := New(time.Minute)
	userID, pcID, androidID := uuid.New(), uuid.New(), uuid.New()
	r.Register(userID, pcID, uuid.New(), "pc")
	r.Register(userID, pcID, uuid.New(), "WINDOWS_PC")
	r.Register(userID, androidID, uuid.New(), "android")
	if got := r.OnlineDeviceCount(userID, "pc"); got != 1 {
		t.Fatalf("pc count=%d, want 1", got)
	}
	if got := r.OnlineDeviceCount(userID); got != 2 {
		t.Fatalf("all device count=%d, want 2", got)
	}
}
