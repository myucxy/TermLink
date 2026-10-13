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

Device-scoped catalog (88/89, `host_session_catalog_v1`) and inactive-session
cleanup (90/91, `host_session_cleanup_v1`) use the PC UUID as envelope session ID
and `ack=0`. They are encrypted peer-to-peer and relayed independently of
terminal subscriptions, ACKs, control leases and Direct session arbitration.
Cleanup requires a MANAGE grant (or the existing same-account AutoTrust policy),
1..64 distinct nonzero session UUIDs and a nonzero request UUID. Windows verifies
host liveness at deletion and returns exactly one outcome per requested UUID:
DELETED, SKIPPED_ACTIVE, NOT_FOUND or FAILED. Responses echo request ID and
sequence; the requester checks peer, key epoch and connection generation.
`MIRROR_SYNC_PENDING` reports a durable host deletion awaiting statistical
mirror synchronization. See [host catalog and cleanup](../../docs/HOST_SESSION_CATALOG.md).

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
machine may do that. Because V1 uses one reliable ordered QUIC stream and the
receipt has no largest-observed field, `missing` MUST be empty. A receipt never
causes another receipt; receipt-of-receipt loops are forbidden.

After mutual ticket/device/QUIC proof, each peer independently proposes a
bounded list of per-session first-direct Envelope sequences for its own
outbound direction. Below a barrier, Relay remains authoritative. The sender
continues using Relay and buffers exact bytes from the barrier while proposal,
accept, commit and commit echo converge. The receiver MUST NOT accept the
proposal until every listed session has continuously applied the common Relay
prefix through `first_direct_seq - 1`; a bounded prefix-wait timeout rejects the
whole proposal and leaves Relay authoritative. This prevents a faster Direct
frame from overtaking an earlier Relay frame and manufacturing a History Gap.
It then replays that bounded buffer
and duplicates the exact Envelope bytes over Relay and direct QUIC for the
agreed short grace window. Receivers deduplicate with the existing
session/direction sequence rules. A half-completed or over-budget negotiation
stays Relay-only; the opposite direction may switch independently.

Direct routing is opt-in by message type. The first production allowlist is
exactly `INPUT_EVENT` for Android-to-PC and `OUTPUT_CHUNK` for PC-to-Android.
All other types, including Gap, input results, Resize, Lease, History,
termination, lifecycle, `HEARTBEAT`, `GOODBYE`, `SESSION_UPSERT`,
`SESSION_REMOVED`, `SUBSCRIBE`, `UNSUBSCRIBE`, `OUTPUT_ACK`, `FLOW_CONTROL`,
`MESSAGE_TYPE_UNSPECIFIED`, an unknown numeric value, or an invalid
direction/message combination are Relay-only. This classification is made
from the already parsed Envelope metadata, but every transmission and fallback
still uses the original serialized Envelope bytes. Expanding the allowlist
requires a mutually negotiated endpoint version and per-type authorization,
ordering, fallback, and interoperability tests.

Before a Direct `INPUT_EVENT` is written to SessionHost, the authoritative PC
Agent obtains a fresh server decision bound to `(session_id,
sender_device_id)`. It validates CONTROL/MANAGE permission, lease identity,
version and expiry, policy version, and the control mode. SINGLE and TAKEOVER
require the sender to be the current lease holder; only COLLABORATIVE permits
another authorized subscriber to share that lease. Missing or unavailable
authority fails that input closed without failing the Relay connection.

Because Relay-only control and Direct-enabled data share one Envelope sequence,
receivers MUST feed both carriers into one peer-scoped serialized ingress and
reorder per `(direction, session_id)` before application. During duplicate
grace, a Direct copy arriving before its Relay copy is held until the matching
Relay bytes arrive or the bounded grace expires. Equal `(direction,
session_id, seq)` and equal bytes are delivered exactly once. Different bytes
for that same key retire the Direct epoch; the Relay copy remains authoritative
and recovery resumes from the existing durable application ACK. A transport
receipt may be emitted for an authenticated Direct frame, but it never bypasses
this ingress, releases an application outbox, or advances a durable ACK.

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
`../test-vectors/TERMINAL_SNAPSHOT_V1.md`. A peer that implements the
Windows-owned data plane advertises `host_history_v1`; snapshot, transcript and
event-range objects then travel over the active recipient-bound E2EE session
route instead of REST/object storage. This does not enable normalized realtime
presentation deltas.

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

