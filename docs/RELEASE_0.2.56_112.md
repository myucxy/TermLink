# TermLink Android 0.2.56 (112)

Date: 2026-09-23 (Asia/Shanghai)

## Scope and validation

- Android only: device long-press action to close all inactive sessions, with
  count, explicit permanent-history-deletion confirmation, progress, cancel
  and partial results. Running sessions and unavailable host catalogs do not
  qualify. Each deletion checks a fresh Windows catalog and reuses the existing
  encrypted host acknowledgement, server mirror deletion and local cleanup.
- Windows and Server have no code changes and require no release for this action.
- Live preflight: Android stable 0.2.54/110; Windows stable 0.2.55/111;
  latest versioned Docker Hub image v0.2.54. Remote v0.2.56 tag absent.
- Android app: 316 unit tests, zero failures/errors/skips; debug APK built.
  Regression tests cover eligibility, stale state, fixed confirmation UUIDs,
  partial failure, request timeout, cancellation and deletion ordering.
- Emulator-5554: same-signer debug overlay retained login data; English and
  Chinese menus showed 245 inactive HOME-PC sessions. Matching confirmation
  and cancel were verified; no crash-buffer entries. Existing user histories
  were not bulk-deleted during UI verification. Live bulk deletion remains
  unexercised; unit tests do not constitute that acceptance.
- Termux references and adaptation boundaries are recorded in
  [HOST_SESSION_CATALOG.md](HOST_SESSION_CATALOG.md).

## Release record

Production build and publication are pending. Record the exact committed source,
APK hash/size/signer, verification results and AppShip identifiers after success.
Expected Android signer SHA-256:
`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.

## Recovery

Preserve the existing stable release, new artifact and resumable AppShip state.
Do not overwrite a published build, uninstall Android or clear app data.
If a defect requires correction, publish a higher build through the same
signer and wrapper. Debug emulator packages cannot replace Production APKs.
