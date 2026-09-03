# TermLink 0.2.32 (90) release record

Date: 2026-09-04 (Asia/Shanghai)

## Source identity

- Release source commit: `482e87d921e27dd50fcde0969fcffb7b385ba4c9`.
- `origin/main` matched the release source commit before the Production build.
- Android and Windows were built from one Production manifest. The server was
  deployed separately from the same source revision as a local Production image.

## Stable Windows PC identity

- Windows derives a versioned SHA-256 machine identifier from `MachineGuid`, the
  normalized OIDC issuer, and the account binding key. The raw `MachineGuid` is
  neither uploaded nor persisted by TermLink.
- `machine_id` is used only to find the existing PC record for the same account.
  The independently generated device ID and device key remain the authentication,
  E2EE, authorization, and revocation identity.
- Normal sign-out and in-place reinstall retain the token-free, DPAPI-protected
  device identity store. If that store is lost, the server reuses the existing PC
  record, rotates the device binding, revokes the old device, and increments the
  key epoch without deleting sessions, policies, or database history.
- The server uses an epoch compare-and-swap for concurrent rebind attempts and
  maps stable-machine uniqueness conflicts to HTTP 409. V3 PC upsert idempotency
  includes the device ID so a cached response for an old device cannot authorize
  a replacement device.
- Android accepts a replacement device ID and public key only with a strictly
  increasing epoch, keeps the transition atomic, and rejects stale catalog
  rollback before it can close a live session.
- Existing duplicate PC rows are intentionally not merged or deleted because they
  can carry independent history, sessions, and policy. Future enrollment uses the
  stable per-account machine identity.
- The E2EE boundary is unchanged: if a device private key is actually destroyed,
  a stable machine identifier cannot recover ciphertext encrypted only for that
  lost key.

## Verification

- Android `testDebugUnitTest`, Room device-rebind and catalog key-transition
  coverage: passed.
- Server `go test ./...`, PostgreSQL + Valkey stable-ID API integration, and ten
  consecutive concurrent-rebind PostgreSQL runs: passed.
- Windows Agent and PC Client Release solution builds: passed with zero warnings
  and zero errors.
- `AgentUpdateValidation`, `ProductionPcHeartbeatValidation`, Avalonia headless
  tests, and `git diff --check`: passed.
- The Production artifacts passed an independent post-build verification of
  manifest hashes, package versions, Android signer continuity, Windows
  Authenticode signatures, all 17 executable Setup payloads, and the RFC 3161
  timestamp.
- The Android package was upgraded in place on `emulator-5558` from `0.2.31 (89)`
  to `0.2.32 (90)`. Its first-install time remained `2026-08-29 18:14:23`, its
  account remained signed in, and HOME-PC appeared online as Agent `0.2.32.90`.
  No TL error, crash, or ANR appeared in the post-launch check.
- The Windows package was upgraded in place from `0.2.30.88` to `0.2.32.90` under
  `D:\Dev\Tool\TermLink`. The Agent remained account-linked and connected with
  `RelayState=2`, and retained PC ID
  `21ed44f8-49a8-4561-a061-23100a87345f` across the upgrade.
- The Production database retained six PC rows across deployment and client
  installation. No duplicate `(owner_user_id, machine_id_hash)` group exists.
- Termux reference: upstream `termux/termux-app` was reviewed at
  `3b66f8799635a4dba4a206563048ff0e6792c487`; the checked-in fork remains pinned
  by `terminal-forks/termux-terminal/UPSTREAM.md`. Termux has no remote PC
  rebind/E2EE epoch equivalent, so it was used only as a behavior boundary and no
  Termux code was copied for this feature.

## Production artifacts

- Manifest: `artifacts/release/0.2.32-90/artifact-manifest.json`.
- Manifest SHA-256:
  `a3eb9fc737620de2a33f54a7ef2ab9046dd1740036ec403f6605fd1c8b796061`.
- Android artifact: `TermLink-Android-0.2.32-90.apk`, 36,271,328 bytes.
- Android SHA-256:
  `672ae51c41968a8ec4df7a3c2f2634e22c45a740a45120c4498a476d518c904b`.
- Android application/version: `dev.termlink.harness`, `0.2.32 (90)`; APK
  signature scheme v2.
- Android signer SHA-256:
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Windows artifact: `TermLink-Setup-0.2.32.90-x64.exe`, 256,752,816 bytes.
- Windows SHA-256:
  `20a075ecf844edfb5f8c80779e1856d134140f2023ead9d5bc14999feded143f`.
- Windows ProductVersion:
  `0.2.32.90+482e87d921e27dd50fcde0969fcffb7b385ba4c9`.
- Windows signer SHA-1:
  `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`; signature status valid and RFC
  3161 timestamp present.

## AppShip

- Android application: `termlink-android`, channel `stable`.
- Android application ID: `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`.
- Android release ID: `10fd7fbb-1d10-4de4-8a3c-0e38534586f2`.
- Android artifact ID: `b93de994-32cc-4ff1-b998-8fcdca42dbbd`.
- Android resumable state:
  `.appship/termlink-android-stable-0.2.32-build90-signature-approved.json`.
- Windows application: `termlink-win64`, channel `stable`.
- Windows application ID: `0ae760b3-294e-48e8-a793-95d162f685e2`.
- Windows release ID: `21556ab4-a857-4b86-906d-60db911357f7`.
- Windows artifact ID: `633991a3-d1f0-43a0-8725-7e20eae3c082`.
- Windows resumable state:
  `.appship/termlink-win64-stable-0.2.32-build90-signature-approved.json`.
- Both releases are `published` and both artifacts are `ready`. The public
  update API returns `optional` with the ready build 90 artifact for build 89,
  and `no_update` for build 90, on both applications.

## Server deployment

- Image: `myucxy/termlink:0.2.32-local-production`.
- Image ID:
  `sha256:8fe0bd96ad433dcab485910ea402c254e1ccab68d48da297bc51c9290778b021`.
- Schema migration: 37 to 38.
- The seven Production services are healthy and
  `https://termlink.kget.cc:8910/readyz` returns `ready`.
- Existing users, PC rows, sessions, and management configuration were preserved.
