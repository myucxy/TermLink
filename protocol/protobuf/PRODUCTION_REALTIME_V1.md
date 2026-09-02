# TermLink Production Realtime V1

This contract is separate from the `TLT1` test protocol. `TLT1` frames are
never accepted by `/v1/realtime`.

## Connection handshake

The WebSocket upgrade uses an OIDC bearer token and these headers:

- `X-TermLink-Device`: canonical UUID of the registered device;
- `X-TermLink-Timestamp`: Unix milliseconds;
- `X-TermLink-Nonce`: base64url 16-32 random bytes;
- `X-TermLink-Signature`: base64url Ed25519 signature;
- `Sec-WebSocket-Protocol`: `termlink.realtime.v1`.

The signed bytes are UTF-8 and use exactly:

```text
TERMLINK-DEVICE-PROOF-V1\n
GET\n
/v1/realtime\n
<timestamp>\n
<nonce>\n
<lowercase SHA-256 of empty body>
```

The server checks a 60 second clock window and consumes the nonce once in
Valkey. The first client message is a bounded JSON `ClientHello` control
record containing `role`, `protocol_versions`, and `capabilities`. It contains
no terminal content. The server answers with `ServerWelcome` and then accepts
binary protobuf `Envelope` messages. If both sides negotiated
`liveness_probe_v1`, the authenticated connection also accepts one bounded
JSON control pair: the client sends `{"type":"ping","probe_id":"<uuid>"}`
and the Relay returns the same canonical UUID in a `pong`. This control carries
no terminal/session identifier or content, never consumes an `Envelope.seq`,
and is used only to prove that a foregrounded Android client still has a live
round trip before replacing its WebSocket generation. All other post-welcome
text frames are protocol errors.

The optional direct-transport signalling lane uses a separate authenticated
WebSocket at `/v1/transport/signals`, subprotocol `termlink.transport.v1`, and
device-proof domain/path `TERMLINK-TRANSPORT-SIGNAL-PROOF-V1` plus
`/v1/transport/signals`. It never replaces or delays this realtime WebSocket.
After its own bounded hello/welcome, that lane accepts only serialized
`TransportSignalEnvelopeV1` from `termlink_transport.proto`.

## Envelope and E2EE

`termlink_realtime.proto` is the authoritative wire schema. Version 1 uses
UUIDs as 16 raw bytes. A binary frame is one serialized `Envelope` and is at
most 1 MiB. Unknown protobuf fields are ignored; an unknown message type is
rejected unless a later negotiated capability explicitly marks it optional.

For encrypted application messages, `nonce` is 12 bytes and `ciphertext` is
ChaCha20-Poly1305 output including the authentication tag. `key_id` identifies
an X25519/HKDF key epoch without revealing key material. Associated data is a
deterministic protobuf encoding of every Envelope field except field 11
(`ciphertext`). Optional field 12 (`recipient_device_id`) is included when set.
The two directions use distinct HKDF output keys. `(key_id, direction, seq)`
must never repeat.

The Relay validates routing fields, authorization, direction, sequence rules,
and size. It never decrypts field 11. `Envelope.ack` is a routing-visible
copy of the cumulative output cursor and is non-zero only for `SUBSCRIBE`,
`OUTPUT_ACK`, and `FLOW_CONTROL`. For those messages the decrypted payload
cursor MUST equal `Envelope.ack`; every other application message MUST carry
`ack = 0`.

A cumulative ACK normally means that every ordered event through that sequence
has been applied. The experimental snapshot path may establish the same cursor
only after an authenticated snapshot anchored at S has been completely
restored and durably bound to the renderer checkpoint. The next subscription is
then `SUBSCRIBE(S)` and the first newly applied event is S+1. Merely downloading
or decrypting a snapshot never authorizes ACK S.

Pairwise device-channel messages set `recipient_device_id` and are delivered
only to that approved device. Empty-recipient messages are limited to message
types whose semantics explicitly allow account/session broadcast. Session
output in V1 is recipient-specific; Relay never maps Key IDs to private keys.

