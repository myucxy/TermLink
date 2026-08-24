# Terminal Snapshot V1 Contract

## Status

The protobuf field allocation and validation rules in this document are the
frozen Phase 1 object contract for implementation and cross-language tests.
The runtime capability is deliberately disabled: clients and Agents MUST NOT
advertise `terminal_snapshot_v1`, upload these objects, or use them to advance
an ACK until producer/importer equivalence tests have passed. This document is
not evidence that an end-to-end production snapshot pipeline is enabled.

The checked-in `terminal-snapshot-v1.json` is the shared cryptographic and
structural golden vector. The Kotlin crypto/domain-decoder tests and C#
ProtocolValidation tests both consume it, reproduce and open the same sealed
snapshot/transcript objects, and exercise metadata-tamper rejection. Passing
this vector proves cross-language object compatibility only; it does not
enable the runtime capability.

## Transport and sequence model

`TerminalSnapshotCiphertextV1` and `TerminalTranscriptCiphertextV1` are opaque
history objects transferred through authenticated REST/object-storage URLs.
They are not WebSocket `Envelope` payloads.

A snapshot is a checkpoint after the producer has applied every ordered event
through `snapshot_seq = S`. It does not allocate or consume an `Envelope.seq`.
The existing `SNAPSHOT_OFFER` and `SNAPSHOT_CHUNK` enum values are reserved and
MUST NOT carry `TerminalSnapshotV1` in Phase 1. In particular, sending several
snapshot chunks with `seq = S` would violate the existing same-sequence,
same-ciphertext replay rule, while assigning new sequence numbers would insert
events that old clients do not understand.

After an Android client has authenticated and restored snapshot S and durably
bound it to its renderer checkpoint, cumulative ACK S has the same semantic
meaning as applying events 1 through S. The client then sends `SUBSCRIBE(S)`.
The first newly applied ordered event MUST be S+1. A missing S+1 is a visible
History Gap and never authorizes ACK advancement.

## Sealed snapshot object

The serialized object stored by the server is
`TerminalSnapshotCiphertextV1`. The following outer fields are authenticated
metadata and MUST match the selected REST metadata and the decrypted inner
message:

- `container_version = 1`;
- 16-byte `session_id`, `pc_device_id`, and `recipient_device_id`;
- positive `key_epoch` and `key_id` exactly
  `snapshot-v1:<recipient UUID N>:<epoch D8>`;
- positive `snapshot_seq`;
- `columns`, `rows`, `format_name`, and `format_version`;
- `format_name = "TERMLINK_TERMINAL_SNAPSHOT"` and `format_version = 1`;
- a Phase 1-supported compression and exact `uncompressed_bytes`;
- a 12-byte nonce and AEAD ciphertext including its 16-byte tag.

Phase 1 supports `SNAPSHOT_COMPRESSION_V1_NONE` only. `UNSPECIFIED` and `ZSTD`
are rejected until a bounded, cross-language Zstandard profile is separately
accepted.

The snapshot key is a new 32-byte HKDF-SHA256 output from the existing X25519
pairwise secret. It uses `session_id` as salt and this info byte string:

```text
TERMLINK-TERMINAL-SNAPSHOT-KEY-V1\0 ||
recipient_device_id || int64be(key_epoch) || uint16be(key_id_length) || key_id
```

It MUST NOT reuse a realtime-direction key or the bounded-history chunk key.
The AEAD algorithm remains ChaCha20-Poly1305. A nonce MUST NOT repeat for the
same derived key. An idempotent upload retry reuses the already sealed object;
it does not reseal the same database object identity.

Snapshot associated data is the following unambiguous byte string:

```text
TERMLINK-TERMINAL-SNAPSHOT-OBJECT-V1\0 ||
uint32be(container_version) ||
session_id || pc_device_id || recipient_device_id ||
int64be(key_epoch) || uint16be(key_id_length) || key_id ||
uint64be(snapshot_seq) || uint32be(columns) || uint32be(rows) ||
uint16be(format_name_length) || format_name || uint32be(format_version) ||
uint32be(compression_enum_number) || uint64be(uncompressed_bytes) || nonce
```

All strings in key derivation and associated data are UTF-8. Lengths count
bytes, not characters. The plaintext is the serialized
`TerminalSnapshotV1`; Phase 1 `NONE` compression leaves those bytes unchanged.

## TerminalSnapshotV1 validation

An importer validates the complete object before changing a live renderer:

- `format_version = 1`, `snapshot_seq >= 1`, and the inner sequence/size equal
  the authenticated outer values;
- `1 <= columns <= 512` and `1 <= rows <= 256`;
- `active_screen` is PRIMARY or ALTERNATE;
- `parser_state` is exactly GROUND; a producer refuses a checkpoint while an
  UTF unit, CSI, OSC, DCS, grapheme, or other parser construct is incomplete;
- both primary and alternate screens exist and each contains exactly `rows`
  visible rows, even when one screen is inactive;
- primary visible-row `line_id` values are positive and strictly increasing;
  `next_transcript_line_id` is exactly the last primary ID plus one;
- a new producer writes `line_id = 0` on every alternate row. An importer may
  accept a pre-freeze V1 object with non-zero alternate IDs for compatibility,
  but it ignores those values: they are never part of the primary/transcript
  namespace, never become a paging cursor, and are not restored as stable IDs;
- each expanded row contains exactly `columns` cells;
- style table size is 1 through 1,024 and every style reference is in range;
- the palette contains exactly 256 `0xRRGGBB` entries and color references are
  default, palette index 0 through 255, or `0xRRGGBB`;
- hyperlink index zero means absent; a non-zero value minus one indexes the
  hyperlink table;
- tab stops are strictly increasing and less than `columns`;
- cursor and saved-cursor coordinates are in bounds; scroll and left/right
  margins are ordered and in bounds;
- charset slots are explicit supported values, while `gl` and `gr` are 0
  through 3;
- all enum fields needed for restoration are non-`UNSPECIFIED`;
- `title` is at most 4,096 UTF-8 bytes and any grapheme is at most 64 UTF-8
  bytes;
- an ENDED projection has an explicit `exit_code`; other lifecycle states do
  not fabricate one.

For `TerminalCellRunV1`, `repeat` is positive. A normal blank or width-one cell
has `display_width = 1`. A width-two leading grapheme has `repeat = 1` and is
immediately followed by one run with `wide_continuation = true`, empty
`grapheme`, `display_width = 0`, and `repeat = 1`. No other width-zero cell is
valid. A non-empty grapheme is never repeated by run-length encoding.

The combined expanded cells in primary screen, alternate screen, and
scrollback MUST NOT exceed 262,144. The sealed snapshot object is at most
2 MiB and `uncompressed_bytes` is at most 8 MiB. An importer checks advertised
sizes before allocation and checks the actual decoded size afterward.

Phase 1 is last-visible-screen only:

- `scrollback_rows` MUST be empty;
- `scrollback_first_line_id` MUST be its zero sentinel;
- no raw VT suffix or middle chunk is used to synthesize scrollback;
- a state containing an active, unrepresentable terminal feature is refused
  by the producer instead of being approximated.

The primary and alternate buffers, pending-wrap state, current rendition,
saved cursor, charset state, margins, tab stops, modes, palette, title, and
session projection are all required because later bytes can depend on them.

The current Phase 1 representation still has four known blockers for general
producer enablement: saved auto-wrap, the saved DEC private-mode bank, DECSACE
state, and the title stack cannot be represented losslessly. Until the wire
contract or a strictly defined producer profile resolves each blocker, a
producer MUST refuse a checkpoint whenever later input can depend on one of
these states. It MUST NOT substitute the current mode, discard the title
stack, or otherwise approximate the missing state.

Every `uint64` value crossing the Kotlin/C# boundary in a snapshot, transcript
page, or sealed-object metadata MUST be at most `Int64.MaxValue` (`2^63-1`). A
field that is defined as positive also rejects zero. Zero remains valid only
where its field semantics allow it, including alternate-row IDs, the empty
scrollback sentinel, a visible snapshot row not yet sealed by ordered output,
an initial local-input epoch, and `next_before_line_id` when no older page
exists. Since `next_transcript_line_id` must remain representable, the last
primary line ID is strictly less than `Int64.MaxValue`.

## Atomic Android restore

The bootstrap state machine is normative:

```text
read durable ACK A
  -> select at most one snapshot S where A <= S <= session last_output_seq
  -> enforce metadata and object-size bounds
  -> verify object SHA-256, outer identity, key epoch, AAD, and AEAD
  -> validate the entire TerminalSnapshotV1
  -> durably cache the sealed object
  -> restore a candidate renderer off-screen
  -> atomically bind {checkpoint identity, digest, renderer generation, ACK S}
  -> install the candidate renderer
  -> SUBSCRIBE(resume_from_seq=S, Envelope.ack=S)
  -> accept S+1 and ACK it only after terminal-event application succeeds
```

