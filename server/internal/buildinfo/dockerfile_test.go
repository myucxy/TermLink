package buildinfo

import (
	"os"
	"strings"
	"testing"
)

func TestServerDockerfileInjectsReviewedBuildMetadata(t *testing.T) {
	contents, err := os.ReadFile("../../Dockerfile")
	if err != nil {
		t.Fatal(err)
	}
	dockerfile := string(contents)
	buildStart := strings.Index(dockerfile, " AS build")
	buildEnd := strings.Index(dockerfile, "FROM gcr.io/distroless")
	if buildStart < 0 || buildEnd <= buildStart {
		t.Fatal("Dockerfile build stage was not found")
	}
	buildStage := dockerfile[buildStart:buildEnd]
	for _, expected := range []string{
		"ARG VERSION", "ARG REVISION", "ARG CREATED",
		"github.com/termlink/termlink/server/internal/buildinfo.injectedVersion=${VERSION}",
		"github.com/termlink/termlink/server/internal/buildinfo.injectedRevision=${REVISION}",
		"github.com/termlink/termlink/server/internal/buildinfo.injectedCreated=${CREATED}",
	} {
		if !strings.Contains(buildStage, expected) {
			t.Fatalf("Dockerfile build stage lacks %q", expected)
		}
	}
	if strings.Contains(buildStage, "ARG VERSION=dev") || strings.Contains(buildStage, "ARG REVISION=unknown") ||
		strings.Contains(buildStage, "ARG CREATED=unknown") {
		t.Fatal("binary build metadata has a release-looking fallback")
	}
}
