# TermLink 0.2.58 (114)

Date: 2026-09-23. Scope: Android AppShip stable and Server Docker Hub.
Windows remains 0.2.57.113; no Windows source change is required.

## Diagnosis and change

- The deployed local Server lacked `host_session_cleanup_v1`; readiness was
  healthy. Android 113 and Windows 113 therefore could not perform native batches.
- Single deletion still used terminal SUBSCRIBE/termination while holding
  `refreshStateLock`. Host-only historical IDs had no Server route row, so
  subscription retries blocked directory publication before failing.
- Single deletion now uses the same native cleanup request as a one-item batch.
  Remote deletion waits do not hold the refresh publication lock. Cleanup and
  catalog responses have separate routes on the shared encrypted socket;
  request/peer/generation/epoch validation and local deletion fences remain.
- Missing capabilities show a paired-upgrade message. Generic operation errors
  no longer assert that the Server is unavailable; bounded logs contain only
  exception type, HTTP status and source location, never credentials/content.
- Machine-local history may contain IDs owned by a previous account/PC.
  The owning PC's batch mirror update now applies only to its own or missing
  rows. Other PC rows and leases remain unchanged, including concurrent insert
  conflicts. These IDs no longer block every retry of the whole batch.

## Validation

- Android Debug/Release: core-network 327 + 327; app 318 + 313; zero failures
  or skips. Includes pending cleanup concurrent with catalog, route retirement,
  upgrade reporting, encrypted route validation and active-session protection.
- Go store/api/realtime/realtimewire unit tests and vet passed. Dedicated
  PostgreSQL 17.5 cleanup integration passed, including foreign-row and foreign-
  lease preservation, owning-PC authentication, revoked devices and replay.
- A broader run with all API integrations enabled exposed existing registration
  fixture failures (`REGISTRATION_CLOSED`); this run is not claimed as passing.
  The changed cleanup integration and normal package checks were run separately.
- Emulator retained login/data via `adb install -r`, debug 0.2.58/114. A real
  single deletion created both the Windows marker and Server DELETED row.
  Device-menu batch cleanup then reported 245 deleted, 0 failed, 0 skipped;
  Windows had 246 deletion markers and no retained session directories.
  Subsequent manual refresh completed in 0.54 seconds and the device list was
  empty. This is real product traffic through the public Server, not a mock.
- The local Server was temporarily upgraded to the exact compiled 0.2.57
  revision for live validation. Its encrypted DPAPI database backup was round-
  trip verified. This local Windows backup differs from the Linux production
  runbook's age backup; it requires the current Windows user profile to restore.
- Termux reference: termux/termux-app pinned `3df69d1da197dd9bd71a3bafd902dffd720576b4`,
  `TermuxTerminalSessionActivityClient.onSessionFinished/removeFinishedSession`.
  Local UPSTREAM/patch queue/sync script/remote adapter reviewed. Lifecycle
  behavior is referenced; local PTY/process architecture and upstream code are
  not copied. TermLink's remote acknowledgement and authority checks are retained.

## Publication

Source: `61348eebb6a0a2385d611cf1b4aa3977c1467ae1`.

- Android `TermLink-Android-0.2.58-114.apk`, 38,128,546 bytes,
  SHA-256 `bb274ae2ed614afc79480b5cd06e2e3df6f1f56ef16e80b7025f1e67c1b51dfa`.
  Existing signer `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`
  independently verified. AppShip stable is published/ready, release
  `80f32117-bddb-415c-8755-11fdd37295ef`, artifact
  `5421807d-257a-4751-be09-09587520bc63`. Build 113 returns `optional`,
  build 114 returns `no_update`.
- Local Server runs **a local amd64 validation image**, compiled with pinned
  Go 1.26.2 from the above source and packaged over the verified 113 runtime.
  Image ID `sha256:e7b39563ae0e80fd9395cd55ca1cdbc4e5d6092cdff96ec017fc7556f64310ae`.
  This is not the Docker Hub multiarch release image. Both permanent containers
  are healthy and public `/readyz` returns `ready`.
- After the 114 Server fix, Windows heartbeat retries converged: all 208
  current-PC mirror records are DELETED; its active/retained count is zero.
  The 38 records belonging to old PC/account identities remained unchanged.
  This proves the foreign-identity entries no longer block current-PC cleanup.
- Final manual emulator refresh completed in 0.41 seconds; crash buffer empty.
  Evidence: `artifacts/validation/cleanup-*`, `release-114-*`, encrypted backups
  in `artifacts/backups/` (requires this Windows user's DPAPI identity).
- **Docker Hub publication remains blocked** by repeated TLS/connection
  failures. Official 114 multiarch BuildKit build also stalls downloading Go
  modules; no successful official multiarch build/push/attestation is claimed.
  Existing published images were not moved or overwritten. Preserve cache and
  encrypted upload state for resumption; 114 supersedes the unverified 113 image.
- Physical LAN/NAT, which this change does not modify, was not re-tested.
