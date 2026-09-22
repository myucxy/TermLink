# TermLink 0.2.47 (103) release record

Date: 2026-09-22 (Asia/Shanghai)

## Source identity and scope

- Release source commit: `3d794d75aff91912208b4ff3cefe36e31233c22f`; the Production clients and
  Server image were built from this commit after `HEAD` and `origin/main` matched with a clean worktree.
- Target clients: Android `0.2.47 (103)` and Windows `0.2.47.103` on AppShip stable.
- Target Server image: `docker.io/myucxy/termlink:v0.2.47`, published for `linux/amd64` and `linux/arm64`.
- Push immutable Git tag `v0.2.47` only to trigger the reviewed Docker Hub workflow. Do not create a GitHub
  Release and do not publish to GHCR/GitHub Packages.
- AppShip stable before release: Android and Windows are both `0.2.46 (102)`; build 103 is strictly newer.

## Changes

- Android and Windows accept the Server's confirmed renewable Direct ticket lifetime of up to 20 minutes.
- Candidate lifetime passed to native Direct gathering is capped at 60 seconds on both clients, within the
  native protocol's two-minute ceiling.
- Android Direct gathering binds its UDP socket to the active Android Network's concrete local address instead
  of the rejected unspecified address `0.0.0.0`.
- Android Direct ticket verification reuses the existing BouncyCastle Ed25519 implementation so verification
  behaves consistently on supported Android runtimes.
- Android can emit opt-in, redacted Direct diagnostics through `TermLinkDirectShadow`; the diagnostics contain
  stages and native reason/status codes only, not addresses, ticket contents, IDs, tokens, or keys.
- Android declares its composing `InputConnection` as a full editor, matching Termux's `TerminalView`, so one
  IME composition commit produces one terminal input while intentional repeated commits remain distinct.
- Android Debug now enables the authoritative Snapshot V2 runtime by default, matching Release. This prevents a
  positive durable ACK from being stranded at `TL-H114` while the Windows host still has a valid Snapshot and WAL.
- Android no longer calls the retired Server history catalog, snapshot, transcript, or object-download paths.
  Snapshot/WAL/transcript restore now requires `host_history_v1` from the online Windows Agent; older Agents fail
  with an explicit `TL-H125` Windows upgrade requirement instead of silently falling back to Server-owned history.
- Server migration verification accepts the canonical migration 49 checksum while retaining the existing
  compatibility checksum for databases created by the earlier migration text.

## Verification before the release build

- Android `:app:testReleaseUnitTest`, `:app:testDebugUnitTest`, `:core-crypto:testDebugUnitTest`,
  `:core-network:testDebugUnitTest`, and `:terminal-engine-native:testDebugUnitTest`: passed.
- Android emulator instrumentation passed on Android 16: Direct/network 8/8 and native terminal/JNI 13/13.
- The native terminal regression reproducing one `setComposingText("1")` plus `commitText("1")` as three native
  submissions fails before the fix and passes with exactly one submission after it. A second independent commit
  still produces a second input, so intentional repeated characters are not suppressed.
- Android `:terminal-engine-native:testDebugUnitTest`, `:app:testDebugUnitTest`, and
  `:app:testReleaseUnitTest` passed after the input fix.
- Debug policy regression, full `:app:testDebugUnitTest`, and full `:app:testReleaseUnitTest` passed after the
  Snapshot V2 default fix.
- Full Android Debug/Release unit tests and Debug APK assembly passed after removing the Server history fallback.
- Windows `TermLink.Windows.sln` Release build: passed with zero warnings and zero errors.
- Windows Setup prerequisite/ownership validation and `ProductionDirectTransportValidation`: passed.
- Direct fault-model suite: 16/16 passed.
- Server `go test ./...`: passed.
- Unified release parser, plan, signature, version, RFC 3161, production-manifest, AppShip gate, and strict-path
  checks: passed.
- Production build, independent artifact verification, and AppShip publication: passed.

## Live validation boundary

- With Android, `HOME-PC`, and `HT-LPC-1326` all on `0.2.47 (103)`, fresh sessions to both Windows Agents
  remained on Relay. No authoritative `LAN_DIRECT` or `NAT_DIRECT` carrier was observed; Relay stayed usable.
