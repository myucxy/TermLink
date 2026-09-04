# TermLink Android 0.2.33 (91) release record

Date: 2026-09-04 (Asia/Shanghai)

## Source identity

- Release source commit: `8f8c54843847c2784628e2c789818c85e5394d0a`.
- `origin/main` matched the release source commit before the Production build.
- This is an Android-only release. Windows and server artifacts were not rebuilt
  or published.

## Android changes

- Removed the Devices page search entry and its associated UI state.
- Added pull-to-refresh and retained a compact transparent refresh action using
  the same refresh state and operation.
- Added one-row and two-row terminal shortcut Dock layouts.
- Kept the built-in shortcuts fixed. One-row custom shortcuts support horizontal
  ordering; two-row custom shortcuts support horizontal ordering and row changes.
- Persisted one-row and two-row custom ordering independently so switching layouts
  does not overwrite either arrangement.
- Updated the shortcut settings, management dialog, tests, and Chinese/English
  resources for the selected layout.

## Verification

- Android `:app:testDebugUnitTest`, `:app:assembleDebug`, and `:app:lintDebug`
  passed before the release commit.
- `git diff --check` passed before the source commit.
- Emulator `emulator-5558` validated both Dock layouts, one-row legacy ordering,
  horizontal scrolling, layout-specific long-press movement, independent order
  persistence across an in-place update and process restart, and Devices refresh
  without crash or ANR.
  The UI validation package used the isolated
  `dev.termlink.harness.shortcuttest` application ID and was not published.
- The Production Release build completed successfully, including release vital
  lint and the native/core modules.
- `release/Verify-LocalRelease.ps1` independently verified the Production
  manifest, APK hash, package identity, version, minimum SDK, and signer.

## Production artifact

- Manifest: `artifacts/release/0.2.33-91/artifact-manifest.json`.
- Manifest SHA-256:
  `a134e48184e5d8e83ce37075dbfd4d3e19365df72d13db00c1132169e9ad3934`.
- Android artifact: `TermLink-Android-0.2.33-91.apk`, 36,288,084 bytes.
- Android SHA-256:
  `ca5880a0f40fc520071644f15cf21c7ec4590803995c1bc6cd473b5c819a1fc4`.
- Android application/version: `dev.termlink.harness`, `0.2.33 (91)`;
  minimum API 26, target API 36, APK signature scheme v2.
- Android signer SHA-256:
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.

## AppShip

- Application: `termlink-android`, channel `stable`.
- Application ID: `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`.
- Release ID: `4eb816d4-08e9-4ecb-9023-d50243c8f787`.
- Artifact ID: `80d59759-abbf-40aa-9dfe-8b05595fdfe3`.
- Resumable state:
  `.appship/termlink-android-stable-0.2.33-build91-signature-approved.json`.
- Final state: release `published`, artifact `ready`, signature review
  `manual_approved`, and downloads enabled.
- The public update API returns `optional` with `0.2.33 (91)` for build 90 and
  `no_update` for build 91.

## Termux reference

- The implementation followed the repository's existing Termux behavior notes
  in `docs/UI_LAYOUT_DESIGNS.md` and the checked-in fork boundary in
  `terminal-forks/termux-terminal/UPSTREAM.md`.
- Termux was used as interaction reference only. No Termux local PTY, shell,
  package, or plugin architecture was copied into this remote terminal flow.
