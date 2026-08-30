# TermLink Terminal Core

This directory contains the first, dependency-free implementation of the
TermLink cross-platform terminal state engine. It is fully self-developed,
original TermLink code: it does not depend on, link to, or copy Windows
Terminal (including `terminal-main/TerminalCore`), termForward, or another
platform terminal implementation. It keeps platform PTY ownership outside the
portable Rust state engine and stable C ABI.
Cross-platform is the source-architecture goal; the verified target matrix is
stated separately below.

## Workspace

- `termlink-terminal-core`: deterministic terminal state, event application,
  VT subset, bounded runtime scrollback, and Snapshot V2.0 through V2.8.
- `termlink-terminal-ffi`: panic-contained C ABI and owned-buffer boundary.
- `include/termlink_terminal.h`: public C declarations.
- [`UNIMPLEMENTED_CAPABILITIES.md`](UNIMPLEMENTED_CAPABILITIES.md): explicit
  DRCS, SIXEL/Kitty graphics, History Page, and Delta gaps, dependencies, and
  completion criteria.
- [`DEVELOPMENT_ROADMAP.md`](DEVELOPMENT_ROADMAP.md): prioritized, resumable
  plan for evolving the core into a broadly compatible CLI/TUI production
  runtime, including cross-platform and release gates.

Status note (2026-08-30): Windows and Android now use the native core in their
production authority/render paths. Later passages that still describe Windows
shadow mode or Android integration as future work are historical phase notes;
their cleanup is tracked as W0 in `DEVELOPMENT_ROADMAP.md`.

Every public C structure carries `abi_version` and `struct_size`; callers set
these fields and may query every public structure size. ABI v1 accepts a known
prefix plus an unknown tail, but rejects a short prefix before reading its
remaining fields. Returned owned buffers are stamped by the library and must
remain unchanged until they are freed by the same library.

The native library ABI remains version 1. `TlTerminalConfigV1` is 40 bytes and
`tl_terminal_create` always creates a terminal with scrollback disabled.
`TlTerminalConfigV2` is an additive 64-byte configuration accepted by
`tl_terminal_create_v2`; its `history_capacity_rows` field is an unsigned count
of physical terminal rows, not a byte or allocator-size limit. All V2 reserved
fields must be zero. The C entry point rejects values above `u32::MAX`, while
the core deterministically caps the effective request to 10,000 physical rows
and the per-screen 262,144-cell limit. The .NET binding validates both native
structure sizes and uses V1 when the requested capacity is zero and V2 when it
is nonzero, without narrowing its public `ulong` argument.

`TlTerminalConfigV3` is an additive, exact-size 88-byte configuration accepted
by `tl_terminal_create_v3`. It retains the V2 history field and adds an explicit
`output_profile`: `TL_OUTPUT_PROFILE_UTF8` preserves the existing UTF-8-only
output path, while `TL_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1` enables the
supported one-byte C1 aliases. V3 requires `struct_size == 88`, every reserved
byte to be zero, a history value no greater than `u32::MAX`, and one of those
two known profile values. V1 and V2 creation continue to select the UTF-8
profile. The .NET binding discovers and validates the V3 layout and exposes an
explicit profile-selecting overload; this does not switch the Windows product
renderer to the native core.

Semantic input V2 is additive and leaves both the ABI version and the complete
56-byte `TlTerminalInputV1` contract frozen. `TlTerminalInputV2` is 96 bytes;
its first 56 bytes are the V1 layout and its extension carries one lossless
Win32 `KEY_EVENT_RECORD` as virtual key, scan code, UTF-16 code unit, make/break
state, control-key state, and repeat count. `tl_terminal_encode_input_v2`
accepts `TL_INPUT_KIND_WIN32_KEY` only while DEC private mode `?9001` is active
and emits `CSI Vk;Sc;Uc;Kd;Cs;Rc _`; otherwise it returns
`TL_STATUS_UNSUPPORTED`. Existing V1 kinds remain byte-identical when all V2
extension fields are zero, and the V1 entry point rejects the new kind.

Render Frame V2 and V3 are additive too: the existing V1/V2 discovery/export
symbols and bytes remain unchanged. V2 adds
`tl_terminal_render_frame_v2_format_version`,
`tl_terminal_render_frame_v2_max_bytes`, and
`tl_terminal_checkpoint_export_render_frame_v2`; V3 adds the corresponding
`tl_terminal_render_frame_v3_format_version`,
`tl_terminal_render_frame_v3_max_bytes`, and
`tl_terminal_checkpoint_export_render_frame_v3` symbols. The .NET binding
performs explicit V2/V3 discovery handshakes, invokes the real checkpoint
exports, and strictly decodes the returned bytes; this is native
binding/validation, not Windows production-path adoption.

