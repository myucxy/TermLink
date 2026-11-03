package agentupdate

import "testing"

func TestWindowsAgentBuildUsesReleaseRevision(t *testing.T) {
	for _, test := range []struct {
		version string
		build   int64
	}{
		{"0.1.31.37", 37},
		{"0.1.31", 31},
		{" 2.0.0.104 ", 104},
	} {
		actual, err := WindowsAgentBuild(test.version)
		if err != nil || actual != test.build {
			t.Fatalf("WindowsAgentBuild(%q)=(%d,%v), want (%d,nil)", test.version, actual, err, test.build)
		}
	}
}

func TestWindowsAgentBuildRejectsAmbiguousVersions(t *testing.T) {
	for _, value := range []string{"0.1", "0.1.2.3.4", "0.1.latest", "0.1.-1", ""} {
		if _, err := WindowsAgentBuild(value); err == nil {
			t.Fatalf("WindowsAgentBuild(%q) unexpectedly succeeded", value)
		}
	}
}

func TestAgentBuildRetainsDesktopAssemblyAndLegacyCompatibility(t *testing.T) {
	for _, value := range []string{"0.3.16.146", "0.3.16", " 2.0.0.104 ", "0.0.0.0"} {
		windows, windowsErr := WindowsAgentBuild(value)
		generic, genericErr := AgentBuild(value)
		if windowsErr != nil || genericErr != nil || windows != generic {
			t.Fatalf("generic Agent build differs from Windows compatibility: %q %d/%v %d/%v", value, windows, windowsErr, generic, genericErr)
		}
	}
	for _, value := range []string{"0.1", "0.1.2.3.4", "0.1.latest", "0.1.-1", ""} {
		if _, err := AgentBuild(value); err == nil {
			t.Fatalf("ambiguous Agent version %q accepted", value)
		}
	}
}
