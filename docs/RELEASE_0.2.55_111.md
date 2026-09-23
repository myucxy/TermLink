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

- Source: `10a9a1e8b011ea82998aebaaaea8faa708909060`, pushed before building with
  clean HEAD equal to origin/main.
- First build failed when the Microsoft RFC 3161 service was unavailable.
  Retrying the unchanged source succeeded; timestamp validation was not relaxed.
- Production build and independent Verify-LocalRelease passed, including
  payload signatures, exact version/revision and RFC 3161 timestamp.
- Setup: `TermLink-Setup-0.2.55.111-x64.exe`, 181,301,960 bytes, SHA-256
  `688cb514bd1cd8e7258112de9f210091b44ca5ebfbc56e0dcb37ee482711edae`.
  Signer SHA-1: `91AE60BC06D866628B8EB2C62F32BDE9DAB56466`.
- AppShip Windows stable release `ec8dc1e1-4966-4063-8a45-4770da5f2573`,
  artifact `008e947d-c5f0-4e98-a5e8-db03428c2f2b`: published/ready.
  Builds 108/110 return optional; build 111 returns no_update.
- State: `.appship/termlink-win64-stable-0.2.55-build111-signature-approved.json`.
- Local installation remains 0.2.52.108 under `D:\Dev\Tool\TermLink`.
  The normal installed UI download failed before installer startup. A recovery
  attempt using the signed component from the verified package returned current
  because its host was PowerShell (the component reads the entry assembly build).
  A subsequent proposed recovery invocation was rejected by automatic approval
  review with only `blocked by policy`; it was not executed. No installed binary,
  signing policy, account data or running Agent was replaced.
- Evidence is under `artifacts/validation/windows-update-110/`: download UI
  observations, old/fixed downloader checks, headless tests, build/publish logs,
  update API results and original installation metadata.
- Local recovery/upgrade is pending; do not describe the old updater as working.
  A standard signed installer repair is the prepared next step.

## Recovery

Keep the existing installation root, account data, immutable AppShip artifact
and resumable state. Do not replace an existing build, relax signatures or clear
data. Repairing a deployed broken updater requires a working update component
or the verified signed installer; subsequent desktop updates use the fixed path.
