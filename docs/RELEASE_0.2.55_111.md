# TermLink Windows 0.2.55 (111)

Date: 2026-09-23 (Asia/Shanghai)

## Fix and verification

- Windows only. Live AppShip preflight found both clients at 0.2.54 (110);
  remote tag v0.2.55 is absent. Android and Server require no rebuild.
- Reproduced with the installed 0.2.52.108 client: its About update action
  displays the main-window confirmation and download progress, then fails
  after downloading 0.2.54.110. Authenticode returns 0x80092003 because the
  download stream still holds FileShare.None when WinTrust reopens the file.
- Dispose the completed download stream before Authenticode verification and
  final file promotion. Hash, size, pinned publisher, TLS and trust checks stay
  unchanged. The existing Agent download path already closes its stream first.
- Added `pc-client/tests/Test-WindowsUpdateDownload.ps1`: calls the actual
  downloader against a saved AppShip offer, verifies its signature/hash, checks
  exclusive reopen and temporary cleanup, and never starts the installer.
  Installed assemblies fail; fixed assemblies pass against the same signed
  0.2.54.110 artifact and unchanged installation trust configuration.
- Windows platform build, Avalonia headless confirmation/progress/cancellation
  checks and AgentUpdateValidation passed.
- The current Android device menu no longer exposes the remote Agent update
  action described in REMOTE_AGENT_UPDATE.md; it is not a usable UI recovery path.

## Publication and local upgrade

Production build, publication and local upgrade are pending. The installed
desktop updater cannot fetch its own repair successfully. Recovery must be
reported separately from the reproduced failure; do not claim that clicking
the old version's update action succeeded.

## Recovery

Keep the existing installation root, account data, immutable AppShip artifact
and resumable state. Do not replace an existing build, relax signatures or clear
data. Repairing a deployed broken updater requires a working update component
or the verified signed installer; subsequent desktop updates use the fixed path.
