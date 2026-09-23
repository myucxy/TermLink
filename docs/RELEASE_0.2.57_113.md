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

Source: `26f2d6804a7ea003abfd1c6bbdfc490be5b59a79`. Both clients are published
and ready in AppShip stable; signer/hash/size and update responses were verified.

| Artifact | SHA-256 | AppShip release | AppShip artifact |
| --- | --- | --- | --- |
| Android APK (38,127,926 bytes) | `ce37d75ff56c163264778a1f5802f960b65b35508183926eefee4fe3368b3e60` | `4895aa28-026e-458f-ab48-81bd674c36d2` | `6bcb13d5-205b-4a6a-873d-106488c17900` |
| Windows Setup (181,308,136 bytes) | `a7d77c7e38abac3b4cae525c1ba8e9b116a97ee529b747342e19937ab967dd4b` | `51713407-4b15-4b1c-b5b5-55ed60c2db57` | `acbd04b1-1390-452a-a83c-62f4961249a0` |

Docker multiarch compilation completed, but Docker Hub upload repeatedly failed
with TLS errors/timeouts. No verified v0.2.57 publication is claimed. Preserve
BuildKit and encrypted resumable upload state. The Server fix in 0.2.58 supersedes
this unpublished image. See [0.2.58 live diagnosis](RELEASE_0.2.58_114.md).

Later live diagnosis found Windows already upgraded to 113 but the local Server
still lacked cleanup capability. Local Server 113 was loaded from hash-verified
BuildKit blobs and deployed for the user's authorized emulator deletion test;
real single/batch outcomes are recorded in the 114 report. Physical LAN/NAT was
not re-tested.

## Recovery

Keep AppShip state and immutable artifacts; resume interrupted uploads with
identical inputs. Never move an existing image tag. Server rollback pins the
previous verified digest; client rollback uses a newer same-signer build.
Never uninstall or clear client data. Deleted history cannot be restored by a
version rollback; use only the explicitly confirmed inactive-session list.
