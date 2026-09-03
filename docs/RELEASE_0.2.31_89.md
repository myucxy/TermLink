# TermLink Android 0.2.31 (89) release record

Date: 2026-09-03 (Asia/Shanghai)

## Source identity

- Release source commit: `03202e5e0638cd46d5790d04fcacedd10c73e539`.
- `origin/main` matched the release source commit before the Production build.
- This AppShip release contains only the Android APK. No Windows or server
  artifact was published as part of this release.

## Android changes

- Long terminal sessions resume through a bounded target-first recovery path,
  with realtime buffering kept independent from snapshot and history replay.
- Stale control requests superseded by a newer server revoke are treated as
  normal control arbitration. The session remains connected and read-only
  instead of displaying `TL-N003` or rebuilding the transport.
- A stale lease obtained after a superseded request is explicitly released.

## Verification

- `:core-network:testDebugUnitTest`: passed.
- `:app:testDebugUnitTest`: passed.
- `:app:lintDebug`: passed.
- Production APK signature continuity: passed.
- Emulator: `emulator-5558`, Android 16, 1080 x 2400.
- The Production package was upgraded in place from `0.2.30 (88)` to
  `0.2.31 (89)`. Its first-install timestamp remained
  `2026-08-29 18:14:23`, preserving account and session data.
- A new HOME-PC PowerShell 7 session reached `Controlling`; no `TL-N003`, crash,
  or ANR was present in the post-launch Android log.
- The older retained long session currently reports `TL-H102` because its newest
  server snapshot is behind the Windows output tail. That independent retained
  session condition is not the control-arbitration failure fixed in this build.
- Termux behavior reference remained pinned to `termux/termux-app` commit
  `3df69d1da197dd9bd71a3bafd902dffd720576b4`. Termux has no equivalent remote
  control-lease model, so it was used only as a terminal-behavior reference and
  no GPL code was copied.

## Production artifact

- Artifact: `TermLink-Android-0.2.31-89.apk`.
- Size: `36271328` bytes.
- SHA-256: `36a382ec2d88177785ae945a59467ed7f51b4e8391af46297cc50baf8322b1e9`.
- Application ID: `dev.termlink.harness`.
- Version: `0.2.31 (89)`.
- Signer SHA-256:
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Production verification result: passed.

## AppShip

- Application: `termlink-android`, channel `stable`.
- Application ID: `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`.
- Release ID: `04bf5c53-a209-4f4d-b46a-fd62335c16bb`.
- Artifact ID: `4eafdd76-2814-4b4b-a9e4-b79a44715172`.
- Final state: release `published`, artifact `ready`, download enabled.
- Resumable state:
  `.appship/termlink-android-stable-0.2.31-build89-signature-approved.json`.
- Public update API: build 88 returned `optional` with `0.2.31 (89)` and the
  ready artifact; build 89 returned `no_update`.