DEC synchronized output is exposed through another additive ABI surface without
changing ABI version 1. `TL_STATUS_PRESENTATION_DEFERRED` is status 13. The
48-byte `TlTerminalPresentationInfoV1`, its structure-size query, and
`tl_terminal_get_presentation_info_v1` report failed-closed, incomplete-parser,
and synchronized-output flags together with source sequence, logical time, and
the active synchronization deadline. The .NET binding performs the same layout
handshake and treats status 13 as temporary; Android JNI/product integration is
outside this Windows-first change.

Reply-producing DA/DSR/CPR now use the additive Host Effect Frame V1 surface.
The legacy apply entry points intentionally discard host effects; authority
hosts use `tl_terminal_apply_event_with_host_effects_v1` or
`tl_terminal_apply_event_batch_with_host_effects_v1`, then write each decoded
`PTY_REPLY` payload to the authoritative PTY in source-key order. Terminal
state is committed only after the owned effect buffer has been allocated and
registered successfully. Publisher failure or panic releases that pending
buffer and leaves the source sequence unconsumed. A future Android mirror must
not execute or feed these effects back into Windows.

The C ABI v1 label is the native library ABI version. It is unrelated to the
disabled experimental `terminal_snapshot_v1` protocol.

The workspace is pinned to Rust 1.97.1 and has no crates.io dependencies.

## Event contract

Creation declares the first expected `source_sequence`, so a durable journal
may start at either zero, one, or another recoverable cursor. Exactly one source
sequence is consumed by each successfully applied `OUTPUT`, `RESIZE`, or
`ADVANCE` event. Single-event and bounded batch application are atomic. A stale
sequence is rejected without mutation; a forward jump or any `GAP` fails the
instance closed. The native batch ABI accepts 1..256 events, limits each output
to 1 MiB and all output payloads in one batch to 4 MiB, validates the complete
batch first, and commits it through one core transaction.
Invalid dimensions, oversized output, and clock overflow leave state unchanged
and may be retried at the same sequence.

Host Effect Frame V1 uses magic `TLHEF1\0\0`, a 64-byte little-endian header,
40-byte records, independent header/body CRC32C values, and required feature
bit 0 for `PTY_REPLY`. Records are strictly ordered by
`(source_sequence, source_offset_end, ordinal)`; V1 ordinal is always zero and
payloads are contiguous. The exact hard maximum is 65,710,862 bytes for the
same 256-event/4-MiB capture limit. A successful apply with no effects still
returns the canonical owned 64-byte empty frame.

An unsupported escape/control sequence also fails the instance closed. A
failed-closed instance accepts no more events and cannot create a checkpoint.
Destroy it and recover from a prior checkpoint plus a continuous source suffix.

`CSI ?2026h` begins DEC synchronized output and `CSI ?2026l` ends it. Begin is
not nested; another begin restarts a one-second logical-time deadline. The core
does not read a system clock or start a timer. An authority host must translate
its monotonic timer into the existing journaled `ADVANCE` event so every replay
observes the same release boundary. Reaching the deadline, an explicit end, or
RIS releases presentation.

Snapshot V2 is a canonical sectioned little-endian binary format with a fixed
header, ordered critical-section directory, and independent header/payload
CRC32C checks. It includes both screens, graphemes and continuation cells, wrap
metadata, cursor/rendition/protection, input and output modes, margins, dynamic
tab stops, charset state, OSC title/palette/default-color/hyperlink state,
source sequence, logical advance time, and the fixed Unicode width profile. A
checkpoint is temporarily ineligible while UTF-8, ESC, CSI, OSC, DCS, SOS, PM,
or APC input is incomplete. While synchronized output is active, state continues to apply
atomically but Snapshot V2, all three Render Frame versions, and checkpoint capture
return `TL_STATUS_PRESENTATION_DEFERRED`. Import validates all limits and
rejects non-Ground, trailing,
non-canonical, or invalid data. Exporting an imported snapshot without applying
an event produces identical bytes.

DECLRMM uses the existing mode-bank record in namespace 1, parameter 69, and
DECSLRM uses the existing 24-byte margin section; it does not add a wire
profile, feature bit, or maximum-size change. Import requires disabled DECLRMM
to carry the canonical full-width, follows-screen horizontal margin. Custom
vertical or horizontal margins must span at least two cells, remain in bounds,
and must not claim follows-screen intent. The active screen's current and saved
pending-wrap states are valid only at the effective right boundary for their
rows. Violations are rejected as malformed snapshots.

