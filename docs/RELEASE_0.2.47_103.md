# TermLink 0.2.47 (103) release record

Date: 2026-09-22 (Asia/Shanghai)

## Source identity and scope

- Release source commit: pending the reviewed source commit; `HEAD` and `origin/main` must match before build.
- Target clients: Android `0.2.47 (103)` and Windows `0.2.47.103` on AppShip stable.
- This is a client-only release. Do not build or publish a Docker image, create a GitHub Release, or push a
  release tag. The Server remains on `v0.2.46`.
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

## Verification before the release build

- Android `:app:testReleaseUnitTest`, `:app:testDebugUnitTest`, `:core-crypto:testDebugUnitTest`,
  `:core-network:testDebugUnitTest`, and `:terminal-engine-native:testDebugUnitTest`: passed.
- Android emulator instrumentation passed on Android 16: Direct/network 8/8 and native terminal/JNI 13/13.
- The native terminal regression reproducing one `setComposingText("1")` plus `commitText("1")` as three native
  submissions fails before the fix and passes with exactly one submission after it. A second independent commit
  still produces a second input, so intentional repeated characters are not suppressed.
- Android `:terminal-engine-native:testDebugUnitTest`, `:app:testDebugUnitTest`, and
  `:app:testReleaseUnitTest` passed after the input fix.
- Windows `TermLink.Windows.sln` Release build: passed with zero warnings and zero errors.
- Windows Setup prerequisite/ownership validation and `ProductionDirectTransportValidation`: passed.
- Direct fault-model suite: 16/16 passed.
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
  Its current `TL-H114` history gap prevented a live remote command input check, so this is not recorded as a
  complete product-chain input pass.

## Termux reference boundary

- Reviewed `termux/termux-app@3df69d1da197dd9bd71a3bafd902dffd720576b4`, including
  `terminal-view/.../TerminalView.java` and its full-editor `BaseInputConnection`,
  `terminal-forks/termux-terminal/UPSTREAM.md`, the checked-in Termux patch queue, and
  `android/terminal-engine-termux/`.
- Also reviewed `termux/termux-app#686` (Samsung keyboard characters not echoed). Termux's full-editor connection
  can be reused as Android IME behavior, but TermLink keeps its own native terminal/E2EE/remote transport path.
- Termux has no equivalent TermLink ticket, NAT, or remote transport implementation. It is behavior reference
  only; no local PTY, shell, Bootstrap/APT, plugin-signing, or local-process architecture was copied.

## Production artifacts and publication

- Manifest, artifact hashes, version/signature evidence: pending Production build and independent verification.
- Android AppShip `termlink-android/stable`: pending.
- Windows AppShip `termlink-win64/stable`: pending.
- Public update checks for build 102 (`optional`) and build 103 (`no_update`): pending.
- Local Windows in-place upgrade and remote `HT-LPC-1326` update acceptance: pending. AppShip publication does
  not imply silent installation on either machine.

## Rollback

- Client packages are immutable in AppShip. Preserve the release and `.appship` state even if publication or
  field validation fails.
- Switch Direct routing to Relay-only if field validation is unstable; do not downgrade the Server or delete
  client data. A client rollback requires publishing a strictly newer, correctly signed build.
