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

Source commit: `994ed74bafb6799a9745fbe8329890c30d47a4b2` (pushed to
`origin/main` before the production build). Production APK verification passed:

- `TermLink-Android-0.2.56-112.apk`
- 38,125,926 bytes
- SHA-256 `3e619e65af8001bc606c7e10f579b8020e09adf3702c07340899f6ea769481e0`
- APK Signature Scheme v2, one signer

Expected Android signer SHA-256:
`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.

AppShip stable publication succeeded:

- application `termlink-android`
- release `61729adf-a3b8-4cdd-8a43-5a14bf9474c2`
- artifact `a078720c-7d37-4d03-a315-002e0c98382e`
- artifact status `ready`, release status `published`

The public update API returns `optional` for build 111 and `no_update` for
build 112. Docker and Windows were not rebuilt or published for this Android
only change.

## Recovery

Preserve the existing stable release, new artifact and resumable AppShip state.
Do not overwrite a published build, uninstall Android or clear app data.
If a defect requires correction, publish a higher build through the same
signer and wrapper. Debug emulator packages cannot replace Production APKs.