Snapshot export now performs the same margin/mode and active pending-wrap
self-checks before encoding. Import skips the otherwise-required frozen-primary
reflow-target preflight when the alternate screen is active and DECLRMM is
enabled, because that primary will be restored by the defined crop path rather
than reflow. A resize crop leaves pending-wrap normalization to `Terminal`,
which revalidates the active current/saved cursor against the effective right
boundary after dimensions and margins have been updated.

Snapshot export selects the lowest canonical profile that preserves the live
state. Default cursor style means blinking block. When no extended SGR state is
present, the exact legacy profile matrix remains:

| Requested history | REP source | Non-default cursor style | Snapshot | Features |
| --- | --- | --- | --- | --- |
| no | no | no | V2.0 | `0x18ff` |
| yes | no | no | V2.1 | `0x38ff` |
| no | yes | no | V2.2 | `0x58ff` |
| yes | yes | no | V2.2 | `0x78ff` |
| no | no | yes | V2.3 | `0x98ff` |
| yes | no | yes | V2.3 | `0xb8ff` |
| no | yes | yes | V2.3 | `0xd8ff` |
| yes | yes | yes | V2.3 | `0xf8ff` |

V2.1 adds history feature `0x2000`; its four screen metadata/row sections use
section version 2 and carry visible logical-line anchors and the normalized
history request. V2.2 adds REP feature `0x4000`; `CORE_STATE` version 2 uses the
32-byte payload's flags at offset 26 and grapheme reference at offset 28.
V2.3 adds cursor-style feature `0x8000` and always uses `CORE_STATE` version 3.
In its flags, bit 0 denotes a REP source, bit 1 denotes non-blinking, and bits
2..3 encode block/underline/bar. A default blinking block never selects V2.3.

Faint, overline, an underline style other than `None`/`Single`, or a non-default
underline color in either screen's visible cells, current attributes, or saved
attributes requires Snapshot V2.4 and feature `0x1_0000`. V2.4 preserves the same eight
history/REP/cursor combinations exactly:

| Requested history | REP source | Non-default cursor style | Snapshot | Features |
| --- | --- | --- | --- | --- |
| no | no | no | V2.4 | `0x118ff` |
| yes | no | no | V2.4 | `0x138ff` |
| no | yes | no | V2.4 | `0x158ff` |
| yes | yes | no | V2.4 | `0x178ff` |
| no | no | yes | V2.4 | `0x198ff` |
| yes | no | yes | V2.4 | `0x1b8ff` |
| no | yes | yes | V2.4 | `0x1d8ff` |
| yes | yes | yes | V2.4 | `0x1f8ff` |

Only `RENDITION_TABLE` changes to section version 2 in V2.4. Its record stays
48 bytes and preserves faint, overline, the six valid underline styles
(`None`, `Single`, `Double`, `Curly`, `Dotted`, and `Dashed`), and underline
color. All other section versions continue to be selected solely by the
history/REP/cursor dimensions above. V2.0 through V2.3 export and import remain
byte-stable; decoding a legacy rendition supplies defaults for the added state.

Enabling DEC private mode `?9001` selects Snapshot V2.5 and feature
`0x2_0000`. V2.5 has 16 exact profiles: the four independent history, REP,
non-default cursor-style, and extended-SGR dimensions determine their existing
feature bits and section versions, while `MODE_BANK` alone advances to section
version 2 and carries namespace 1 / parameter 9001. Disabling the mode selects
the lowest V2.0..V2.4 profile that still preserves the remaining state. Hard
reset also disables the mode. V2.0 through V2.4 bytes and selection rules are
unchanged.

Kitty keyboard protocol negotiation is implemented independently of Win32
Input Mode. `CSI = flags ; mode u`, `CSI ? u`, `CSI > flags u`, and
`CSI < count u` manage independent, bounded eight-entry main/alternate stacks.
Only flags `0x1f` are supported; unknown future bits are masked. Query replies
use the existing ordered PTY-reply effect path. Hard reset clears both stacks,
while ordinary main/alternate switching preserves each stack.

Any non-empty Kitty stack selects Snapshot V2.6 with feature `0x4_0000` and a
13th critical `KEYBOARD_PROTOCOL_STATE` section. The section stores both stacks
oldest-to-current and composes with all history, REP, cursor-style,
extended-SGR, and Win32-mode dimensions. Empty stacks downgrade to the exact
pre-existing V2.0..V2.5 profile and bytes.

Any state that requires a national replacement character set selects Snapshot
V2.7 and feature `0x8_0000`. This includes DEC private mode `?42`, an active NRC
designation, or an NRC designation in either screen's saved charset state.
V2.7 composes NRC with history, REP, cursor style, extended SGR, Win32 input
mode, and Kitty keyboard state, for 64 exact profiles. NRC advances
`MODE_BANK` to section version 3 and `CHARSET_STATE` to version 2; its 12
designator IDs are bounded and strictly validated. Removing all NRC state from
the UTF-8 profile canonically downgrades to the lowest matching V2.0..V2.6
profile and reproduces the older bytes.