Production data routing additionally requires the independent optional
capabilities `direct_transport_data_v1` and `direct_session_cutover_v1` to be
present in the server config and negotiated by both Realtime and transport
signalling connections. The former permits opaque Envelope carriage over the
authenticated Direct path; the latter freezes per-session, per-direction
barrier commit, deregistration, and fallback semantics. Missing either
capability is fail-closed to Relay and must not invalidate the Relay connection.

An `OUTPUT_CHUNK` from a metadata-capable producer sets `source_event_seq` to
the authoritative SessionHost journal sequence and a monotonic
`presentation_revision`. A legacy zero value means `Envelope.seq`. A receiver
must resync instead of applying an update whose non-zero source anchor differs
from `Envelope.seq` or whose presentation revision regresses.

### Persistent terminal state profile

The graphics state contracts are distinct persistent profiles, not alternate
decoders for the same running session:

| State contract | Local descriptor | Width profile | Graphics resources |
| --- | --- | --- | --- |
| `terminal_state_2_22_v1` | `terminal_state_2_22_v1;width=1;output=0;resources=2` | `LEGACY_V1` (1) | 2 |
| `terminal_state_2_22_v2` | `terminal_state_2_22_v2;width=2;output=0;resources=2` | `UNICODE_16` (2) | 2 |

Windows persists the exact descriptor with each session and restores that
profile when reopening its history. New Unicode16 sessions use v2; an existing
v1 session remains v1. Android lists the state contracts it can decode in
`SUBSCRIBE.subscriber_capabilities`; this list does not select a profile.
The authoritative Windows Agent checks the session's persisted state against
that list and returns the exact `required_state` in `TERMINAL_ATTACH_RESULT`.
Android accepts that result only for the current request and connection
generation, and rejects a change from its already accepted or expected profile.
The Relay must have negotiated `terminal_attach_v1` and the host geometry,
theme, and resize capabilities before the attachment is used. An unsupported
v2 session fails with an explicit upgrade error; it cannot be decoded as v1.
History selection, snapshots, Delta, frozen pages, and Broker captures must
match this accepted persistent profile. Advertised decoder support alone never
converts old history or changes a running session's width rules.

The native Graphics snapshot keeps its existing outer container version 3 and
format version 1 in both contracts. Its v1 Key ID and key/AAD prefixes remain
unchanged. V2 uses `terminal-core-graphics-snapshot-v2:` and the separate
`TERMLINK-TERMINAL-CORE-GRAPHICS-SNAPSHOT-KEY-V2\0` and
`TERMLINK-TERMINAL-CORE-GRAPHICS-SNAPSHOT-OBJECT-V2\0` prefixes; its AAD also
authenticates the exact state contract. Open validates the canonical checkpoint
width at byte offset 48 against that contract (1 for v1, 2 for v2). A v2
snapshot cannot be opened as v1 or used to upgrade stored v1 history.

## Direction and message ACL

The authenticated WebSocket role fixes the Envelope direction. The Relay
applies this allowlist before looking up a route:

| Direction | Message types |
| --- | --- |
| Android → PC | `SUBSCRIBE`, `UNSUBSCRIBE`, `OUTPUT_ACK`, `FLOW_CONTROL`, `INPUT_EVENT`, `RESIZE_REQUEST`, `RESUME_REQUEST`, `LEASE_REQUEST`, `SESSION_TERMINATE_REQUEST`, `HOST_HISTORY_REQUEST`, `HOST_HISTORY_CONTROL`, `GOODBYE`, `HEARTBEAT` |
| PC → Android | `SESSION_UPSERT`, `SESSION_REMOVED`, `OUTPUT_CHUNK`, `GAP`, `INPUT_ACCEPTED`, `INPUT_REJECTED`, `RESIZE_APPLIED`, `RESIZE_RESPONSE`, `SNAPSHOT_OFFER`, `SNAPSHOT_CHUNK`, `LEASE_GRANTED`, `LEASE_DENIED`, `LEASE_REVOKED`, `THEME_CHANGED`, `SESSION_STARTED`, `SESSION_INTERRUPTED`, `SESSION_ENDED`, `HOST_HISTORY_RESPONSE`, `SESSION_TERMINATE_RESPONSE`, `GOODBYE`, `HEARTBEAT` |

