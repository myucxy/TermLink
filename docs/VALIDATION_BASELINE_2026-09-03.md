# TermLink validation baseline — 2026-09-03

This ledger is the durable source of truth for completed validation. A passing
scenario must not be repeated merely to reconfirm it. Re-run only the smallest
affected scope when a production source, test contract, dependency, build mode,
or runtime environment named by that evidence has changed.

## Evidence policy

- `active` means the evidence still applies to its recorded source/artifact.
- `artifact-pinned` means the evidence remains valid for that exact installed
  build, but does not automatically qualify a later build whose affected source
  changed.
- Failed or inconclusive attempts are not a baseline and may be repeated until a
  deterministic result exists.
- Before adding a test, compare the changed files with the evidence scope below.
  Unaffected passing suites and manual scenarios are inherited; do not rerun them.
- Record the command/scenario, source commit or file hashes, environment, outcome,
  and invalidation condition immediately after a new result.
- Packaging or installation alone does not invalidate source-level unit evidence.
  A new artifact receives only the smoke checks required by changed packaging or
  runtime integration paths.

## Active automated evidence

| Area | Evidence | Result | Reference / invalidation condition |
| --- | --- | --- | --- |
| Android Snapshot/auth fence | `ProductContractsTest` and `TerminalSnapshotBootstrapTest` | 92/92 passed (49 + 43), 2026-09-03 | Worktree hashes listed below. Invalidate only when the repository auth lifecycle, Snapshot readiness/bootstrap, isolated Direct HTTP ownership contract, or either test class changes. |
| Android Direct lifecycle | `ProductionDirectShadowRuntimeTest` | 15/15 passed, 2026-09-03 | Includes positive Relay generation A→B retirement/rebuild and stalled platform preparation shutdown. Invalidate when `ProductionDirectShadowRuntime`, its platform/native/signalling interfaces, or this test changes. |
| Android Direct HTTP | three targeted `DirectTransportSignalingClientTest` cases | 3/3 passed, 2026-09-03 | Covers cancellation after MockWebServer starts the config response (without deterministically proving that `onResponse` has entered the blocked body read), exact Ticket request cancellation, and successful cancellable Config→Ticket validation. Static review separately confirms that production ownership retains the exact `Call` until body consumption finishes. Invalidate when cancellable HTTP execution/body consumption or these cases change. |
| Android Direct multipair V4 | `ProductionDirectMultipairV4RuntimeTest` | 7/7 passed, 2026-09-03 | Covers V4 pair selection, diagnostics, signalling, fail-closed paths, and shutdown against the final Direct runtime source. Invalidate on multipair/native V4/signalling/runtime changes. |
| Android terminal API | JVM unit suite | 21/21 passed | No later changes to this module in the recorded work. Invalidate on module source/dependency changes. |
| Android native terminal engine | JVM unit suite | 113/113 passed | No later changes to this module in the recorded work. Invalidate on native engine/JNI/terminal-core dependency changes. |
| Android core-network prior full suite | JVM unit suite | 177/177 passed before the final Direct changes | Preserve all unaffected non-Direct results. The changed Direct scope is superseded by the targeted final-source rows above; do not repeat unrelated core-network tests. |
| Android JNI and packaging | connected/JNI, XTVERSION, four-ABI static 16 KiB alignment checks | passed | Invalidate only on JNI/native library, ABI packaging, XTVERSION host-effect, NDK, or packaging changes. |
| Native Direct V4 FFI | FFI validation | 53/53 passed | Audited `v4.rs` SHA-256 `9A4E9CBF6211D50D16BA66D8B793E39BA5334F174441C609497149E4A4D03BE5`. Invalidate when this file or FFI/build inputs change. |
| Native NAT | NAT validation | 13/13 passed | Audited dependency SHA-256 `B3FE9F7B51B8366E5B90DADB6B7966B72305A20A9D739783E2043C1562906DF9`. Invalidate when NAT source/dependencies/build inputs change. |
| Native workspace | formatting, clippy, workspace tests | passed | Invalidate only for changed Rust workspace scope. |
| Windows | Release x64 build and 24 validation entry points | passed | Current Windows evidence includes commit `4e2e4ff` (`feat(windows): add isolated direct diagnostics`). Invalidate on Windows production/test source, native binary, .NET SDK, architecture, or packaging changes. |
| Server clean-room acceptance | Keycloak initialization; OIDC/TLS/API/Realtime; Direct config/ticket; PC↔Android signals; bidirectional opaque byte equality | passed | Includes commit `5088511`. Invalidate on Server, Keycloak realm/forms, Compose, schema/migrations, Relay, or Direct control-plane changes. |
| Server real Valkey | subscriber-count replay-state scenario | passed | With 0 or 2 subscribers sequence/nonce was not consumed; after returning to exactly 1, the same frame retried successfully. Invalidate on Direct signalling Hub/Valkey scripts or subscriber ownership policy changes. |

The disposable clean-room acceptance Docker project was cleaned after validation:
containers, volumes, and networks all had a final count of zero.

## Active manual and device evidence

