package pushnotify

import (
	"bytes"
	"errors"
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestCodecBindsTokenToAccountDeviceTopicEnvironment(t *testing.T) {
	codec, err := New(bytes.Repeat([]byte{0x41}, 32))
	if err != nil {
		t.Fatal(err)
	}
	account, device := uuid.New(), uuid.New()
	token := strings.Repeat("aB", 32)
	sealed, err := codec.Seal(token, account, device, "dev.termlink.ios", EnvironmentSandbox)
	if err != nil {
		t.Fatal(err)
	}
	opened, err := codec.Open(sealed, account, device, "dev.termlink.ios", EnvironmentSandbox)
	if err != nil || opened != strings.ToLower(token) || bytes.Contains(sealed, []byte(strings.ToLower(token))) {
		t.Fatalf("cipher round trip failed: plaintext matched=%v error=%v", opened == strings.ToLower(token), err)
	}
	second, err := codec.Seal(token, account, device, "dev.termlink.ios", EnvironmentSandbox)
	if err != nil || bytes.Equal(sealed, second) {
		t.Fatal("AES-GCM nonce was reused")
	}
	for _, change := range []struct {
		account, device uuid.UUID
		topic, env      string
	}{
		{uuid.New(), device, "dev.termlink.ios", EnvironmentSandbox},
		{account, uuid.New(), "dev.termlink.ios", EnvironmentSandbox},
		{account, device, "dev.other.ios", EnvironmentSandbox},
		{account, device, "dev.termlink.ios", EnvironmentProduction},
	} {
		if _, err := codec.Open(sealed, change.account, change.device, change.topic, change.env); !errors.Is(err, ErrCipher) {
			t.Fatalf("mismatched AAD accepted: %v", err)
		}
	}
	for _, mutate := range []func([]byte){func(value []byte) { value[0]++ }, func(value []byte) { value[3] ^= 1 }, func(value []byte) { value[len(value)-1] ^= 1 }} {
		bad := bytes.Clone(sealed)
		mutate(bad)
		if _, err := codec.Open(bad, account, device, "dev.termlink.ios", EnvironmentSandbox); !errors.Is(err, ErrCipher) {
			t.Fatal("tampered cipher accepted")
		}
	}
	other, _ := New(bytes.Repeat([]byte{0x42}, 32))
	if _, err := other.Open(sealed, account, device, "dev.termlink.ios", EnvironmentSandbox); !errors.Is(err, ErrCipher) {
		t.Fatal("wrong key accepted")
	}
}

func TestCodecTokenValidationAndOpaqueGlobalHash(t *testing.T) {
	codec, _ := New(make([]byte, 32))
	account, device := uuid.New(), uuid.New()
	for _, token := range []string{strings.Repeat("ab", 16), strings.Repeat("cd", 32), strings.Repeat("EF", 512)} {
		sealed, err := codec.Seal(token, account, device, "dev.termlink.ios", EnvironmentProduction)
		if err != nil {
			t.Fatal("valid variable-length token rejected", err)
		}
		got, err := codec.Open(sealed, account, device, "dev.termlink.ios", EnvironmentProduction)
		if err != nil || got != strings.ToLower(token) {
			t.Fatal("valid token changed")
		}
	}
	for _, token := range []string{"", strings.Repeat("a", 31), strings.Repeat("a", 33), strings.Repeat("a", 1026), strings.Repeat("z", 64), " " + strings.Repeat("a", 63), "<" + strings.Repeat("a", 62) + ">"} {
		if _, err := codec.Seal(token, account, device, "dev.termlink.ios", EnvironmentProduction); !errors.Is(err, ErrToken) {
			t.Fatal("invalid token accepted")
		}
		if _, err := codec.TokenHash(token, "dev.termlink.ios", EnvironmentProduction); !errors.Is(err, ErrToken) {
			t.Fatal("invalid token hashed")
		}
	}
	token := strings.Repeat("ab", 32)
	hash, err := codec.TokenHash(token, "dev.termlink.ios", EnvironmentProduction)
	if err != nil || len(hash) != 32 || bytes.Equal(hash, []byte(token)) {
		t.Fatal("invalid opaque hash")
	}
	upper, _ := codec.TokenHash(strings.ToUpper(token), "dev.termlink.ios", EnvironmentProduction)
	if !bytes.Equal(hash, upper) {
		t.Fatal("case-normalized token lost global uniqueness")
	}
	for _, binding := range [][2]string{{"dev.other", EnvironmentProduction}, {"dev.termlink.ios", EnvironmentSandbox}} {
		other, _ := codec.TokenHash(token, binding[0], binding[1])
		if bytes.Equal(hash, other) {
			t.Fatal("hash ignored environment/topic binding")
		}
	}
	otherKey, _ := New(bytes.Repeat([]byte{1}, 32))
	otherHash, _ := otherKey.TokenHash(token, "dev.termlink.ios", EnvironmentProduction)
	if bytes.Equal(hash, otherHash) {
		t.Fatal("hash was not keyed")
	}
	if _, err := New(make([]byte, 31)); !errors.Is(err, ErrConfiguration) {
		t.Fatal("short key accepted")
	}
	if _, err := New(make([]byte, 33)); !errors.Is(err, ErrConfiguration) {
		t.Fatal("long key accepted")
	}
}
