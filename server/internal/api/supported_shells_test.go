package api

import (
	"reflect"
	"testing"
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
