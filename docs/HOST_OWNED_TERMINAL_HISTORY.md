# Host-owned terminal history

## Decision

The Windows Agent is the only authoritative store for terminal snapshots,
transcript pages, and replayable terminal events. Android reads those objects
from the Agent over the existing recipient-bound E2EE session route. Relay and
direct/P2P transports carry the same opaque protocol messages; the TermLink
server does not inspect or retain terminal history.

This design intentionally makes history unavailable while the Windows machine
is offline. Android hides terminal content and releases live/history renderers
when the owning Windows catalog is unavailable; cached content is not an
independent history authority. See [host session catalog](HOST_SESSION_CATALOG.md).

## Storage and lifecycle

Windows stores the durable WAL, checkpoints, snapshots, and transcript data
under the current machine identity in:

```text
%LOCALAPPDATA%\TermLink\Data\History\<machine-id>\
```

The per-user data directory is used instead of an executable/install directory
because installed applications may be read-only and upgrades may replace their
contents.

History is destroyed only after an explicit user action:

| Event | History result |
| --- | --- |
| Windows explicitly closes a TermLink session | Destroy |
| Android explicitly closes or deletes a session and Windows acknowledges it | Destroy |
| Shell exits naturally | Retain |
| Windows Agent restarts or upgrades | Retain |
| Android is backgrounded, killed, or the phone locks | Retain |
| Relay disconnects, direct transport fails, or transport switches | Retain |

Deletion first creates a durable marker and then removes the session directory.
On startup the Agent resumes incomplete marked deletions. This prevents a crash
between the user's delete action and physical removal from resurrecting history.

## Protocol

Capability `host_history_v1` enables four E2EE message types:

- `HOST_HISTORY_REQUEST` (`84`): Android requests a latest snapshot, transcript
  page, or contiguous event range.
- `HOST_HISTORY_RESPONSE` (`85`): Windows returns status and an authenticated,
  recipient-bound object in bounded chunks.
- `HOST_HISTORY_CONTROL` (`86`): Android acknowledges each response chunk or
  cancels a request.
- `SESSION_TERMINATE_RESPONSE` (`87`): Windows confirms that an Android
  termination request was accepted before Android removes session metadata.

History chunks do not consume the ordered terminal-event sequence and do not
advance terminal replay high-water marks. A request has its own UUID, object
identity, digest, size, selection metadata, cancellation token, and per-chunk
acknowledgement. Windows sends at most one unacknowledged chunk for a request,
which bounds Relay and Android queue pressure. Closing a temporary bootstrap
endpoint sends cancellation so late history chunks cannot enter a later live
subscription.

The Android open path is:

1. Establish a temporary session endpoint with a zero terminal replay window.
2. Fetch and authenticate the latest host snapshot.
3. Request missing contiguous event ranges and replay them into the renderer.
4. Close the temporary endpoint.
5. Subscribe to live output from the recovered cumulative ACK.

Transcript scrolling uses the same temporary host-history request path. Large
objects are assembled into one bounded buffer and sensitive buffers are cleared
when consumed, rejected, cancelled, or left undelivered.

## Server boundary and compatibility

The Relay validates message direction and negotiated capability, then forwards
opaque ciphertext. It never assigns terminal replay sequence numbers to host
history responses. The server keeps session/account routing metadata but no
terminal history payloads.

Legacy REST history read/upload/commit endpoints return `426 Upgrade Required`
while clients migrate. Old Android or Windows clients that lack
`host_history_v1` cannot provide reliable history after this change and must be
upgraded as a pair. Realtime File Transfer V2 no longer uses MinIO/S3. Privacy
data export and its object artifacts are also retired, so the production
deployment has no server-side object store or S3 credentials.

## Termux reference review

The Android behavior was compared with `termux/termux-app`, especially the
separation between `TerminalView`, `TerminalSession`, and in-memory scrollback,
and with the repository-pinned sources in:

- `terminal-forks/termux-terminal/UPSTREAM.md`
- `terminal-forks/termux-patches/`
- `terminal-forks/Sync-TermuxTerminal.ps1`
- `android/terminal-engine-termux/`

Termux issues `#3709` and `#5259` were also reviewed for lifecycle and retained
terminal-state behavior. Termux is useful as a UI/session behavior reference,
but its local PTY process and in-memory scrollback are not reusable as a remote
history authority. TermLink must preserve its Windows ConPTY, recipient-bound
E2EE, control lease, cumulative ACK, History Gap, Snapshot, Relay/P2P, and native
`terminal-core` boundaries. No Termux code or assets were copied for this
change.

## Required regression coverage

- Identical event-range retries return identical object IDs and ciphertext.
- Out-of-order and duplicate chunks are accepted only when byte-identical;
  changed duplicates, digest errors, metadata changes, gaps, and oversized
  objects are rejected.
- Host-history traffic does not modify ordered terminal ACK state.
- Cancellation, endpoint close, timeout, explicit deletion, and subscriber
  removal stop the Windows response task.
- Natural exit, disconnection, and Agent restart retain readable history.
- Explicit Windows and acknowledged Android closure delete local history, even
  if the Agent restarts during deletion.
