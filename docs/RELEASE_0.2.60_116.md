# TermLink 0.2.60 (116)

Date: 2026-09-24. Scope: Windows x64 Setup and Android APK, AppShip stable.
Includes the multi-interface, subnet, STUN isolation, promoted-path recovery and
Android check lifecycle fixes described in [115](RELEASE_0.2.59_115.md).
No Server/protocol/schema changes; no Docker image is required.

## Additional real-product finding

The unpublished 115 packages exposed a diagnostic lifecycle defect during actual
emulator operations. LAN returned no matching path (emulator 10.0.2.x versus PC
192.168.9.x), but Android did not tell Windows that discovery had ended. Windows
kept waiting, consumed subsequent offers in the old attempt and entered rate/
failure cooldown. This was not evidence that the physical network rejects NAT.

- Android now sends authenticated existing-protocol PATH_CLOSE before releasing
  discovery secrets on completion/cancellation/failure. Signal sequence emission
  is serialized with retirement; promoted data-plane ownership remains separate.
- Windows validates/decrypts/canonicalizes close signals, retires their exact
  attempt, and does not count explicit cancellation as a failed network path.
  The next fresh ticket can start a check immediately; epoch replay checks and
  Server ticket/signal rate limits remain. Normal failed attempts retain the
  30-second minimum interval; repeated-failure cooldown is one minute instead
  of five, and successful negotiation resets accumulated failures.
- Admission rejection sends authenticated PATH_CLOSE instead of silently dropping
  the offer. Android reports cooldown separately from probe timeout.
- STUN resolution retains one address per family per configured server, keeping
  both server choices rather than using two DNS answers from the first. Interface
  fallback matches the peer's advertised address family.

## Validation

- Final Android Debug/Release core-network 329/329, app 318/313 passed, zero
  failures/errors/skips. Added encrypted close-on-completion regression.
- Windows Direct runtime suite includes close retirement, no failure penalty,
  immediate next attempt and stale epoch rejection, plus the 115 route/callback
  tests. Native ABI, Rust socket, desktop/setup/architecture and release gates
  are recorded in 115; native code is unchanged from that tested revision.
- Real HOME-PC route selection resolves to 192.168.9.28. The 115 emulator retained
  firstInstallTime/account and opened a new Windows test session as Controlling
  over Relay; LAN/NAT menu transitions and reasons were observed. Final 116
  in-place smoke and immutable publication evidence are appended after packaging.
- Termux reference/architecture and licensing boundary remain as recorded in 115.

## Publication

Pending committed-source Production build and independent artifact verification.
115 is preserved locally and was not published to AppShip. Both clients should
upgrade together. Existing Android signer and Windows Authenticode signer remain;
the Windows certificate is the existing locally trusted development signer,
not a newly obtained public-CA certificate.

Physical same-Wi-Fi and cross-NAT end-to-end success are not established by the
emulator or unit tests. The release must not claim those acceptance scenarios.
Rollback uses Relay-only policy and a strictly newer same-signer client build;
do not clear app data, replace keys or mutate published artifact/version history.
