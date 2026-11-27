package main

import (
	"context"
	"os"

	"github.com/termlink/termlink/server/internal/agentupdate"
	"github.com/termlink/termlink/server/internal/webportal"
)

func newAppShipSources() (agentupdate.Catalog, webportal.PublicReleaseSource, error) {
	// AppShip remains the legacy reader for already-installed clients. New
	// remote Agent updates prefer the fixed GitHub Release catalog and fall back
	// only when it has no suitable newer release.
	privateIPs := os.Getenv("TERMLINK_APPSHIP_ALLOWED_PRIVATE_IPS")
	legacyCatalog, err := agentupdate.NewAppShipCatalogWithPlatformAnchors(
		agentupdate.DefaultAppShipBaseURL, nil, privateIPs, os.Getenv("TERMLINK_APPSHIP_SIGNER_FINGERPRINT"),
		os.Getenv("TERMLINK_APPSHIP_MACOS_TEAM_ID"),
	)
	if err != nil {
		return nil, nil, err
	}
	githubCatalog, err := agentupdate.NewGitHubReleaseCatalog(nil)
	if err != nil {
		return nil, nil, err
	}
	publicSource, err := webportal.NewAppShipPublicReleaseSourceWithAllowedPrivateIPs(
		agentupdate.DefaultAppShipBaseURL, nil, privateIPs,
	)
	if err != nil {
		return nil, nil, err
	}
	return &agentupdate.FallbackCatalog{Primary: githubCatalog, Legacy: legacyCatalog}, githubPublicReleases{githubCatalog, publicSource}, nil
}

// Use the same verified release identity for the portal and the Agent catalog.
type githubPublicReleases struct {
	github agentupdate.PlatformCatalog
	legacy webportal.PublicReleaseSource
}

func (s githubPublicReleases) Latest(ctx context.Context, target webportal.PublicReleaseTarget) (webportal.PublicRelease, error) {
	if !((target.Application == "termlink-win64" && target.Platform == "windows" && target.Architecture == "x64" && target.FileKind == "exe") ||
		(target.Application == "termlink-android" && target.Platform == "android" && target.Architecture == "universal" && target.FileKind == "apk")) {
		return webportal.PublicRelease{}, webportal.ErrPublicReleaseUnavailable
	}
	offer, err := s.github.LatestStable(ctx, target.Platform, target.Architecture, 148)
	if err != nil || !offer.Available || offer.SignerFingerprint == nil {
		return s.legacy.Latest(ctx, target)
	}
	return webportal.PublicRelease{
		Target: target, Version: offer.Version, Build: offer.Build, ReleaseNotes: offer.ReleaseNotes, PublishedAt: offer.PublishedAt,
		DownloadURL: offer.DownloadURL, ArtifactFilename: offer.ArtifactFilename, SizeBytes: offer.SizeBytes,
		SHA256: offer.SHA256Hex, SigningStatus: "verified",
		SignerFingerprint: *offer.SignerFingerprint,
	}, nil
}