- Android repeatedly reached Direct `GATHERING` and `NEGOTIATING`, but every attempt retained
  `selected_pair_rank=0` and returned to `RELAY_ONLY`. The Server allocated new path epochs only for the
  Android/`HT-LPC-1326` pair; no Android/`HOME-PC` epoch was created. After enabling the existing redacted
  Windows diagnostic and restarting only the local client, `HOME-PC` reported `DIRECT_NO_AUTHORIZED_PEER`
  during initial authority warm-up and then repeated `DIRECT_OFFER_TIMEOUT`.
- Source and timing evidence identified a client scheduling defect: the two-second session-catalog poll calls
  `refreshDirectAuthorizedPeers` even when peer authority is unchanged, and `replaceAuthorizedPeers` always
  resets `peerSelectionCursor` to zero and retires the active attempt. Because `HT-LPC-1326` sorts first by
  device ID, it is repeatedly selected while later peers, including `HOME-PC`, are starved. LAN/NAT validation
  is blocked until unchanged peer refreshes become idempotent and a newer Android build is field-tested.
- `GATHERING`, `NEGOTIATING`, ticket allocation, and a working Relay session are not Direct carrier passes.
  The emulator also runs behind the Android emulator NAT, so it cannot provide physical same-Wi-Fi LAN proof.
- Relay remains the stable fallback and the Server's Relay-only strategy remains the operational rollback.
- The emulator's debug-signed app was upgraded in place from `0.2.46 (102)` to `0.2.47 (103)` without changing
  `appId=10224` or `firstInstallTime=2026-09-19 10:16:29`; the retained logged-in session opened without a crash.
  The first Debug build incorrectly disabled Snapshot V2 by default and therefore reported `TL-H114` without
  requesting the available Windows snapshot. After the shared build-policy fix, the same installed app and
  session recovered its bound Snapshot at sequence 7, requested the encrypted Windows-owned event range 8..35,
  reached `Controlling`, and retained the Relay fallback. The Server session reported `snapshot_status=READY` and
  `last_output_seq=34`; Windows retained a contiguous local WAL 1..35.
- After removing the Server history fallback, the same debug-signed emulator app was upgraded in place: `appId=10224`,
  `firstInstallTime=2026-09-19 10:16:29`, login and open tab remained intact, and the session showed `Controlling`
  with Relay. This is a local Debug smoke test, not a published Production or physical LAN/NAT pass.

## Termux reference boundary

- Reviewed `termux/termux-app@3df69d1da197dd9bd71a3bafd902dffd720576b4`, including
  `terminal-view/.../TerminalView.java` and its full-editor `BaseInputConnection`,
  `terminal-forks/termux-terminal/UPSTREAM.md`, the checked-in Termux patch queue, and
  `android/terminal-engine-termux/`.
- Also reviewed `termux/termux-app#686` (Samsung keyboard characters not echoed). Termux's full-editor connection
  can be reused as Android IME behavior, but TermLink keeps its own native terminal/E2EE/remote transport path.
- Reviewed `termux/termux-app#3709` and `#5259` for local session persistence and process lifecycle. They have no
  remote Snapshot/ACK equivalent; the TL-H114 fix therefore changes only TermLink's build policy and reuses its
  existing Windows-owned, recipient-bound E2EE history protocol.
- Termux has no equivalent TermLink ticket, NAT, or remote transport implementation. It is behavior reference
  only; no local PTY, shell, Bootstrap/APT, plugin-signing, or local-process architecture was copied.

## Production artifacts and publication

