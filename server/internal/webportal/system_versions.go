package webportal

import (
	"context"
	"strings"
	"time"

	"github.com/termlink/termlink/server/internal/buildinfo"
)

// SystemVersionsOptions contains only read-only release inputs. It has no
// AppShip credentials and no persistence or mutation capability.
type SystemVersionsOptions struct {
	Server        buildinfo.Info
	ReleaseSource PublicReleaseSource
	Clock         func() time.Time
	CacheTTL      time.Duration
}

// SystemVersionsService loads the small, display-safe system version model.
// Routing and authorization remain the responsibility of the admin portal.
type SystemVersionsService struct {
	server buildinfo.Info
	source PublicReleaseSource
	cache  *publicReleaseCache
	now    func() time.Time
}

type SystemVersions struct {
	CheckedAt        time.Time
	Server           SystemServerVersion
	Clients          []SystemClientVersion
	ProtocolVersions []uint32
}

type SystemServerVersion struct {
	Known     bool
	Version   string
	Revision  string
	CreatedAt time.Time
}

// SystemClientVersion intentionally omits download URLs, artifact hashes,
// signer fingerprints, AppShip IDs, errors, and all publish-operation data.
type SystemClientVersion struct {
	Platform                    string
	DisplayName                 string
	Available                   bool
	Required                    bool
	Version                     string
	Build                       int64
	PublishedAt                 time.Time
	MinimumOS                   string
	SigningStatus               string
	ReleaseNotes                string
	CompatibilityKnown          bool
	MinimumSupportedClientBuild int64
	ForceAfter                  *time.Time
}

func NewSystemVersionsService(options SystemVersionsOptions) *SystemVersionsService {
	if options.Clock == nil {
		options.Clock = time.Now
	}
	if options.CacheTTL <= 0 || options.CacheTTL > 5*time.Minute {
		options.CacheTTL = publicReleaseCacheTTL
	}
	server := options.Server
	if server == (buildinfo.Info{}) {
		server = buildinfo.Current()
	}
	if !server.Known() {
		server = buildinfo.Info{}
	}
	return &SystemVersionsService{
		server: server, source: options.ReleaseSource,
		cache: newPublicReleaseCache(options.CacheTTL, options.Clock), now: options.Clock,
	}
}

// Load returns partial availability instead of propagating provider details.
// A failed platform is marked unavailable; errors and stale expired data are
// never placed in the model where a template could disclose them.
func (service *SystemVersionsService) Load(parent context.Context) SystemVersions {
	if service == nil || service.now == nil {
		return SystemVersions{Clients: systemClientPlaceholders(), ProtocolVersions: []uint32{1}}
	}
	result := SystemVersions{
		CheckedAt: service.now().UTC(), Clients: systemClientPlaceholders(),
		ProtocolVersions: []uint32{1},
	}
	if service.server.Known() {
		result.Server = SystemServerVersion{
			Known: true, Version: service.server.Version, Revision: service.server.Revision,
			CreatedAt: service.server.CreatedAt,
		}
	}
	if parent == nil || service.source == nil || service.cache == nil {
		return result
	}
	ctx, cancel := context.WithTimeout(parent, publicReleaseRequestTimeout)
	defer cancel()
	type loaded struct {
		index   int
		release PublicRelease
		err     error
	}
	loadedReleases := make(chan loaded, len(fixedPublicReleaseTargets))
	for index, target := range fixedPublicReleaseTargets {
		go func(index int, target PublicReleaseTarget) {
			release, err := service.cache.latest(ctx, service.source, target)
			loadedReleases <- loaded{index: index, release: release, err: err}
		}(index, target)
	}
	for remaining := len(fixedPublicReleaseTargets); remaining > 0; remaining-- {
		select {
		case item := <-loadedReleases:
			target := fixedPublicReleaseTargets[item.index]
			if item.err != nil || !validSystemClientRelease(item.release, target) {
				continue
			}
			client := &result.Clients[item.index]
			client.Available = true
			client.Required = item.release.Required
			client.Version = item.release.Version
			client.Build = item.release.Build
			client.PublishedAt = item.release.PublishedAt.UTC()
			client.MinimumOS = item.release.MinimumOS
			client.SigningStatus = item.release.SigningStatus
			client.ReleaseNotes = item.release.ReleaseNotes
			if item.release.MinimumSupportedBuild != nil {
				client.CompatibilityKnown = true
				client.MinimumSupportedClientBuild = *item.release.MinimumSupportedBuild
			}
			if item.release.ForceAfter != nil {
				forceAfter := item.release.ForceAfter.UTC()
				client.ForceAfter = &forceAfter
			}
		case <-ctx.Done():
			return result
		}
	}
	return result
}

func systemClientPlaceholders() []SystemClientVersion {
	clients := make([]SystemClientVersion, len(fixedPublicReleaseTargets))
	for index, target := range fixedPublicReleaseTargets {
		clients[index] = SystemClientVersion{Platform: target.Platform, DisplayName: target.DisplayName}
	}
	return clients
}

func validSystemClientRelease(release PublicRelease, target PublicReleaseTarget) bool {
	if release.Target != target || !safePublicVersion(release.Version) || release.Build < 1 ||
		release.Build > 1_000_000_000 || release.PublishedAt.IsZero() ||
		!safePublicText(release.MinimumOS, 128, false) ||
		!safePublicText(release.ReleaseNotes, 16*1024, true) || strings.TrimSpace(release.ReleaseNotes) == "" ||
		!validPublicSigningStatus(release.SigningStatus) {
		return false
	}
	if release.MinimumSupportedBuild != nil &&
		(*release.MinimumSupportedBuild < 1 || *release.MinimumSupportedBuild > release.Build) {
		return false
	}
	return release.ForceAfter == nil || !release.ForceAfter.IsZero()
}