## Optional direct data path

The WSS Relay is the permanent control and fallback path. A direct path is a
make-before-break optimization between two currently approved devices. Its
reliable QUIC stream carries `DirectPathFrameV1`; the
`realtime_envelope` field is the exact received serialization of this existing
`Envelope`. A sender must preserve all bytes, including unknown protobuf
fields, and must not parse then reserialize an Envelope for path forwarding.
Thus direction ACL, lease, Gap, replay, E2EE nonce uniqueness and application
sequence semantics are identical on Relay and direct paths.

Transport signalling has its own `connection_id`, monotonic `path_epoch`,
`signal_seq`, `signal_ack`, key/nonce space and AEAD domain
`TERMLINK-TRANSPORT-SIGNAL-AEAD-V1`. None is an alias for `Envelope.seq`,
`Envelope.ack` or the terminal session key domain. Likewise,
`DirectTransportReceiptV1` acknowledges only direct `frame_seq` bytes. Receipt
of bytes must never commit, persist or advertise a terminal cumulative ACK;
only successful application through the existing renderer/recovery state
machine may do that.

After mutual ticket/device/QUIC proof, each peer independently proposes a
bounded list of per-session first-direct Envelope sequences for its own
outbound direction. Below a barrier, Relay remains authoritative. The sender
continues using Relay and buffers exact bytes from the barrier while proposal,
accept, commit and commit echo converge. It then replays that bounded buffer
and duplicates the exact Envelope bytes over Relay and direct QUIC for the
agreed short grace window. Receivers deduplicate with the existing
session/direction sequence rules. A half-completed or over-budget negotiation
stays Relay-only; the opposite direction may switch independently.

On timeout, malformed direct data, ticket expiry, address change, QUIC close,
app background restriction or peer-proof failure, the affected route switches
locally to Relay immediately. The sender retransmits every unreceipted direct
Envelope data frame as the original Envelope bytes over WSS and continues with
the next existing Envelope sequence. A transport receipt may release only the
QUIC retransmission copy: the authoritative PC journal remains retained until
the existing cumulative application ACK, and Android commands remain in their
existing semantic outbox until the corresponding input/resize/lease result or
idempotent retry rule completes. Fallback resumes those existing sources even
when QUIC previously receipted the bytes. The socket, subscriptions,
durable ACK and renderer are not reset. A fallback notice is best-effort
convergence metadata; losing the signalling lane cannot delay local fallback.
Old epochs are ignored after retirement, and a new direct attempt requires a
higher server-ticketed epoch.

The only V1 QUIC application record is the four-byte unsigned big-endian
length-prefixed `DirectPathFrameV1`. In each direction frame 1 is
`peer_proof`, frame 2 is `peer_proof_result`, and no ping, receipt, or
`realtime_envelope` is accepted before both directions authenticate. A `TLPP1`
or other platform-private proof record is not V1. Signalling carries only the
SHA-256 fingerprint of each temporary QUIC leaf certificate's exact DER bytes;
the raw certificate arrives through the TLS handshake and is pinned to that
role-bound fingerprint.

Transport-control protobuf is strict V1 wire: unknown fields, duplicate
singular fields, unknown enums, non-minimal varints, and non-canonical field
ordering retire only the current direct attempt. JSON negotiation/config may
ignore bounded unknown properties. The opaque `realtime_envelope` bytes are
the deliberate exception and preserve all Realtime protobuf unknown fields
byte-for-byte across direct delivery, duplicate grace, and Relay fallback.

Candidate, ticket, timeout, signature, state-machine and address validation
bounds are normative in
`../../docs/NETWORK_CONNECTION_AND_DIRECT_TRANSPORT.md`.

## Capabilities