| Scenario | Result | Scope |
| --- | --- | --- |
| Correct OpenCode window `OC \| Greeting`, three Windows resize cycles | passed; Android remained `控制中`; SessionHost did not restart | This is the authoritative resize evidence. It remains valid while terminal resize/reflow, SessionHost lifecycle, Android rendering, and relevant transport framing are unchanged. |
| Android 5,200-line history | passed | Artifact-pinned to Android `0.2.25 (82)`. Do not repeat on build 82. Re-test a later build only if Snapshot/history/bootstrap/rendering code changed. |
| Android lock-screen resume | passed | Artifact-pinned to Android `0.2.25 (82)`. Re-test only after app lifecycle, socket resume, auth generation, or relevant packaging changes. |
| Android 35-second network interruption and recovery | passed | Artifact-pinned to Android `0.2.25 (82)`. Re-test only after authoritative Relay reconnect/lifecycle code changes. Direct-shadow-only changes do not invalidate Relay recovery evidence. |

The earlier resize observation from a different Windows terminal was a wrong-window
measurement. It is explicitly discarded and must never be cited as evidence.

## Current uncommitted Android evidence identity

The 2026-09-03 targeted Android results above were produced at repository HEAD
`4e2e4ff7651c1a69c80097572a53cdd3f90d5db4` plus the following worktree files:

```text
985B9539672BC58FC0C3ECB4A4EFFE4342D8BBBCD83F1C6CD1DEF43195EF3A97  android/app/src/main/kotlin/dev/termlink/product/ProductionProductRepository.kt
801441AFE6963F4B92469BB5CE14F098DD790C5DD85929582CFD5BC7060DA408  android/app/src/test/kotlin/dev/termlink/product/ProductContractsTest.kt
8E8712B5B9868A91D4AFD1A616788D990CB106D9BAF7F68A25BFD41121D0FB42  android/app/src/test/kotlin/dev/termlink/product/TerminalSnapshotBootstrapTest.kt
0F81B58ABA6A54F727C8D64F05D4A67C04BA55883F367343AB77AF53E3B30177  android/core-network/src/main/kotlin/dev/termlink/network/direct/DirectTransportSignalingClient.kt
2E3CEB7C7CF96F42F5F6EDBA2CA6EA4371AA9667B58554A612B35FDFFA22DA93  android/core-network/src/main/kotlin/dev/termlink/network/direct/ProductionDirectShadowRuntime.kt
B899321087EB28487520B43562AEE772BF098F5120B4B4FF0D468676D2D5412F  android/core-network/src/test/kotlin/dev/termlink/network/direct/DirectTransportSignalingClientTest.kt
381FD1DE902DEF0714EF5D0C35B2631E781A3862F306733FA58CC05785DA7337  android/core-network/src/test/kotlin/dev/termlink/network/direct/ProductionDirectMultipairV4RuntimeTest.kt
14E180D8CB951B05EDC72D05D041876281F815A848B2BB5FE8FEA1657007738E  android/core-network/src/test/kotlin/dev/termlink/network/direct/ProductionDirectShadowRuntimeTest.kt
```

Commands used for the current targeted evidence:

```powershell
.\gradlew.bat :app:testDebugUnitTest `
  --tests dev.termlink.product.TerminalSnapshotBootstrapTest `
  --tests dev.termlink.product.ProductContractsTest --no-daemon

.\gradlew.bat :core-network:testDebugUnitTest `
  --tests dev.termlink.network.direct.ProductionDirectShadowRuntimeTest `
  --tests dev.termlink.network.direct.DirectTransportSignalingClientTest.cancellingConfigAfterHeadersCancelsAStalledResponseBody `
  --tests dev.termlink.network.direct.DirectTransportSignalingClientTest.cancellingTicketRequestCancelsTheExactOkHttpCall `
  --no-daemon

.\gradlew.bat :core-network:testDebugUnitTest `
  --tests dev.termlink.network.direct.ProductionDirectMultipairV4RuntimeTest `
  --tests dev.termlink.network.direct.DirectTransportSignalingClientTest.cancellableConfigAndTicketPreserveValidatedSuccess `
  --no-daemon
```

Environment: HOME-PC, Windows, JDK 21 at `D:\Dev\Env\Jdk\jdk-21`, Android
SDK at `D:\Dev\Env\Android\Sdk`, repository Gradle Wrapper 8.13.

## Termux reference boundary

The Android review used the pinned `termux/termux-app` commit
`3df69d1da197dd9bd71a3bafd902dffd720576b4` and the repository's patch queue as
behavioural reference. Termux has no reusable remote Snapshot/OIDC/Relay/NAT
lifecycle implementation for these fixes. Its local PTY, shell, bootstrap, and
network architecture remains excluded; TermLink owns authentication generation,
remote transport, E2EE, ACK, History Gap, and Snapshot fencing.

## Final read-only release audit

The final 2026-09-03 review found no release blocker and did not rerun tests.
Two Direct-shadow-only limitations remain recorded so they are not rediscovered
or incorrectly attributed to the authoritative Relay path:

- A positive Relay generation change retires an active or preparing Direct
  attempt immediately, but an already scheduled Direct retry cooldown is not
  cancelled. A new shadow attempt can therefore be delayed until that cooldown
  expires. Revisit only when the Direct retry scheduler or routing authority
  changes.
- Android system DNS may ignore interruption. The runtime uses a bounded daemon
  lane and does not wait for that lane during Relay/auth shutdown, but the old
  daemon thread can remain until the platform DNS call returns. Revisit only
  when the resolver implementation or runtime ownership model changes.

## Release state (not test evidence)

- AppShip latest published release at the time of this ledger: `0.2.25 (81)`.
- Emulator installed formal package at the time of this ledger: `0.2.25 (82)`.
- The next formal Android build number must be greater than 82.
- The original Windows signing PFX and private key were found locally and
  verified against continuity fingerprint
  `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`; no certificate rotation is needed.
