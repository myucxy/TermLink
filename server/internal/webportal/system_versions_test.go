package webportal

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"html/template"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/termlink/termlink/server/internal/buildinfo"
)

func TestSystemVersionsLoadIsBoundedRedactedAndDoesNotInferCompatibility(t *testing.T) {
	now := time.Date(2026, 8, 27, 8, 30, 0, 0, time.UTC)
	revision := strings.Repeat("a", 40)
	minimum := int64(40)
	source := publicReleaseSourceFunc(func(_ context.Context, target PublicReleaseTarget) (PublicRelease, error) {
		release := systemReleaseFixture(target, now)
		release.DownloadURL = "https://appship.example/secret-download"
		release.SHA256 = strings.Repeat("b", 64)
		release.SignerFingerprint = strings.Repeat("c", 40)
		if target.Platform == "windows" {
			release.MinimumSupportedBuild = &minimum
		}
		return release, nil
	})
	service := NewSystemVersionsService(SystemVersionsOptions{
		Server:        buildinfo.Parse("0.1.35", revision, "2026-08-26T15:44:03Z"),
		ReleaseSource: source, Clock: func() time.Time { return now },
	})
	snapshot := service.Load(context.Background())
	if !snapshot.Server.Known || snapshot.Server.Version != "0.1.35" || snapshot.Server.Revision != revision ||
		len(snapshot.ProtocolVersions) != 1 || snapshot.ProtocolVersions[0] != 1 || len(snapshot.Clients) != 2 {
		t.Fatalf("unexpected system snapshot: %#v", snapshot)
	}
	android, windows := snapshot.Clients[0], snapshot.Clients[1]
	if !android.Available || android.CompatibilityKnown || android.MinimumSupportedClientBuild != 0 {
		t.Fatalf("unknown Android compatibility was inferred: %#v", android)
	}
	if !windows.Available || !windows.CompatibilityKnown || windows.MinimumSupportedClientBuild != minimum {
		t.Fatalf("declared Windows compatibility was lost: %#v", windows)
	}
	encoded, err := json.Marshal(snapshot)
	if err != nil {
		t.Fatal(err)
	}
	lower := strings.ToLower(string(encoded))
	for _, forbidden := range []string{"secret-download", strings.Repeat("b", 64), strings.Repeat("c", 40), "downloadurl", "sha256", "signerfingerprint", "releaseid", "applicationid", "idempotency"} {
		if strings.Contains(lower, strings.ToLower(forbidden)) {
			t.Fatalf("system-version model leaked %q: %s", forbidden, encoded)
		}
	}
}

func TestSystemVersionsUnknownServerAndProviderFailureStayCoarse(t *testing.T) {
	service := NewSystemVersionsService(SystemVersionsOptions{
		Server: buildinfo.Info{Version: "dev"},
		ReleaseSource: publicReleaseSourceFunc(func(context.Context, PublicReleaseTarget) (PublicRelease, error) {
			return PublicRelease{}, errors.New("https://user:secret@appship.internal release_id=private")
		}),
	})
	snapshot := service.Load(context.Background())
	if snapshot.Server.Known || snapshot.Server.Version != "" || snapshot.Server.Revision != "" {
		t.Fatalf("partial Server identity was exposed: %#v", snapshot.Server)
	}
	for _, client := range snapshot.Clients {
		if client.Available || client.CompatibilityKnown || client.ReleaseNotes != "" {
			t.Fatalf("provider failure leaked into client model: %#v", client)
		}
	}
	encoded, _ := json.Marshal(snapshot)
	for _, forbidden := range []string{"secret", "appship.internal", "release_id", "private"} {
		if strings.Contains(strings.ToLower(string(encoded)), forbidden) {
			t.Fatalf("failure model leaked %q: %s", forbidden, encoded)
		}
	}
}

func TestSystemVersionsSingleFlightAndHTMLTemplateEscaping(t *testing.T) {
	now := time.Now().UTC()
	var mutex sync.Mutex
	calls := make(map[string]int)
	source := publicReleaseSourceFunc(func(_ context.Context, target PublicReleaseTarget) (PublicRelease, error) {
		mutex.Lock()
		calls[target.Platform]++
		mutex.Unlock()
		time.Sleep(10 * time.Millisecond)
		release := systemReleaseFixture(target, now)
		release.ReleaseNotes = `<script>alert("version")</script>`
		return release, nil
	})
	service := NewSystemVersionsService(SystemVersionsOptions{ReleaseSource: source, Clock: func() time.Time { return now }})
	var wait sync.WaitGroup
	for index := 0; index < 10; index++ {
		wait.Add(1)
		go func() {
			defer wait.Done()
			if snapshot := service.Load(context.Background()); !snapshot.Clients[0].Available || !snapshot.Clients[1].Available {
				t.Errorf("concurrent load unavailable: %#v", snapshot.Clients)
			}
		}()
	}
	wait.Wait()
	mutex.Lock()
	if calls["android"] != 1 || calls["windows"] != 1 {
		t.Fatalf("system-version calls=%v, want one per target", calls)
	}
	mutex.Unlock()

	view := template.Must(template.New("versions").Parse(`{{range .Clients}}{{.ReleaseNotes}}{{end}}`))
	var rendered bytes.Buffer
	if err := view.Execute(&rendered, service.Load(context.Background())); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(rendered.String(), `<script>`) || !strings.Contains(rendered.String(), `&lt;script&gt;`) {
		t.Fatalf("release notes were not HTML-escaped: %s", rendered.String())
	}
}

func systemReleaseFixture(target PublicReleaseTarget, now time.Time) PublicRelease {
	return PublicRelease{
		Target: target, Version: "0.2.6", Build: 47, PublishedAt: now,
		MinimumOS: "supported OS", SigningStatus: "manual_approved", ReleaseNotes: "safe notes",
	}
}