Version 1 requires `e2ee_x25519_chacha20poly1305`, `cumulative_ack`,
`flow_control_v1`, and `lease_v1`. `bounded_event_replay_v1` is supported for
recovery. The additive Phase 1 `TerminalSnapshotV1` and
`TerminalTranscriptPageV1` object field allocations are frozen in
`../test-vectors/TERMINAL_SNAPSHOT_V1.md`. The recipient-bound REST
`terminal_snapshot_v1` path may be advertised after producer/importer
equivalence and cross-language E2EE gates pass; it remains independently
rollbackable. This does not enable normalized realtime presentation deltas.

Additive implementations may advertise `terminal_presentation_metadata_v1`
and `terminal_typed_input_v1`. The Relay returns the bounded intersection of
recognized capabilities in `ServerWelcome.capabilities`; older peers may omit
or ignore that field. Full normalized Snapshot/Delta/Resync delivery is a
separate `terminal_presentation_v1` gate and is not implied by metadata support.

`liveness_probe_v1` is optional and transport-scoped. A long Android
background interval alone is not evidence that a negotiated connection is
dead: the client probes the exact current generation, retains it on a matching
bounded response, and replaces it only after timeout or structural failure.
A probe from an older generation cannot retire its successor. When an older
Relay does not negotiate this capability, the client retains the conservative
foreground replacement behavior.

`direct_transport_v1` is an independent, rollbackable capability. It may be
advertised only when the client keeps `/v1/realtime` connected while gathering
LAN host candidates, querying the configured STUN service and probing QUIC.
TURN carrier and relay candidate values are reserved for a later
`direct_transport_turn_v1` capability and must otherwise be rejected for the
path attempt. A rejected or unknown transport capability never changes the
Realtime V1 capability intersection.

An `OUTPUT_CHUNK` from a metadata-capable producer sets `source_event_seq` to
the authoritative SessionHost journal sequence and a monotonic
`presentation_revision`. A legacy zero value means `Envelope.seq`. A receiver
must resync instead of applying an update whose non-zero source anchor differs
from `Envelope.seq` or whose presentation revision regresses.

## Direction and message ACL

The authenticated WebSocket role fixes the Envelope direction. The Relay
applies this allowlist before looking up a route:

| Direction | Message types |
| --- | --- |
| Android → PC | `SUBSCRIBE`, `UNSUBSCRIBE`, `OUTPUT_ACK`, `FLOW_CONTROL`, `INPUT_EVENT`, `RESIZE_REQUEST`, `RESUME_REQUEST`, `LEASE_REQUEST`, `SESSION_TERMINATE_REQUEST`, `GOODBYE`, `HEARTBEAT` |
| PC → Android | `SESSION_UPSERT`, `SESSION_REMOVED`, `OUTPUT_CHUNK`, `GAP`, `INPUT_ACCEPTED`, `INPUT_REJECTED`, `RESIZE_APPLIED`, `SNAPSHOT_OFFER`, `SNAPSHOT_CHUNK`, `LEASE_GRANTED`, `LEASE_DENIED`, `LEASE_REVOKED`, `THEME_CHANGED`, `SESSION_STARTED`, `SESSION_INTERRUPTED`, `SESSION_ENDED`, `GOODBYE`, `HEARTBEAT` |

`SNAPSHOT_OFFER` and `SNAPSHOT_CHUNK` retain their existing numbers and ACL
positions for wire compatibility, but are reserved and unused by
`terminal_snapshot_v1`. A Phase 1 terminal snapshot is a pairwise E2EE history
object delivered over REST. It anchors the existing terminal sequence at S,
does not consume an `Envelope.seq`, and is never inserted into the realtime
event stream.

`FLOW_CONTROL` is Android → PC only in V1. It is encrypted and contains the
same `highest_contiguous_seq` as `Envelope.ack`, absolute finite limits for
unacknowledged events and serialized Envelope/WebSocket binary payload bytes,
a monotonic control epoch, and an optional reason. A zero limit or
`paused=true` pauses the complete ordered PC-to-Android session stream. V1
uses one sequence for output, resize, lifecycle, lease and input-result events,
so no later control frame may overtake a paused terminal frame. The PC must
still service the local ConPTY and Shell. It resumes from the subscriber's
cumulative ACK when a newer positive window arrives.