The explicit `Utf8WithStandaloneC1` output profile selects Snapshot V2.8 and
feature `0x10_0000`. It composes with the seven optional dimensions of history,
REP, cursor style, extended SGR, Win32 input mode, Kitty keyboard state, and
NRC, for 128 exact profiles. The profile itself adds no section; V2.8 persists
it through its minor/feature pair, so import plus suffix replay retains the
same byte interpretation and RIS does not change the profile. The default UTF-8
profile continues to select the lowest V2.0..V2.7 profile that preserves its
state.

The format minor and minimum-reader minor must match exactly. This reader
strictly accepts only the eight legacy profiles, eight V2.4 profiles, 16 V2.5
profiles, 32 V2.6 profiles, 64 V2.7 profiles, and 128 V2.8 profiles described
above, and rejects feature,
core/screen/rendition/mode-section-version, rendition enum, color/reserved
bits, reference, UTF-8, width, and canonical-table mismatches.
Import followed by export without an event is byte-identical for every accepted
profile. The exact current source-derived maximum canonical Snapshot encoding
is 44,568,090 bytes; import retains the independent 64 MiB hard limit.

All history-enabled profiles deliberately project only the two visible screens:
hidden scrollback cells, their sealed revisions, and history-only
grapheme/rendition entries are not included. The separately bounded OSC state
remains part of the snapshot. Import therefore restores an empty history prefix
while preserving the capacity policy and visible logical anchors for
subsequent output.

The C ABI freezes an opaque immutable checkpoint before exporting its bytes or
metadata. Import constructs and validates a separate terminal before publishing
its handle, so a failed import cannot partially mutate a live terminal.

The same checkpoint independently freezes canonical renderer-only Render Frame
V1, V2, and V3 bytes. All use a 96-byte little-endian header, the same six
ordered payload sections and zero padding, with independent header/payload
CRC32C checks. V1/V2 APIs and bytes remain frozen. V2 uses magic `TLRFRM2\0`,
major 2 and required features `0x3f`, and stores cursor shape/blinking at header
offsets 68/69. V3 uses magic `TLRFRM3\0`, major 3, required features `0x7f`, and
inherits the V2 cursor header. Only its rendition section advances to version 2;
the record remains 16 bytes, with flags bits 7/8 for faint/overline, bits 9..11
for underline style `0..5`, and packed underline color at offset 12. The
underline-present bit must agree exactly with a non-`None` style, and unknown
flags, styles, colors, section versions, or reserved data are rejected.
The Windows .NET V3 DTO/decoder tests enforce layout, version isolation, CRC,
canonical-table/reference, color, and malicious-frame rejection rules. They
validate the Windows wire adapter only; they do not establish renderer authority.

V3 preserves all extended rendition state. V1/V2 deliberately drop faint,
overline, and underline color and map every non-`None` underline to single,
then canonical-sort, deduplicate, and remap rendition IDs. This deterministic
downgrade keeps the legacy byte contracts stable. A frame is not a restorable
snapshot. All three versions have a 16 MiB hard limit, and the exact current
maximum canonical encoding remains 13,395,208 bytes. Hidden history is excluded
from all frame tables, row metadata, and cells.

## Runtime scrollback

Scrollback is opt-in through `TerminalOptions.history_capacity_rows` or C/.NET
Config V2. Stored rows are represented as `[history prefix][visible suffix]`,
with `viewport_top` equal to the current history-row count. Public `cell()`
coordinates and all three Render Frame versions remain viewport-relative.

Only upward scrolling of the full primary viewport caused while applying an
output event is captured. This includes ordinary line feed/autowrap and a
full-viewport `CSI S`. Alternate-screen output, partial-margin scrolling,
insert/delete line, reverse index, `CSI T`, and ordinary visible editing neither
append to nor mutate the history prefix. Eviction occurs before append. When
the head of a soft-wrapped logical line is evicted, the first retained fragment
keeps its logical ID/offset and is marked head-truncated. A hard-terminated
logical line wholly outside the viewport is sealed with that output event's
post-commit source revision; every resident fragment of the line receives the
same revision. Failed atomic batches roll back the history, seals, and logical
ID frontier together with the rest of terminal state.

Resize/crop/reflow preserves the history/viewport boundary and visible cursor,
saved-cursor, and logical anchors within the configured limits. This is runtime
storage only: no public History Page wire/API exists yet, and a checkpoint does
not freeze the hidden history prefix.

## Phase-one behavior

