# TermLink Android 0.2.50 (106) release record

Date: 2026-09-23 (Asia/Shanghai)

## Source identity and scope

- Release source commit: pending until the tested source and this pre-release record are committed and pushed.
- This is an Android-only release. Windows remains on `0.2.49 (105)` and the Server remains on
  `docker.io/myucxy/termlink:v0.2.47`; neither source changed, so neither is rebuilt or republished.
- AppShip stable before release is Android `0.2.49 (105)`. Local release directory `0.2.50-106`, AppShip state,
  remote Git tag `v0.2.50`, and Docker tag were absent before the build. This client-only release creates no tag.

## Android change

- Retryable Direct signalling socket failures no longer become permanently terminal after three retries. The
  existing reconnect path continues with exponential full-jitter backoff capped at approximately 30 seconds.
- Authentication, protocol, replay, malformed-frame, and queue-overflow faults remain non-retryable. Relay ownership,
  peer selection, signalling wire format, retry budgets for individual Direct paths, and Server policy are unchanged.
- This fixes a field-observed state where the shared dispatcher exhausted its short reconnect burst and every later
  Direct attempt failed locally until the Android process or authentication generation was replaced.

## Verification before the release build

- New regression `retryableSignalFailuresContinueAfterInitialBurstWithoutTerminalFault`: passed.
- Android `:core-network:testDebugUnitTest`, `:app:testDebugUnitTest`, and `:app:testReleaseUnitTest`: passed.
- Direct deterministic fault matrix: 16/16 passed. This is model coverage, not production socket evidence.
- `git diff --check`: passed.

## Termux reference boundary

- Rechecked `termux/termux-app` through the repository-pinned commit
  `3df69d1da197dd9bd71a3bafd902dffd720576b4`, `terminal-forks/termux-terminal/UPSTREAM.md`, the local patch queue,
  sync script, and `android/terminal-engine-termux/` boundary.
- The imported Termux code explicitly excludes networking and authentication, and searches of the official
  `termux/termux-app` issues found no equivalent WebSocket/Direct reconnect implementation. Termux is behavior
  context only; no local PTY, service, or process architecture was copied.

## Production artifact and AppShip

- Production APK hash, signer continuity, AppShip IDs, and final state are recorded only after the immutable
  release-source commit is built and the wrapper-driven upload completes.
- Required Android signer SHA-256:
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Planned state file: `.appship/termlink-android-stable-0.2.50-build106-signature-approved.json`.

## Live validation boundary

- Upgrade the debug-signed emulator from the same source/version without uninstalling or clearing data; preserve
  `appId`, first install time, login, device identity, and sessions.
- Force a retryable signalling interruption and verify attempts resume after the former three-retry boundary while
  Relay remains usable. Reopen the fresh `HOME-PC` session and verify history, one-copy input, and no History Gap.
- A real Direct pass still requires authoritative `LAN_DIRECT` or `NAT_DIRECT`. The emulator's `10.0.2.15` host-NAT
  network cannot establish physical same-Wi-Fi LAN coverage, and the two remote Windows peers remain on 0.2.47.

## Rollback

- Preserve the immutable AppShip release and resumable state. Android rollback requires a strictly newer build
  signed by the continuity signer; do not uninstall the app or clear client data.
- If field validation is unstable, select the existing Server Relay-only policy. No Windows or Server rollback is
  part of this Android-only release.
