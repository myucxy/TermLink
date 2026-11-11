package config

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// An .env entry alone is not forwarded to a container. Keep opt-in iOS fields
// mapped at each supported Compose entry point so deployment cannot silently
// leave the client disabled after an operator has selected it.
func TestIOSComposeEntryPointsForwardOptionalIdentity(t *testing.T) {
	for _, name := range []string{"../../compose.yml", "../../compose/core.compose.yml", "../../compose/single-node.compose.yml"} {
		raw, err := os.ReadFile(name)
		if err != nil {
			t.Fatal(err)
		}
		for _, field := range []string{
			"TERMLINK_IOS_OIDC_CLIENT_ID: ${TERMLINK_IOS_OIDC_CLIENT_ID:-}",
			"TERMLINK_IOS_OIDC_REDIRECT_URI: ${TERMLINK_IOS_OIDC_REDIRECT_URI:-dev.termlink.ios:/oauth2redirect}",
		} {
			if !strings.Contains(string(raw), field) {
				t.Errorf("%s does not forward %s", name, field)
			}
		}
	}
	example, err := os.ReadFile("../../.env.example")
	if err != nil {
		t.Fatal(err)
	}
	for _, value := range []string{"TERMLINK_IOS_OIDC_CLIENT_ID=\n", "TERMLINK_IOS_OIDC_REDIRECT_URI=dev.termlink.ios:/oauth2redirect", "termlink-android,termlink-windows,termlink-ios"} {
		if !strings.Contains(string(example), value) {
			t.Errorf(".env.example omitted iOS opt-in instruction %q", value)
		}
	}
}

func TestIOSSingleFileInstallerRendersOptionalIdentityWithoutDeployment(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("renderer requires Unix shell utilities; source contracts are checked on every platform")
	}
	bash, err := exec.LookPath("bash")
	if err != nil {
		t.Skip("bash is unavailable")
	}
	script, err := filepath.Abs("../../deploy/single-node-production/termlink-deploy")
	if err != nil {
		t.Fatal(err)
	}
	if output, err := exec.Command(bash, "-n", script).CombinedOutput(); err != nil {
		t.Fatalf("installer syntax: %v: %s", err, output)
	}
	root := t.TempDir()
	cmd := exec.Command(bash, script, "render-assets", "--output", filepath.Join(root, "runtime"))
	if runtime.GOOS == "darwin" {
		// The installer targets Linux/GNU coreutils. This narrow adapter handles
		// its one realpath call for the already-absolute disposable output path;
		// no installed tools or deployment assets are changed on the Mac runner.
		bin := filepath.Join(root, "bin")
		if err := os.Mkdir(bin, 0700); err != nil {
			t.Fatal(err)
		}
		wrapper := "#!/bin/sh\n[ \"$1\" = -m ] && [ \"$2\" = -- ] && [ \"$#\" = 3 ] || exit 1\nprintf '%s\\n' \"$3\"\n"
		if err := os.WriteFile(filepath.Join(bin, "realpath"), []byte(wrapper), 0700); err != nil {
			t.Fatal(err)
		}
		cmd.Env = append(os.Environ(), "PATH="+bin+string(os.PathListSeparator)+os.Getenv("PATH"))
	}
	if output, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("render-assets: %v: %s", err, output)
	}
	raw, err := os.ReadFile(filepath.Join(root, "runtime", "compose.yml"))
	if err != nil {
		t.Fatal(err)
	}
	for _, field := range []string{
		"TERMLINK_IOS_OIDC_CLIENT_ID: ${TERMLINK_IOS_OIDC_CLIENT_ID:-}",
		"TERMLINK_IOS_OIDC_REDIRECT_URI: ${TERMLINK_IOS_OIDC_REDIRECT_URI:-dev.termlink.ios:/oauth2redirect}",
		"TERMLINK_OIDC_ALLOWED_PARTIES: ${TERMLINK_OIDC_ALLOWED_PARTIES:-}",
	} {
		if !strings.Contains(string(raw), field) {
			t.Errorf("rendered Compose omitted optional identity setting %q", field)
		}
	}
}

func TestIOSConfigurationRejectsOldRuntimeAssetsBeforeSaving(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("requires Unix bash")
	}
	bash, err := exec.LookPath("bash")
	if err != nil {
		t.Skip("bash is unavailable")
	}
	raw, err := os.ReadFile("../../deploy/single-node-production/termlink-deploy")
	if err != nil {
		t.Fatal(err)
	}
	text := strings.TrimSpace(string(raw))
	if !strings.HasSuffix(text, `main "$@"`) {
		t.Fatal("installer entry point changed")
	}
	// Load actual functions without invoking deployment/root/Docker operations.
	text = strings.TrimSuffix(text, `main "$@"`) + "\nRUNTIME_DIR=\"$1\"\nIOS_CLIENT_ID=\"$2\"\nrequire_ios_runtime_assets\n"
	root := t.TempDir()
	script := filepath.Join(root, "installer-functions")
	if err = os.WriteFile(script, []byte(text), 0700); err != nil {
		t.Fatal(err)
	}
	for _, fixture := range []struct {
		name, id, compose string
		want              bool
	}{
		{"disabled-existing-deployment", "", "services: {}", true},
		{"enabled-old-assets", "termlink-ios", "services: {}", false},
		{"enabled-current-assets", "termlink-ios", "TERMLINK_IOS_OIDC_CLIENT_ID:\nTERMLINK_IOS_OIDC_REDIRECT_URI:\nTERMLINK_OIDC_ALLOWED_PARTIES:\n", true},
	} {
		t.Run(fixture.name, func(t *testing.T) {
			if err = os.WriteFile(filepath.Join(root, "compose.yml"), []byte(fixture.compose), 0600); err != nil {
				t.Fatal(err)
			}
			output, runErr := exec.Command(bash, script, root, fixture.id).CombinedOutput()
			if (runErr == nil) != fixture.want {
				t.Fatalf("runtime mapping result %v: %s", runErr, output)
			}
			if !fixture.want && !strings.Contains(string(output), "first run upgrade") {
				t.Fatalf("missing upgrade instruction: %s", output)
			}
		})
	}
}
