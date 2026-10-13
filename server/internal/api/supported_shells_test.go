package api

import (
	"reflect"
	"testing"

	"github.com/termlink/termlink/server/internal/store"
)

func TestNormalizeSupportedShells(t *testing.T) {
	tests := []struct {
		name    string
		input   []string
		want    []string
		wantErr bool
	}{
		{name: "old agent", input: nil, want: []string{}},
		{name: "ordered and unique", input: []string{"wsl", "windows-powershell", "powershell-7", "command-prompt", "wsl"}, want: []string{"command-prompt", "windows-powershell", "powershell-7", "wsl"}},
		{name: "unknown rejected", input: []string{"pwsh"}, wantErr: true},
		{name: "case variants rejected", input: []string{"COMMAND-PROMPT"}, wantErr: true},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			got, err := normalizeSupportedShells(test.input)
			if (err != nil) != test.wantErr {
				t.Fatalf("normalizeSupportedShells() error = %v, wantErr %v", err, test.wantErr)
			}
			if !test.wantErr && !reflect.DeepEqual(got, test.want) {
				t.Fatalf("normalizeSupportedShells() = %#v, want %#v", got, test.want)
			}
		})
	}
}

func TestNormalizeTerminalCommands(t *testing.T) {
	valid := []store.TerminalCommandCapability{
		{ID: "command-prompt", DisplayName: "Command Prompt", ShellType: "command-prompt", Source: "detected"},
		{ID: "windows-powershell", DisplayName: "Windows PowerShell", ShellType: "windows-powershell", Source: "detected"},
		{ID: "powershell-7", DisplayName: "PowerShell 7", ShellType: "powershell-7", Source: "detected"},
		{ID: "wsl", DisplayName: "WSL", ShellType: "wsl", Source: "detected"},
		{ID: "powershell-7-enhanced", DisplayName: "PowerShell 7（增强终端）", ShellType: "powershell-7", Source: "detected"},
		{ID: "custom-123", DisplayName: "Project CLI", ShellType: "custom", Source: "manual"},
	}
	got, err := normalizeTerminalCommands(valid)
	if err != nil || !reflect.DeepEqual(got, valid) {
		t.Fatalf("valid terminal commands got=%#v err=%v", got, err)
	}
	for _, invalid := range [][]store.TerminalCommandCapability{
		{{ID: "CUSTOM", DisplayName: "Bad", ShellType: "custom", Source: "manual"}},
		{{ID: "custom-1", DisplayName: " Bad", ShellType: "custom", Source: "manual"}},
		{{ID: "custom-1", DisplayName: "Bad", ShellType: "wsl", Source: "manual"}},
		{{ID: "command-prompt", DisplayName: "Bad", ShellType: "custom", Source: "detected"}},
		{{ID: "powershell-7-enhanced", DisplayName: "PowerShell 7（增强终端）", ShellType: "custom", Source: "detected"}},
		{{ID: "powershell-7-enhanced", DisplayName: "PowerShell 7（增强终端）", ShellType: "wsl", Source: "detected"}},
		{{ID: "powershell-7-enhanced", DisplayName: "PowerShell 7（增强终端）", ShellType: "powershell-7", Source: "manual"}},
		{{ID: "other-enhanced", DisplayName: "Other Enhanced", ShellType: "powershell-7", Source: "detected"}},
		{{ID: "custom-1", DisplayName: "One", ShellType: "custom", Source: "manual"}, {ID: "custom-1", DisplayName: "Two", ShellType: "custom", Source: "manual"}},
	} {
		if _, err := normalizeTerminalCommands(invalid); err == nil {
			t.Fatalf("invalid terminal command capabilities accepted: %#v", invalid)
		}
	}
}