`SNAPSHOT_OFFER` and `SNAPSHOT_CHUNK` retain their existing numbers and ACL
positions for wire compatibility, but are reserved and unused by
`terminal_snapshot_v1`. A Phase 1 terminal snapshot is a pairwise E2EE history
object selected by the authoritative Windows Agent. It anchors the existing
terminal sequence at S, does not consume an ordered terminal `Envelope.seq`,
and is never inserted into the realtime event stream.

`FLOW_CONTROL` is Android → PC only in V1. It is encrypted and contains the
same `highest_contiguous_seq` as `Envelope.ack`, absolute finite limits for
unacknowledged events and serialized Envelope/WebSocket binary payload bytes,
a monotonic control epoch, and an optional reason. A zero limit or
`paused=true` pauses the complete ordered PC-to-Android session stream. V1
uses one sequence for output, resize, lifecycle, lease and input-result events,
so no later ordered control frame may overtake a paused terminal frame. The PC must
still service the local ConPTY and Shell. It resumes from the subscriber's
cumulative ACK when a newer positive window arrives.

## Explicit window resize

`terminal_window_resize_v1` in the PC catalog advertises support for an explicit,
leased `RESIZE_REQUEST` (40). Legacy automatic requests without a nonzero UUID
remain ignored. The request carries `request_id`, target columns/rows (2..1000),
phone cell metrics, `lease_id`, `lease_version`, `observed_local_input_epoch`, and
an absolute UTC `expires_at_ms` (phone now + 10 seconds). Both device clocks must
be synchronized. `restore=true` requests the saved desktop grid instead.
Android also requires this optional capability echoed in the current Relay
WELCOME before sending. Old servers remain usable for all existing operations;
the new menu reports that the server needs upgrading without sending new messages.

The Host requests the outer window resize and waits for the outer grid and its
real ConPTY to agree. Its normal resize loop remains the sole source of ordered
`RESIZE_APPLIED` (41). `RESIZE_RESPONSE` (42, PC → Android only) is a separate
E2EE control reply containing the same UUID, observed grid, and an error code
(empty on success). Like termination replies it echoes the request Envelope
sequence; it never enters terminal replay, advances its high-water mark, or
consumes an output ACK. The phone reports success only after its authoritative
renderer has also applied the returned grid. Control replies may overtake replay.

Resize controls use Relay; they do not expand the LAN/NAT data allowlist. Only
one operation per session executes at a time, requests are not automatically
retried, and local input epochs fence queued requests. Server, Windows Agent,
SessionHost, and Android must all support this extension before use.

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

## Host-owned history bootstrap

The normative object schema, validation bounds, E2EE domains, atomic restore
state machine, transcript cursor, compatibility behavior, and enablement gate
are defined in `../test-vectors/TERMINAL_SNAPSHOT_V1.md`.

At a high level, an enabled client reads durable ACK A, creates a temporary
zero-window session endpoint, and asks the authoritative Windows Agent for at
most one eligible pairwise snapshot S where `A <= S <= last_output_seq`. It
verifies and restores the snapshot off-screen, requests any contiguous missing
event ranges through R, atomically binds the checkpoint and ACK R, closes the
temporary endpoint, and only then sends `SUBSCRIBE(R)` on the live endpoint.
If any required event is unavailable, the PC returns a host-history Gap and the
client does not advance its ACK.

