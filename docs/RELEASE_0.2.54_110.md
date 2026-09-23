# TermLink 0.2.54 (110)

Date: 2026-09-23 (Asia/Shanghai)

## Scope

- Android and Windows AppShip stable; Server image on Docker Hub only.
- Live AppShip preflight: Android 0.2.53 (109), Windows 0.2.52 (108).
  Docker Hub's newest version was v0.2.47; v0.2.54 was absent before push.
- Production source: `5dadf96498f665bbf12f4afa0317fa633613b250`, committed and
  pushed before building, with a clean worktree and HEAD equal to origin/main.
- Windows supplies the authenticated E2EE session catalog and process state.
  Android hides content when its host is offline or the catalog is unavailable,
  and its session page contains only locally opened tabs.
- Server stores a statistical/routing snapshot and retires omitted RUNNING rows.
- Transport policy now distinguishes loading, failure and unavailable support;
  WebSocket capability validation uses that connection's original HELLO.
- Upgrade order: Server, Windows, Android. Older peers fail closed; there is no
  fallback to a stale server catalog. No schema migration or history deletion.

## Verification

- Go realtime, realtimewire, API and store tests and vet passed.
- Windows Release solution build: zero warnings/errors. PC heartbeat,
  session fault isolation and Windows Setup validations passed.
- Android core-network Debug/Release and App Debug/Release unit tests passed:
  325 + 325 + 313 + 308 = 1,271 tests, zero failures/errors/skips.
- Prior isolated PostgreSQL and emulator smoke results are recorded in
  HOST_SESSION_CATALOG.md. Authenticated product/offline recovery and physical
  LAN/NAT acceptance are not established by these unit tests.
- Signing preflight matches Android SHA-256
  f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b and Windows SHA-1
  91AE60BC06D866628B8EB2C62F32BDE9DAB56466 (valid until 2036-08-04).

## Publication

- Production build and independent `release/Verify-LocalRelease.ps1` passed.
  Android APK identity/version, unique v2 signer, Windows ProductVersion/revision,
  Authenticode, RFC 3161 timestamp and signed payload validation passed.
- Android: `TermLink-Android-0.2.54-110.apk`, 38,106,710 bytes, SHA-256
  `d16fe94f4b19e5e8c2bc5851a55e527cbbcc51730cf90d843e133b97fafde3aa`.
- Windows: `TermLink-Setup-0.2.54.110-x64.exe`, 181,301,984 bytes, SHA-256
  `d76d70c5db9352d5b8b74c194a7bb6291fe7f82edd4dfacf700ad1fcbf4658f6`.
- AppShip `termlink-android/stable`: release
  `a83816f3-526e-4d71-92ac-554d7c90f8d6`, artifact
  `20648538-6d28-4f80-81f6-7af4ff3209d4`, published/ready.
- AppShip `termlink-win64/stable`: release
  `55c53807-dbb9-4dca-89f2-a1db6174bd72`, artifact
  `3d5c72c8-2366-4f28-acec-6b36ffefa02c`, published/ready.
- Public update checks: Android build 109 and Windows build 108 return optional;
  build 110 returns no_update on both. Public artifact hashes, sizes and signer
  fingerprints match the verified local manifest. Persistent state is under
  `.appship/termlink-{android,win64}-stable-0.2.54-build110-signature-approved.json`.
- Docker Hub: `docker.io/myucxy/termlink:v0.2.54`, OCI index digest
  `sha256:a5858187e38d9fbf7613242bd7f92f10537ed38d3d367e5d88af0e48e243a82f`.
  Linux amd64 manifest:
  `sha256:e8b193c949b2e3c86e95fbfce76b3386f48c925643643f2583c662ca238e3e90`.
  Linux arm64 manifest:
  `sha256:4be3ab9555e854d9afe9371b0522667f718fa9e1f6dd2841c6c67006a29f8aa4`.
- Registry reads independently confirmed tag/index digest, both platform
  manifests, exact version/revision/source/created labels, and SPDX/SLSA
  attestation descriptors for both platforms.
- Docker used the documented local BuildKit fallback after GitHub private
  Actions credentials could not be inspected. The temporary BuildKit image was
  pinned to `sha256:28a898719c18a33f4e8000685287fa36fd0dd9560c6440227d3a732d79bb41d8`.
  Existing local proxy access recovered Docker Hub without system network changes.
  This is a degraded publication: no GitHub OIDC/Cosign signature and no separate
  CycloneDX attestation. No GitHub Release, GHCR image or Git tag was published.
- Evidence: `artifacts/validation/release-110-{build,docker-build}.log`,
  `release-110-updates.json`, `release-110-image-metadata.json`, and
  `release-110-image-verification.json`.
- This turn published artifacts only. Local Server and client installations were
  not upgraded. Authenticated deployment/reconnect and physical LAN/NAT acceptance
  remain pending; upgrade Server, then Windows, then Android before field testing.

## Recovery

Keep immutable artifacts, tags and AppShip state. Resume interrupted uploads
with identical inputs. Client rollback uses a newer same-signer build; never
uninstall or clear application data. Server rollback pins the previously
verified image digest; do not move an existing tag.
