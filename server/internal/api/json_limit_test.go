package api

import (
	"bytes"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestDecodeSessionTitleCipherPresence(t *testing.T) {
	empty := ""
	for name, encoded := range map[string]*string{"absent": nil, "empty": &empty} {
		t.Run(name, func(t *testing.T) {
			decoded, err := decodeSessionTitleCipher(encoded)
			if err != nil || decoded != nil {
				t.Fatalf("decoded=%v err=%v, want nil title update", decoded, err)
			}
		})
	}

	want := []byte("recipient-bound-title")
	encoded := base64.RawURLEncoding.EncodeToString(want)
	decoded, err := decodeSessionTitleCipher(&encoded)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(decoded, want) {
		t.Fatalf("decoded=%q, want %q", decoded, want)
	}

	invalid := "not+base64url"
	if _, err := decodeSessionTitleCipher(&invalid); err == nil {
		t.Fatal("invalid title ciphertext was accepted")
	}

	maximum := base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{1}, maxSessionTitleCipherBytes))
	decoded, err = decodeSessionTitleCipher(&maximum)
	if err != nil || len(decoded) != maxSessionTitleCipherBytes {
		t.Fatalf("maximum title ciphertext length=%d err=%v", len(decoded), err)
	}
	oversized := base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{1}, maxSessionTitleCipherBytes+1))
	if _, err := decodeSessionTitleCipher(&oversized); err == nil {
		t.Fatal("oversized title ciphertext was accepted")
	}
}

func TestDecodeJSONAcceptsBodyAtLimit(t *testing.T) {
	const prefix = `{"value":"`
	const suffix = `"}`
	body := prefix + strings.Repeat("a", maxJSONBody-len(prefix)-len(suffix)) + suffix
	if len(body) != maxJSONBody {
		t.Fatalf("fixture length=%d, want %d", len(body), maxJSONBody)
	}
	request := httptest.NewRequest(http.MethodPost, "/", strings.NewReader(body))
	var decoded struct {
		Value string `json:"value"`
	}
	if err := decodeJSON(request, &decoded); err != nil {
		t.Fatalf("exact-limit JSON was rejected: %v", err)
	}
	if len(decoded.Value) != maxJSONBody-len(prefix)-len(suffix) {
		t.Fatalf("decoded value length=%d", len(decoded.Value))
	}
}

func TestDecodeJSONRejectsBodyAboveLimitWithTrailingWhitespace(t *testing.T) {
	// A streaming decoder over an io.LimitReader can mistake the artificial
	// limit for the real EOF after decoding the first value. Trailing whitespace
	// makes the JSON itself valid while proving that the absolute HTTP bound is
	// still enforced.
	body := append([]byte(`{"value":"ok"}`), bytes.Repeat([]byte{' '}, maxJSONBody)...)
	request := httptest.NewRequest(http.MethodPost, "/", bytes.NewReader(body))
	var decoded struct {
		Value string `json:"value"`
	}
	if err := decodeJSON(request, &decoded); err == nil {
		t.Fatal("above-limit JSON was accepted")
	}
}
