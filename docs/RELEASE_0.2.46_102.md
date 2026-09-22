# TermLink 0.2.46 (102) release record

Date: 2026-09-22 (Asia/Shanghai)

## Source identity

- Release source commit: `d3ea4c1e650b2a2d23bfe21d27bb57df6ad2f448`; `origin/main` matched
  before the Production build, and Git tag `v0.2.46` points to this commit.
- Git tag and Docker version: `v0.2.46` / `v0.2.46`.
- GitHub is CI-only for this release: do not create a GitHub Release and do not publish an image to
  GHCR/GitHub Packages. The only Server image destination is `docker.io/myucxy/termlink:v0.2.46`.
- Target clients: Android `0.2.46 (102)` and Windows `0.2.46.102`.
- AppShip state before release: Android `0.2.44 (100)` and Windows `0.2.42 (98)`;
  build 102 is strictly newer than both and the locally installed Windows build 101.

## Changes

- Production Android and Windows builds enable Direct signalling and the Direct data plane.
- A missing server routing preference defaults to automatic LAN/NAT Direct preference with Relay
  kept as the fallback. An explicitly selected Relay-only preference remains authoritative.
- Android terminal input uses the shared native `terminal-core` path for hardware functional keys,
  modifiers, repeat/release events, dead keys, IME composition, and forward/backward deletion.
- Android session reconciliation and file transfer failure handling preserve existing live state and
  return actionable errors instead of terminating the UI flow.
- The Avalonia Windows client checks AppShip directly and accepts an update only after HTTPS,
  bounded size, SHA-256, Authenticode, embedded trust-anchor, and signer-pin validation.
- The removed legacy WinForms executable is not restored.

## Verification before the release build

- Android `:app:testReleaseUnitTest`, `:app:testDebugUnitTest`,
  `:core-network:testDebugUnitTest`, and `:terminal-engine-native:testDebugUnitTest`: passed.
- Android emulator instrumentation: 13/13 passed on `emulator-5554`.
- Windows `TermLink.Windows.sln` Release build: passed with zero warnings and zero errors.
- Production Direct Release, DirectAcceptance, native ABI v3/v4/v5, Direct native packaging,
  Avalonia Windows adapter, and isolated Setup install/upgrade/uninstall validations: passed.
- Rust network/terminal workspace tests: passed; only existing dead-code/linker warnings remained.
- Direct fault-model suite: 16/16 passed.
- Server `go test ./...`, isolated PostgreSQL Direct routing integration, and deployment package
  tests: passed.
- `git diff --check`: passed; only Git line-ending conversion notices were reported.

## Known validation boundary

- Real physical LAN and cross-NAT production socket coverage is `0`; it is not represented as an
  on-device pass. Relay-only remains the operational rollback switch if field Direct validation
  exposes a problem.
- The emulator required an in-place same-revision debug APK because its installed package is signed
  with the debug signer. It was upgraded from `0.1.26 (32)` to `0.2.46 (102)` with `adb install -r`;
  `firstInstallTime=2026-09-19 10:16:29` was unchanged. It opened without crash/ANR. Its existing login
  had expired and the locally available test credentials were rejected, so a live authenticated
  Claude session and `/` input could not be manually retested. Unit/instrumentation coverage must
  not be represented as this missing product-chain test.

## Termux reference boundary

- The fixed reference is `termux/termux-app@3df69d1da197dd9bd71a3bafd902dffd720576b4`
  through `terminal-forks/termux-terminal/UPSTREAM.md`, plus the checked-in patch queue and
  `android/terminal-engine-termux/` boundary.
- Termux is used as terminal-input behavior reference only. No local PTY, shell, Bootstrap/APT,
  plugin-signing, or local-process architecture is copied into TermLink's remote ConPTY/E2EE flow.

## Production artifacts and publication

- Manifest: `artifacts/release/0.2.46-102/artifact-manifest.json`; SHA-256
  `32f3e391749eabdd17556899257ed3e59403d31cef07244a880da22843518b95`.
- Android: `TermLink-Android-0.2.46-102.apk`, 38,114,726 bytes, SHA-256
  `dca0f76f9e493bab33ab3259f6b54cb4fb6559f341b66fa4636b359106d6a6dd`;
  package `dev.termlink.harness`, APK signature v2, signer SHA-256
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Windows: `TermLink-Setup-0.2.46.102-x64.exe`, 181,291,224 bytes, SHA-256
  `171955c492a69357ba24fa8f677dd7b6ffbb9ac7171b61715e91356aa03eb8d3`;
  ProductVersion `0.2.46.102+d3ea4c1e650b2a2d23bfe21d27bb57df6ad2f448`,
  Authenticode `Valid`, signer SHA-1 `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`,
  RFC 3161 timestamp present, all 17 executable payload binaries verified. This is the
  existing self-signed development signer, not a public CA certificate.
- Android AppShip `termlink-android/stable`: application
  `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`, release
  `477900a1-7835-498c-bfcf-8ec284bcf857`, artifact
  `dd5ac432-481a-476b-b4c3-ab33b404ee81`; release `published`, artifact `ready`.
- Windows AppShip `termlink-win64/stable`: application
  `0ae760b3-294e-48e8-a793-95d162f685e2`, release
  `6c2a5144-759d-4220-88cd-3cfe739e978b`, artifact
  `62a93a42-e618-4033-99d1-a7405ecb285b`; release `published`, artifact `ready`.
- AppShip public API: previous Android build 100 and Windows build 101 both return `optional` with
  the ready build 102; build 102 returns `no_update` on both applications.
- Windows installation: upgraded in place under `D:\Dev\Tool\TermLink`; install marker and the
  Agent, desktop, SessionHost, updater, and uninstaller all report `0.2.46.102`, ProductVersion
  points to the release source commit, signatures are valid, and the desktop/Agent processes run.
- Docker: tag `v0.2.46` was pushed to GitHub to trigger the Docker Hub-only workflow. Docker Hub
  registry and Hub API were inaccessible from the current host due to DNS/network resolution;
  workflow result, OCI index digest, amd64/arm64 manifests, Cosign signature, provenance and SBOM
  attestations **remain unverified**. A Git tag alone is not proof that a Docker image was published.
  A documentation/workflow follow-up removes the unused GitHub Packages write permission for
  future tags; the immutable `v0.2.46` source tag is not moved.
- Local Docker Server: still on the older `termlink-server:local` image; the immutable Docker Hub
  image has not been verified and this local Server has **not** been upgraded. Its migration, volume
  preservation and routing-default deployment checks remain pending.
