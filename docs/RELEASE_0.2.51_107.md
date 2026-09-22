# TermLink Android 0.2.51 (107) release record

Date: 2026-09-23 (Asia/Shanghai)

## Source identity and scope

- Release source commit: pending until this tested change and pre-release record are committed and pushed.
- Android only. Windows remains `0.2.49 (105)` and Server remains
  `docker.io/myucxy/termlink:v0.2.47`; neither is rebuilt or republished.
- AppShip stable before release is Android `0.2.50 (106)`. Build 107, local release directory, AppShip state,
  remote Git tag `v0.2.51`, and Docker tag were unused before build. This client-only release creates no Git tag.

## Android change

- Removed the independent three-retry ceiling in `ProductionDirectShadowRuntime`. Failed Direct discovery now keeps
  the existing 30-second cooldown and re-arms while Relay remains the active transport.
- The lower signalling socket still uses capped exponential backoff. Authentication, protocol, replay, malformed
  frame, queue overflow, generation, and peer-authorization boundaries remain fail-closed.
- No protocol, Server policy, Direct ticket, E2EE, Relay, ACK, history, snapshot, or input behavior changed.

## Verification before the release build

- Regression `retryLoopContinuesBeyondFormerAttemptBudget`: passed through six consecutive attempts.
- Android `:core-network:testDebugUnitTest`, `:app:testDebugUnitTest`, and `:app:testReleaseUnitTest`: passed.
- Direct deterministic fault matrix: 16/16 passed. This is model coverage, not production socket evidence.
- `git diff --check`: passed.

## Termux reference boundary

- Rechecked repository-pinned `termux/termux-app@3df69d1da197dd9bd71a3bafd902dffd720576b4`, the local patch queue,
  sync script, and `android/terminal-engine-termux/` boundary.
- Networking, authentication, WebSocket signalling, and reconnect policy are explicitly outside the imported Termux
  scope. No Termux local PTY, shell, process, or network code was copied; Termux remains behavior context only.

## Production artifact and AppShip

- Production APK identity, hash, signer verification, AppShip IDs, and public update results are recorded only after
  the release-source commit is built and the repository wrapper finishes publishing.
- Required Android signer SHA-256:
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Planned state file: `.appship/termlink-android-stable-0.2.51-build107-signature-approved.json`.

## Live validation boundary

- Upgrade the debug-signed emulator with a same-source release-behavior APK by `adb install -r`; do not uninstall or
  clear data. Preserve UID, first-install time, login, device identity, tabs, and sessions.
- Keep Relay usable and verify at least a fifth Direct Runtime attempt after four failures and 30-second cooldowns.
  Recheck session recovery and one-copy input after a process restart.
- Only authoritative `LAN_DIRECT` or `NAT_DIRECT` is a Direct success. Emulator host NAT cannot substitute for a
  physical LAN or cross-NAT test, and old Windows 0.2.47 responders are not acceptance peers.

## Rollback

- Preserve immutable AppShip state. Android rollback requires a strictly newer build signed by the continuity
  signer; do not uninstall the app or clear client data.
- If Direct validation is unstable, select the existing Server Relay-only policy. No Windows or Server rollback is
  part of this Android-only release.
