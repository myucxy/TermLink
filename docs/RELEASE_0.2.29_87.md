# TermLink Android 0.2.29 (87) release record

Date: 2026-09-03 (Asia/Shanghai)

## Source identity

- Release source commit: `3348ad264a0cb7477b4d0d8ceac810d47616b367`.
- `origin/main` matched the release source commit before the Production build.
- This release changes only the Android client. Windows and server artifacts were
  not rebuilt or deployed.

## Android changes

- The ten built-in Dock keys use a fixed, aligned two-row layout and cannot be
  reordered.
- Custom shortcuts can be placed in either row, scrolled horizontally, and moved
  within or across rows after a long press.
- The shortcut manager is a dedicated scrollable dialog and includes a special-key
  picker for keys that are difficult to enter from a mobile keyboard.
- Session tabs use a compact 36 dp height, bounded width, middle ellipsis, and a
  persistent close control.
- Landscape hides the status bar, portrait restores it, the portrait bottom
  navigation is more compact, and a keep-screen-on setting is available.

## Verification

- `:app:testDebugUnitTest`: passed.
- `:app:assembleDebug`: passed.
- `:app:lintDebug`: passed; the new Compose modifier-order warning was resolved.
- Emulator: `emulator-5558`, Android 16, 1080 x 2400.
- The isolated test package was upgraded in place to
  `0.2.29-shortcut-test (87)`. Its first-install timestamp and persisted custom
  shortcut rows were unchanged.
- Long-press selection, left/right reorder, cross-row movement, fallback menu
  labels, portrait layout, landscape status-bar behavior, and compact navigation
  were exercised without a crash or ANR.
- The Production package was upgraded in place from `0.2.25 (82)` to
  `0.2.29 (87)`. Its first-install timestamp remained
  `2026-08-29 18:14:23`, and the app started successfully.
- Termux behavior reference: `termux/termux-app` commit
  `3df69d1da197dd9bd71a3bafd902dffd720576b4`, especially
  `ExtraKeysInfo.java` and `ExtraKeysView.java`. Only the two-dimensional layout
  behavior was referenced; no GPL code was copied.

## Production artifact

- Artifact: `TermLink-Android-0.2.29-87.apk`.
- Size: `36271328` bytes.
- SHA-256: `dc84be794189d4c374cb61ae0bd3227a0dcf8c12782b4526cc76c98103676364`.
- Application ID: `dev.termlink.harness`.
- Version: `0.2.29 (87)`.
- APK signature scheme: v2.
- Signer SHA-256:
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Production verification result: passed.

## AppShip

- Application: `termlink-android`, channel `stable`.
- Application ID: `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`.
- Release ID: `a92d576a-914f-4ecc-835d-74941a3f2278`.
- Artifact ID: `461c504e-b9c3-450f-84fc-4400aab4f646`.
- Final state: release `published`, artifact `ready`, download enabled.
- Resumable state:
  `.appship/termlink-android-stable-0.2.29-build87-signature-approved.json`.
- Public update API: build 83 returned `optional` with `0.2.29 (87)`; build 87
  returned `no_update`.
