# Terminal engine native adapter

This module is the Android adapter for the self-developed TermLink terminal core. It owns bounded
Kotlin DTOs, stable status mapping, an idempotent native-handle wrapper, and the production
`JniNativeBridge` object. Local JVM tests keep using the pure Kotlin fake and therefore never try to
load an Android shared library.

The Gradle build uses the pinned NDK 27.0.12077973 and Rust 1.97.1 inventory under `D:\Dev`. It
builds the Rust static library and links it into one `libtermlink_terminal_jni.so` for arm64-v8a,
armeabi-v7a, x86, and x86_64. `TERMLINK_CMAKE`, `TERMLINK_NINJA`, `TERMLINK_CARGO`, and
`ANDROID_SDK_ROOT` may override the inventory probe paths for CI without changing machine PATH.
Every final ELF is stripped and linked with a 16 KiB maximum page size.

Production callers use the stable bridge object directly:

```kotlin
val terminal = NativeTerminalHandle.create(
    bridge = JniNativeBridge,
    size = TerminalSize(80, 24),
    initialNextSourceSequence = 1,
)
```

Kotlin receives only positive opaque registry tokens. Native addresses never cross the JNI
boundary, and stale, forged, and double-destroyed tokens are rejected.

Semantic mouse input is exposed as Kotlin DTOs and an injectable bridge call corresponding to the
fixed 64-byte `TlTerminalMouseInputV1` C ABI. The handle does not synthesize VT mouse bytes: it
passes press/release/move/wheel semantics to the bridge because reporting depends on the native
terminal's current tracking and 1006/1016 wire modes. The JVM fake exercises that contract without
claiming JNI availability.

The adapter does not parse VT. Source events are passed to the native core in strictly continuous,
bounded batches; render frames and checkpoints are treated as validated binary products of that
core.

Production Android engines retain up to 2,000 primary-screen rows (further bounded by the core cell
limit). The JNI adapter can render an immutable V3 window at a retained row offset. Touch scrolling
first moves within an oversized live grid, then browses this local history; new output keeps a
history reader anchored, while accepted local input returns to the live cursor. At the oldest local
row the existing product callback may request older authenticated server transcript pages.

`initialNextSourceSequence` and every reported `nextSourceSequence` are journal positions supplied
by the authoritative session journal. The adapter does not invent or renumber them. Each output,
resize, advance, or gap event consumes exactly one continuous sequence value, and a checkpoint
stores the next value to consume. A session whose first durable journal event is `1` therefore
creates its native mirror with `initialNextSourceSequence = 1`.

Bridge batches are all-or-nothing: a failed call exposes no committed prefix. A gap may only latch
the native instance failed closed. `TL_STATUS_SNAPSHOT_NOT_ELIGIBLE` is temporary and means the
native parser has not reached a checkpoint-safe boundary; the same live handle can accept more
journal events and retry export later. Kotlin treats those event bytes as opaque and never tries to
infer parser boundaries itself.
