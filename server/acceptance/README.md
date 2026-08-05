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

On a clean deployment, `keycloak-init` also creates the product account
`admin`, assigns the realm bootstrap role `SUPER_ADMIN`, and installs a random
24-character temporary password. Retrieve that password exactly once from a
trusted local terminal, then complete Keycloak's mandatory password change:

```powershell
.\server\ops\Get-InitialAdminPassword.ps1
```

The retrieval command rotates the host bootstrap input before printing the
original and records that it was claimed. Do not redirect or log its output.
If it was already claimed and then lost, issue and retrieve a new temporary
credential without exposing the Keycloak bootstrap credential:

```powershell
.\server\ops\Reset-InitialAdminPassword.ps1
.\server\ops\Get-InitialAdminPassword.ps1
```

If Docker Desktop cannot pull Quay layers directly, the start script downloads
the exact official platform manifest through the OCI registry API, verifies the
manifest and every blob SHA-256, and loads it under an acceptance-only local
tag. It never falls back to an unpinned image.

The local endpoints are:

- API: `https://api.127.0.0.1.sslip.io:9443`
- Public product home and account portal: `https://termlink.kget.cc:8910/`
- Public API/OIDC through Lucky: `https://termlink.kget.cc:8910`

The public root is a product introduction page. Selecting **Login** starts an
OIDC Authorization Code + PKCE flow through the `termlink-web` public client,
then returns a regular user to `/account` or a console role to `/admin`. The portal stores only an encrypted, Secure,
HttpOnly, SameSite browser session; access and ID tokens are not persisted in
the browser cookie. Signing out is protected by an origin check and CSRF token.

Keycloak serves the responsive TermLink account theme from
`acceptance/keycloak-theme/termlink`. The realm import selects the theme for a
clean deployment, and `keycloak-init` applies the same setting idempotently to
an existing realm. Acceptance remains invitation-only because this stack does
not configure SMTP; the login page reports that policy instead of advertising
an unusable public registration flow.

The cross-process deployment probe uses the dedicated `acceptance-probe` OIDC
user and a persistent probe PC identity. Interactive Windows and browser
acceptance uses `acceptance-user`, so repeated probes cannot consume that
user's first trusted Windows-device slot. Both users are disposable and exist
only in this acceptance realm.

The acceptance stack binds an HTTP-only Lucky upstream on port `8080`. It is
intended for the configured private-LAN hop from Lucky; TLS continues to
terminate at the public `https://termlink.kget.cc:8910` origin. Identity paths
are routed to Keycloak and all other paths to the TermLink Server.

The Caddy CA is disposable and is not a production trust anchor. Local browser
or emulator testing must explicitly trust only the exported acceptance CA;
production clients continue to require a publicly trusted HTTPS chain.
The Windows Agent test path can instead set `TERMLINK_ACCEPTANCE_CA_FILE` to
the exported certificate. That override is accepted only for
`*.127.0.0.1.sslip.io` acceptance hosts and still enforces the TLS hostname.

Stop the stack while retaining data:

```powershell
.\server\ops\Stop-ProductionAcceptance.ps1
```

Remove the disposable volumes:

```powershell
.\server\ops\Stop-ProductionAcceptance.ps1 -RemoveVolumes
```

For a first-version clean-room deployment test, reset only the managed
`termlink-production-acceptance` project, regenerate its disposable secrets,
rebuild, and run the production protocol probe:

```powershell
.\server\ops\Start-ProductionAcceptance.ps1 -Reset
```

The reset path checks the acceptance marker before deleting its ignored secret
directory and runs Compose only with `server/compose/acceptance.compose.yml`.
It does not remove containers or volumes owned by other Docker projects.
