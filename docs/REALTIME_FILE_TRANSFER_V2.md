# Realtime File Transfer V2

TermLink file and directory downloads use a dedicated
`termlink.file-transfer.v2` WebSocket at `/v1/file-transfers`. The server checks
the account, device, operation, role, expiry, routing header, and bounded frame
size, then relays opaque end-to-end encrypted bytes. It does not persist file
content, file chunks, resumable offsets, or file keys.

## Resume authority and completion

- Windows sends a stable offer followed by 256 KiB chunks. Ordinary files are
  seeked directly; directories use a stable ZIP for the operation ID.
- Android appends only at its current contiguous offset, synchronizes the
  private `.part` file, and only then acknowledges the committed offset.
- On reconnect, Android's committed offset is authoritative. Duplicate chunks
  must be byte-identical; a forward gap produces `RESUME`, not sparse output.
- Android verifies exact length and SHA-256 before copying the private cache
  file to the selected SAF document. A destination-bound completion marker
  prevents a Relay reconnect or process restart from repeating a completed SAF
  commit merely while the final REST state is becoming visible.
- Windows reports `SUCCEEDED` only after receiving `COMPLETE_ACK`. Cancellation,
  expiry, authorization loss, invalid metadata, and hash mismatch fail closed.

The transfer WebSocket and its bounded queues are independent of terminal
`/v1/realtime`, so a slow or interrupted file receiver cannot consume the
terminal connection or its heartbeat lane.

## Temporary-file ownership

All file-transfer-generated temporary files are contained by one task-scoped
cache root on each endpoint:

```text
Windows: %LOCALAPPDATA%\TermLink\Cache\FileTransfers\<transfer-id>\
         payload.zip
         payload.zip.tmp

Android: <app cacheDir>/file-transfers/<transfer-id>/
         payload.part
         document.committed
         document.committed.tmp
```

An ordinary Windows source file is not a generated temporary and is not copied
into the cache; it remains open as a seekable read-only source. The Android SAF
destination is user-owned output, not a temporary file. Cache directories are
retained across transient disconnects and process restarts, then removed after
confirmed success, explicit cancellation, expiry/authorization loss, or record
removal. Paths are task-ID-derived and cleanup refuses unexpected/reparse-point
entries rather than recursively following them.

## Termux reference boundary

Android behavior was checked against `termux/termux-app` at pinned commit
`3df69d1da197dd9bd71a3bafd902dffd720576b4c`, specifically:

- `app/src/main/java/com/termux/app/api/file/FileReceiverActivity.java`
- `app/src/main/java/com/termux/filepicker/TermuxDocumentsProvider.java`
- `termux-shared/src/main/java/com/termux/shared/file/FileUtils.java`
- `termux-shared/src/main/java/com/termux/shared/net/uri/UriUtils.java`
- `terminal-forks/termux-terminal/UPSTREAM.md`
- `terminal-forks/termux-patches/`
- `terminal-forks/Sync-TermuxTerminal.ps1`
- `android/terminal-engine-termux/`

Termux is applicable as behavioral guidance for `content://` URI handling,
bounded streaming, deterministic close, and keeping partial internal work away
from the final destination. It has no directly reusable implementation for
TermLink's Windows ConPTY source, remote E2EE envelope, authenticated Relay,
control lease, durable remote ACK, History Gap/Snapshot rules, or native
`terminal-core` boundary. No Termux code, resources, fonts, themes, or test
vectors were copied for this change.

## Required regression coverage

- zero-byte, one-chunk, multi-chunk, duplicate, forward-gap, oversized, and
  wrong-hash transfers;
- Relay offline/reconnect, slow consumer, cancellation, expiry, and role/device
  authorization changes;
- Android process restart and completed-document/final-status race;
- Windows Agent restart with stable directory ZIP reuse;
- cross-platform fixed vectors for directional file-transfer keys;
- success/cancel cleanup without cache-root escape; and
- simultaneous file transfer plus terminal input, output, and heartbeat.
