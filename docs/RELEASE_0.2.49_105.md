# TermLink clients 0.2.49 (105) release record

Date: 2026-09-22 (Asia/Shanghai)

## Source identity and scope

- Release source commit: pending until the tested source and this pre-release record are committed and pushed.
- Android and Windows are in scope because both Direct runtimes changed. The Server remains on
  `docker.io/myucxy/termlink:v0.2.47`; no Server source changed, so no Docker image or Git tag is created.
- Verified pre-release stable state: Android `0.2.48 (104)` and Windows `0.2.47 (103)`.
- Verified that AppShip build 105, local `artifacts/release/0.2.49-105`, and remote Git tag `v0.2.49` were unused.

## Client changes

- Android stops automatically re-arming Direct negotiation after the existing maximum retry budget is exhausted.
  Network, Relay generation, and peer-authority changes still reset the budget and wake the shared coordinator.
- Windows interrupts its 30-second responder cooldown when a valid pre-promotion `PathOffer` is queued. Offers
  queued immediately before cooldown installation are detected as well, so Android's 12-second attempt timeout no
  longer expires solely because Windows is sleeping.
- Both fixes remain isolated from the authoritative Relay route and add no protocol, configuration, or dependency.

## Verification before the release build

- Android targeted retry-budget and unchanged-peer-refresh regressions: passed.
- Android `:core-network:testDebugUnitTest`, `:app:testDebugUnitTest`, and `:app:testReleaseUnitTest`: passed.
- Windows `TermLink.ProductionDirectTransport.Validation`, including cooldown interruption: passed.
- Direct deterministic fault matrix: 16/16 passed. This is model coverage, not production socket evidence.
- `git diff --check`: passed.

## Termux reference boundary

- Reused the repository-pinned review of
  `termux/termux-app@3df69d1da197dd9bd71a3bafd902dffd720576b4`,
  `terminal-forks/termux-terminal/UPSTREAM.md`, the local patch queue, sync script, and
  `android/terminal-engine-termux/` boundary.
- Termux has no Relay, STUN/ICE, remote Direct peer, responder cooldown, or E2EE authority/retry coordinator
  equivalent. It remains a lifecycle/terminal behavior reference only; no Termux code or local PTY architecture
  was copied.

## Production artifacts and AppShip

- Production APK/Setup hashes, signer continuity, AppShip IDs, and final states are recorded only after the
  immutable release-source commit is built and both wrapper-driven uploads complete.
- Required Android signer SHA-256:
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Required Windows signer SHA-1:
  `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`; this is the existing self-signed development certificate with
  update continuity, not public-CA trust.
- Planned state files:
  `.appship/termlink-android-stable-0.2.49-build105-signature-approved.json` and
  `.appship/termlink-win64-stable-0.2.49-build105-signature-approved.json`.

## Live validation boundary

- Upgrade the debug-signed emulator from the same source/version without uninstalling or clearing data; preserve
  `appId`, `firstInstallTime`, login, device identity, and existing sessions.
- Upgrade the local Windows installation in place using its registered install root. Remote Windows clients can
  consume the published stable update without claiming silent installation.
- A fresh `HOME-PC` session passes only with authoritative `LAN_DIRECT`; a fresh `HT-LPC-1326` session passes only
  with authoritative `NAT_DIRECT`. `GATHERING`, `NEGOTIATING`, epoch allocation, or Relay success is not a Direct
  pass. Relay fallback must remain lossless and must not create duplicate input or a History Gap.

## Rollback

- Keep published AppShip releases and idempotency state immutable. Client rollback requires a strictly newer build
  signed by the continuity signer; do not uninstall Android or clear client data.
- If live Direct validation is unstable, switch the existing Server policy to Relay-only. The Server itself is not
  changed by this release.
