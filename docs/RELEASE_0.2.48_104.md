# TermLink Android 0.2.48 (104) release record

Date: 2026-09-22 (Asia/Shanghai)

## Source identity and scope

- Release source commit: `59eb495140ac0fc23e6e7934b4bb5a499ae4cb11`.
- This is an Android-only release. Windows remains on `0.2.47 (103)` and the Server remains on
  `docker.io/myucxy/termlink:v0.2.47`; neither is rebuilt or republished because neither source changed.
- AppShip stable before release: Android and Windows are both `0.2.47 (103)`.
- Remote Git tag and Docker Hub tag `v0.2.48` were absent. This Android-only release does not create that tag
  or publish a Docker image.

## Android change

- An unchanged server-authoritative Direct peer refresh is now idempotent. It preserves the active negotiation,
  peer-selection cursor, retry state, and peer generation instead of cancelling the attempt every two seconds.
- Real authority changes still replace and zero the owned peer keys, reset selection, retire the active attempt,
  and wake reconciliation through the existing path.
- This removes the observed first-peer starvation where `HT-LPC-1326` was selected repeatedly and later peers,
  including `HOME-PC`, never received a Direct attempt.

## Verification before the release build

- `ProductionDirectShadowRuntimeTest.unchangedPeerRefreshDoesNotRetireActiveAttempt`: passed.
- Android `:core-network:testDebugUnitTest`, `:app:testDebugUnitTest`, and `:app:testReleaseUnitTest`: passed.
- Direct deterministic fault matrix: 16/16 passed. This is model coverage, not production socket evidence.
- `git diff --check`: passed.

## Termux reference boundary

- Reviewed `termux/termux-app` at the repository-pinned commit
  `3df69d1da197dd9bd71a3bafd902dffd720576b4`,
  `terminal-forks/termux-terminal/UPSTREAM.md`, `terminal-forks/termux-patches/`,
  `terminal-forks/Sync-TermuxTerminal.ps1`, and `android/terminal-engine-termux/`.
- GitHub searches for peer-refresh starvation, Direct/Relay transport, and network-session reconnect found no
  equivalent implementation or applicable fix. `termux/termux-app#603` concerns local terminal output performance,
  not remote peer authority or transport negotiation.
- Termux has no Relay, STUN/ICE, remote Direct peer, E2EE authority-refresh, ACK, History Gap, or Snapshot
  equivalent. It is an architecture-boundary reference only; no Termux code or local PTY/process design was copied.

## Production artifact and AppShip

- APK: `artifacts/release/0.2.48-104/TermLink-Android-0.2.48-104.apk`.
- Size: `38066222` bytes.
- SHA-256: `5d02e0245c149c33c89cefef792730623c4068d41590b5312dfecf4a4f89ed7f`.
- Signer SHA-256: `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- AppShip application: `termlink-android`; release
  `291617b8-e92d-4fc2-bdd0-83fce37b9c1f`; artifact
  `72c7cf62-8c39-4a2f-9812-c4190c05ab42`.
- Final state: release `published`, artifact `ready`; build 103 returned `optional` and build 104
  returned `no_update`.
- Resumable state path:
  `.appship/termlink-android-stable-0.2.48-build104-signature-approved.json`.

## Live validation boundary

- The signed Production APK passed independent manifest, version, size, SHA-256, v2 signer-count, and signer
  continuity verification before AppShip publication.
- The debug-signed emulator was upgraded in place without clearing app data: `appId=10224` and
  `firstInstallTime=2026-09-19 10:16:29` remained unchanged; login and the existing `HOME-PC` and
  `HT-LPC-1326` sessions were preserved.
- Server authority epochs advanced across all three PCs after the fix: `HT-LPC-1326` 476→477,
  `WIN10-20240606Q` 2→3, and `HOME-PC` 1→2. This proves peer-selection progress, not Direct success.
- `HOME-PC` received an Android offer and entered `GATHERING`/`NEGOTIATING`, then fell back through
  `DIRECT_ATTEMPT_TIMEOUT`; Relay remained usable. Follow-up analysis found a separate Windows responder
  cooldown delay and Android exhausted-retry busy loop, deferred to the next release.
- A fresh `HOME-PC` session passes only after an authoritative `LAN_DIRECT` carrier is observed. A fresh
  `HT-LPC-1326` session passes only after an authoritative `NAT_DIRECT` carrier is observed.
- `GATHERING`, `NEGOTIATING`, path-epoch allocation, or a usable Relay session is not a Direct pass. Direct
  failure must retain automatic Relay fallback without duplicate input or a History Gap.

## Rollback

- Preserve the immutable AppShip release and its resumable state. Client rollback requires a strictly newer,
  correctly signed build; do not uninstall the Android app or clear its data.
- If field validation is unstable, use the existing Server Relay-only policy. No Server or Windows rollback is
  part of this Android-only release.