- Android APK: `TermLink-Android-0.2.47-103.apk`, SHA-256
  `2b08bfb45a5d31cf7e3853b9f86333fceac50cab363e92f33156075b5fe0f5a4`; version `0.2.47 (103)`, APK v2,
  single signer SHA-256 `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Windows Setup: `TermLink-Setup-0.2.47.103-x64.exe`, SHA-256
  `1ef7549215dfd360012eb46551a05cdbf03d0cfd9aca76aa24a8518b75521ae3`; ProductVersion
  `0.2.47.103+3d794d75aff91912208b4ff3cefe36e31233c22f`, signer SHA-1
  `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`, valid RFC 3161 timestamp, and all 17 payload binaries verified.
- Android AppShip `termlink-android/stable`: application `4759ec79-a6a2-45d1-9eb9-7ea16b2a3fb2`, release
  `4ba4b9b8-6175-4efe-b833-be97d7c76519`, artifact `cac10648-acea-461a-a55b-8acaf0c08471`, `published/ready`.
- Windows AppShip `termlink-win64/stable`: application `0ae760b3-294e-48e8-a793-95d162f685e2`, release
  `4699888c-8d5f-419d-8fe7-3d3d2a19a60c`, artifact `21154676-03ef-4cb4-af8a-3baff1fd5b5a`, `published/ready`.
- Public update checks passed: both clients return `optional` for build 102 and `no_update` for build 103.
- Git tag `v0.2.47` is immutable and resolves to the release source commit. The preferred private GitHub workflow
  did not produce a Docker Hub tag during the observed release window, so the documented pinned-BuildKit fallback
  published `docker.io/myucxy/termlink:v0.2.47` with OCI index digest
  `sha256:67aac2fd50d24f3466028cabdbe8ca1bc82c43acccfa7fd134c3265f2fafd125`.
- Registry verification passed for `linux/amd64` manifest
  `sha256:6fb27acad124ba5c8013810fa63418723810b1e0dbd101007cd21925cd6790d5` and `linux/arm64` manifest
  `sha256:93caf34083be775cac24ae462239210901b22745d7e132db7dd9dcf84e52bcd3`; both carry the exact release
  version, revision, source and created labels, plus SPDX and SLSA provenance attestations. This is a degraded
  publication: it has no GitHub OIDC/Cosign signature and no independent CycloneDX attestation.
- The Android Debug app was covered in place with the same source/version; `appId=10224` and
  `firstInstallTime=2026-09-19 10:16:29` were preserved, and its cold start produced no AndroidRuntime fatal error.
- Local Windows was upgraded in place under `D:\Dev\Tool\TermLink`; the main app, Agent, SessionHost and Updater
  all report `0.2.47.103+3d794d75aff91912208b4ff3cefe36e31233c22f` with valid signatures. Remote
  `HT-LPC-1326` update acceptance remains pending; AppShip publication does not imply silent installation.

## Local Server deployment

- Before upgrade, a PostgreSQL custom dump was encrypted with Windows DPAPI CurrentUser and round-trip verified.
  The encrypted recovery point is `artifacts/backups/termlink-local-pre-v0.2.47-20260922T104903Z.dump.dpapi`,
  SHA-256 `a16c2c802a7f0146e8cc1362a3da9dfd08437607c064b835e42667f83d8733cc`; plaintext staging was removed.
  This local fallback is not an off-host age backup and is recoverable only by the same Windows user profile.
- `termlink-local` now runs the exact tag and index digest above. Only the Server container was recreated;
  PostgreSQL container `8523967489edd14355fdaa771b86a5fdeb03ccc8992063c29dd8465b4ebf5413` and volume
  `termlink-local_postgres-data` were preserved.
- Exactly one Server and one PostgreSQL container are running and healthy. OCI revision is the release commit,
  schema migration is 50 (the source maximum), and the `direct_routing_policy` table exists.
- Loopback `/healthz`, `/readyz`, embedded OIDC discovery and public
  `https://termlink2.kget.cc:8910/readyz` all returned HTTP 200 after upgrade.

## Rollback

- Client packages are immutable in AppShip. Preserve the release and `.appship` state even if publication or
  field validation fails.
- The Server tag and digest are immutable. Do not move `v0.2.47`; a Server rollback must pin the previous
  verified `v0.2.46@sha256:...` digest, and a repaired image requires a new version tag.
- The local pre-upgrade Server image remains available as content ID
  `sha256:f5971f9dac62cbdeaf89529ac5c608ec4d96176770e2a7681ac68ba7fd258625`; preserve the PostgreSQL volume and
  use the verified DPAPI backup if a database restore is required.
- Switch Direct routing to Relay-only if field validation is unstable; do not downgrade the Server or delete
  client data. A client rollback requires publishing a strictly newer, correctly signed build.
