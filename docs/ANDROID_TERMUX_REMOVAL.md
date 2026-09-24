# Android production Termux dependency replacement

The production live terminal already uses `NativeTerminalEngine` and Rust `terminal-core`.
This change replaces the remaining detached history renderer with `NativeTranscriptViewport`
and removes parser-backed placeholders. The legacy replay activity and Termux dependency are
debug-only; Termux remains available as a test reference. There is no Windows/server protocol change.

## Source and implementation boundary

Reference: `termux/termux-app`, pinned commit
`3df69d1da197dd9bd71a3bafd902dffd720576b4`. The local source inventory is
`terminal-forks/termux-terminal/UPSTREAM.md`; adaptations and reproduction boundaries are in
`terminal-forks/termux-patches/` and `terminal-forks/Sync-TermuxTerminal.ps1`.
Online upstream retrieval timed out during the dependency investigation; no claim is made about
newer upstream issues or changes.

Files examined for behavior:

- `terminal-view/.../TerminalRenderer.java`: integer text size, X glyph advance, rounded line
  spacing/ascent, top inset, synthetic bold/italic, bright indexed colors, dimming, reverse selection
  and scaling glyphs to their assigned cells.
- `terminal-view/.../TerminalView.java`: row/column scrolling, viewport size, line anchors,
  partial bottom positioning and copy interaction.
- `terminal-emulator/.../TerminalBuffer.java`: detached projection row placement.
- TermLink's `TermuxTranscriptViewport`, mapper and regression tests: authenticated pagination,
  authoritative width fitting, resident capacity and view remount behavior.

These are behavior references. The new code consumes TermLink's immutable Render Frame V3 cells
and uses the existing native Android view. It does not embed Termux's parser, local PTY, shell,
width tables or process APIs. History has no feed/input/native-handle/ACK boundary. Projection
validation retains page, sequence, palette, style, wide-cell and 1000-row capacity checks.
Host-resolved grapheme widths are not reinterpreted using a second wcwidth implementation.

The user requires unchanged display. Historical rendering therefore retains existing theme
precedence, font metrics and synthetic style presentation, including the existing single-underline
appearance. Additional underline shapes/colors are not enabled as part of dependency replacement.
Production live rendering keeps its existing behavior.

## Licenses

Renaming or translating upstream classes does not erase their copyright or license obligations.
The retained debug/reference fork keeps its source and notices unchanged. Upstream declares GPLv3
with component exceptions; see `UPSTREAM_LICENSE.txt`. This change is not a blanket relicensing
statement for either the retained fork or previous distributions.

## Validation

`NativeTranscriptViewportTest` compares identical history data through the old and new renderer,
including bitmap output, metrics, pagination anchors, remounts, malformed pages, capacity and
host-resolved graphemes. Reference PNGs are generated under
`android/terminal-engine-native/build/reports/transcript-parity/`.
Validation completed on 2026-09-24:

- Native module: 123 JVM tests passed; App: 320 JVM tests passed, zero skipped/failures.
- Android 16 emulator `TermLinkE2E`: 16 instrumentation tests passed, including JNI/input and
  real Android Canvas comparison using system monospace and the bundled Nerd Font.
- 14 JVM bitmap scenarios and 8 emulator bitmap scenarios matched the pinned reference pixel
  for pixel. They cover font sizes, line spacing, styling, wide/combining characters, narrow and
  tiny viewports and short history. This is evidence for these cases, not a claim of exhaustive
  coverage of every custom font, GPU, accessibility setting or selection-handle animation.
- Prepend, resize, remount, horizontal position, clearing/reopening, new snapshot anchors,
  capacity and malformed-page tests passed. History exposes no input connection/feed API.
- `:app:assembleRelease` and its vital lint checks passed. `releaseRuntimeClasspath` contains no
  Termux modules. The unsigned APK contains no `Lcom/termux/` or
  `Ldev/termlink/terminal/termux/` DEX descriptors or Termux selection resources, and retains all
  four existing JNI ABIs.
- Unsigned verification APK SHA-256:
  `5910e0faf0c8d22bd893989a7180b207130735cd6e0e2278c6b76d9f3c7e86ce`.
- Logs, test XML and reference bitmap evidence:
  `artifacts/research/termux-dependency/`, including `validation-evidence.zip` and `result.json`.

Final validation command, using the existing clean Gradle 8.13 distribution and environment from
`RELEASE_RUNBOOK.md`:

```text
gradle :terminal-engine-native:testDebugUnitTest :terminal-engine-native:connectedDebugAndroidTest
       :app:testDebugUnitTest :app:assembleRelease --continue --no-parallel --max-workers=2
gradle :app:dependencies --configuration releaseRuntimeClasspath
```

The APK is an unsigned local validation artifact, not a production release. No version increment,
AppShip upload, Windows deployment or Server image change was performed.
