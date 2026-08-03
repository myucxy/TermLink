package api

import (
	"net/http"
	"strings"
	"testing"
)

func TestPurchasePayloadCannotSupplyClientEntitlementClaims(t *testing.T) {
	request, err := http.NewRequest(http.MethodPost, "/v1/billing/google/verify", strings.NewReader(`{"product_id":"pro.monthly","purchase_token":"provider-token","isPro":true,"plan":"pro_monthly","quota":99999}`))
	if err != nil {
		t.Fatal(err)
	}
	var input purchaseRequest
	if err := decodeJSON(request, &input); err == nil {
		t.Fatal("forged client entitlement claims were accepted")
	}
}

func TestPurchaseValidationBounds(t *testing.T) {
	if err := validatePurchaseRequest(purchaseRequest{ProductID: "", PurchaseToken: "token"}); err == nil {
		t.Fatal("empty product id accepted")
	}
	if err := validatePurchaseRequest(purchaseRequest{ProductID: "product", PurchaseToken: strings.Repeat("x", 4097)}); err == nil {
		t.Fatal("oversized provider token accepted")
	}
}
