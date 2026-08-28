# Portable Terminal Model V1 validation boundary

This project validates the portable Windows producer used by the production history pipeline.
The cross-device `TerminalSnapshotV1` contract remains defined in `protocol/`; this project proves
only the parser/profile and snapshot+suffix equivalence needed to publish a checkpoint.

The production bridge still receives the raw terminal stream rather than an exported
`terminal-main` TerminalCore buffer, so this model remains a compatibility projection. It follows
the corresponding Windows Terminal behavior for the supported rows below and fails closed when
the cross-device grid protocol cannot preserve an operation (notably SIXEL graphics).

| Input or state | Result | Restored state |
| --- | --- | --- |
| Printable assigned Unicode, NUL, BEL, BS, HT, CR, LF, VT, FF | Supported | Cells, wrap flags, cursor and bounded scrollback |
| SGR attributes, ANSI 16/256 colors and 24-bit RGB | Supported | Per-cell/current style and lossless palette/RGB references |
| CSI `A/B/C/D/E/F/G/d/H/f` | Supported | Relative movement is margin-clamped independently of origin mode; absolute addressing retains origin semantics |
| CSI ED/EL modes `0/1/2`, ECH `X` | Supported | Erased cells retain current colors but clear rendition attributes and hyperlinks |
| CSI ICH/DCH/IL/DL `@/P/L/M`, SU/SD `S/T` | Supported | Standard-erase fillers, IL/DL left-margin cursor placement, and region-only scrolling; explicit SU never creates history |
| CSI DECSTBM `r` | Supported | Global vertical scrolling margins shared by primary and alternate screens; a temporarily offscreen configured bottom survives shrink and re-expansion |
| DECSC/DECRC (`ESC 7/8`, CSI `s/u`) | Supported | Per-screen origin-relative position, delayed wrap, rendition, G0/G1 designation and GL selector; saved DECAWM metadata is exposed without being restored |
| Insert/origin/autowrap modes (`4`, `?6`, `?7`) | Supported | Mode state, cursor constraints and wrap behavior |
| Primary/alternate screen (`?47`, `?1047`, `?1048`, `?1049`) | Supported | Independent grids; `?1049` copies cursor position and standard erase colors into a fresh alternate screen, then restores the complete primary cursor state |
| HTS/TBC and CHT/CBT | Supported | Dynamic and temporarily offscreen tab stops, backing capacity, default-tab initialization state, resize/checkpoint persistence, and HT delayed-wrap preservation |
| CSI `?25h/l`, `?1004h/l`, `?2004h/l` | Supported | Cursor visibility, focus reporting and bracketed-paste mode |
| CSI `0..6 SP q` (DECSCUSR) | Supported | Cursor shape and blink state |
| CSI `?2026h/l` synchronized output | Supported | Checkpoints are withheld until the atomic output batch ends |
| `ESC c` | Supported | Full safe-subset reset |
| Non-empty resize | Supported | Soft-wrapped logical rows and wide graphemes are reflowed; the cursor's logical row remains visible and successful snapshots stay within the 2,000-row restore bound |
| Incomplete ESC/CSI or active synchronized-output batch | Temporarily ineligible | Eligible again only after a supported sequence reaches ground / the batch ends |
| Common assigned Unicode, including CJK wide graphemes | Supported | Deterministic width-one/two cells; ambiguous characters remain width one |
| Combining marks and variation selectors | Supported with a preceding base | Appended to the preceding grapheme; VS15/VS16 adjusts known emoji presentation width |
| Incomplete UTF-8 scalar | Temporarily ineligible | Completion resumes parsing; invalid or non-continuation input is permanently rejected |
| OSC title, palette/default colors and OSC 8 hyperlinks | Supported | UTF-8 title, palette and per-style hyperlink references are checkpointed; invalid C0 payload bytes are ignored and link-table overflow fails closed |
| DEC G0/G1 special graphics with SI/SO | Supported | Complete VT100 DEC Special Graphics table and active GL selector |
| DEC rectangle fill/erase/copy (`$x/$z/$v`) | Supported | Origin-relative source and destination coordinates are clipped to vertical margins |
| Invalid UTF-8, orphan combining marks, unassigned/private-use width | Permanently rejected | Terminal-dependent or malformed state is never guessed |
| Non-SIXEL DCS | Permanently rejected after its identifier | DRCS, macros, user-defined keys and query/response semantics are not representable in V1; ESC, CAN and SUB still cancel an incomplete identifier from any parser state, including across `ApplyOutput` event boundaries |
| SIXEL DCS | Permanently rejected | The V1 cross-device protocol has no raster-image plane |
| APC/PM/SOS | Permanently rejected | Unsupported control-string state is not modeled |
| Unsupported ESC/CSI/private mode/SGR/C0 | Permanently rejected | Unknown terminal state is not approximated |
| More than 2,000 scrollback rows, including resize reflow expansion | Permanently rejected | The model never emits a snapshot that its own restore path rejects |
| Resize while the alternate screen is active | Permanently rejected | V1 cannot encode Windows Terminal's deferred main-buffer reflow without approximation |
| More than 256 distinct OSC 8 hyperlinks | Permanently rejected | The previous hyperlink is never silently reused for a new URI |
| Non-contiguous producer sequence | Permanently rejected | A snapshot never crosses an unobserved event |

The validation replays every checked-in Golden Stream. At every eligible event boundary it compares
full replay with `snapshot@S + suffix(S+1..N)`. A corpus case may finish rejected; that is an expected,
tested outcome when its VT stream leaves the safe subset.
