package api

import "testing"

func TestDeviceRegistrationBindsOIDCPartyToDeviceType(t *testing.T) {
	service := &API{ClientConfig: ClientConfiguration{
		OIDCClientID:               "termlink-android",
		WindowsClientID:            "termlink-windows",
		WindowsRegistrationParties: []string{"termlink-windows", "termlink-acceptance-cli"},
	}}
	tests := []struct {
		name       string
		deviceType string
		party      string
		want       bool
	}{
		{"Android from Android", "ANDROID", "termlink-android", true},
		{"Android from Windows", "ANDROID", "termlink-windows", false},
		{"Android from acceptance CLI", "ANDROID", "termlink-acceptance-cli", false},
		{"Windows from Windows", "WINDOWS_PC", "termlink-windows", true},
		{"Windows from explicit acceptance CLI", "WINDOWS_PC", "termlink-acceptance-cli", true},
		{"Windows from Android", "WINDOWS_PC", "termlink-android", false},
		{"Windows from unrelated allowed token party", "WINDOWS_PC", "unrelated-client", false},
		{"missing party", "WINDOWS_PC", "", false},
		{"unknown device type", "LINUX_PC", "termlink-windows", false},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if got := service.canRegisterDeviceType(test.deviceType, test.party); got != test.want {
				t.Fatalf("canRegisterDeviceType(%q, %q) = %t, want %t", test.deviceType, test.party, got, test.want)
			}
		})
	}
}