The client may establish and authenticate the device transport before this
history work, but it MUST keep the recovering session at a zero terminal replay
window until the renderer binding is durable. `SUBSCRIBE(A)` followed by
`FLOW_CONTROL(paused=true)` is not equivalent: output may be routed between
those two frames, and the Relay cannot inspect their encrypted payloads. After
Snapshot S is installed, the client MUST request an authenticated and
contiguous suffix S+1 through R from Windows before connecting the live stream.
It MUST
persist cumulative ACK R without regressing an older durable ACK, then set both
`Envelope.ack` and the decrypted `SubscribePayload.resume_from_seq` to R. The
first ordinary replay frame is exactly R+1. With no restored suffix, R equals S.

`HOST_HISTORY_REQUEST` and `HOST_HISTORY_RESPONSE` are correlated by a 16-byte
request ID. Each response repeats bounded authenticated selection metadata and
is divided into chunks smaller than the Relay frame limit. Android sends a
`HOST_HISTORY_CONTROL(CHUNK_ACK)` after accepting each chunk; Windows sends no
next chunk for that request until the acknowledgement arrives. Timeout,
endpoint close, unsubscribe, or explicit cancellation sends/acts as
`HOST_HISTORY_CONTROL(CANCEL)`. These messages are request-scoped and never
advance the ordered terminal replay high-water mark.

The additive frozen history field and enum numbers are fixed in
`FROZEN_HOST_HISTORY_V1.md`. Its Windows capture lease and Android encrypted
page path are connected behind the peer `host_history_frozen_v1` and `tlhp_v2`
capabilities; full product acceptance still requires the documented end-to-end
tests. A Relay Welcome containing only `host_history_v1` does not enable it.

### State Delta V1 over Host History

This is an additive wire allocation with Windows and Android product adapters.
It remains optional and must fall back to a full snapshot whenever either peer
does not negotiate `state_delta_v1` or the selected base is unsuitable.

`state_delta_v1` is a separate **peer** capability: both the authoritative
Windows Host and the Android subscriber must support it for the session's
persistent terminal profile. It is carried in Host attach capabilities and
`HostHistoryRequestPayload.subscriber_capabilities`; the Relay continues to
negotiate only `host_history_v1` for opaque routing. A Relay Welcome that lists
`host_history_v1` does not prove either peer can encode or install Delta.
Older clients continue using the existing full-snapshot path.

`HOST_HISTORY_REQUEST_KIND_V1_STATE_DELTA = 7` carries
`HostHistoryRequestPayload.state_delta = 10`. Its `StateDeltaRequestV1`
identifies a 32-byte context, one supported Delta V1 profile, and the exact
base checkpoint's exclusive next source sequence, byte count, and 32-byte
SHA-256. Graphics v1 requires native Delta profile 1 (`LegacyV1`);
Unicode16 Graphics v2 requires profile 2 (`Unicode16V1`). The host maps this
from its persisted session profile and rejects a different request profile
with `NEEDS_FULL_SNAPSHOT`; the Android base checkpoint must carry that same
width and output profile. Native profiles 3/4 describe standalone-C1 output;
the current persisted Graphics descriptors use UTF-8 output 0 and do not
select them. Outer `snapshot_seq = 0` asks Windows to pin its
latest current snapshot sequence S when it accepts the request; nonzero selects
that exact S.
The response always states the actual S. A base/target mismatch, missing
journal entry, incompatible persistent profile, or ineffective/oversized
Delta returns `REJECTED` with `NEEDS_FULL_SNAPSHOT`, without an object. Android
then requests the existing full snapshot explicitly; it must not guess a new
Delta base or silently reinterpret the response as frozen history.
For this empty failure only, `snapshot_seq` may be zero when no valid target
exists, or the known current S; it is not used as a replacement Delta base.

