# Offline xterm.js reference harness

This development-only harness replays the exact Golden Stream event chunks into pinned
`@xterm/xterm` 6.0.0 with Unicode 11 width data. It is a compatibility oracle, not a production
Relay, network protocol, Android WebView bridge, or cross-device Snapshot format.

## Build and verify

```powershell
cd terminal-reference\xterm-harness
npm ci
npm run sync-vendor
npm test

cd ..\..
python -m http.server 4180 --bind 127.0.0.1
```

Open `http://127.0.0.1:4180/terminal-reference/xterm-harness/`.

The case field accepts all ten checked-in bundles: four Shell smoke cases, ANSI/Unicode,
Terminal Features, and the WSL Vim, less, fzf, and htop TUI cases. No Android build or separate
reference export is required. The browser validates every bundle
hash, strict sequence, monotonic timestamp, output range, resize, exit code, final size, and UTF-8
marker before writing original output chunks to xterm.js. `Verified` means that bundle validation
and replay succeeded; it does not mean a match against another terminal engine.
`Export xterm grid` downloads the current grid. Export is disabled during replay or after failure,
so a failed case cannot download the previous case's result. The export starts at the active
buffer's `baseY`, so scrollback is not mistaken for the visible grid.

The former Termux comparison UI, Gradle export dependency and phase 0 batch script were retired
on 2026-10-08; see the [cleanup and source record](../../docs/archive/TERMUX_CLEANUP_20261008.md).
The earlier 10-case / 122-checkpoint report in
[`docs/validation/phase0-grid-report.json`](../../docs/validation/phase0-grid-report.json)
is historical evidence, not a current test gate. The engine-independent grid comparison helper
and its unit tests remain useful for comparing caller-supplied drafts with matching capture IDs
and sequence numbers.

## Dependency and security boundary

- Runtime assets are checked into `vendor/`; the page never loads a CDN.
- `package-lock.json`, npm integrity values, copied license texts, and generated file SHA-256 values
  make the vendor refresh reviewable.
- The CSP allows only same-origin scripts, styles, and Golden Bundle reads.
- Clipboard integration, link handling, external navigation, terminal input, and a Kotlin bridge
  are absent. The explicit local JSON export is the only download action.
- `termlink.terminal-grid-draft` version 0 is a disposable comparison artifact. It must not be used
  as `TerminalSnapshotV1` or written into `expectations.normalized_grid` until the multi-engine
  corpus and protocol review are complete.
