# TermLink 0.2.59 (115)

Date: 2026-09-24. Scope: Windows x64 Setup and Android APK, AppShip stable.
Server source/protocol/schema are unchanged; no Docker image is included.

## Changes

- Windows selects the interface matching the authenticated peer's LAN subnet or
  the OS route to STUN, instead of the first enumerated address. Failed paths are
  penalized per peer for two minutes and alternate eligible interfaces are tried
  on later offers. LAN interfaces without a gateway remain eligible. The existing
  native single-socket ownership and signed ticket/peer validation are preserved;
  this is sequential failover, not simultaneous multi-interface ICE gathering.
- A retired promoted path reopens discovery, fenced by peer, epoch, connection
  and attempt. Unchanged selected addresses/routes survive unrelated adapter
  notifications; changed paths use the existing Relay fallback and negotiation.
- LAN host checks use actual local prefixes rather than fixed IPv4 /24 or IPv6
  /64 assumptions. Private-address and unicast checks remain mandatory.
- STUN DNS timeout/failure preserves host candidates. Native STUN send failures
  and an unreachable candidate pair no longer abort independently usable pairs
  on the same socket. Successful STUN alone is not claimed as NAT connectivity.
- Android check results are authoritative per peer/kind/request. Repeated equal
  outcomes publish fresh progress, network changes clear old results, explicit
  checks interrupt retry cooldown, and pending checks have a 30-second ceiling.
  The menu distinguishes timeout, missing candidates, negotiation failure and
  incompatible capabilities without exposing private endpoints or credentials.

## Validation before packaging

- Android core-network: Debug 329 and Release 329; app: Debug 318 and Release
  313. Zero failures/errors/skips in the final runs. An earlier signaling socket
  test timed out once; rerun passed. Added repeated-check/network-reset and /20
  subnet regression coverage.
- Rust: direct-ffi 87, direct-nat 14, direct-quic 18 and integration 2 passed.
  The new real UDP test verifies that failed STUN send and an unreachable pair
  preserve successful authenticated LAN checks on the original socket.
- Windows Direct runtime and native ABI v3/v4/v5 validations passed; coverage
  includes route priority, alternate uplink, peer isolation, gateway-free LAN,
  STUN DNS failure, promoted-path retirement and stale callback fencing.
- Windows solution built with zero warnings/errors. Setup ownership/prerequisite,
  WindowsAdapterValidation, ArchitectureValidation and Avalonia headless checks
  passed. Release parser/signature/version/manifest/AppShip gate tests passed.
- Executing the modified Windows platform against HOME-PC selected
  `192.168.9.28/24`; previous enumeration selected `10.20.1.236`. Independent UDP
  tests found the latter immediately unreachable to both configured STUN servers;
  the former received Cloudflare binding success in 156 ms. Google timed out in
  that sample. This is route/STUN evidence, not end-to-end NAT acceptance.

## Termux reference boundary

Reviewed termux/termux-app pinned commit
`3df69d1da197dd9bd71a3bafd902dffd720576b4`, especially
`terminal-emulator/src/main/java/com/termux/terminal/TerminalSession.java`, the
local UPSTREAM, patch queue, Sync-TermuxTerminal.ps1 and terminal-engine-termux
adapter. Upstream issue searches for STUN and NAT traversal did not identify a
reusable implementation (the STUN search hit #4285, an unrelated input report).
Termux's local PTY is not TermLink's remote transport authority. No upstream code
or assets were copied; TermLink socket/protocol regression tests apply here.

## Publication and operational evidence

Production packages were built from `4faac41c5919e7a1ff3a3e5b9ac12a1843a57b98`
and independently signature/version/hash verified. HOME-PC and the emulator were
upgraded in place for validation. **115 was not uploaded to AppShip**: real menu
testing exposed missing discovery PATH_CLOSE and responder cooldown interference
between consecutive LAN/NAT checks. Final publication moves to 0.2.60/116.

Physical phone same-Wi-Fi LAN and cross-NAT acceptance require the actual devices
and networks. Emulator host NAT and unit tests do not establish that matrix.
Both clients should upgrade for the subnet and transport changes.

Rollback: keep published release/artifact history immutable. Use the Server's
Relay-only runtime policy if Direct misbehaves; retain account/session data and
signers. Any Android rollback must use a newer build signed by the same key.
