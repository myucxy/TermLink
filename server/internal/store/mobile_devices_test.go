package store

import "testing"

func TestMobileDeviceTypesAreAnExplicitClosedSet(t *testing.T) {
	for _, deviceType := range []string{"ANDROID", "IOS"} {
		if !IsMobileDeviceType(deviceType) {
			t.Fatalf("mobile identity rejected: %s", deviceType)
		}
	}
	for _, deviceType := range []string{"", "ios", "WINDOWS_PC", "PC_AGENT", "WEB", "IOS_ADMIN"} {
		if IsMobileDeviceType(deviceType) {
			t.Fatalf("non-mobile identity accepted: %s", deviceType)
		}
	}
}
