# TermLink Android 0.2.53 (109)

Date: 2026-09-23 (Asia/Shanghai)

## Scope and source

- Android only. Live AppShip checks found Android and Windows stable at `0.2.52 (108)`;
  the new Android version is `0.2.53 (109)`. No matching remote Git tag exists.
- Source revision and publication evidence will be completed after the tested source is committed and built.

## Changes

- Restore the session header's natural height by removing the status control's 48 dp layout minimum.
  The status text and carrier icon remain one Compose clickable button with an accessible action label;
  Compose's minimum touch target does not reserve extra header layout space.
- Separate the connection menu's bold, shaded heading and static status summary from actionable rows with
  a divider. Control actions and LAN/NAT tests have icons; network results appear at the right.
- Keep the menu open during network tests so checking and completion are visible. Server Relay-only
  and unknown policy remain noninteractive explanations and hide network tests.
- Fix a conflict found during emulator testing: explicit menu release used the lifecycle-release reason,
  so foreground auto-acquisition immediately reclaimed control. Explicit release now supplies a distinct
  local reason that the existing UI auto-acquisition gate respects. Lifecycle release keeps its existing
  default, and manual acquisition still uses the existing authoritative lease request.

## Verification

- `:app:testDebugUnitTest`, `:app:testReleaseUnitTest`, `:core-network:testDebugUnitTest`,
  and `:app:assembleDebug` passed. A regression checks explicit read-only intent across foreground generations
  while preserving lifecycle auto-acquisition.
- `release/Test-BuildRelease.ps1` and `git diff --check` passed.
- Same-signer debug overlay on `emulator-5554` preserved app ID `10224`, first install time
  `2026-09-19 10:16:29`, login, existing session and terminal history. Installed version: `0.2.53 (109)`.
- At 420 dpi and font scale 1.0, the old status control occupied 126 px (48 dp); it now occupies 47 px
  (18 dp icon height), with the header determined by its original title text. Tapping both text and cloud
  opens the menu. English and Chinese were visually checked at font scale 1.5; original locale/font settings
  were restored afterward.
- Real session verification reproduced and then fixed immediate auto-reacquisition after explicit release;
  the final build stays read-only, offers Request control, and returns to Controlling after that action.
- With shadow discovery enabled in the debug acceptance build, LAN/NAT checks displayed checking and
  not established without dismissing the menu. This verifies feedback, not successful LAN/NAT connectivity.
  Policy-loading UI was also checked. Shared Server policy was not changed to exercise Relay-only visually.
- One emulator ANR after repeated locale/font/install changes was captured: main waited in HWUI DrawFrame,
  RenderThread waited in QEMU EGL synchronization (`qemu_pipe_read` / `rcCreateSyncKHR_enc`). After an App
  restart, menu/control checks succeeded with no additional ANR. Evidence is retained under ignored
  `artifacts/validation/session-menu-109/`; this is not represented as a clean run with no incident.

## Termux reference

- Reviewed `termux/termux-app` `TermuxActivity.onCreateContextMenu` at pinned commit
  `3df69d1da197dd9bd71a3bafd902dffd720576b4` and PR [#5264](https://github.com/termux/termux-app/pull/5264)
  (session drawer actions), plus `terminal-forks/termux-terminal/UPSTREAM.md`, the local patch queue,
  `Sync-TermuxTerminal.ps1`, and `android/terminal-engine-termux/`.
- Termux's Android View context menu is a behavior reference; it does not implement this Compose header.
  No upstream code/resources were copied. Remote ConPTY, E2EE, ACK/history and terminal-core ownership remain
  at their existing boundaries. Native Material headings/buttons and the real menu interaction provide
  the applicable behavior regression instead of importing local-PTY Termux tests.

## Production publication

- Pending production build, independent APK verification and repository-wrapped AppShip publication.
- Android continuity signer SHA-256: `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- State path: `.appship/termlink-android-stable-0.2.53-build109-signature-approved.json`.
- Keep the published history/state. Rollback requires a newer same-signer build; never uninstall or clear
  application data to downgrade.
