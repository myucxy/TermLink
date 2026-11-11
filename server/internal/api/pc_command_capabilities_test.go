package api

import (
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"

	"github.com/termlink/termlink/server/internal/store"
)

func TestPCCommandCapabilitiesNegotiationRequiresExactHeaderToken(t *testing.T) {
	tests := []struct {
		name      string
		header    string
		want      bool
		method    string
		path      string
		duplicate bool
	}{
		{name: "missing", want: false},
		{name: "empty", header: "", want: false},
		{name: "exact", header: "powershell-7-enhanced", want: true},
		{name: "unknown", header: "future-command", want: false},
		{name: "combined", header: "powershell-7-enhanced,future-command", want: false},
		{name: "duplicate", header: "powershell-7-enhanced", want: false, duplicate: true},
		{name: "wrong method", header: "powershell-7-enhanced", want: false, method: http.MethodPost},
		{name: "wrong path", header: "powershell-7-enhanced", want: false, path: "/v1/pcs/11111111-1111-1111-1111-111111111111"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			method := test.method
			if method == "" {
				method = http.MethodGet
			}
			path := test.path
			if path == "" {
				path = "/v1/pcs"
			}
			request := httptest.NewRequest(method, path, nil)
			if test.header != "" {
				request.Header.Set(pcCommandCapabilitiesHeader, test.header)
				if test.duplicate {
					request.Header.Add(pcCommandCapabilitiesHeader, test.header)
				}
			}
			if got := pcCommandCapabilitiesRequested(request); got != test.want {
				t.Fatalf("pcCommandCapabilitiesRequested(%q) = %t, want %t", test.header, got, test.want)
			}
		})
	}
}

func TestProjectPCCommandCapabilitiesFiltersOnlyEnhancedWithoutMutatingSource(t *testing.T) {
	original := []store.TerminalCommandCapability{
		{ID: "powershell-7", DisplayName: "PowerShell 7", ShellType: "powershell-7", Source: "detected"},
		{ID: "powershell-7-enhanced", DisplayName: "PowerShell 7（增强终端）", ShellType: "powershell-7", Source: "detected"},
		{ID: "custom-agent", DisplayName: "Custom Agent", ShellType: "custom", Source: "manual"},
		{ID: "powershell-7-enhanced", DisplayName: "Manual Enhanced", ShellType: "custom", Source: "manual"},
	}
	wantOriginal := append([]store.TerminalCommandCapability(nil), original...)
	items := []store.PCRecord{{TerminalCommands: original}}
	projectPCCommandCapabilities(items, false)
	wantLegacy := []store.TerminalCommandCapability{original[0], original[2], original[3]}
	if !reflect.DeepEqual(items[0].TerminalCommands, wantLegacy) {
		t.Fatalf("legacy projection = %#v, want %#v", items[0].TerminalCommands, wantLegacy)
	}
	if !reflect.DeepEqual(original, wantOriginal) {
		t.Fatalf("source commands changed = %#v, want %#v", original, wantOriginal)
	}
	if len(items[0].TerminalCommands) > 0 && &items[0].TerminalCommands[0] == &original[0] {
		t.Fatal("legacy projection reused source command slice")
	}

	fullItems := []store.PCRecord{{TerminalCommands: original}}
	projectPCCommandCapabilities(fullItems, true)
	if !reflect.DeepEqual(fullItems[0].TerminalCommands, original) {
		t.Fatalf("negotiated projection = %#v, want %#v", fullItems[0].TerminalCommands, original)
	}
}

func TestPCShellCapabilitiesNegotiationRequiresExactHeaderToken(t *testing.T) {
	tests := []struct {
		name      string
		header    string
		want      bool
		method    string
		path      string
		duplicate bool
	}{
		{name: "missing", want: false},
		{name: "exact", header: "posix-shells-v1", want: true},
		{name: "unknown", header: "future-shells", want: false},
		{name: "combined", header: "posix-shells-v1,future-shells", want: false},
		{name: "duplicate", header: "posix-shells-v1", want: false, duplicate: true},
		{name: "wrong method", header: "posix-shells-v1", want: false, method: http.MethodPost},
		{name: "wrong path", header: "posix-shells-v1", want: false, path: "/v1/pcs/11111111-1111-1111-1111-111111111111"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			method := test.method
			if method == "" {
				method = http.MethodGet
			}
			path := test.path
			if path == "" {
				path = "/v1/pcs"
			}
			request := httptest.NewRequest(method, path, nil)
			if test.header != "" {
				request.Header.Set(pcShellCapabilitiesHeader, test.header)
				if test.duplicate {
					request.Header.Add(pcShellCapabilitiesHeader, test.header)
				}
			}
			if got := pcShellCapabilitiesRequested(request); got != test.want {
				t.Fatalf("pcShellCapabilitiesRequested(%q) = %t, want %t", test.header, got, test.want)
			}
		})
	}
}

func TestProjectPCShellCapabilitiesFiltersPOSIXCommandsAndShellsWithoutMutatingSource(t *testing.T) {
	originalShells := []string{"bash", "powershell-7", "zsh", "fish", "sh", "wsl"}
	originalCommands := []store.TerminalCommandCapability{
		{ID: "bash", DisplayName: "Bash", ShellType: "bash", Source: "detected"},
		{ID: "zsh-custom", DisplayName: "Zsh custom", ShellType: "zsh", Source: "manual"},
		{ID: "powershell-7", DisplayName: "PowerShell 7", ShellType: "powershell-7", Source: "detected"},
		{ID: "custom-agent", DisplayName: "Custom Agent", ShellType: "custom", Source: "manual"},
	}
	item := store.PCRecord{SupportedShells: originalShells, TerminalCommands: originalCommands}
	items := []store.PCRecord{item}
	projectPCShellCapabilities(items, false)
	if !reflect.DeepEqual(items[0].SupportedShells, []string{"powershell-7", "wsl"}) {
		t.Fatalf("legacy shells = %#v", items[0].SupportedShells)
	}
	wantCommands := []store.TerminalCommandCapability{originalCommands[2], originalCommands[3]}
	if !reflect.DeepEqual(items[0].TerminalCommands, wantCommands) {
		t.Fatalf("legacy commands = %#v", items[0].TerminalCommands)
	}
	if !reflect.DeepEqual(item.SupportedShells, originalShells) || !reflect.DeepEqual(item.TerminalCommands, originalCommands) {
		t.Fatal("projection mutated source slices")
	}

	fullItems := []store.PCRecord{item}
	projectPCShellCapabilities(fullItems, true)
	if !reflect.DeepEqual(fullItems[0], item) {
		t.Fatalf("negotiated projection changed capabilities: %#v", fullItems[0])
	}
}

func TestPCCommandAndShellNegotiationAreIndependent(t *testing.T) {
	commandRequest := httptest.NewRequest(http.MethodGet, "/v1/pcs", nil)
	commandRequest.Header.Set(pcCommandCapabilitiesHeader, enhancedPowerShellCommandID)
	if !pcCommandCapabilitiesRequested(commandRequest) || pcShellCapabilitiesRequested(commandRequest) {
		t.Fatal("command negotiation unexpectedly enabled POSIX shell projection")
	}
	shellRequest := httptest.NewRequest(http.MethodGet, "/v1/pcs", nil)
	shellRequest.Header.Set(pcShellCapabilitiesHeader, posixShellCapabilitiesToken)
	if pcCommandCapabilitiesRequested(shellRequest) || !pcShellCapabilitiesRequested(shellRequest) {
		t.Fatal("shell negotiation unexpectedly enabled enhanced PowerShell projection")
	}
}
