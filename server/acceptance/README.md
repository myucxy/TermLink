# Production Protocol Acceptance Stack

This disposable stack runs the production TermLink `serve` command behind TLS
with a real Keycloak OIDC issuer, PostgreSQL, and Valkey. It never registers the
LAN Replay or `TLT1` routes.

Start and validate the complete stack from the repository root:

```powershell
.\server\ops\Start-ProductionAcceptance.ps1
```

The script generates random credentials under the ignored
`server/secrets/acceptance` directory, starts the containers, exports only the
disposable public CA certificate, and runs a cross-container probe. The probe
uses a real signed OIDC access token to register and pair PC/Android devices,
open both Realtime roles, route opaque production Protobuf Envelopes, enforce a
control lease, release it as the PC, and verify device revocation disconnects
the Android socket. No credential or token is printed.

If Docker Desktop cannot pull Quay layers directly, the start script downloads
the exact official platform manifest through the OCI registry API, verifies the
manifest and every blob SHA-256, and loads it under an acceptance-only local
tag. It never falls back to an unpinned image.

The local endpoints are:

- API: `https://api.termlink.localhost:9443`
- OIDC: `https://id.termlink.localhost:9443/realms/termlink`

The Caddy CA is disposable and is not a production trust anchor. Local browser
or emulator testing must explicitly trust only the exported acceptance CA;
production clients continue to require a publicly trusted HTTPS chain.

Stop the stack while retaining data:

```powershell
.\server\ops\Stop-ProductionAcceptance.ps1
```

Remove the disposable volumes:

```powershell
.\server\ops\Stop-ProductionAcceptance.ps1 -RemoveVolumes
```
