# TermLink 0.2.47 (103) release record

Date: 2026-09-22 (Asia/Shanghai)

## Source identity and scope

- Release source commit: pending the reviewed source commit; `HEAD` and `origin/main` must match before build.
- Target clients: Android `0.2.47 (103)` and Windows `0.2.47.103` on AppShip stable.
- Target Server image: `docker.io/myucxy/termlink:v0.2.47`, published for `linux/amd64` and `linux/arm64`.
- Push immutable Git tag `v0.2.47` only to trigger the reviewed Docker Hub workflow. Do not create a GitHub
  Release and do not publish to GHCR/GitHub Packages.
- AppShip stable before release: Android and Windows are both `0.2.46 (102)`; build 103 is strictly newer.

## Changes

- Android and Windows accept the Server's confirmed renewable Direct ticket lifetime of up to 20 minutes.
- Candidate lifetime passed to native Direct gathering is capped at 60 seconds on both clients, within the
  native protocol's two-minute ceiling.
- Android Direct gathering binds its UDP socket to the active Android Network's concrete local address instead
  of the rejected unspecified address `0.0.0.0`.
- Android Direct ticket verification reuses the existing BouncyCastle Ed25519 implementation so verification
  behaves consistently on supported Android runtimes.
- Android can emit opt-in, redacted Direct diagnostics through `TermLinkDirectShadow`; the diagnostics contain
  stages and native reason/status codes only, not addresses, ticket contents, IDs, tokens, or keys.
- Android declares its composing `InputConnection` as a full editor, matching Termux's `TerminalView`, so one
  IME composition commit produces one terminal input while intentional repeated commits remain distinct.
- Android Debug now enables the authoritative Snapshot V2 runtime by default, matching Release. This prevents a
  positive durable ACK from being stranded at `TL-H114` while the Windows host still has a valid Snapshot and WAL.
- Android no longer calls the retired Server history catalog, snapshot, transcript, or object-download paths.
  Snapshot/WAL/transcript restore now requires `host_history_v1` from the online Windows Agent; older Agents fail
  with an explicit `TL-H125` Windows upgrade requirement instead of silently falling back to Server-owned history.
- Server migration verification accepts the canonical migration 49 checksum while retaining the existing
  compatibility checksum for databases created by the earlier migration text.

## Verification before the release build

- Android `:app:testReleaseUnitTest`, `:app:testDebugUnitTest`, `:core-crypto:testDebugUnitTest`,
  `:core-network:testDebugUnitTest`, and `:terminal-engine-native:testDebugUnitTest`: passed.
- Android emulator instrumentation passed on Android 16: Direct/network 8/8 and native terminal/JNI 13/13.
- The native terminal regression reproducing one `setComposingText("1")` plus `commitText("1")` as three native
  submissions fails before the fix and passes with exactly one submission after it. A second independent commit
  still produces a second input, so intentional repeated characters are not suppressed.
- Android `:terminal-engine-native:testDebugUnitTest`, `:app:testDebugUnitTest`, and
  `:app:testReleaseUnitTest` passed after the input fix.
- Debug policy regression, full `:app:testDebugUnitTest`, and full `:app:testReleaseUnitTest` passed after the
  Snapshot V2 default fix.
- Full Android Debug/Release unit tests and Debug APK assembly passed after removing the Server history fallback.
- Windows `TermLink.Windows.sln` Release build: passed with zero warnings and zero errors.
- Windows Setup prerequisite/ownership validation and `ProductionDirectTransportValidation`: passed.
- Direct fault-model suite: 16/16 passed.
- Server `go test ./...`: passed.
- Unified release parser, plan, signature, version, RFC 3161, production-manifest, AppShip gate, and strict-path
  checks: passed.
- Production build, independent artifact verification, and AppShip publication: pending.

## Live validation boundary

- The Android emulator now reaches Direct `GATHERING` and `NEGOTIATING`, proving Android Network capture,
  UDP socket creation/binding, host-candidate generation, and signaling candidate upload.
- No authoritative `LAN_DIRECT` or `NAT_DIRECT` carrier has yet been observed. Physical LAN and cross-NAT
  production socket validation remains pending until the remote Windows client `HT-LPC-1326` upgrades to this
  release. Unit, instrumentation, and shadow-state evidence must not be reported as a Direct carrier pass.
- Relay remains the stable fallback and the Server's Relay-only strategy remains the operational rollback.
- The emulator's debug-signed app was upgraded in place from `0.2.46 (102)` to `0.2.47 (103)` without changing
  `appId=10224` or `firstInstallTime=2026-09-19 10:16:29`; the retained logged-in session opened without a crash.
  The first Debug build incorrectly disabled Snapshot V2 by default and therefore reported `TL-H114` without
  requesting the available Windows snapshot. After the shared build-policy fix, the same installed app and
  session recovered its bound Snapshot at sequence 7, requested the encrypted Windows-owned event range 8..35,
  reached `Controlling`, and retained the Relay fallback. The Server session reported `snapshot_status=READY` and
  `last_output_seq=34`; Windows retained a contiguous local WAL 1..35.
- After removing the Server history fallback, the same debug-signed emulator app was upgraded in place: `appId=10224`,
  `firstInstallTime=2026-09-19 10:16:29`, login and open tab remained intact, and the session showed `Controlling`
  with Relay. This is a local Debug smoke test, not a published Production or physical LAN/NAT pass.

## Termux reference boundary

- Reviewed `termux/termux-app@3df69d1da197dd9bd71a3bafd902dffd720576b4`, including
  `terminal-view/.../TerminalView.java` and its full-editor `BaseInputConnection`,
  `terminal-forks/termux-terminal/UPSTREAM.md`, the checked-in Termux patch queue, and
  `android/terminal-engine-termux/`.
- Also reviewed `termux/termux-app#686` (Samsung keyboard characters not echoed). Termux's full-editor connection
  can be reused as Android IME behavior, but TermLink keeps its own native terminal/E2EE/remote transport path.
- Reviewed `termux/termux-app#3709` and `#5259` for local session persistence and process lifecycle. They have no
  remote Snapshot/ACK equivalent; the TL-H114 fix therefore changes only TermLink's build policy and reuses its
  existing Windows-owned, recipient-bound E2EE history protocol.
- Termux has no equivalent TermLink ticket, NAT, or remote transport implementation. It is behavior reference
  only; no local PTY, shell, Bootstrap/APT, plugin-signing, or local-process architecture was copied.

## Production artifacts and publication

- Manifest, artifact hashes, version/signature evidence: pending Production build and independent verification.
- Android AppShip `termlink-android/stable`: pending.
- Windows AppShip `termlink-win64/stable`: pending.
- Docker Hub `docker.io/myucxy/termlink:v0.2.47` index digest, `linux/amd64`/`linux/arm64` manifests, Cosign
  signature, provenance, SPDX and CycloneDX attestations: pending.
- Public update checks for build 102 (`optional`) and build 103 (`no_update`): pending.
- Local Windows in-place upgrade and remote `HT-LPC-1326` update acceptance: pending. AppShip publication does
  not imply silent installation on either machine.

## Rollback

- Client packages are immutable in AppShip. Preserve the release and `.appship` state even if publication or
  field validation fails.
- The Server tag and digest are immutable. Do not move `v0.2.47`; a Server rollback must pin the previous
  verified `v0.2.46@sha256:...` digest, and a repaired image requires a new version tag.
- Switch Direct routing to Relay-only if field validation is unstable; do not downgrade the Server or delete
  client data. A client rollback requires publishing a strictly newer, correctly signed build.