`HOST_HISTORY_OBJECT_KIND_V1_STATE_DELTA = 5` has
`format_name = TERMLINK_STATE_DELTA_V1`, `format_version = 1`, and repeats
`HostHistoryResponsePayload.state_delta = 27` on every chunk. The metadata
contains context/profile and base/target exclusive next source sequences,
canonical snapshot byte counts, and 32-byte SHA-256 hashes. It requires
`target_next_source_sequence = response.snapshot_seq + 1` and a strictly newer
target than base. The object is a separate `TLDSEAL1` binary AEAD container
with a fixed 336-byte header, wrapping one complete `TLDELTA1` packet. Its
authenticated identity includes session, sender and recipient devices, key
epoch, persistent state contract, context, profile, and both checkpoint
identities. The outer 16-byte `request_id` correlates this transfer and its
chunk ACKs; it is not a field in the sealed object. The
outer request, every response chunk, sealed container, and decrypted core
packet must agree on these values and on the session's persistent profile.
`object_sha256` checks the sealed object bytes; the base/target hashes
identify canonical snapshot bytes. No frozen-history token or lease is used.
The outer `columns`, `rows` and `initial_transcript_before_line_id` fields are
zero for Delta objects. Dimensions come from the validated reconstructed
native snapshot, and the transcript anchor comes from the sealed header.

For `terminal_state_2_22_v1`, the context is
`SHA256(UTF8("TermLink state delta context V1\n") || RFC4122(session_id) ||
RFC4122(pc_device_id) || UTF8(state_contract))`. For
`terminal_state_2_22_v2`, the same construction uses the distinct
`TermLink state delta context V2\n` prefix. The v1 and v2 sealed objects also
use separate `TERMLINK-STATE-DELTA-KEY-V1\0` / `-V2\0` HKDF prefixes,
`TERMLINK-STATE-DELTA-OBJECT-V1\0` / `-V2\0` AAD prefixes, and
`delta-v1:` / `delta-v2:` Key IDs. `TLDSEAL1`, its container version, the
`TERMLINK_STATE_DELTA_V1` object format, and the v1 test vector retain their
existing meanings; the authenticated state contract selects the crypto domain.
Its derivation is identical on Windows and Android; a request cannot select
another session's context. The authenticated header also carries the target
`observedLocalInputEpoch` as a big-endian uint64 at offset 306 and the target
`initialTranscriptBeforeLineId` at offset 314. Bytes 322 through 335 are zero.
The target input epoch must be within signed 64-bit range and no smaller than
the trusted base snapshot's epoch. The native graphics transcript anchor is
currently 1. These two product fields are not part of `TLDELTA1`; Android must
use the authenticated target values when sealing the reconstructed snapshot,
never copy them from the base. AEAD failures and malformed native state are
errors, not `NEEDS_FULL_SNAPSHOT` responses.

The **sealed** object must be at least 608 bytes (336-byte header, 256-byte
native packet minimum, 16-byte AEAD tag) and at most 8 MiB inclusive.
`object_chunk` is at most
512 KiB; `chunk_count` is exactly `ceil(object_bytes / 512 KiB)` and at most
16, with contiguous indices and exact final-chunk length. The existing
`HOST_HISTORY_CONTROL(CHUNK_ACK/CANCEL)` provides request-scoped backpressure
and cancellation. Host and client each admit at most one pending Delta per
session/recipient; timeout, disconnect, cancellation, and failed verification
release its memory. The Relay forwards only the original encrypted Envelope
bytes under its existing 1 MiB frame and 8 MiB peer queue limits. Delta
chunks do not consume terminal event sequence, advance replay high-water, or
advance durable ACK. Android may change its durable recovery checkpoint only
after the complete sealed object and reconstructed target pass all identity,
hash, profile, and atomic-install checks, then resumes the ordered suffix.
Admission covers the base and reconstructed target as well as the wire
object before decryption or native apply; a small Delta does not authorize an
unbounded target allocation. The existing full snapshot cache and renderer
binding transaction persist the reconstructed state before terminal ACK
advances. The shared AEAD fixture is
`protocol/test-vectors/state-delta-sealed-v1.json`; native packet validity is
tested separately by terminal-core and is not implied by that crypto fixture.

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
the normal lifecycle/removal events. It durably marks and removes the local
session history, then returns `SESSION_TERMINATE_RESPONSE` for the same request
ID. Android removes server session metadata and local renderer/cache state only
after this acknowledgement. An ended session with retained local history uses
the same operation without requiring a live Shell. If Windows is offline the
operation fails; Android must not claim that authoritative history was deleted.
