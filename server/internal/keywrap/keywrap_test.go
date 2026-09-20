package keywrap

import (
	"bytes"
	"errors"
	"strings"
	"testing"
)

func TestWrapUnwrapRoundTrip(t *testing.T) {
	master := bytes.Repeat([]byte{0xAB}, 32)
	wrapped, err := Wrap("op-passphrase", master)
	if err != nil {
		t.Fatalf("wrap: %v", err)
	}
	if !IsWrapped(wrapped) {
		t.Fatal("wrapped value missing prefix")
	}
	restored, err := Unwrap("op-passphrase", wrapped)
	if err != nil {
		t.Fatalf("unwrap: %v", err)
	}
	if !bytes.Equal(restored, master) {
		t.Fatalf("round trip mismatch: %v", restored)
	}
}

func TestUnwrapRejectsWrongPassphraseAndTamper(t *testing.T) {
	master := bytes.Repeat([]byte{0xCD}, 32)
	wrapped, err := Wrap("correct-passphrase", master)
	if err != nil {
		t.Fatalf("wrap: %v", err)
	}
	if _, err := Unwrap("wrong-passphrase", wrapped); !errors.Is(err, ErrAuthFailed) {
		t.Fatalf("wrong passphrase err=%v", err)
	}
	tampered := wrapped[:len(wrapped)-4] + "AAAA"
	if _, err := Unwrap("correct-passphrase", tampered); !errors.Is(err, ErrAuthFailed) {
		t.Fatalf("tampered err=%v", err)
	}
}

func TestWrapRejectsWeakInputs(t *testing.T) {
	if _, err := Wrap("", bytes.Repeat([]byte{1}, 32)); err == nil {
		t.Fatal("empty passphrase accepted")
	}
	if _, err := Wrap("passphrase", []byte("short")); err == nil {
		t.Fatal("short master accepted")
	}
	if IsWrapped("plain-text-value") {
		t.Fatal("plain value misdetected as wrapped")
	}
}

func TestWrapUsesRandomSaltAndNonce(t *testing.T) {
	master := bytes.Repeat([]byte{0x42}, 32)
	first, err := Wrap("pass", master)
	if err != nil {
		t.Fatalf("wrap first: %v", err)
	}
	second, err := Wrap("pass", master)
	if err != nil {
		t.Fatalf("wrap second: %v", err)
	}
	if first == second {
		t.Fatal("two wraps produced identical storage values")
	}
	if strings.Count(first, Prefix) != 1 {
		t.Fatalf("unexpected prefix usage in %q", first)
	}
}
