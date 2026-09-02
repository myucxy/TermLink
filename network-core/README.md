# TermLink direct network core

This workspace is the transport-only, stability-first boundary for TermLink's
optional direct path. It does not own the realtime WebSocket, terminal sessions,
renderer state, durable acknowledgements, or application command outboxes.

The first stage deliberately runs as **shadow direct**:

- `termlink-direct-wire` implements the bounded `TLD1\n` connectivity datagram,
  deterministic protobuf encoding, SHA-256/HMAC authentication, and strict
  PROBE/PROBE_ACK anti-amplification validation.
- `termlink-direct-nat` owns one UDP socket continuously from bind through host
  candidate and RFC 8489 STUN discovery. Its handoff consumes that socket; there
  is no release-and-rebind API.
- `termlink-direct-quic` consumes that exact socket in Quinn 0.11.11/rustls
  0.23.43, pins a temporary certificate fingerprint, uses TLS 1.3 only and ALPN
  `termlink-direct/1`, derives the path-scoped TLS exporter, and runs bounded
  shadow ping/echo. Its portable socket adapter exposes one datagram per call,
  rejects packets above MTU 1200, disables GSO/GRO semantics, and strips ECN.
- `termlink-direct-path` keeps Relay health independent from the optional path.
  Every direct failure synchronously returns to `RelayOnly` and can emit only
  direct-path events.
- `termlink-direct-ffi` ABI v2 exposes fixed-size owned records and bounded
  command/event queues for the complete shadow attempt: ticket validation,
  same-socket discovery/probe/QUIC, platform-signature polling, peer proof, and
  path-binding output. Native worker threads never call JVM or CLR code, and
  device private keys never enter this core. The canonical declaration is
  `include/termlink_direct_ffi_v2.h`; Rust size/offset tests fence ABI drift.
- `termlink-direct-ffi` ABI v3 is the production-shaped, two-stage shadow API:
  `Gather` binds and retains one socket while returning HOST/SRFLX candidates,
  then `Nominate` consumes that same owner for authenticated probe, QUIC, peer
  proof, and shadow ping. Cancel and network-change fence the exact current
  epoch/attempt synchronously. Its fixed layout is
  `include/termlink_direct_ffi_v3.h`; successful shadow validation still
  cannot request application-data cutover.
- `termlink-direct-ffi` ABI v4 adds bounded, incremental multipair checks,
  HOST-first candidate trickle, exact-pair nomination and the same-socket QUIC
  handoff required by `direct_transport_shadow_multipair_v2`. Event generations,
  owner replacement and terminal failure publication are linearized; Relay
  remains authoritative after `ShadowReady`. Destroy synchronously cancels and
  joins the handle's worker before returning, so a platform may unload the
  native library only after all code from that handle has stopped executing.
  Its frozen declaration is `include/termlink_direct_ffi_v4.h`.

All direct dependencies use exact versions and resolve fully offline from the
pinned `D:\Dev` Cargo cache. Cryptographic primitives and TLS are provided by
audited `ring` and `rustls`; TermLink does not implement cryptography itself.
Realtime QUIC Envelope transport, signalling WebSocket, cutover barriers,
receipts, and Relay routing are explicitly out of scope for shadow-direct and
must not be inferred from a successful UDP probe. Platform device signing uses
the transcript returned by this core; private device keys remain in Android
Keystore or Windows CNG.

The FFI crate builds both `staticlib` (Android JNI shim) and `cdylib` (Windows
P/Invoke). ABI v1 remains available as the original state-only compatibility
surface. ABI v2 is frozen for symbol/layout discovery and its create entry
point always fails closed; production callers must require ABI v3 and must not
fall back to v2. The retained v2 records are shadow-only: they cannot receive Relay, session, renderer,
Envelope, durable ACK, or routing handles, and its event vocabulary cannot ask
the platform to disconnect Relay. Android requires a non-zero `Network` handle
and calls `android_setsocknetwork` before the first packet. QUIC receives only
from the nominated endpoint and validates the handshake leaf certificate by
the signalled SHA-256 fingerprint; the full peer certificate is not passed in
the ABI. Successful shadow validation still does not authorize route cutover.
