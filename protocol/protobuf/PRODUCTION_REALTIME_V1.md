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
only binary protobuf `Envelope` messages.

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

Phase 1 snapshots contain both visible terminal buffers but an empty snapshot
scrollback. Older rows are separate, bounded `TerminalTranscriptPageV1`
objects fetched only on upward scroll. Transcript browsing never changes the
live ACK and transcript data is never prepended to a live VT parser.

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
