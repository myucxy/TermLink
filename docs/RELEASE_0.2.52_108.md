# TermLink Android and Windows 0.2.52 (108) release record

Date: 2026-09-23 (Asia/Shanghai)

## Source identity and scope

- Release source commit: `73de5f79af19fa712d81835aa378e76218cab924`; the production artifacts were built
  from this commit, and `HEAD` matched `origin/main` with a clean tracked worktree before publishing.
- Android and Windows clients only. Server and Docker sources did not change and are not rebuilt or published.
- AppShip stable before release: Android `0.2.51 (107)`, Windows `0.2.49 (105)`. The shared version
  `0.2.52 (108)` is strictly newer than both.

## Changes

- Android session status and carrier area is now a 48 dp clickable connection/control menu. A read-only session can
  request control, a controlling session can release control, and the current Relay/LAN/NAT carrier remains visible.
- The menu follows the Server-authoritative routing policy. Automatic mode exposes separate forced LAN and NAT
  authenticated end-to-end checks; Relay-only mode hides both checks and states that Direct is disabled by the Server.
- A forced check reuses the existing bounded Direct v4 discovery and never promotes its diagnostic path into session
  traffic. LAN accepts same-LAN host pairs only; NAT requires at least one server-reflexive candidate. Failed checks are
  reported as “not established” for that attempt, not as permanent network incompatibility.
- The initial routing-policy read now runs immediately when the Direct coordinator starts; the existing low-frequency
  refresh remains in place.
- Windows update confirmation and live download progress now render inside the main window, including cancellation.
  The tray update action first restores the main window. SHA-256, artifact size, publisher-signature verification, and
  installer launch behavior remain unchanged.

## Verification before the release build

- Android `:app:testDebugUnitTest`, `:core-network:testDebugUnitTest`, `:app:testReleaseUnitTest`, and
  `:app:assembleDebug`: passed.
- Windows `pc-client/TermLink.Pc.sln` and `win-agent/TermLink.Windows.sln` Release builds: passed with 0 warnings and
  0 errors. ArchitectureValidation, Desktop.HeadlessTests, and WindowsAdapterValidation passed.
- Direct deterministic fault matrix: 16/16 passed. This remains model coverage, not production-socket coverage.
- Release parser/signing/version/RFC3161/manifest/AppShip gate tests passed. `git diff --check` passed.
- A same-signer `adb install -r` preserved the emulator login and sessions. The live session menu immediately resolved
  the Server automatic policy, displayed control/LAN/NAT actions, and did not crash. Authenticated forced LAN and NAT
  checks against `HT-LPC-1326` both completed as “not established” on the tested emulator network while Relay remained
  usable. Relay-only UI behavior is covered by unit logic; the shared Server policy was not changed during this test.

## Termux reference boundary

- Reviewed `termux/termux-app` behavior around `TermuxActivity` context menus, issue `#5237`, PR `#5264`, and issue
  `#1548`, plus repository-pinned commit `3df69d1da197dd9bd71a3bafd902dffd720576b4`, the local patch queue,
  `Sync-TermuxTerminal.ps1`, and `android/terminal-engine-termux/`.
- The Termux terminal-view menu behavior is reference-only. TermLink binds the menu to its own top status control and
  keeps Windows ConPTY, remote E2EE transport, control leases, ACK, History Gap, Snapshot, and native terminal-core
  authority unchanged. No Termux GPL code, local PTY, shell, bootstrap, package, or process architecture was copied.

## Production artifacts and AppShip

- Android APK: `TermLink-Android-0.2.52-108.apk`, 38,087,666 bytes, SHA-256
  `37ee624e8e4f42ebd5ba15aad96e29c44cb77e5b015bcb61cd3ba4f125f8ab3a`. AppShip application
  `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`, release `d96c39dc-a2f7-4183-bc66-b4eb20fc2f64`, artifact
  `82b7d5a1-d00d-4704-94f7-2e81c0b04ecd`; release is `published` and artifact is `ready`.
- Windows Setup: `TermLink-Setup-0.2.52.108-x64.exe`, 181,294,816 bytes, SHA-256
  `cac6ac199025eccba73b8f3e479ee25700bfb550a86b26e815030748e89a0e31`. AppShip application
  `0ae760b3-294e-48e8-a793-95d162f685e2`, release `c15e3d14-ac0b-4836-a5b0-5e211913ce77`, artifact
  `b7b12120-4aea-4542-b11b-a699d7f1a956`; release is `published` and artifact is `ready`.
- Public update verification: Android build 107 and Windows build 105 return `optional` for `0.2.52 (108)` with a
  ready artifact; Android and Windows build 108 both return `no_update`.
- Android signer SHA-256: `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Windows signer SHA-1: `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`; the existing self-signed continuity
  certificate is not represented as public-CA trust.
- Preserved AppShip state files:
  `.appship/termlink-android-stable-0.2.52-build108-signature-approved.json` and
  `.appship/termlink-win64-stable-0.2.52-build108-signature-approved.json`.

## Rollback

- Preserve published releases and resumable AppShip state. Client rollback requires a strictly newer build signed by
  the same continuity signer; do not uninstall Android or clear application data.
- If Direct behavior is unstable, use the existing Server Relay-only policy. No Server or Docker rollback belongs to
  this client-only release.
