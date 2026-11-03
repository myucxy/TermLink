package store

// ManagedShellTypes are wire identities, never executable paths or arguments.
// The Agent still resolves a selected, explicitly advertised command locally.
func ManagedShellTypes() [9]string {
	return [...]string{
		"command-prompt", "windows-powershell", "powershell-7", "wsl",
		"bash", "zsh", "sh", "fish", "pwsh",
	}
}

func IsManagedShellType(value string) bool {
	for _, shell := range ManagedShellTypes() {
		if shell == value {
			return true
		}
	}
	return false
}

func ShellAllowedOnPlatform(platform, shell string) bool {
	switch platform {
	case PCPlatformWindows:
		return shell == "command-prompt" || shell == "windows-powershell" || shell == "powershell-7" || shell == "wsl"
	case PCPlatformMacOS, PCPlatformLinux:
		return shell == "bash" || shell == "zsh" || shell == "fish" || shell == "pwsh" || shell == "sh"
	default:
		return false
	}
}
