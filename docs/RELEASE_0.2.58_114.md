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

Exact artifact IDs, hashes, source revision and deployment evidence follow
verification. Do not reuse 113 artifacts for this fix. Keep existing signatures,
AppShip state and failed Docker upload state. Docker 0.2.57 upload had repeated
TLS/timeout failures; the corrected Server is to supersede that unpublished tag.
Physical LAN/NAT, which this change does not modify, was not re-tested.
