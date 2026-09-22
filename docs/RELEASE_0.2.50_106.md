# TermLink Android 0.2.50 (106) release record

Date: 2026-09-23 (Asia/Shanghai)

## Source identity and scope

- Release source commit: `0bf2cdb18c369010f815895d79e097092a156adc`.
- This is an Android-only release. Windows remains on `0.2.49 (105)` and the Server remains on
  `docker.io/myucxy/termlink:v0.2.47`; neither source changed, so neither is rebuilt or republished.
- AppShip stable before release is Android `0.2.49 (105)`. Local release directory `0.2.50-106`, AppShip state,
  remote Git tag `v0.2.50`, and Docker tag were absent before the build. This client-only release creates no tag.

## Android change

- Retryable Direct signalling socket failures no longer become permanently terminal after three retries. The
  existing reconnect path continues with exponential full-jitter backoff capped at approximately 30 seconds.
- Authentication, protocol, replay, malformed-frame, and queue-overflow faults remain non-retryable. Relay ownership,
  peer selection, signalling wire format, retry budgets for individual Direct paths, and Server policy are unchanged.
- This fixes a field-observed state where the shared dispatcher exhausted its short reconnect burst and every later
  Direct attempt failed locally until the Android process or authentication generation was replaced.

## Verification before the release build

- New regression `retryableSignalFailuresContinueAfterInitialBurstWithoutTerminalFault`: passed.
- Android `:core-network:testDebugUnitTest`, `:app:testDebugUnitTest`, and `:app:testReleaseUnitTest`: passed.
- Direct deterministic fault matrix: 16/16 passed. This is model coverage, not production socket evidence.
- `git diff --check`: passed.

## Termux reference boundary

- Rechecked `termux/termux-app` through the repository-pinned commit
  `3df69d1da197dd9bd71a3bafd902dffd720576b4`, `terminal-forks/termux-terminal/UPSTREAM.md`, the local patch queue,
  sync script, and `android/terminal-engine-termux/` boundary.
- The imported Termux code explicitly excludes networking and authentication, and searches of the official
  `termux/termux-app` issues found no equivalent WebSocket/Direct reconnect implementation. Termux is behavior
  context only; no local PTY, service, or process architecture was copied.

## Production artifact and AppShip

- APK: `artifacts/release/0.2.50-106/TermLink-Android-0.2.50-106.apk`; size `38066218` bytes; SHA-256
  `04ccccedb26d340a0afda9f3251ebb78df15ef73fb0bec2ebbde0aa74d4724bd`.
- Android signer SHA-256:
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`; APK Signature Scheme v2 passed with
  exactly one signer.
- AppShip release `a2616d6a-c39e-4076-baac-66efd3f72811`; artifact
  `b100b931-dfb2-490f-9a80-b0b0e2b537ed`; final state `published / ready`.
- State file: `.appship/termlink-android-stable-0.2.50-build106-signature-approved.json`.
- Public stable update checks returned build 105=`optional` and build 106=`no_update`.

## Live validation

- The same-source release-behavior emulator APK had SHA-256
  `423f660db5522b0062c09ceac7ce4f81770f6734aa7e8665e56dfba2347d138c`, version `0.2.50 (106)`, and the existing
  debug signer `b0bcabe365a44108af2101fa3b34899dfeed5efef306639a004097060871d343`.
- `adb install -r` upgraded the emulator from `0.2.49 (105)` without uninstalling or clearing data. UID `10224`,
  first-install time `2026-09-19 10:16:29`, login, tabs, and sessions were preserved.
- A process cold start authenticated and installed cached snapshot sequence 10, committed its renderer binding, and
  returned the session to `Controlling` over Relay. `echo 1` displayed and executed one copy only.
- The first post-upgrade resume briefly showed `TL-H108`; a cold start restored the same snapshot successfully, so
  no key or app data was lost. The older session still reports the previously known `TL-H113` incomplete-history
  condition.
- Field logs showed Direct attempts at the 30-second cooldown through the former signalling retry boundary, but a
  separate three-retry ceiling in `ProductionDirectShadowRuntime` then left Direct idle after
  `DIRECT_SHADOW_FAILED`. This follow-up defect is fixed by Android 0.2.51; 0.2.50 therefore does not receive full
  Direct recovery acceptance.
- No authoritative `LAN_DIRECT` or `NAT_DIRECT` was observed. The emulator's `10.0.2.15` host-NAT network cannot
  prove physical same-Wi-Fi LAN coverage, and the remote Windows peers remain on 0.2.47.

## Rollback

- Preserve the immutable AppShip release and resumable state. Android rollback requires a strictly newer build
  signed by the continuity signer; do not uninstall the app or clear client data.
- If field validation is unstable, select the existing Server Relay-only policy. No Windows or Server rollback is
  part of this Android-only release.