`S < A` is rejected because an ACK never regresses. If the process restarts
after the durable binding, it MUST restore that same cached checkpoint before
advertising ACK S. If any step before the durable binding fails, ACK S is not
published and the implementation uses its earlier safe recovery path.

The REST selector used by a snapshot-capable deployment MUST return at most one
eligible metadata record and one bounded object. A server without that selector
or a session without an eligible snapshot falls back to bounded raw replay or
an explicit History Gap; compatibility never permits a middle raw VT chunk to
be fed into an empty parser.

## Transcript pages

`TerminalTranscriptPageV1` is a separate read-only primary-screen row
projection. It is not terminal parser state. Lines receive stable, increasing,
positive `line_id` values when the producer seals them into transcript history.
Transcript and primary rows share that single namespace. Consequently, the
last transcript row is less than the first visible primary row. Alternate
screen rows are not transcript rows in Phase 1, regardless of a legacy
alternate ID accepted by an importer.

A page is anchored by `as_of_snapshot_seq = S`. Its rows are oldest to newest,
have strictly increasing line IDs, and every `sealed_at_seq` is positive and no
greater than S. `first_line_id` and `last_line_id` equal the endpoint row IDs.
A non-empty page contains at most 200 rows. When `has_more_before` is true,
`next_before_line_id` equals `first_line_id`; otherwise it is zero.

Paging uses an exclusive `before_line_id = B` cursor: every returned row ID is
strictly less than B. The first upward-scroll request uses the first visible
primary row ID as B. A subsequent request keeps the same S and uses the prior
page's `next_before_line_id`; changing S during a traversal is rejected. The
wire page is deliberately non-empty. If no row exists below B, the history
endpoint returns no page (for example, an empty/404 result), rather than an
empty `TerminalTranscriptPageV1`. A final non-empty page sets
`has_more_before = false` and `next_before_line_id = 0`.

The initial live bootstrap downloads no transcript page. An upward-scroll
request downloads only the preceding page for the fixed S. Android renders
pages in an independent history viewport. It MUST NOT prepend their cells or
reconstructed raw bytes to the live terminal parser, and browsing MUST NOT
change the live ACK. The live parser may continue applying new events while
history is shown.

The serialized REST object is `TerminalTranscriptCiphertextV1`. It follows the
snapshot rules with these substitutions:

- key ID prefix `transcript-v1:`;
- HKDF info prefix `TERMLINK-TERMINAL-TRANSCRIPT-KEY-V1\0`;
- AAD prefix `TERMLINK-TERMINAL-TRANSCRIPT-OBJECT-V1\0`;
- AAD fields after key ID are `as_of_snapshot_seq`, `first_line_id`,
  `last_line_id`, format metadata, compression, uncompressed size, and nonce;
- `format_name = "TERMLINK_TERMINAL_TRANSCRIPT"` and `format_version = 1`;
- sealed size at most 512 KiB and uncompressed size at most 2 MiB.

Transcript styles, palette, rows, and hyperlinks obey the same structural
validation as snapshot rows.

## Compatibility and enablement gate

All protobuf additions are new messages and enum values; existing field
numbers, `MessageType` values, Envelope rules, and history messages are
unchanged. Old clients and Agents continue bounded event replay. New clients
fall back when the server or PC has no eligible object.

The shared C#/Kotlin seal/open vector and its metadata-tamper checks are checked
in and passing. The runtime feature nevertheless remains disabled by default
until all of the following remaining gates are checked in and passing:

1. producer/importer comparisons proving
   `replay(1..N) == restore(snapshot S) + replay(S+1..N)` across the accepted VT
   corpus;
2. crash tests at cache, renderer restore, durable binding, and subscribe;
3. S+1 concurrency, duplicate, retained-buffer Gap, size-bound, and
   decompression-bomb tests;
4. primary/alternate screen, wrap, margins, cursor, charset, palette, wide
   grapheme, resize, lifecycle, and local-input-epoch coverage;
5. explicit producer refusal or a lossless representation for saved
   auto-wrap, the saved DEC private-mode bank, DECSACE state, and the title
   stack;
6. Windows integration that consumes durable ordered `LocalFrame` events,
   produces per-recipient sealed objects, uploads them idempotently, and only
   then negotiates `terminal_snapshot_v1`.
