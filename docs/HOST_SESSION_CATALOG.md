# Windows-authoritative session catalog

Windows owns session existence, process lifecycle, titles, terminal history and
snapshots. Android obtains the catalog from the authenticated Windows Agent.
Server session rows are a statistical/routing mirror, never the operational
catalog for Android. Existing device trust, grant, lease and routing checks
remain enforced; no terminal content is added to the server.

The device page lists the selected PC's entire retained catalog, including
running sessions and ended/interrupted sessions with local history. The Android
session page lists only this Android installation's opened tabs. Closing a tab
does not terminate the Windows process or delete its history. When the PC is
offline, or catalog verification fails, terminal content is hidden and cannot
be used as proof of a running process. Tab identities may be retained for a
later user-initiated reopen. An explicit remote delete remains destructive.

## Android bulk cleanup

The device long-press menu offers “关闭所有失效会话” with the eligible count.
Only EXITED/INTERRUPTED sessions with `online == false` from a trusted, online
PC's verified catalog qualify. Offline devices, catalog errors and RUNNING
sessions (including temporarily unreachable ones) never qualify. An empty
selection disables the action. Confirmation names the PC, count and permanent
history deletion; its fixed UUID list cannot grow during the operation.

Cleanup reuses the existing per-session E2EE host acknowledgement → server
mirror deletion → local cleanup path, sequentially. Each deletion checks a
fresh Windows catalog before sending termination. Progress, partial failures,
skipped sessions and cancellation are reported; completed deletions remain
completed when cancelled. Newly ended sessions require another confirmation.
No wire format, server API or Windows implementation changes are required.

Termux reference: `termux/termux-app` at the pinned commit below,
`app/src/main/java/com/termux/app/terminal/TermuxTerminalSessionActivityClient.java`
(`onSessionFinished`, `removeFinishedSession`) and `TermuxActivity.java` (kill
action gated by `isRunning`). These distinguish finished-session cleanup from
process termination. Used as behavior references only: TermLink retains its
remote Windows authority and explicit durable-history deletion acknowledgement.
Reviewed the local UPSTREAM.md, both patches, Sync-TermuxTerminal.ps1 and
terminal-engine-termux adapter; no Termux code/resources were copied.

Bulk cleanup verification (2026-09-23): Android app unit tests cover eligibility,
fixed UUID selection, stale state, partial failure, request timeout and caller
cancellation, alongside existing host → mirror → local deletion ordering tests.
On emulator-5554 the signed-in debug app was upgraded in place with the same
signer; long-press showed 245 eligible HOME-PC sessions and the confirmation
showed the same count and history-deletion warning. Cancelling closed the
dialog. Existing user sessions were not deleted during this UI check; actual
bulk deletion against a live host has not been exercised.

## Wire boundary

`host_session_catalog_v1` negotiates device-scoped E2EE request/response messages
88/89 on the existing authenticated relay. The envelope session UUID is the PC
UUID for this message family only. The relay checks PC identity, account,
device trust, routing permission and grants independently of session rows.
No terminal subscription, terminal ACK or control lease is created. Responses
echo a random request ID and request sequence and identify the Agent instance;
Android accepts them only on the requesting connection generation and key
epoch. Catalogs never enter Direct terminal output arbitration.

Snapshots are complete, bounded to the envelope limit; an oversized snapshot
returns an explicit failure instead of silently omitting sessions. Request
timeout, capability mismatch, invalid identities or stale responses fail
closed for catalog use. They do not replace a healthy device socket.

Windows checks registered host process identity (PID and start time) and local
terminal lifecycle, including retired registrations and locally retained
history. History presence alone never proves a live process. Statistics sent
to the server contain identifiers/countable lifecycle/dimensions/sequence,
not plaintext title or terminal output. Complete mirror replacement retires
missing old RUNNING rows. A disconnected PC makes the last snapshot stale,
not current; network loss alone does not prove that a shell exited.

The Agent sends `PUT /v1/pcs/{id}/session-snapshot` on the existing 15-second
metadata heartbeat. Each observation has a fresh idempotency key so idle
sessions still refresh their timestamps. The server applies the complete
snapshot in one transaction, authenticates the owning PC, preserves deletion
tombstones and terminal EXITED states, and marks omitted RUNNING rows as
INTERRUPTED. Per-session metadata writes are serialized with snapshot writes.
Android polls the E2EE catalog every two seconds (an individual request has an
eight-second deadline); these intervals bound detection, not instantaneous
knowledge of remote network failure. A failed refresh removes usable content.

Rollout order: server, Windows, then Android. Old Windows/servers without the
catalog capability fail closed with an upgrade/unavailable message; Android
does not silently fall back to server session rows. No database migration or
history deletion is needed. Existing leases and session routing still use the
server's authenticated routing mirror.

## References and verification

Reviewed termux/termux-app at pinned commit
`3df69d1da197dd9bd71a3bafd902dffd720576b4`, particularly
`terminal-emulator/.../TerminalSession.java` (`waitFor`, `isRunning`,
`onSessionFinished`), plus the local UPSTREAM.md, patch queue, sync script and
terminal-engine-termux adapter. This supports separating process lifecycle
from a retained screen. Its local PTY architecture is not reused; no upstream
code or resource is copied.

Required checks: isolated catalog authorization, wrong PC/account/generation,
request replay, Windows dead/retired host with retained history, offline UI,
Android-local tab filtering, restart recovery, catalog-only traffic preserving
terminal ACK and control, and the independent transport-policy handshake race.

## Verification — 2026-09-23

- Android `:core-network:testDebugUnitTest` and `:app:testDebugUnitTest`:
  325 + 313 tests, zero failures/skips. Includes encrypted catalog round trips,
  wrong sender/key epoch/request replay, stale connection generations,
  catalog/terminal isolation, offline renderers, and the HELLO/config race.
- Windows Agent compilation, `ProductionPcHeartbeatValidation` and
  `ProductionSessionFaultIsolationValidation` passed. Persisted retired host
  plus RUNNING WAL remains INTERRUPTED after restart; later local EXITED is
  respected. Repeated idle snapshots use different observation keys.
- Go realtime/realtimewire/api/store tests and vet passed. Isolated PostgreSQL
  17.5 tests exercised ownership, revoked devices, missing grants, orphan row
  retirement, terminal/deletion preservation and existing lifecycle/freshness.
- Debug APK built and launched on emulator-5554 using the separate
  `dev.termlink.harness.catalogverify` package. Initial launch wait timed out;
  the process and rendered login screen were subsequently verified and a
  second start returned OK. No crash-buffer entry was observed.
- The old `Test-SessionRegistry.ps1` cannot compile existing AgentControl code
  with its .NET Framework compiler. The new retired-host checks were executed
  in the .NET 8 validation program above instead.

No production installation, publication or server deployment was performed.
Authenticated phone/Windows/server E2E, offline/reconnect UI and physical LAN/NAT
acceptance remain release checks after a coordinated three-platform upgrade.
Local evidence: `artifacts/validation/host-catalog-policy/verification.json` and
`artifacts/validation/catalog-final-tests.log`.
