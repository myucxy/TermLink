package main

import (
	"bytes"
	"os"
	"path/filepath"
	"testing"
)

func TestLoadOrCreatePCIdentityIsStable(t *testing.T) {
	path := filepath.Join(t.TempDir(), "probe", "pc.json")
	first, firstPCID, err := loadOrCreatePCIdentity(path)
	if err != nil {
		t.Fatal(err)
	}
	firstDeviceID := first.id
	firstPublic := append([]byte(nil), first.public...)
	firstPrivate := append([]byte(nil), first.private...)
	firstExchange := append([]byte(nil), first.exchange...)
	first.destroy()

	second, secondPCID, err := loadOrCreatePCIdentity(path)
	if err != nil {
		t.Fatal(err)
	}
	defer second.destroy()
	if second.id != firstDeviceID || secondPCID != firstPCID {
		t.Fatalf("stored identity changed: device %s/%s PC %s/%s", second.id, firstDeviceID, secondPCID, firstPCID)
	}
	if !bytes.Equal(second.public, firstPublic) ||
		!bytes.Equal(second.private, firstPrivate) ||
		!bytes.Equal(second.exchange, firstExchange) {
		t.Fatal("stored identity key material changed")
	}
}

func TestLoadPCIdentityRejectsCorruptState(t *testing.T) {
	path := filepath.Join(t.TempDir(), "pc.json")
	if err := os.WriteFile(path, []byte(`{"device_id":"invalid"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, _, err := loadPCIdentity(path); err == nil {
		t.Fatal("loadPCIdentity accepted corrupt state")
	}
}
