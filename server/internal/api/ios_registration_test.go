package api

import (
	"github.com/termlink/termlink/server/internal/store"
	"testing"
)

func TestIOSRegistrationRequiresItsOwnOptInParty(t *testing.T) {
	service := &API{ClientConfig: ClientConfiguration{OIDCClientID: "android", IOSClientID: "ios", WindowsRegistrationParties: []string{"windows"}}}
	for _, test := range []struct {
		device, party string
		want          bool
	}{
		{"IOS", "ios", true}, {"IOS", "android", false}, {"IOS", "windows", false},
		{"ANDROID", "ios", false}, {"PC_AGENT", "ios", false}, {"IOS", "", false},
	} {
		if got := service.canRegisterDeviceType(test.device, test.party); got != test.want {
			t.Fatalf("%s/%s: got %t", test.device, test.party, got)
		}
	}
	service.ClientConfig.IOSClientID = ""
	if service.canRegisterDeviceType("IOS", "ios") {
		t.Fatal("disabled iOS client can register")
	}
}

func TestIOSV1ProjectionPreservesIdentityAndKeys(t *testing.T) {
	items := []store.DeviceRecord{{Type: "IOS", Name: "iPad", ExchangePublicKey: []byte{1, 2, 3}}, {Type: "ANDROID"}}
	projectDeviceTypesForV1(items)
	if items[0].Type != "ANDROID" || items[0].Name != "iPad" || len(items[0].ExchangePublicKey) != 3 || items[1].Type != "ANDROID" {
		t.Fatal("V1 compatibility projection changed identity fields")
	}
}
