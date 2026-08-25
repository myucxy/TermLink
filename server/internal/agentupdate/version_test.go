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