Implemented controls are CR, LF, BS, HT, DEL, ESC `7`, `8`, `D`, `E`, `H`,
`M`, `c`, `=`, `>`, CSI cursor movement/position, character/line insertion and
deletion (`@/P/L/M`), character erase (`X`), active scrolling (`S/T`),
REP (`CSI Ps b`), erase-in-display modes 0..3 and erase-in-line modes 0..2,
save/restore cursor, and ordered SGR
including bold/faint, italic, blink, inverse, conceal, strikethrough, overline,
six underline styles, and foreground/background/underline color. Selectors
38/48/58 accept both semicolon (`P;5;n`, `P;2;r;g;b`) and colon
(`P:5:n`, `P:2::r:g:b`) forms,
DECSTBM vertical margins, DECLRMM (`CSI ?69h/l`) and DECSLRM
(`CSI Pl;Pr s`) horizontal margins, IRM, DECOM, DECAWM, cursor visibility, DECSCUSR
(`CSI Ps SP q`) cursor shape/blinking for `Ps=0..6`, the
`?2026` synchronized-output begin/end mode,
`?47`, `?1047`, `?1048`, and `?1049` alternate/save/restore transitions,
dynamic HTS/TBC/CHT/CBT tab stops, and the
tracked application cursor/keypad, bracketed-paste, focus, xterm
`modifyOtherKeys` levels 0..3, and mouse modes.
The implemented DEC rectangle subset includes DECSCA, DECERA, DECSERA, DECFRA,
DECCRA, DECCARA, DECRARA, and DECSACE with stream/rectangle extent semantics. Charset
state includes G0-G3, GL/GR invocation, SI/SO, SS2/SS3, ASCII, DEC Special
Graphics, ISO Latin-1 supplemental mapping, and 12 Western NRC repertoires.
DEC private mode `?42h/l` enables or disables NRC replacement without
discarding designation or invocation state; both ordinary GL output and
DECFRA use the mode-aware mapping. Stateful presentation support
includes OSC titles, the unified 10-slot CSI 22/23 `t` title stack, the
256-color palette, default foreground/background/
cursor colors, and OSC 8 hyperlinks stored on cells independently of SGR.
Parser and UTF-8 state can span arbitrarily split output events. Known
state-changing but unimplemented OSC/DCS families fail closed; safely ignorable
queries and genuinely unknown strings are consumed without mutating state.

