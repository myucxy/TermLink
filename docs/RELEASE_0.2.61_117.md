# TermLink 0.2.61 (117)

Date: 2026-09-24. Scope: Android APK and Windows x64 Setup to AppShip stable;
Server image compiled locally at the same version. No Server deployment or
Docker Hub/GitHub image publication is included in this request.

## Changes

- Add the session cloud menu actions to fit the Windows terminal to the measured
  phone viewport and restore the previous desktop grid. Measure actual font,
  SP/font scale, line spacing and available content area without changing the
  phone authority grid early. Confirm the host result and applied phone grid.
- Retain control lease, local-input priority, request deadlines and per-session
  admission. A local desktop resize supersedes the saved restore point. Control
  results are E2EE and do not advance terminal replay or ACK cursors.
- Negotiate optional Server support before sending the explicit resize request.
  Older servers keep working for existing operations and display an upgrade
  instruction for this feature, avoiding an unsupported reply on a shared socket.
- Ship pinned Microsoft ConPTY 1.24.260710001 and its MIT notice. Preserve Codex
  faint styling and palette queries. Native terminal-core accepts the exact
  OpenConsole startup window-restore hint, retaining strict handling elsewhere.
- Allow THIRD_PARTY_NOTICES.md in the Server Docker build context, as required
  by the existing Dockerfile COPY instruction.

## Upgrade order and limits

Upgrade Android before Windows, then open new Windows terminal sessions. Android
116 may reject the newer OpenConsole startup sequence; do not assume older
phones can render output from the new Windows host. Window resizing additionally
needs Server 0.2.61 to be deployed separately. No database migration is added.

Windows Terminal must accept the window resize (single tab/pane, non-fullscreen,
non-quake, within size limits). This changes terminal grid/window dimensions,
not monitor resolution or Windows DPI. Phone and PC clocks must be synchronized.
See [phone window resize](PHONE_WINDOW_RESIZE.md) and
[terminal fidelity](TERMINAL_FIDELITY.md) for implementation, upstream references,
license boundaries and validation commands.

## Validation and release state

The final implementation passed Android Debug app 318, network 331 and native
118 tests; Release app 313, network 331 and native 118 tests; and 15 Android 16
emulator instrumentation tests. This includes the older-Server capability guard.
Windows validation used a disposable outer ConPTY with the real SessionHost/inner
ConPTY to check fit, restore, refusal, local-input priority, expiry and local-resize
precedence. History WAL/producer/outbox validation, full Go tests, the Release
Windows solution build, Setup prerequisite/ownership checks and release-script
checks passed. The isolated Setup install/upgrade/uninstall lifecycle passed using
a separately built validation package from the same source; the final Production
packages passed independent version, hash, signer and timestamp verification.

The Android emulator was upgraded in place to the same-source Debug 0.2.61/117
package because its existing installation uses the Debug signer. Login, appId
10224 and firstInstallTime (2026-09-19 10:16:29) were retained. On an isolated
Windows test session, the new cloud menu measured 49 columns by 40 rows in a
1038 by 1730 pixel viewport. Clicking Fit against the currently deployed older
Server displayed "Update the server to enable window resizing." and retained
the Controlling/Relay connection. This validates the compatibility guard, not a
deployed new-Server resize round trip. The disposable test host and its launcher
exited after their five-minute timeout. No successful UI input-marker result or
new physical LAN/NAT matrix is claimed for this release.

Windows uses the existing development Authenticode identity, not a public CA
certificate. Both signers were verified against the published build 116 identities.
Rollback keeps the previous deployment/image and uses a strictly newer client
build with the same signing identities; do not replace keys, erase user data or
overwrite an already published release.

## Immutable source and client artifacts

All artifacts below were built from source commit
`9be56278d83c5ab1b2bd26e6fa34417f4db9b579`, pushed to `origin/main` before
building. A later documentation-only commit records the observed release results;
it is not the binary source revision. The production manifest is retained at
`artifacts/release/0.2.61-117/artifact-manifest.json` and was not rewritten after
publication.

