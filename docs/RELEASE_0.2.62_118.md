# TermLink 0.2.62 / build 118

## Scope and behavior

Android and Windows stable clients. Server has no changes in this release; the
existing 0.2.61 Server supports the window-resize protocol. No Docker image,
Docker Hub upload, Git release tag or GitHub Release is part of this publication.

Windows directory requests no longer wait for a retired session's recursive WAL
cleanup or synchronously decrypt its history. A durable deletion marker is read
without the session lock; existing live events and background history recovery
populate a small in-memory catalog. Field sequence fences preserve newer titles,
sizes and lifecycle events. Registry process liveness remains authoritative.
Old, inactive history may briefly have a default title/size during background
recovery, but cannot become falsely RUNNING.

Android retains the last in-memory directory only for a timeout of the same
online, authorized PC identity/key epoch. Rows are marked unconfirmed and cannot
be newly opened or individually deleted/stopped. Existing connected tabs survive
the timeout. Offline/revoked/replaced devices and successful empty directories
still remove their rows. Authenticated late replies are ignored without accepting
wrong-peer, wrong-epoch or mismatched-request responses.

Fit session to phone window now uses the available height with the software
keyboard hidden, without changing keyboard visibility. It restores only the
IME-only padding allocated by the existing scaffold. Native font metrics,
internal padding and the actual one-row, two-row or collapsed shortcut dock
remain part of the measurement. Protocol, control lease and Windows resize
validation are unchanged.

HT-LPC-1326 must upgrade its Windows client to receive the directory fix. Android
alone cannot remove a lock inside an older remote Windows process. The observed
117 incident included repeated eight-second directory timeouts during old-session
cleanup and later spontaneous recovery. An isolated probe of the installed 117
DLL reproduced the lock dependency; no remote 1326 thread dump was captured.

## References and checks

Termux reference: termux/termux-app at pinned commit
`3df69d1da197dd9bd71a3bafd902dffd720576b4`,
`app/src/main/java/com/termux/app/TermuxService.java` (session lifecycle) and
`terminal-view/src/main/java/com/termux/view/TerminalView.java::updateSize`
(viewport pixels/font metrics). Repository UPSTREAM.md, patches, sync script and
Android adapter boundaries were checked. These are behavioral references only;
no GPL source/resource was copied. Termux's local PTY lifecycle cannot replace
TermLink's remote ConPTY, E2EE, control lease and native terminal authority.

Windows fault-isolation validation passed, including directory queries while
deleted-session and retained-WAL locks are held, persisted tombstones, stale
metadata and dead-session liveness. Native batch cleanup passed for 245 sessions
in four batches (1847 ms against an isolated local mirror). History
WAL/producer/outbox validation and the Release Windows solution build passed.
The isolated Setup install/upgrade/uninstall lifecycle passed for 0.2.62.118.

Android app Debug 320 and Release 315 tests passed. Updated encrypted catalog/
cleanup tests passed in both variants, including authenticated late responses.
The first broad rerun was stopped after an unrelated existing Direct signaling
test stalled; its thread dump is retained. The earlier full app/network run
passed before the late-reply/IME additions; affected suites were rerun afterward.

Evidence is under `artifacts/validation/`: `catalog-118-*`,
`release-118-android-verified.log`, `release-118-windows-build.log`,
`release-118-setup-lifecycle.json` and `resize-118-*`.

The emulator was updated in place to same-source Debug 0.2.62/118, preserving
login, appId 10224 and firstInstallTime (2026-09-19 10:16:29). On an isolated
Windows session, the two-row dock measured 49 columns by 40 rows / 1038 by
1730 pixels both with and without the keyboard (`mInputShown=true` before the
second menu opening). One/two-row dock and multiple cell heights are covered by
the app regression. Further UI validation hit an emulator OpenGL surface-creation
stall (main waited in HWUI, RenderThread in QemuPipeStream) and then a public
`https://termlink2.kget.cc:8910/readyz` timeout also reproduced on the host.
The emulator was restarted without clearing data, using software graphics; local
Docker health remained healthy. No one-row UI result, successful resize round
trip, new physical LAN/NAT result, or remote 1326 upgrade is claimed here.
The disposable SessionHost exited at its ten-minute timeout. No user terminal
was terminated or resized during this validation.

AppShip preflight still returned 0.2.61/117 for both platforms; remote Git
`v0.2.62` was absent. Docker Hub preflight was network-limited; this release
does not build or publish an image or create a tag that triggers Docker CI.

## Publication record

Production artifact hashes, immutable source commit and AppShip publication
results will be recorded after build and independent signature verification.
Android reuses signer SHA-256
`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`.
Windows reuses SHA-1 `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`, valid through
2036-08-04, with RFC 3161 timestamping. This is the existing development
Authenticode certificate, not a public CA certificate. Rollback requires a
strictly newer build with the same identities; never overwrite published bytes
or clear user data to change signing keys.
