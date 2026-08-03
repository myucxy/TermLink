package billing

import (
	"context"
	"errors"
	"testing"
)

func TestRegistryRejectsUnavailableAndInvalidResults(t *testing.T) {
	registry := Registry{}
	if _, err := registry.Verify(context.Background(), ProviderGooglePlay, "pro.monthly", "token"); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("unconfigured provider error = %v", err)
	}
	fake := &FakeProvider{Values: map[string]VerifiedPurchase{
		"token": {Provider: ProviderGooglePlay, ProductID: "pro.monthly", Reference: "purchase-1", Status: "ACTIVE"},
	}}
	registry.GooglePlay = fake
	verified, err := registry.Verify(context.Background(), ProviderGooglePlay, "pro.monthly", "token")
	if err != nil || verified.Reference != "purchase-1" || fake.Calls != 1 {
		t.Fatalf("verified = %#v, calls=%d, err=%v", verified, fake.Calls, err)
	}
	if _, err := registry.Verify(context.Background(), ProviderGooglePlay, "pro.yearly", "token"); !errors.Is(err, ErrInvalid) {
		t.Fatalf("wrong product error = %v", err)
	}
}
