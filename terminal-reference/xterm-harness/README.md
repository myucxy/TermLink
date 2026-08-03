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

cd ..\..\android
.\gradlew.bat :terminal-engine-termux:testDebugUnitTest

cd ..
python -m http.server 4180 --bind 127.0.0.1
```

Open `http://127.0.0.1:4180/terminal-reference/xterm-harness/`.

The case field accepts all ten checked-in bundles: four Shell smoke cases, ANSI/Unicode,
Terminal Features, and the WSL Vim, less, fzf, and htop TUI cases. The comparator reports matches
and differences against generated Termux draft grids without weakening validation.

The Gradle test writes the Termux reference to
`android/terminal-engine-termux/build/termlink-reference/`. The browser validates every bundle
hash, strict sequence, monotonic timestamp, output range, resize, exit code, final size, and UTF-8
marker before writing original output chunks to xterm.js. It then verifies both drafts have the
same capture ID and final sequence before comparing size, visible row text, style runs, cursor, and
the common mode subset. The xterm export starts at the active buffer's `baseY`, so scrollback is not
mistaken for the visible grid.

The phase 0 batch comparison currently passes all 10 cases and all 122 event checkpoints, with no
grid differences. The machine-readable report is
`docs/validation/phase0-grid-report.json`. A final-grid match alone is insufficient; the batch test
must continue checking every recorded output event so transient alternate-screen and TUI states are
not hidden by the final shell prompt.

## Dependency and security boundary

- Runtime assets are checked into `vendor/`; the page never loads a CDN.
- `package-lock.json`, npm integrity values, copied license texts, and generated file SHA-256 values
  make the vendor refresh reviewable.
- The CSP allows only same-origin scripts, styles, and Golden Bundle reads.
- Clipboard, links, downloads, external navigation, terminal input, and a Kotlin bridge are absent.
- `termlink.terminal-grid-draft` version 0 is a disposable comparison artifact. It must not be used
  as `TerminalSnapshotV1` or written into `expectations.normalized_grid` until the multi-engine
  corpus and protocol review are complete.
