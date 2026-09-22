# TermLink 0.2.46 (102) release record

Date: 2026-09-22 (Asia/Shanghai)

## Source identity

- Release source commit: pending until the reviewed source and this initial record are committed.
- Target Git tag and Docker version: `v0.2.46` / `v0.2.46`.
- Target clients: Android `0.2.46 (102)` and Windows `0.2.46.102`.
- AppShip state before release: Android `0.2.44 (100)` and Windows `0.2.42 (98)`;
  build 102 is strictly newer than both and the locally installed Windows build 101.

## Changes

- Production Android and Windows builds enable Direct signalling and the Direct data plane.
- A missing server routing preference defaults to automatic LAN/NAT Direct preference with Relay
  kept as the fallback. An explicitly selected Relay-only preference remains authoritative.
- Android terminal input uses the shared native `terminal-core` path for hardware functional keys,
  modifiers, repeat/release events, dead keys, IME composition, and forward/backward deletion.
- Android session reconciliation and file transfer failure handling preserve existing live state and
  return actionable errors instead of terminating the UI flow.
- The Avalonia Windows client checks AppShip directly and accepts an update only after HTTPS,
  bounded size, SHA-256, Authenticode, embedded trust-anchor, and signer-pin validation.
- The removed legacy WinForms executable is not restored.

## Verification before the release build

- Android `:app:testReleaseUnitTest`, `:app:testDebugUnitTest`,
  `:core-network:testDebugUnitTest`, and `:terminal-engine-native:testDebugUnitTest`: passed.
- Android emulator instrumentation: 13/13 passed on `emulator-5554`.
- Windows `TermLink.Windows.sln` Release build: passed with zero warnings and zero errors.
- Production Direct Release, DirectAcceptance, native ABI v3/v4/v5, Direct native packaging,
  Avalonia Windows adapter, and isolated Setup install/upgrade/uninstall validations: passed.
- Rust network/terminal workspace tests: passed; only existing dead-code/linker warnings remained.
- Direct fault-model suite: 16/16 passed.
- Server `go test ./...`, isolated PostgreSQL Direct routing integration, and deployment package
  tests: passed.
- `git diff --check`: passed; only Git line-ending conversion notices were reported.

## Known validation boundary

- Real physical LAN and cross-NAT production socket coverage is `0`; it is not represented as an
  on-device pass. Relay-only remains the operational rollback switch if field Direct validation
  exposes a problem.
- The emulator still requires an in-place same-revision debug APK because its installed package is
  signed with the debug signer. It must not be uninstalled to bypass signer continuity.

## Termux reference boundary

- The fixed reference is `termux/termux-app@3df69d1da197dd9bd71a3bafd902dffd720576b4`
  through `terminal-forks/termux-terminal/UPSTREAM.md`, plus the checked-in patch queue and
  `android/terminal-engine-termux/` boundary.
- Termux is used as terminal-input behavior reference only. No local PTY, shell, Bootstrap/APT,
  plugin-signing, or local-process architecture is copied into TermLink's remote ConPTY/E2EE flow.

## Production artifacts and publication

- Production manifest, file hashes, sizes, signer/timestamp evidence: pending Production build.
- Android AppShip release/artifact IDs and update decisions: pending publication.
- Windows AppShip release/artifact IDs and update decisions: pending publication.
- Docker OCI index digest, amd64/arm64 manifests, Cosign signature, provenance and SBOM
  attestations: pending tagged workflow.
- Local Docker deployment health, migration, volume preservation and routing default: pending
  non-destructive upgrade.
