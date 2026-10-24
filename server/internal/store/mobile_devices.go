package store

// Mobile identity remains distinct from its legacy 'android' Relay wire role.
func IsMobileDeviceType(deviceType string) bool {
	return deviceType == "ANDROID" || deviceType == "IOS"
}
