# TermLink clients 0.2.49 (105) release record

Date: 2026-09-22 (Asia/Shanghai)

## Source identity and scope

- Release source commit: `498949eb1cc8958e0f0dca630af12cbc138836c4`.
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

- APK: `artifacts/release/0.2.49-105/TermLink-Android-0.2.49-105.apk`; size `38066218` bytes; SHA-256
  `947f3fd2630b34dc75b3749e299761a6d6de998f172756d3efad3ae5b4c632b0`; signer SHA-256
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
- Setup: `artifacts/release/0.2.49-105/TermLink-Setup-0.2.49.105-x64.exe`; size `181291736` bytes; SHA-256
  `ecec4f05e998b11fb34b3d32449b1be3c6ae1f28944c6e614a671ed76e5b5a15`; signer SHA-1
  `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`. This is the existing self-signed development certificate with
  update continuity, not public-CA trust.
- Android AppShip release `b9e96460-8679-4019-a4cf-fd24e50e0b3e`, artifact
  `d3276053-3563-48f0-960f-45d55fe0897b`; Windows release
  `1f45bc26-17a7-4503-9f99-6605cf81e2ca`, artifact `b3dea17f-9ff2-4766-89a6-39a4b5b89dca`.
  Both releases reached `published` and both artifacts reached `ready` through the repository wrapper.
- Stable update checks returned `optional` for build 104 and `no_update` for build 105. Resumable state remains in
  `.appship/termlink-android-stable-0.2.49-build105-signature-approved.json` and
  `.appship/termlink-win64-stable-0.2.49-build105-signature-approved.json`.

## Live validation boundary

- The local Windows installation was upgraded in place at `D:\Dev\Tool\TermLink`; Tray, Agent, and SessionHost
  report ProductVersion `0.2.49.105` and retain the approved signer. The debug-signed emulator was upgraded in
  place with the same source/version Release behavior build; `appId=10224`, first install time
  `2026-09-19 10:16:29`, login, device identity, and sessions were retained. This emulator build is not the
  Production APK published to AppShip.
- A fresh `HOME-PC` CMD session reached `Controlling` over Relay. `echo termlink_probe_0922` was applied once, and
  after an Android process restart the authoritative Snapshot at sequence 59 restored completely with the command
  and output still present. No duplicate input or new History Gap was observed on that fresh session.
- One pre-existing `HOME-PC` session remains unavailable with `TL-H113`: Snapshot sequence 10 and event 11 restore,
  but the Windows host returns `EVENT_RANGE_EMPTY` for required event 12. It was preserved for diagnosis rather
  than deleted or treated as a Direct failure.
- Android allocated new epochs for all three PCs. For `HOME-PC`, Windows 0.2.49 received the offer immediately and
  entered `GATHERING`/`NEGOTIATING`, proving the responder-cooldown fix is active; neither side validated a candidate
  pair and both fell back to Relay. `HT-LPC-1326` and `WIN10-20240606Q` remain on Windows 0.2.47.103, so they cannot
  qualify the 0.2.49 responder fix.
- No authoritative `LAN_DIRECT` or `NAT_DIRECT` carrier was observed. The Android emulator uses `10.0.2.15` behind
  its host NAT and cannot provide a physical same-LAN result. Epoch allocation, signalling, and Relay success are
  not recorded as Direct success.

## Rollback

- Keep published AppShip releases and idempotency state immutable. Client rollback requires a strictly newer build
  signed by the continuity signer; do not uninstall Android or clear client data.
- If live Direct validation is unstable, switch the existing Server policy to Relay-only. The Server itself is not
  changed by this release.
