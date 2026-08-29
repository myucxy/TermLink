// Package buildinfo exposes immutable metadata embedded in the Server binary
// by the release build. Missing or malformed linker values deliberately yield
// an unknown build instead of a release-looking fallback such as "dev".
package buildinfo

import (
	"strings"
	"time"
)

// These variables are set only by the reviewed Docker release build. Keep the
// names stable: server/Dockerfile addresses them with go build -ldflags -X.
var (
	injectedVersion  string
	injectedRevision string
	injectedCreated  string
)

// Info is the validated identity of the running Server binary. The zero value
// means unknown and is safe for local/test builds.
type Info struct {
	Version   string
	Revision  string
	CreatedAt time.Time
}

// Current returns an all-or-nothing release identity. Partial linker metadata
// is discarded so a local build can never be presented as an official build.
func Current() Info {
	return Parse(injectedVersion, injectedRevision, injectedCreated)
}

// Parse validates release metadata and is also the injection boundary used by
// callers and tests. It accepts the release tag's optional leading "v" but
// exposes the canonical three-component version without it.
func Parse(version, revision, created string) Info {
	version = strings.TrimSpace(version)
	if strings.HasPrefix(version, "v") {
		version = strings.TrimPrefix(version, "v")
	}
	revision = strings.ToLower(strings.TrimSpace(revision))
	createdAt, err := time.Parse(time.RFC3339, strings.TrimSpace(created))
	if !validVersion(version) || !validRevision(revision) || err != nil || createdAt.IsZero() {
		return Info{}
	}
	return Info{Version: version, Revision: revision, CreatedAt: createdAt.UTC()}
}

// Known reports whether the complete identity remains valid. Callers should
// gate all rendering on this method rather than testing Version alone.
func (info Info) Known() bool {
	if info.Version == "" || info.Revision == "" || info.CreatedAt.IsZero() {
		return false
	}
	return Parse(info.Version, info.Revision, info.CreatedAt.UTC().Format(time.RFC3339)) == info
}

func validVersion(value string) bool {
	parts := strings.Split(value, ".")
	if len(value) < 5 || len(value) > 64 || len(parts) != 3 {
		return false
	}
	for _, part := range parts {
		if part == "" || len(part) > 10 {
			return false
		}
		for _, character := range part {
			if character < '0' || character > '9' {
				return false
			}
		}
	}
	return true
}

func validRevision(value string) bool {
	if len(value) != 40 && len(value) != 64 {
		return false
	}
	for _, character := range value {
		if (character < '0' || character > '9') && (character < 'a' || character > 'f') {
			return false
		}
	}
	return true
}
