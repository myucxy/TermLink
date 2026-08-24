# Portable Terminal Model V1 validation boundary

This project validates a Windows-producer foundation only. It does not define the cross-device
`TerminalSnapshotV1` wire format and does not enable `terminal_snapshot_v1` in the production
runtime.

No compatible mature VT parser is currently referenced by `TermLink.Agent`. The checked-in
xterm.js package is a browser-only oracle and the checked-in Termux parser is Android/Java code.
The local model therefore accepts only a strict subset whose complete restorable state is stored
in `PortableTerminalSnapshotV1`.

| Input or state | Result | Restored state |
| --- | --- | --- |
| Printable ASCII, NUL, BEL, CR, LF | Supported | Cells, wrap flags, cursor and bounded scrollback |
| Basic SGR attributes and ANSI 16 colors | Supported | Per-cell and current style |
| CSI `A/B/C/D/G/d/H/f` | Supported | Cursor and pending-wrap state |
| CSI ED/EL modes `0/1/2` | Supported | Visible cells and styles |
| CSI `?25h` / `?25l` | Supported | Cursor visibility |
| `ESC c` | Supported | Full safe-subset reset |
| Resize before content or exact no-op resize | Supported | New dimensions without reflow |
| Incomplete ESC/CSI | Temporarily ineligible | Eligible again only after a supported sequence reaches ground |
| Common assigned Unicode, including CJK wide graphemes | Supported | Deterministic width-one/two cells; ambiguous characters remain width one |
| Combining marks and variation selectors | Supported with a preceding base | Appended to the preceding grapheme; VS15/VS16 adjusts known emoji presentation width |
| Incomplete UTF-8 scalar | Temporarily ineligible | Completion resumes parsing; invalid or non-continuation input is permanently rejected |
| OSC terminated by BEL or ST | Supported and skipped | UTF-8 is validated, termination may span events, and following output is preserved |
| Invalid UTF-8, orphan combining marks, unassigned/private-use width | Permanently rejected | Terminal-dependent or malformed state is never guessed |
| DCS, APC/PM/SOS | Permanently rejected | Unsupported control-string state is not modeled |
| Unsupported ESC/CSI/private mode/SGR/C0 | Permanently rejected | Unknown terminal state is not approximated |
| Main/alternate screen, scroll regions, saved cursor, tab-stop mutation | Permanently rejected | These commands enter through unsupported CSI/ESC paths |
| Resize requiring reflow | Permanently rejected | ConPTY/terminal reflow is not approximated |
| More than 2,000 scrollback rows | Permanently rejected | The model never silently truncates a snapshot |
| Non-contiguous producer sequence | Permanently rejected | A snapshot never crosses an unobserved event |

The validation replays every checked-in Golden Stream. At every eligible event boundary it compares
full replay with `snapshot@S + suffix(S+1..N)`. A corpus case may finish rejected; that is an expected,
tested outcome when its VT stream leaves the safe subset.