## Input authority

An input payload contains a unique `input_id`, the active `lease_id`, lease
version, and input sequence. The PC Agent validates the lease again immediately
before writing to ConPTY and keeps a bounded duplicate-input cache. Local PC
input revokes the remote lease and always takes priority.

During the V1 typed-input compatibility window, `InputEventPayload.input`
remains present. A sender may additionally set `typed_input`; a capable PC
deterministically encodes it and requires byte equality with the legacy field
before writing to ConPTY. This permits old/new peers in either direction and
prevents a plugin-visible semantic command from differing from executed bytes.

Revocation fences input that was already in flight; it does not permanently
disable a foreground same-account Android client. After observing the newer
`local_input_epoch`, that client may acquire a fresh lease and continue typing.

The ordered `LEASE_REVOKED` payload carries the new monotonic
`local_input_epoch`. Subsequent Android input echoes the latest observed epoch,
so a stale lease or in-flight input cannot cross a local-input boundary.

## Experimental REST snapshot bootstrap

The normative object schema, validation bounds, E2EE domains, atomic restore
state machine, transcript cursor, compatibility behavior, and enablement gate
are defined in `../test-vectors/TERMINAL_SNAPSHOT_V1.md`.

At a high level, an enabled client reads durable ACK A, asks authenticated REST
metadata for at most one eligible pairwise snapshot S where
`A <= S <= last_output_seq`, verifies and restores it off-screen, atomically
binds the checkpoint and ACK S, and only then sends `SUBSCRIBE(S)`. Existing
replay serialization prevents a concurrent live event from overtaking replay.
If S+1 is unavailable, the PC sends an explicit Gap and the client does not
advance its ACK.

The client may establish and authenticate the device WebSocket before this
REST work, but it MUST keep the recovering session unsubscribed until the
renderer binding is durable. The unsubscribed connection is the only atomic
V1 pause before replay starts. `SUBSCRIBE(A)` followed by
`FLOW_CONTROL(paused=true)` is not equivalent: output may be routed between
those two frames, and the Relay cannot inspect their encrypted payloads. After
Snapshot S is installed, the client MAY restore a locally cached, authenticated
and contiguous suffix S+1 through R before connecting the live stream. It MUST
persist cumulative ACK R without regressing an older durable ACK, then set both
`Envelope.ack` and the decrypted `SubscribePayload.resume_from_seq` to R. The
first ordinary replay frame is exactly R+1. With no restored suffix, R equals S.

Legacy Phase 1 Termux Snapshot V1 objects contain both visible terminal buffers
but an empty snapshot scrollback. Terminal-core Snapshot V2.10 instead retains
its bounded primary scrollback inside the canonical checkpoint; older V2.x
profiles remain visible-only. Any separately published older rows are bounded
`TerminalTranscriptPageV1` objects fetched only on upward scroll. Transcript
browsing never changes the live ACK, and transcript data is never prepended to
a live VT parser.

## Lifecycle results

`SESSION_ENDED` and an ended `SESSION_UPSERT` carry a
`SessionUpsertPayload`. Its optional `exit_code` preserves the Shell's signed
32-bit exit result inside E2EE. Presence is required even when the real value is
zero. An absent field means that an older sender did not publish the result; a
receiver must not reinterpret that unknown value as a successful exit.

`SESSION_TERMINATE_REQUEST` is a distinct Android-to-PC MANAGE operation. Its
encrypted payload carries a 16-byte request ID and the fixed reason
`USER_REQUESTED`. The Relay authorizes and routes the opaque frame but cannot
read it. The Windows Agent validates the request, terminates only the
registered SessionHost identity and its child Shell tree, and then publishes
the normal lifecycle/removal events. Permanent deletion of offline history
remains a separate REST operation.
