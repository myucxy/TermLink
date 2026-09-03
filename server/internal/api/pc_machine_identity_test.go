package api

import (
	"encoding/base64"
	"testing"
)

func TestDecodeMachineIDAcceptsOnlyCanonicalSHA256Value(t *testing.T) {
	want := make([]byte, 32)
	for index := range want {
		want[index] = byte(index + 1)
	}
	encoded := base64.RawURLEncoding.EncodeToString(want)
	got, err := decodeMachineID(encoded)
	if err != nil || string(got) != string(want) {
		t.Fatalf("decodeMachineID(valid) = %x, %v", got, err)
	}
	for _, invalid := range []string{
		"", " " + encoded, encoded + "=", encoded[:42] + "!",
		base64.RawURLEncoding.EncodeToString(want[:31]),
	} {
		if _, err := decodeMachineID(invalid); err == nil {
			t.Fatalf("decodeMachineID(%q) accepted an invalid value", invalid)
		}
	}
}
