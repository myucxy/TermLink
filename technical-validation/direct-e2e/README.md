# Direct transport fault matrix

This directory is a deterministic cross-carrier model for the Android-to-
Windows Direct transport cutover. It is a release-gate test of routing
invariants, not evidence that production sockets, NAT traversal, or device
lifecycle have passed end to end.

Run the matrix from the repository root:

```powershell
pwsh -NoProfile -File .\technical-validation\direct-e2e\Test-DirectFaultMatrix.ps1
```

The runner invokes the pinned `.NET 8.0.303` SDK at
`D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe` and has no package dependencies.

## Deterministic coverage

The executable uses `IDirectProductAdapter` and an owned-copy fake transport.
The 16 cases currently cover:

- complete `Relay -> LAN Direct -> Relay` and
  `Relay -> NAT Direct -> Relay` sequences;
- exact-byte fallback, serialized ingress ordering, and one-application-only
  behavior when Relay and Direct copies race or overlap during duplicate grace;
- Direct protocol, bounded-queue, and carrier failures, including the current
  Envelope and all retained Envelopes in ordered Relay fallback;
- predecessor recovery actions (replay exact Relay data, then retire Direct)
  drained before a newer successor path fence is committed;
- a long-output stream and a 1,024-item Relay-only resize burst;
- transport receipts versus application acceptance for durable ACK authority;
- signalling interruption, network change, Android background/lock recovery,
  symmetric-NAT negotiation failure, malformed/conflicting ingress, and two
  independent Android peer states.
- Direct failure while the Relay socket is offline, with byte-identical
  recovery transferred to the resident Relay supervisor's bounded retry owner
  and flushed exactly once only after an explicit Relay reconnect.

Every Direct-failure case snapshots the long-lived Relay supervisor identity,
connection state, socket generation, close count, and rebuild count. It asserts
that Direct protocol/queue/carrier retirement does not close or rebuild that
existing Relay. The explicit Relay interruption case separately proves that a
real Relay interruption can rebuild only its own socket and leave healthy
Direct state resident.

The model preserves opaque Envelope bytes at every transport boundary. It
does not deserialize and reserialize terminal payloads, and it keeps resize
control separate from the output sequence space so resize bursts cannot create
an output gap.

## Production adapter gap

`IDirectProductAdapter` is the explicit seam for future black-box drivers. This
gate deliberately launches no Android APK, Windows Agent, server, WebSocket,
QUIC socket, STUN exchange, emulator, or physical device. It therefore reports
zero real-network E2E coverage. A passing matrix proves the model invariants
only; it does not turn on production Direct routing.

Before enabling production routing, adapters must run the same assertions
against installed Android and Windows builds and collect bounded, non-sensitive
evidence for:

- same-Wi-Fi LAN and distinct-NAT UDP/QUIC paths, including symmetric NAT,
  IPv4/IPv6, VPN, and firewall denial;
- independent Relay, signalling, and Direct protocol/socket interruption;
- Android lock/wake and Wi-Fi/mobile-data switching;
- long terminal output with repeated resize bursts;
- two concurrently connected Android devices;
- exact fallback bytes, public-ingress dedupe/order, predecessor recovery
  actions, and durable application ACK cursors.

Those production adapters remain an explicit gap rather than being inferred
from this deterministic model.
