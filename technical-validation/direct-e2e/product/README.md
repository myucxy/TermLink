# Direct product E2E acceptance

This directory is an acceptance harness, not simulated network evidence. A
`passed` scenario requires observations from an isolated Android Debug build,
the running Windows Agent, and a process-scoped fault backend. Missing physical
topology is `skipped`; missing platform hooks are `blocked`; neither is passed.

## Android artifact

Build with all four explicit isolation inputs:

```powershell
.\gradlew.bat :app:assembleDebug `
  -PtermlinkServerUrl=https://10.0.0.10 `
  -PtermlinkEnableDirectTransportShadow=true `
  -PtermlinkEnableDirectTransportDataPlane=true `
  -PtermlinkDebugApplicationIdSuffix=directacceptance
```

Only that Debug variant contains
`assets/termlink-direct-product-e2e-contract-v1.json`. Release and ordinary
Debug APKs do not contain it.

Before probing the receiver, the runner launches the installed application
through ADB's launcher category. This is required because Android 10 and later
may silently deny an Activity launch attempted by a background receiver. The
`android_lock_wake` scenario uses external ADB `KEYCODE_SLEEP`/`KEYCODE_WAKEUP`
and confirms `dumpsys power` state; the application hook is not allowed to
pretend the device was locked or woken.

## Fault backend

`New-DirectFaultAdapterContract.ps1` pins the checked-in driver hash and a local
named-pipe endpoint. The named-pipe service is a lab-owned WFP/proxy backend,
not part of the product. It must:

- bind every fault lease to the exact Agent PID, UTC start time, and image hash;
- isolate `direct_quic`, `direct_udp`, `relay_wss`, and `signalling_wss`;
- restore each action idempotently and expire all leases when the harness dies;
- use the driver's 32-bit little-endian length-prefixed UTF-8 JSON protocol;
- return only bounded counters, hashes, enum values, and process identities.

The checked-in driver validates the live process identity before applying or
restoring a fault. It calls `restore_all` after every scenario and again from the
runner's outer `finally`. If the backend is absent or cannot prove process scope
and automatic cleanup, the result is `blocked`.

Windows firewall program rules are intentionally unsupported: they identify an
executable path, not the exact running Agent process, and therefore cannot prove
the isolation required by this matrix.

Peer-specific fault actions additionally carry the in-memory Android peer UUID
and test-session UUID. The checked driver validates both as non-empty D-format
UUIDs and the generated contract declares exact peer targeting. Neither UUID is
written to evidence.

## Windows resize lab driver

`long_output_continuous_resize` requires a separate, optional Windows lab
control contract. Generate it with `New-DirectWindowsLabControlContract.ps1`
and pass its path as `-WindowsLabControlContractPath`. The backend must bind to
the exact SessionHost PID, UTC start time, image hash, session UUID, and role;
start a preconfigured bounded workload without accepting an arbitrary command;
confirm three requested geometries; and guarantee cleanup. If the contract or
live backend is absent, only this scenario is `blocked`. Android
`request_resize` and `start_long_output` responses are never treated as resize
evidence.

## Assertion authority

The fault backend proves only topology, process-scoped fault application,
leases, and cleanup. It is not allowed to return application assertions. The
runner computes process continuity, carrier/generation transitions, gap and
duplicate counters, ACK bounds, peer isolation, and late-session behavior from
redacted Android and Windows samples selected by exact device/session role.
Resize assertions additionally require the Windows lab driver's confirmation.

The current diagnostics cannot prove marker occurrence cardinality, exact byte
replay, the causal statement that a transport receipt did not advance durable
ACK, or complete frame-set delivery. Scenarios requiring those statements end
as `blocked` with `ASSERTION_EVIDENCE_UNAVAILABLE`; an all-true response from a
fault backend cannot promote them to `passed`.

## Windows diagnostics selector

The Windows diagnostics driver accepts only `probe` and `inspect`. A `probe`
request has no session selector. Every `inspect` request must carry exactly a
non-empty D-format `session_id` UUID, `session_role` (`primary` or `late`), and
the non-empty D-format Windows `peer_device_id` UUID reported by the Android
acceptance bridge. Missing, malformed, extra, unknown, or role/device-mismatched
selectors fail closed. The Agent must resolve the exact session, role, and peer;
it must never select an arbitrary active session.

The driver exchanges bounded UTF-8 JSON over the local named pipe with an
unsigned 32-bit little-endian length prefix. Session and peer UUIDs exist only
in the transient request path and in memory. They are not logged or copied into
`evidence.json`; samples expose only `session_role` so primary and late-session
observations remain distinguishable without leaking identifiers.

## Evidence rules

`evidence.json` contains no tokens, host names, IP addresses, account data,
terminal text, or command strings. Origins, application IDs, receiver names,
markers, and rendered viewports are represented by SHA-256 when evidence needs
stable correlation. `expected_carrier_sequence` comes from the matrix;
`carrier_sequence` contains observations only.

The Termux terminal fork provides emulator behavior and lifecycle regression
references, but it has no Relay, signalling, STUN/ICE, QUIC, or remote fault
injection mechanism. Its local PTY/service architecture is not reused here.
