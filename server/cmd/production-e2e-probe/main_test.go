package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestWaitForPresignedStatusAllowsAsynchronousObjectDeletion(t *testing.T) {
	var attempts atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, _ *http.Request) {
		if attempts.Add(1) < 3 {
			writer.WriteHeader(http.StatusOK)
			return
		}
		writer.WriteHeader(http.StatusNotFound)
	}))
	defer server.Close()

	p := &probe{http: server.Client()}
	err := p.waitForPresignedStatus(presignedRequest{
		URL: server.URL, Method: http.MethodGet, Headers: map[string][]string{},
	}, http.StatusNotFound, 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if attempts.Load() != 3 {
		t.Fatalf("attempts = %d, want 3", attempts.Load())
	}
}

func TestValidateDiscoveredListItemAllowsAdditionalResources(t *testing.T) {
	tests := []struct {
		name       string
		ownerField string
	}{
		{name: "PC", ownerField: "device_id"},
		{name: "session", ownerField: "pc_id"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			itemID := uuid.New()
			ownerID := uuid.New()
			response := map[string]any{"items": []any{
				map[string]any{"id": uuid.NewString(), test.ownerField: uuid.NewString()},
				map[string]any{"id": itemID.String(), test.ownerField: ownerID.String()},
			}}
			if err := validateDiscoveredListItem(response, itemID, test.ownerField, ownerID); err != nil {
				t.Fatalf("target item among additional resources was rejected: %v", err)
			}
		})
	}
}

func TestValidateDiscoveredListItemRejectsMissingTarget(t *testing.T) {
	itemID := uuid.New()
	ownerID := uuid.New()
	response := map[string]any{"items": []any{
		map[string]any{"id": uuid.NewString(), "device_id": ownerID.String()},
	}}
	if err := validateDiscoveredListItem(response, itemID, "device_id", ownerID); err == nil {
		t.Fatal("missing target item was accepted")
	}
}

func TestValidateDiscoveredListItemRejectsWrongOwner(t *testing.T) {
	itemID := uuid.New()
	response := map[string]any{"items": []any{
		map[string]any{"id": itemID.String(), "pc_id": uuid.NewString()},
	}}
	if err := validateDiscoveredListItem(response, itemID, "pc_id", uuid.New()); err == nil {
		t.Fatal("target item with the wrong owner was accepted")
	}
}

func TestHistoryRecipientKeyIDUsesCanonicalRecipientAndEpoch(t *testing.T) {
	recipientID := uuid.MustParse("12345678-90ab-cdef-1234-567890abcdef")
	if got, want := historyRecipientKeyID(recipientID, 7), "history-v1:1234567890abcdef1234567890abcdef:00000007"; got != want {
		t.Fatalf("historyRecipientKeyID() = %q, want %q", got, want)
	}
}

func TestPositiveInt64FieldRequiresExactPositiveJSONInteger(t *testing.T) {
	tests := []struct {
		name  string
		value any
		set   bool
		want  int64
		ok    bool
	}{
		{name: "positive", value: json.Number("9223372036854775807"), set: true, want: 9223372036854775807, ok: true},
		{name: "zero", value: json.Number("0"), set: true},
		{name: "negative", value: json.Number("-1"), set: true},
		{name: "fraction", value: json.Number("1.5"), set: true},
		{name: "overflow", value: json.Number("9223372036854775808"), set: true},
		{name: "float", value: float64(7), set: true},
		{name: "string", value: "7", set: true},
		{name: "boolean", value: true, set: true},
		{name: "null", value: nil, set: true},
		{name: "missing"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			response := map[string]any{}
			if test.set {
				response["key_epoch"] = test.value
			}
			got, ok := positiveInt64Field(response, "key_epoch")
			if got != test.want || ok != test.ok {
				t.Fatalf("positiveInt64Field() = (%d, %t), want (%d, %t)", got, ok, test.want, test.ok)
			}
		})
	}
}

func TestPositiveInt64FieldAcceptsIntegerDecodedWithUseNumber(t *testing.T) {
	var response map[string]any
	decoder := json.NewDecoder(bytes.NewBufferString(`{"key_epoch":12}`))
	decoder.UseNumber()
	if err := decoder.Decode(&response); err != nil {
		t.Fatal(err)
	}
	if got, ok := positiveInt64Field(response, "key_epoch"); !ok || got != 12 {
		t.Fatalf("positiveInt64Field() = (%d, %t), want (12, true)", got, ok)
	}
}

func TestProbeRunIdempotencyKeyIsFresh(t *testing.T) {
	first := newProbeRunIdempotencyKey("pc-presence")
	second := newProbeRunIdempotencyKey("pc-presence")
	if first == second {
		t.Fatal("probe presence updates reused one idempotency key")
	}
	const prefix = "acceptance-pc-presence-"
	for _, value := range []string{first, second} {
		if len(value) <= len(prefix) || value[:len(prefix)] != prefix {
			t.Fatalf("probe idempotency key %q does not have prefix %q", value, prefix)
		}
		if _, err := uuid.Parse(value[len(prefix):]); err != nil {
			t.Fatalf("probe idempotency key %q has invalid UUID suffix: %v", value, err)
		}
	}
}

func TestLoadOrCreatePCIdentityIsStable(t *testing.T) {
	path := filepath.Join(t.TempDir(), "probe", "pc.json")
	first, firstPCID, err := loadOrCreatePCIdentity(path, "https://issuer-a.example")
	if err != nil {
		t.Fatal(err)
	}
	firstDeviceID := first.id
	firstPublic := append([]byte(nil), first.public...)
	firstPrivate := append([]byte(nil), first.private...)
	firstExchange := append([]byte(nil), first.exchange...)
	first.destroy()

	second, secondPCID, err := loadOrCreatePCIdentity(path, "https://issuer-a.example")
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

func TestLoadOrCreatePCIdentityRotatesAcrossIssuerScopes(t *testing.T) {
	path := filepath.Join(t.TempDir(), "probe", "pc.json")
	first, firstPCID, err := loadOrCreatePCIdentity(path, "https://issuer-a.example")
	if err != nil {
		t.Fatal(err)
	}
	firstDeviceID := first.id
	first.destroy()

	second, secondPCID, err := loadOrCreatePCIdentity(path, "https://issuer-b.example")
	if err != nil {
		t.Fatal(err)
	}
	defer second.destroy()
	if second.id == firstDeviceID || secondPCID == firstPCID {
		t.Fatal("cross-issuer acceptance identity was reused")
	}
	_, _, scope, err := loadPCIdentity(path)
	if err != nil {
		t.Fatal(err)
	}
	if scope != "https://issuer-b.example" {
		t.Fatalf("stored scope = %q", scope)
	}
}

func TestLoadPCIdentityRejectsCorruptState(t *testing.T) {
	path := filepath.Join(t.TempDir(), "pc.json")
	if err := os.WriteFile(path, []byte(`{"device_id":"invalid"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, _, _, err := loadPCIdentity(path); err == nil {
		t.Fatal("loadPCIdentity accepted corrupt state")
	}
}
