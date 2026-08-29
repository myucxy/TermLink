package accountdeletion

import (
	"bytes"
	"errors"
	"testing"

	"github.com/google/uuid"
)

func TestIdentityHandleRoundTripAndRandomizedReplay(t *testing.T) {
	t.Parallel()
	codec := testIdentityHandleCodec(t)
	accountID := uuid.MustParse("10000000-0000-4000-8000-000000000001")
	requestID := uuid.MustParse("20000000-0000-4000-8000-000000000002")
	subject := "30000000-0000-4000-8000-000000000003"
	first, err := codec.Encrypt(accountID, requestID, 7, subject)
	if err != nil || len(first) != IdentityHandleCiphertextSize {
		t.Fatalf("Encrypt length=%d error=%v", len(first), err)
	}
	second, err := codec.Encrypt(accountID, requestID, 7, subject)
	if err != nil || bytes.Equal(first, second) {
		t.Fatalf("randomized encryption replay equal=%t error=%v", bytes.Equal(first, second), err)
	}
	for index, encrypted := range [][]byte{first, first, second} {
		got, err := codec.Decrypt(accountID, requestID, 7, encrypted)
		if err != nil || got != subject {
			t.Fatalf("Decrypt replay %d=(%q,%v), want %q", index, got, err, subject)
		}
	}
}

func TestIdentityHandleRejectsTamperAndWrongAAD(t *testing.T) {
	t.Parallel()
	codec := testIdentityHandleCodec(t)
	accountID, requestID := uuid.New(), uuid.New()
	encrypted, err := codec.Encrypt(accountID, requestID, 7, uuid.NewString())
	if err != nil {
		t.Fatal(err)
	}
	tampered := append([]byte(nil), encrypted...)
	tampered[len(tampered)-1] ^= 0x80
	for name, decrypt := range map[string]func() error{
		"tamper":          func() error { _, err := codec.Decrypt(accountID, requestID, 7, tampered); return err },
		"wrong account":   func() error { _, err := codec.Decrypt(uuid.New(), requestID, 7, encrypted); return err },
		"wrong request":   func() error { _, err := codec.Decrypt(accountID, uuid.New(), 7, encrypted); return err },
		"wrong version":   func() error { _, err := codec.Decrypt(accountID, requestID, 8, encrypted); return err },
		"unknown version": func() error { _, err := codec.Decrypt(accountID, requestID, 9, encrypted); return err },
		"truncated": func() error {
			_, err := codec.Decrypt(accountID, requestID, 7, encrypted[:len(encrypted)-1])
			return err
		},
	} {
		if err := decrypt(); !errors.Is(err, ErrIdentityHandleInvalid) {
			t.Fatalf("%s error=%v, want ErrIdentityHandleInvalid", name, err)
		}
	}
}

func TestIdentityHandleAcceptsOnlyCanonicalKeycloakUUID(t *testing.T) {
	t.Parallel()
	codec := testIdentityHandleCodec(t)
	accountID, requestID := uuid.New(), uuid.New()
	for _, invalid := range []string{
		"", uuid.Nil.String(), "30000000-0000-4000-8000-00000000000A",
		"{30000000-0000-4000-8000-000000000003}", "30000000000040008000000000000003",
		"keycloak-user-name",
	} {
		if _, err := codec.Encrypt(accountID, requestID, 7, invalid); !errors.Is(err, ErrIdentityHandleInvalid) {
			t.Fatalf("non-canonical subject %q error=%v", invalid, err)
		}
	}
}

func TestIdentityHandleCodecConfigurationIsStrictAndCopiesKeys(t *testing.T) {
	t.Parallel()
	for name, keys := range map[string]map[int16][]byte{
		"none":         nil,
		"zero version": {0: make([]byte, IdentityHandleKeySize)},
		"short key":    {1: make([]byte, IdentityHandleKeySize-1)},
	} {
		if _, err := NewIdentityHandleCodec(keys); !errors.Is(err, ErrInvalidConfiguration) {
			t.Fatalf("%s configuration error=%v", name, err)
		}
	}
	key := bytes.Repeat([]byte{0x7a}, IdentityHandleKeySize)
	codec, err := NewIdentityHandleCodec(map[int16][]byte{7: key})
	if err != nil {
		t.Fatal(err)
	}
	clear(key)
	accountID, requestID, subject := uuid.New(), uuid.New(), uuid.NewString()
	encrypted, err := codec.Encrypt(accountID, requestID, 7, subject)
	if err != nil {
		t.Fatal(err)
	}
	if got, err := codec.Decrypt(accountID, requestID, 7, encrypted); err != nil || got != subject {
		t.Fatalf("codec retained caller key alias: got=%q error=%v", got, err)
	}
}

func testIdentityHandleCodec(t *testing.T) *IdentityHandleCodec {
	t.Helper()
	codec, err := NewIdentityHandleCodec(map[int16][]byte{
		7: bytes.Repeat([]byte{0x71}, IdentityHandleKeySize),
		8: bytes.Repeat([]byte{0x82}, IdentityHandleKeySize),
	})
	if err != nil {
		t.Fatal(err)
	}
	return codec
}