| Artifact | Version | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| `TermLink-Android-0.2.61-117.apk` | 0.2.61 / versionCode 117 | 38124194 | `6a2ee44b59e868f42bc9c675999818c2fa2134f740f3fee80e20852412cc3880` |
| `TermLink-Setup-0.2.61.117-x64.exe` | 0.2.61.117 | 182468320 | `31163b358e247de8525771d2991114456cc74a6a993de4713436ae8ef3aa4a18` |

Android signer SHA-256:
`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
Windows signer SHA-1: `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`, valid through
2036-08-04; RFC 3161 timestamp verification passed for the Production package.
The existing local Windows installation remains build 116; this publication did
not upgrade or restart the user's Windows client or sessions.

## AppShip stable publication

Both releases are `published`; both artifacts are `ready`, `manual_approved` and
download-enabled. Public update responses match the local Production manifest's
version, size, SHA-256 and approved signer. For both applications, build 116 gets
`optional` with 0.2.61/117, and build 117 gets `no_update`.

| Application | Application ID | Release ID | Artifact ID | Published (UTC) |
| --- | --- | --- | --- | --- |
| `termlink-android` | `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2` | `3bcc88c6-59a2-4247-b727-dbc783d6a815` | `a962611b-49cd-412c-8c98-0696c668253c` | 2026-09-24 08:01:22 |
| `termlink-win64` | `0ae760b3-294e-48e8-a793-95d162f685e2` | `efc6dd13-478b-4d23-a125-b6338454e9fa` | `f5b2dd14-1049-44b8-a64b-d3d33bc38d8c` | 2026-09-24 08:03:02 |

Resumable publish state is retained under `.appship/` in each application's
`<slug>-stable-0.2.61-build117-signature-approved.json` file. Sanitized public
update evidence is in `artifacts/validation/release-117-appship-public.json`.

## Local Server image

Built `linux/amd64` and `linux/arm64` OCI images with version `0.2.61+117`,
revision `9be56278d83c5ab1b2bd26e6fa34417f4db9b579`, source
`https://github.com/myucxy/TermLink` and created label `2026-09-24T15:51:26+08:00`.
Verified the OCI index, both platform configurations and every layer digest.
The loaded amd64 image also passed an isolated, read-only, network-disabled
startup smoke check (expected invalid-command response).

- OCI archive: `artifacts/release/0.2.61-117/server/TermLink-Server-0.2.61-117.oci.tar`
  (20467712 bytes).
- Archive SHA-256: `9d573184dc54d1eb080eca610c948d940a1e9781d309d8bc72c3de04c1211c30`.
- Index digest: `sha256:1e8818a339c65dd3fb7d064c4b3470012bf295f05e8171eb7043d099aae70222`.
- amd64 manifest: `sha256:646776dbba495edc1fcd8fa45fb9f66c9b7a8cac03c75384d0567d92e5018bb7`.
- arm64 manifest: `sha256:17dc781d701cb885a86dc3554875b12d706cf20d13eea8f6880b2083df49fefd`.
- Local amd64 tags: `termlink-server:0.2.61-build.117`, `myucxy/termlink:v0.2.61`.
- Local amd64 image ID: `sha256:6f16ac1c9bfa5d6f6822aa7902dafeb0c057b50768b2816ec0e1cd505fc46127`.

The default Docker driver could not export OCI. A separate pinned BuildKit
builder (`termlink-release-117`) produced the archive; a build-scoped proxy
resolved the subsequent Go module download stall. The builder was stopped after
completion. No global Docker configuration was changed.

This is a local build only: no Docker Hub upload, Git release tag, Cosign signature,
production supply-chain attestation qualification or Server deployment is claimed.
The existing `termlink-local-server-1` and `termlink-local-postgres-1` remained
healthy; Server still runs `termlink-server:release-114-validation`. Database
migration, OIDC and public readiness deployment acceptance were not rerun because
the service was not upgraded. The new resize feature requires a later Server
deployment.

Build, signature, test and image evidence remains in the ignored
`artifacts/validation/release-117-*` files, including
`release-117-production-build.log`, `release-117-setup-lifecycle.json`,
`release-117-server-verification.json`, `release-117-menu.xml` and
`release-117-result.xml`. These local evidence files and all publish credentials
are excluded from Git.
