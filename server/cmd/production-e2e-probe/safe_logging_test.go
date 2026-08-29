package main

import (
	"bytes"
	"errors"
	"strings"
	"testing"
)

func TestWriteProbeFailureRedactsWrappedCredentials(t *testing.T) {
	secret := "https://history.example/object?X-Amz-Credential=secret-token"
	var output bytes.Buffer
	writeProbeFailure(&output, errors.New(secret))
	if strings.Contains(output.String(), secret) || strings.Contains(output.String(), "Credential") ||
		output.String() != "production acceptance probe failed (details redacted)\n" {
		t.Fatalf("probe failure output was not stable and redacted: %q", output.String())
	}
}
