# TermLink 0.2.57 (113)

Date: 2026-09-23 (Asia/Shanghai)

## Scope and behavior

- Android and Windows AppShip stable; Server Docker Hub only.
- Preflight: Android 0.2.56/112, Windows 0.2.55/111, Docker Hub v0.2.54;
  0.2.57/113 and v0.2.57 were unused. No new Git tag or GitHub image is needed.
- Native Windows cleanup replaces serial per-session terminal setup. Android
  sends fixed confirmed IDs in batches of 64 over the encrypted Server bridge.
  Windows checks current process identity, preserves active sessions, writes
  durable tombstones and returns per-ID outcomes. No batch process termination.
- Server requires MANAGE for forwarding and the owning PC for atomic mirror
  synchronization. Missing rows can be tombstoned; foreign IDs reject the entire
  batch. Windows retries failed mirror writes from durable deletion markers.
- Cancellation settles the current batch; failed/unknown requests stop later
  batches. Old peers show an upgrade message; there is no slow serial fallback.
- Upgrade order: Server, Windows, Android. No schema migration. Architecture
  and Termux behavior references: [HOST_SESSION_CATALOG.md](HOST_SESSION_CATALOG.md).

## Validation before production build

- Android Debug/Release tests: core-network 326 + 326, app 317 + 312; all 1,281
  passed with no failures/skips. Includes real encrypted WebSocket round trips,
  wrong peer/epoch/request replay, missing capability, exact result-ID matching,
  endpoint isolation, four batches for 245 sessions and in-flight cancellation.
- Windows Release solution build: zero warnings/errors. Session fault isolation,
  PC heartbeat and Windows Setup prerequisite/ownership validations passed.
  Native cleanup test used actual temporary history and registry stores:
  245 sessions, four requests, 1.6–1.9 seconds with an isolated local HTTP mirror.
  This is not an internet/phone latency measurement. Live process protection,
  invalid-batch atomic validation, replay, delayed registration rejection,
  mirror retry and durable restart markers passed.
- Go realtime/realtimewire/api/store tests and vet passed. Isolated PostgreSQL
  17.5 validated VIEW/CONTROL rejection, MANAGE, wrong/revoked PC, all-or-nothing
  foreign-ID rejection, missing rows, repeated deletion, stale snapshots and
  lease removal. Production data was not used.
- Emulator upgraded in place to debug 0.2.57/113, retaining appId 10224 and
  firstInstallTime 2026-09-19 10:16:29. Logged-in device page and 245-entry menu
  worked; selecting cleanup against old Windows showed the upgrade message.
  No crash-buffer entries. User history was not deleted for acceptance.
- Android signer SHA-256:
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
  Windows signer SHA-1: `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`,
  currently valid through 2036-08-04. Signing preflight passed.
- Evidence is in ignored `artifacts/validation/native-cleanup-*` and
  `release-113-*`. The release plan selects Production APK and x64 Setup.

## Publication and deployment

Production build/publication results and exact source revision will be recorded
after verification. AppShip uses the resumable repository wrapper and existing
signers. Docker uses the documented pinned local BuildKit fallback if GitHub
Actions credentials cannot be inspected, with SPDX/SLSA evidence; that path
lacks GitHub OIDC/Cosign and an independent CycloneDX attestation.

This request publishes packages; local Server/Windows deployment is separate.
Actual native deletion from the installed Android through the deployed Server
to an upgraded Windows, and physical LAN/NAT regression, remain field checks
after the coordinated upgrade. Unit tests and the old-peer emulator check do
not establish that live acceptance.

## Recovery

Keep AppShip state and immutable artifacts; resume interrupted uploads with
identical inputs. Never move an existing image tag. Server rollback pins the
previous verified digest; client rollback uses a newer same-signer build.
Never uninstall or clear client data. Deleted history cannot be restored by a
version rollback; use only the explicitly confirmed inactive-session list.
