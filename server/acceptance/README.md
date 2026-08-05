# Production Protocol Acceptance Stack

This disposable stack runs the production TermLink `serve` command behind TLS
with Keycloak in production start mode, PostgreSQL, Valkey, and MinIO. It never
registers the LAN Replay or `TLT1` routes. The embedded identity database,
object store, and local CA remain disposable acceptance fixtures; they are not
production data stores or trust anchors.

The acceptance Keycloak image is augmented at image-build time and then starts
with `--optimized` on a read-only root filesystem. Runtime startup therefore
does not use Keycloak's development mode or mutate the image layer.

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

The managed secret directory rejects reparse points and does not retain
inherited host permissions. On Windows its ACL is limited to the invoking
account, `SYSTEM`, and local Administrators; on Unix the directory is `0700`
and each file is `0600`.

The acceptance Valkey password is generated into the managed
`valkey-password` file. The companion `redis-url` file contains only the
matching authenticated Redis URI; Compose never renders either value as an
environment variable. Valkey creates its ACL on a private tmpfs, runs as the
non-root Valkey UID, and exposes no host port.

Docker Desktop does not reliably apply Compose `uid`, `gid`, and `mode` to
file-backed secrets. Before any long-running service starts, an isolated
one-shot container copies each credential into a service-private named volume,
sets the exact runtime owner, and applies mode `0400`. The start script then
checks the metadata seen inside Keycloak, PostgreSQL, Valkey, and MinIO and
fails the deployment if any credential is group- or world-readable. Secret
contents are never included in that check or its output.

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
then returns every role to `/account`; authorized console roles can continue to
`/admin` from there. The portal stores only an encrypted, Secure,
HttpOnly, SameSite browser session; access and ID tokens are not persisted in
the browser cookie. Signing out is protected by an origin check and CSRF token.
The Keycloak account console uses the TermLink account theme, preserves the
same `termlink-theme` preference, and exposes a validated return link to
`/account`. The portal's allowlisted `/account/identity` bridge first renews the
`termlink-web` OIDC session and then redirects to the configured realm Account
Console. This keeps the product navigation and theme available even when the
shorter Keycloak SSO session expired while the portal session remained valid.

Self-service registration is enabled after `keycloak-init` has created or
verified the managed bootstrap administrator. The public Lucky upstream remains
stopped during that reconciliation, and an existing unmarked `admin` account
fails the deployment closed instead of receiving `SUPER_ADMIN`. The first
version does not require email verification; creating an account never grants
access to a Windows PC or trusts a device by itself.

Keycloak serves the responsive TermLink account theme from
`acceptance/keycloak-theme/termlink`. The realm import selects the theme for a
clean deployment, and `keycloak-init` applies the same setting idempotently to
an existing realm. Self-service registration is enabled and can be completed
from the login page. This disposable stack does not configure SMTP, so email
verification is explicitly disabled instead of leaving newly registered users
in an action they cannot complete. A production operator must configure a
working SMTP provider before enabling Keycloak email verification.

The cross-process deployment probe uses the dedicated `acceptance-probe` OIDC
user and a persistent probe PC identity. Interactive Windows and browser
acceptance uses `acceptance-user`, so repeated probes cannot consume that
user's first trusted Windows-device slot. Both users are disposable and exist
only in this acceptance realm.

History is enabled in this stack. The probe creates an encrypted-object
metadata record through the production API, uploads the opaque bytes through a
TLS Caddy presigned PUT, commits and lists the record, downloads it through the
TLS presigned GET, then deletes the session and verifies the old URL returns
404. It also checks that unsigned, tampered, unsupported-method, wrong-bucket,
and over-limit object requests never reach MinIO as successful operations.

The history host (`history.127.0.0.1.sslip.io`) is attached to an isolated
object network shared only by Caddy and MinIO. Server-to-MinIO management
traffic uses a separate internal `object-control` network; neither Caddy nor
the stateful PostgreSQL/Valkey backend network is reachable from that path.
Caddy forwards only
`GET`/`PUT /termlink-history/accounts/...` requests carrying the required
AWS SigV4 query fields; MinIO performs the final signature and credential
validation. MinIO's management and console endpoints are not exposed.

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
