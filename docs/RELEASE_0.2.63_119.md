# TermLink Android 0.2.63 / build 119

## Scope and behavior

Android stable only. Detached history now uses TermLink's native Android view
and an immutable Render Frame V3 projection. Production APKs no longer include
the Termux adapter, emulator or view. The legacy replay activity and reference
implementation remain available only in debug/test configurations.

History retains the existing font metrics, line spacing, colors, synthetic
styles and host-resolved wide-character layout. Pagination, remounts, horizontal
anchors and clearing/reopening have regression coverage. Live terminal rendering,
remote protocols, E2EE, control leases and ACK authority are unchanged. Windows
and Server do not need an upgrade for this release.

No Windows package, Docker image, Git release tag or GitHub Release is part of
this publication. Publication does not install or restart local clients.

## References and validation

Termux behavioral reference: `termux/termux-app` at pinned commit
`3df69d1da197dd9bd71a3bafd902dffd720576b4`, primarily `TerminalRenderer.java`,
`TerminalView.java` and `TerminalBuffer.java`. Local upstream provenance,
patches and adapter boundaries were checked. Online upstream retrieval timed
out; no review of newer upstream changes is claimed. Retained reference sources
keep their license notices. See [implementation record](ANDROID_TERMUX_REMOVAL.md).

Passed on the final implementation:

- Native module: 123 JVM tests; app: 320 Debug and 315 Release JVM tests.
- Android 16 emulator: 16 instrumentation tests, including JNI/input checks.
- 14 JVM and 8 emulator bitmap scenarios match the pinned Termux reference
  pixel for pixel, including system monospace, bundled Nerd Font, font sizes,
  line spacing, styled/wide/combining characters and narrow/tiny viewports.
- History lifecycle, pagination, capacity and malformed-page regressions.
- Release assembly and vital lint. Release runtime dependencies have no
  Termux modules. The prepublication unsigned APK has no Termux DEX descriptors
  or selection resources and retains all four existing JNI ABIs.

Pixel equality applies to the tested cases, not every custom font, GPU,
accessibility configuration or selection-handle animation. Instrumentation
does not claim a new physical LAN/NAT or remote Windows end-to-end acceptance.

Evidence: `artifacts/research/termux-dependency/`, including `result.json`,
`completed-validation.log` and `validation-evidence.zip`; additional Release
tests: `artifacts/validation/release-119-unit-tests.{log,json}`.

## Release gates and rollback

Live AppShip preflight returned 0.2.62/118 for Android and Windows. Authenticated
Android stable defaults returned next build 119, including existing drafts.
Remote Git `v0.2.63` was absent. Docker Hub preflight timed out; no Docker
operation is in scope. Existing Android signing key remains a PrivateKeyEntry,
valid through 2053-12-03, with SHA-256
`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`, matching
the currently published Android artifact.

Production build uses `scripts/Build-Release.ps1`, followed by independent
`release/Verify-LocalRelease.ps1`. Publication uses only
`scripts/Publish-AppShipProduction.ps1` and preserves its resumable state.
The exact source commit, signed artifact and AppShip result are recorded after
completion in a documentation-only commit.

Rollback is a strictly newer Android build made from the previous known-good
implementation with the same app identity and signer. Never overwrite published
bytes, replace the signing key or uninstall/clear user data to force an upgrade.
