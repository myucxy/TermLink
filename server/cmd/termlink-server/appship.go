package main

import (
	"os"

	"github.com/termlink/termlink/server/internal/agentupdate"
	"github.com/termlink/termlink/server/internal/webportal"
)

func newAppShipSources() (*agentupdate.AppShipCatalog, webportal.PublicReleaseSource, error) {
	// Both readers share one immutable startup policy; legacy database overrides are ignored.
	privateIPs := os.Getenv("TERMLINK_APPSHIP_ALLOWED_PRIVATE_IPS")
	catalog, err := agentupdate.NewAppShipCatalogWithSignerAnchor(
		agentupdate.DefaultAppShipBaseURL, nil, privateIPs, os.Getenv("TERMLINK_APPSHIP_SIGNER_FINGERPRINT"),
	)
	if err != nil {
		return nil, nil, err
	}
	publicSource, err := webportal.NewAppShipPublicReleaseSourceWithAllowedPrivateIPs(
		agentupdate.DefaultAppShipBaseURL, nil, privateIPs,
	)
	if err != nil {
		return nil, nil, err
	}
	return catalog, publicSource, nil
}
