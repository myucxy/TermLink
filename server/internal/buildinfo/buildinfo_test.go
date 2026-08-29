package buildinfo

import (
	"os"
	"strings"
	"testing"
	"time"
)

func TestParseAcceptsOnlyCompleteReleaseIdentity(t *testing.T) {
	revision := strings.Repeat("aB", 20)
	info := Parse(" v1.2.3 ", revision, "2026-08-27T08:09:10+08:00")
	if !info.Known() || info.Version != "1.2.3" || info.Revision != strings.ToLower(revision) ||
		!info.CreatedAt.Equal(time.Date(2026, 8, 27, 0, 9, 10, 0, time.UTC)) {
		t.Fatalf("unexpected build info: %#v", info)
	}
}

func TestLinkerInjectedBuildInfoSmoke(t *testing.T) {
	if os.Getenv("TERMLINK_TEST_LINKER_BUILDINFO") != "1" {
		t.Skip("set TERMLINK_TEST_LINKER_BUILDINFO=1 with reviewed -ldflags values")
	}
	info := Current()
	if !info.Known() || info.Version != "9.8.7" || info.Revision != strings.Repeat("d", 40) ||
		!info.CreatedAt.Equal(time.Date(2026, 8, 27, 1, 2, 3, 0, time.UTC)) {
		t.Fatalf("linker metadata was not injected: %#v", info)
	}
}

func TestParseNeverReturnsPartialOrReleaseLookingFallback(t *testing.T) {
	revision := strings.Repeat("a", 40)
	valid := []string{"1.2.3", revision, "2026-08-27T00:00:00Z"}
	tests := [][]string{
		{"", revision, valid[2]},
		{"dev", revision, valid[2]},
		{"1.2", revision, valid[2]},
		{valid[0], "unknown", valid[2]},
		{valid[0], strings.Repeat("a", 39), valid[2]},
		{valid[0], revision, "unknown"},
		{valid[0], revision, ""},
	}
	for index, values := range tests {
		if got := Parse(values[0], values[1], values[2]); got != (Info{}) || got.Known() {
			t.Fatalf("case %d returned a partial identity: %#v", index, got)
		}
	}
}

func TestCurrentUsesLinkerFieldsAllOrNothing(t *testing.T) {
	priorVersion, priorRevision, priorCreated := injectedVersion, injectedRevision, injectedCreated
	t.Cleanup(func() {
		injectedVersion, injectedRevision, injectedCreated = priorVersion, priorRevision, priorCreated
	})
	injectedVersion = "0.1.35"
	injectedRevision = strings.Repeat("b", 40)
	injectedCreated = "2026-08-26T15:44:03Z"
	if got := Current(); !got.Known() || got.Version != "0.1.35" {
		t.Fatalf("valid linker fields were not exposed: %#v", got)
	}
	injectedRevision = ""
	if got := Current(); got != (Info{}) {
		t.Fatalf("partial linker fields were exposed: %#v", got)
	}
}
