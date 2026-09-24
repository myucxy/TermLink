# TermLink 0.2.61 (117)

Date: 2026-09-24. Scope: Android APK and Windows x64 Setup to AppShip stable;
Server image compiled locally at the same version. No Server deployment or
Docker Hub/GitHub image publication is included in this request.

## Changes

- Add the session cloud menu actions to fit the Windows terminal to the measured
  phone viewport and restore the previous desktop grid. Measure actual font,
  SP/font scale, line spacing and available content area without changing the
  phone authority grid early. Confirm the host result and applied phone grid.
- Retain control lease, local-input priority, request deadlines and per-session
  admission. A local desktop resize supersedes the saved restore point. Control
  results are E2EE and do not advance terminal replay or ACK cursors.
- Negotiate optional Server support before sending the explicit resize request.
  Older servers keep working for existing operations and display an upgrade
  instruction for this feature, avoiding an unsupported reply on a shared socket.
- Ship pinned Microsoft ConPTY 1.24.260710001 and its MIT notice. Preserve Codex
  faint styling and palette queries. Native terminal-core accepts the exact
  OpenConsole startup window-restore hint, retaining strict handling elsewhere.
- Allow THIRD_PARTY_NOTICES.md in the Server Docker build context, as required
  by the existing Dockerfile COPY instruction.

## Upgrade order and limits

Upgrade Android before Windows, then open new Windows terminal sessions. Android
116 may reject the newer OpenConsole startup sequence; do not assume older
phones can render output from the new Windows host. Window resizing additionally
needs Server 0.2.61 to be deployed separately. No database migration is added.

Windows Terminal must accept the window resize (single tab/pane, non-fullscreen,
non-quake, within size limits). This changes terminal grid/window dimensions,
not monitor resolution or Windows DPI. Phone and PC clocks must be synchronized.
See [phone window resize](PHONE_WINDOW_RESIZE.md) and
[terminal fidelity](TERMINAL_FIDELITY.md) for implementation, upstream references,
license boundaries and validation commands.

## Validation and release state

The implementation passed Android Debug app 318, network 330 and native 118
tests, plus 15 Android 16 emulator instrumentation tests. Windows validation
used a disposable outer ConPTY with the real SessionHost/inner ConPTY to check
fit, restore, refusal, local-input priority, expiry and local-resize precedence.
History WAL/producer/outbox validation passed. Release preflight adds current
server-capability regression, Debug/Release Android suites, full Go tests,
Windows solution build, Setup validation and independent package verification.

This record is prepared before immutable source/build publication. Exact source
revision, hashes, signatures, artifact/release IDs, image identity and final
verification are appended after they are observed; this paragraph is not a
claim that upload or deployment has completed.

Windows uses the existing development Authenticode identity, not a public CA
certificate. Both signers must match the current published 116 artifacts.
Rollback keeps the previous deployment/image and uses a strictly newer client
build with the same signing identities; do not replace keys, erase user data or
overwrite an already published release.