The 7-bit SOS (`ESC X`), PM (`ESC ^`), and APC (`ESC _`) introducers use a
bounded-state ignore path through 7-bit ST (`ESC \`). Their contents are
discarded rather than buffered, so arbitrarily split strings retain constant
parser memory; CAN/SUB cancels them, NUL/DEL and ordinary embedded C0 controls
including BEL are inert, and ESC drives ST/escape handling. An incomplete
string gates checkpoints and Snapshot/Render Frame export. In the default
UTF-8 profile these are only the 7-bit spellings. The explicit standalone-C1
profile also recognizes the corresponding one-byte C1 aliases at UTF-8 scalar
boundaries, including SOS/PM/APC and ST. Neither profile interprets a protocol
carried inside APC, such as Kitty graphics.

OSC and DCS likewise handle embedded C0 bytes explicitly instead of executing
them against the screen or retaining them as payload. CAN/SUB cancels a string,
and `ESC \` is the 7-bit ST terminator. BEL terminates OSC, including when an
ST is tentative, but remains inert inside DCS; other embedded C0 bytes are
ignored in OSC selector/payload and DCS entry/passthrough/ignore states. The
existing conservative fail-closed rule for CAN/SUB after a tentative ESC in a
dispatchable DCS remains in force.

Extended SGR parsing is strict and ordered. `2` enables faint and `22` clears
both bold and faint; `4`, `21`, and `24` select single, double, and no
underline; `4:0..5` selects none/single/double/curly/dotted/dashed; `5` and `6`
both enable blink; `53/55` toggle overline; and `58/59` set/reset underline
color. Incomplete or malformed colors, invalid styles, mixed separators, and
unknown SGR parameters fail closed before any operation is applied, preserving
event and batch atomicity. The parser retains its fixed maximum of 16 scalar
parameters.

Standard blank-producing erase/edit/scroll paths use a standard erase
attribute: they retain only the current foreground and background colors and
reset bold, faint, italic, underline and underline color, blink, inverse,
conceal, strikethrough, and overline. The new blank is unprotected and has no
hyperlink. This applies to ED/EL/ECH, ICH/DCH, IL/DL, ordinary inserted or
full-width scrolled-in blanks, and DECERA. DECSERA deliberately differs by
retaining the erased cell's attributes, while DECFRA uses the complete current
presentation.

ED mode 3 (ED3) clears only the active screen's hidden runtime scrollback
prefix. It does not erase visible cells or change the cursor, modes, rendition, or
configured history capacity, and subsequent full-primary scrolling resumes
history capture. ED mode 2 (ED2) remains a visible-display erase and does not
emulate the special Windows console/terminal behavior that also clears or repositions
history. Accepted ED/EL/ECH/ICH/DCH/IL/DL operations cancel delayed wrap before
the next printable character, and resetting DECAWM (`CSI ?7l`) does the same.

DECCARA reuses the same complete ordered SGR operation parser. It changes only
cell attributes, leaving characters, protection, hyperlinks, meaningful ends,
row metadata, and current/saved presentation unchanged. DECRARA reverses bools
and valid underline enum states without creating invalid styles; parsed color
selectors are defined no-ops for reversal.

Each DECLRMM enable or disable resets the horizontal margins to the full screen.
While DECLRMM is enabled, `CSI s` is DECSLRM: missing or zero left/right values
default to 1/the current column count, a valid range spans at least two columns,
and an invalid range leaves both margins and cursor unchanged. A valid DECSLRM
homes the cursor according to DECOM. While DECLRMM is disabled, parameterless
`CSI s` retains ANSI save-cursor semantics; a parameterized form is unsupported
and fails closed rather than being misread as a margin command.

When the cursor intersects both margin axes, normal printing and REP, delayed
autowrap, LF/VT/FF/IND and RI, ICH/DCH/ECH, IL/DL, and `CSI S/T` use the
half-open `[top,bottom) x [left,right)` rectangle. A custom-right delayed wrap
continues at the custom left edge. Rectangle-local scrolling does not append
primary history or create whole-row soft-wrap metadata, cells outside the
rectangle are preserved, and wide graphemes cut by either horizontal boundary
are repaired without orphan continuation cells. Outside the applicable axes,
operations retain the physical-screen behavior; the full-width path keeps its
existing history capture, row metadata, and background-color-erase semantics.
CR and forward/backward tab movement likewise select the custom edge only when
the cursor position makes that axis applicable.

DECCRA accepts `CSI Pts;Pls;Pbs;Prs;Pps;Ptd;Pld;Ppd $ v` with at most eight
parameters. Missing or zero coordinates default to the corresponding origin or
page edge, and missing or zero page parameters select page 1. This is a
single-page model: effective page 1 is accepted, while any other page fails
closed. DECOM makes source/destination rows relative to DECSTBM; columns are
relative to DECSLRM only when DECOM and DECLRMM are both enabled. A reversed
source rectangle is a no-op, and source/destination rectangles are clipped to
the applicable page. The source is staged before any destination mutation, so
overlapping copies are deterministic. Each copied cell retains its complete
grapheme/combining data, rendition, protection, and hyperlink; clipped source
or destination wide-character boundaries are repaired. The operation leaves
the current/saved cursor, pending-wrap, current presentation, history,
soft-wrap flags, and logical-row metadata unchanged.

DECCRA is an existing-grid edit and adds no Snapshot, Render Frame, C ABI, or
other wire field/version. Its result is captured through the already-defined
cell presentation in later snapshots and frames.

CUP/HVP/CHA/VPA use page coordinates with DECOM disabled. With DECOM enabled,
rows are relative to DECSTBM; columns become relative to DECSLRM only when both
DECOM and DECLRMM are enabled. CPR and DECXCPR use the same coordinate rule and
clamp only the reported values without moving the real cursor or changing
pending-wrap state.

DECSCUSR treats missing, 0, and 1 as blinking block; 2 as steady block; 3/4 as
blinking/steady underline; and 5/6 as blinking/steady bar. Multiple parameters,
subparameters, private/intermediate variants, and values outside 0..6 fail
closed. Hard reset restores the default blinking block.

DEC synchronized output defers only publication, not terminal mutation.
Output, resize, alternate-screen changes, OSC state, and other successful
events continue to update the transactional candidate while the mode is
active. Snapshot, Render Frame V1/V2/V3, and checkpoint export are gated in the
order failed-closed, incomplete parser/UTF-8, then synchronized-output deferred.
The synchronization deadline is runtime presentation state and is deliberately
not stored in Snapshot V2; every imported profile starts inactive. This keeps
the legacy V2.0 through V2.3 profiles and the V1/V2 inactive Render Frame
encodings byte-compatible.

REP defaults missing or zero `Ps` to one and is a no-op when no source exists.
Its source is the latest complete printable grapheme: a new base replaces the
source, while combining scalars and a joined ZWJ continuation extend it across
event boundaries. The source stores character/combining/width only. Repeated
cells use the current rendition, protection, hyperlink, IRM, DECAWM, margins,
and output revision rather than presentation captured from the original cell.
NUL and DEL preserve the source; another C0 control, ESC, non-REP CSI, OSC, DCS,
CAN/SUB abort, hard reset, or a completed REP clears it. REP itself consumes
the source after completing, so a second REP without a new printable grapheme
is a no-op. Failed atomic transactions roll source changes back with the grid.

The semantic input encoder supports Unicode/control characters, navigation,
F1-F12, DEC numeric/application keypad, xterm modifiers, newline-mode Enter,
bounded bracketed paste, focus reports, and mouse tracking modes 9/1000/1002/
1003 with encodings 1005/1006/1015/1016. Keyboard/focus/paste use the versioned
`TlTerminalInputV1`; mouse input uses the independent fixed-size
`TlTerminalMouseInputV1`. Returned encoded bytes use the same owned-buffer/free
contract as snapshots. The older raw-byte
`tl_terminal_encode_input` placeholder remains unsupported so its ambiguous
signature is not silently repurposed.

Paste follows terminal text-paste behavior in plain and bracketed modes: LF and
CRLF normalize to CR, C0 controls other than HT/LF/CR are removed, and DEL/C1
controls are removed before optional bracket delimiters are added. This prevents
pasted text from terminating its own bracketed-paste frame.

Current deliberate boundaries:

- Dimensions are 1..512 columns by 1..512 rows, at most 262,144 cells.
- An output event is at most 1 MiB.
- Width profile v1 supports bounded combining/ZWJ graphemes, CJK/emoji width 2,
  and explicit continuation cells. Full Unicode grapheme segmentation, bidi,
  ordinary-output 8-bit GR mapping, DRCS and other unsupported charset families,
  stateful DCS, and SIXEL/images are not yet implemented.
- The default UTF-8 output profile does not reinterpret raw `0x80..0x9f` bytes
  as standalone C1. The explicit standalone-C1 profile recognizes 13 supported
  one-byte aliases only at UTF-8 scalar boundaries: IND, NEL, HTS, RI, SS2,
  SS3, DCS, SOS, CSI, ST, OSC, PM, and APC. Other C1 controls fail closed, and
  a UTF-8-encoded C1 scalar is not reinterpreted as standalone C1. The input
  paste encoder independently strips raw and UTF-8-encoded C1 controls.
- Runtime history capture is implemented, but immutable History Page V1 tokens,
  bounded sealed-logical-line pagination, durable transcript projection, and a
  public history FFI are not. All history-enabled Snapshot profiles and all
  three Render Frame versions intentionally exclude hidden history cells.
- Tab stops default to every eight columns and are dynamically changed by
  HTS/TBC; the full bounded tab bitset survives resize and Snapshot V2.
- Primary resize normally reflows soft-wrapped logical lines. While DECLRMM is
  active it uses deterministic crop/pad instead, so custom-margin wraps are not
  reinterpreted as whole-row logical lines; alternate resize always crops/pads.
  A primary screen frozen behind `?1049h` is resized only when it becomes active
  again, using the then-current DECLRMM policy. Resizing clips custom margins;
  if either custom axis would become narrower than two cells, that axis resets
  to the full resized screen. Cursor and saved-cursor pending-wrap are
  revalidated against the effective right boundary after resize, mode/margin
  changes, restore, and screen switches.
- Delta export and the legacy raw-input placeholder return
  `TL_STATUS_UNSUPPORTED`; semantic input v1 is implemented.
- Android JNI/NDK, renderer contracts, packaged `.so`, a self-developed terminal
  View and product wiring are outside this Windows-first change. A future Android
  mirror must treat Host Effects as non-executable data.
- The Windows P/Invoke binding performs real Render Frame V1/V2/V3, Host Effect
  Frame V1, and Input V2 discovery; effect-aware single/batch apply; lossless
  Win32 key encoding; Config V3 discovery/profile selection; owned-buffer
  release; and strict decode. Decoder or
  buffer-contract failure retires the wrapper.
  Windows Phase 1 also replays ordered `LocalFrame` batches through this core
  on an asynchronous per-session shadow queue. Native checkpoint/export does
  not run under the production history lock. `PortableTerminalModelV1` remains
  the production Snapshot/Transcript authority; the shadow cannot reject a
  durable frame, change an uploaded artifact, write a Host Effect to ConPTY,
  alter Relay payloads, or participate in Android recovery.
  The shadow is enabled by default; setting the process environment variable
  `TERMLINK_ENABLE_TERMINAL_CORE_SHADOW` to trimmed `0` or `false`
  (case-insensitive) is the immediate rollback. Each session admits at most
  eight queued work items and a conservative 16 MiB managed-retention estimate.
  That estimate is not a payload, wire, native-heap, or exact CLR limit: it
  includes cloned payload/frame/array objects and the retained immutable
  Portable snapshot object graph.
  Queue overload or shadow failure permanently stops that session's shadow and
  preserves a tombstone so late history retries cannot create another native
  generation. Its native state is disposed after already queued work; only the
  final durable session release removes the dispatcher tombstone and Portable
  projection, while a producer-lifetime released-session tombstone still blocks
  late work. Because the worker and observer are in-process and release is
  chained behind the per-session tail, a permanently blocked native call or
  observer can prevent clean release until process exit. Enforceable hard
  isolation would require a future out-of-process worker that can be terminated.
  `SHADOW_MATCH` means only that the compared current active-screen visible
  presentation and selected metadata match. A structured Title frame produces
  `SHADOW_MATCH_LIMITED/TITLE_OUT_OF_BAND`; unresolved terminal-theme palette
  entries produce `SHADOW_MATCH_LIMITED/PALETTE_THEME_UNRESOLVED`. Hidden
  terminal state, hidden scrollback, and the inactive screen are not yet a
  complete equivalence surface.
- Kitty keyboard semantic Input V3 encoding, negotiation and Snapshot V2.6
  state persistence are implemented. Kitty graphics and general query-reply
  coverage are not implemented. DEC private mode `?9001` and lossless ConPTY
  Win32 key records remain an independent path. Bounded APC ignore must not be
  interpreted as Kitty graphics support. XTQMODKEYS and OSC clipboard/window
  queries still need additional host-effect kinds.
  DA1/DA2, DSR 5/6, DEC DSR 5 and DECXCPR 6 are the currently implemented
  ordered PTY replies, and only an authority host may write them to a PTY.

These exclusions are explicit rather than approximated. Unknown controls and
escape/CSI forms fail closed so a caller cannot mistake partial interpretation
for an equivalent terminal state.

## Proposed Windows Phase 2 authority path

Phase 2 is a design only; this change does not move authority. The proposed
owner of `NativeTerminal` is `SessionHost`, beside the ConPTY it represents.
For each common `LocalFrame` sequence, Agent must first durably journal the
frame and acknowledge that durability; only after that ACK may SessionHost
apply the same sequence to the native terminal. An authority apply would use
Host Effect Frame V2 and write its ordered PTY replies back to that same
ConPTY.

That transition is blocked until the journal/ACK boundary is crash-proven with
`Flush(true)`, Windows Terminal and terminal-core query replies cannot produce
double responses, `Gap` fail-closed has an explicit recovery/resynchronization
policy, and ConPTY input plus Host Effect writes share one serialized ordering
domain. Until those conditions are implemented and validated, Portable V1 and
the existing Windows Terminal/ConPTY path remain authoritative.

## Verification

The implementation is written without platform-terminal dependencies. The
current final gates include the strict Rust workspace checks, all
repository-owned golden captures, a Windows x64 release DLL loaded by the real
.NET validation program, and non-host `cargo check` coverage for Linux, macOS
aarch64, and four Android ABI targets. The Windows validation covers Config V3 profile selection and
rejection, distinct UTF-8/standalone-C1 behavior, and Snapshot restoration in
addition to the existing native ABI/decoder checks.

The Windows product shadow validation also replays all ten repository-owned
ConPTY captures through the real native DLL. Six captures currently produce
`SHADOW_MATCH_LIMITED/PALETTE_THEME_UNRESOLVED`; four reach a valid native
checkpoint while the legacy Portable model reports its documented Unicode-width
or CSI coverage limit. Setup/MSIX validation requires both `TermLink.TerminalCore.dll` and
`termlink_terminal_ffi.dll` and includes the native DLL in the signed-binary
count.

These checks deliberately avoid presenting a volatile test count, DLL size, or
hash as a lasting contract. Cross-target `cargo check` proves source-level
portability only. There is still no Android JNI/NDK package, native `.so`, native
exporter, self-developed terminal View, or device/runtime acceptance of this
core. Windows renderer authority is also unswitched, so the current artifacts
must not be described as production cross-platform support.

From this directory, using the pinned tools under `D:\Dev`:

```powershell
& D:\Dev\Env\Rust\cargo\bin\cargo.exe +1.97.1 fmt --all -- --check
& D:\Dev\Env\Rust\cargo\bin\cargo.exe +1.97.1 clippy --workspace --all-targets --all-features --locked --offline -- -D warnings
& D:\Dev\Env\Rust\cargo\bin\cargo.exe +1.97.1 test --workspace --all-targets --all-features --locked --offline
& D:\Dev\Env\Rust\cargo\bin\cargo.exe +1.97.1 build -p termlink-terminal-ffi --release --locked --offline --target x86_64-pc-windows-msvc
```

The default test suite includes every repository-owned ConPTY capture in
`tests/golden_streams.rs`; none is ignored.
