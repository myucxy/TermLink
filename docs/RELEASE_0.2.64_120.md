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
recorded below.

Rollback uses a strictly newer build with the same app identities and signers.
Do not overwrite published artifacts or uninstall/clear user data to force an
upgrade. Returning to the previous core also restores its 1007 limitation.

## Publication record

Both clients were published to AppShip stable at `https://appship.kget.cc:8910`
on 2026-09-25. The binary source revision is
`69ec9392b21f2ffc383e63247650528619a0c431`; the subsequent documentation commit
only records publication results and does not change the published artifacts.
Windows ProductVersion is
`0.2.64.120+69ec9392b21f2ffc383e63247650528619a0c431`.

Artifacts are retained under `artifacts/release/0.2.64-120/`, alongside
`artifact-manifest.json`:

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `TermLink-Android-0.2.64-120.apk` | 38039678 | `619406d3abd8b346f61cf04d6e11397b6a0fc27a3b1f820b47fa1bbb20c37464` |
| `TermLink-Setup-0.2.64.120-x64.exe` | 182468832 | `0fc931324b2efc04867b8b97e2496df1794450e1783d60b80400fff74d29ddc2` |

| AppShip field | Windows | Android |
| --- | --- | --- |
| Slug | `termlink-win64` | `termlink-android` |
| Application ID | `0ae760b3-294e-48e8-a793-95d162f685e2` | `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2` |
| Release ID | `78c16893-b30b-48fd-aa32-327806529c0b` | `f81934c4-58c8-4047-a85e-74cce5955927` |
| Artifact ID | `b38172c9-ab5f-4c07-b485-ed2e3681ce03` | `5abfea31-89b5-45bf-8bae-ee70c57376de` |
| Published at (UTC) | `2026-09-25T02:45:07.57649Z` | `2026-09-25T02:45:48.419457Z` |

Both releases are `published`; their artifacts are `ready`, `manual_approved`
and download-enabled. The published signer fingerprints match the identities
above. Android's certificate is valid through 2053-12-03. Windows Setup and
payload signature checks passed with the existing development certificate and
Microsoft RFC 3161 timestamps.

The public stable update endpoints were checked for each app: builds 118 and
119 return `optional` with latest version 0.2.64/build 120; build 120 returns
`no_update`. Download metadata matches the local artifact sizes, SHA-256 hashes
and signer identities. Sanitized evidence is retained in
`artifacts/validation/release-120-appship-public.json`.

Final production-package checks also passed:

- Production build and independent `Verify-LocalRelease.ps1` gates, including
  versions and signatures: `artifacts/validation/release-120-gates.json` and
  `artifacts/validation/release-120-production-build.log`.
- The core DLL extracted from the signed Windows package executed the 1007
  sequence and snapshot roundtrip successfully:
  `artifacts/validation/release-120-packaged-core.json`.
- The final Android APK contains no Termux DEX descriptors. Its four ABI JNI
  libraries match the tested instrumentation APK libraries byte-for-byte after
  Gradle symbol stripping:
  `artifacts/validation/release-120-apk-content.json`.
- Publication logs: `artifacts/validation/release-120-appship-windows.log` and
  `artifacts/validation/release-120-appship-android.log`.

The installed local Windows client, emulator product app and Server were not
upgraded by this publication. No Docker image, Git release tag or GitHub Release
was created.
