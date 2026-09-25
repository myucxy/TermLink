# TermLink 0.2.64 / build 120

## Scope

Windows x64 Setup and Android universal APK, AppShip stable. Both clients contain
the shared terminal-core fix for TL-H122 when a session emits `DECSET 1007` on
entering the alternate screen. The core consumes this frontend scroll hint and
continues rendering/replaying; it does not add wheel-to-arrow input behavior.
Existing display, input, Snapshot formats, E2EE and ACK rules remain unchanged.
Both clients should upgrade. Server does not require an update for this fix.

Android also permits screenshots, screen recordings and recents thumbnails on
the product and embedded sign-in activities. The Secure screen setting and its
stored preference are no longer used. Android's production package continues to
exclude Termux runtime modules.

This release publishes client packages; it does not replace the local Windows,
emulator or Server installation. No Docker image, Git release tag or GitHub
Release is part of this publication.

## Evidence and gates

See [H122 investigation and regression record](BUGFIX_H122_ALTERNATE_SCROLL.md).
Termux behavior was checked against pinned commit
`3df69d1da197dd9bd71a3bafd902dffd720576b4`; no upstream source was copied.

Completed implementation checks:

- Rust: 462 core, 10 golden-stream and 48 FFI tests; Clippy passed.
- Android emulator: 16 real JNI tests, including the 1007/Termux differential
  check and snapshot roundtrip. All four JNI ABIs built successfully.
- Android app: Release compilation and 315 JVM tests after screenshot removal.
- Windows: production history WAL/producer/outbox/uploader validation, including
  rebuilding a previously rejected checkpoint after restarting with the new core.
- The actual HOME-PC incident's 24 WAL events replayed successfully in an
  isolated process using the new core. Snapshot export/import retained next
  sequence 25; the original history and installed client were untouched.
- Windows Setup prerequisites/ownership validation and the isolated Setup
  install/upgrade/uninstall lifecycle passed, with
  evidence saved as `artifacts/validation/release-120-setup-lifecycle.json`.
- Two existing test-only formatting differences in `input.rs` were normalized;
  full workspace rustfmt now passes. No input behavior changed.

Live preflight: Android stable 0.2.63/119 (next build 120), Windows stable
0.2.62/118 (next build 119). Authenticated release defaults confirmed that 120 is
available for both. Remote Git has no v0.2.64 tag. Docker Hub reports latest and
v0.2.61; no image operation is required.

Existing signer identities remain mandatory:

- Android SHA-256: `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Windows SHA-1: `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`, valid through
  2036-08-04; use RFC 3161 timestamping. This is the existing development
  Authenticode certificate, not a public CA certificate.

Production builds use `scripts/Build-Release.ps1`; independent verification uses
`release/Verify-LocalRelease.ps1`. Uploads use only
`scripts/Publish-AppShipProduction.ps1`, preserving the per-platform resumable
state in `.appship/`. Source revision, artifact hashes and publication IDs are
recorded below after the final gates finish.

Rollback uses a strictly newer build with the same app identities and signers.
Do not overwrite published artifacts or uninstall/clear user data to force an
upgrade. Returning to the previous core also restores its 1007 limitation.
