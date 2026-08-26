# TermLink PC client Avalonia migration plan

This document is the executable P0-P8 plan for the Windows, Linux, and macOS PC client. The immutable
pre-migration reference is annotated tag `pc-avalonia-baseline-v0.1.32` at commit
`896b3a4947230087b26500d5a7f945142b1adfc0`.

## Supported product matrix

| Platform | Initial architecture | UI | Terminal source | Credential store | Autostart | Package |
| --- | --- | --- | --- | --- | --- | --- |
| Windows 10/11 | x64, then arm64 | Avalonia | terminal-main async channel; legacy SessionHost only as migration oracle | DPAPI / Credential Manager | per-user startup | signed Setup; MSIX remains optional |
| Linux desktop | x64, arm64 | Avalonia | POSIX PTY | Secret Service or KWallet; fail closed if unavailable | systemd user with XDG fallback | AppImage first; deb/rpm later |
| macOS 13+ | arm64, x64 | Avalonia | POSIX PTY | Keychain | launchd user agent | signed and notarized PKG/DMG |
| Android | arm64 and emulator targets | Compose | remote presentation only | Android Keystore | platform lifecycle | APK |

## Target process model

```text
TermLink.Desktop -> authenticated local IPC -> TermLink.Agent.Host
                                               |-- platform terminal host
                                               |-- Relay and REST
                                               |-- durable history/outbox
                                               |-- file operations
                                               `-- update worker
```

The UI may be stopped, upgraded, or restarted without stopping the Agent. The Agent must not trust a local
client merely because it can discover an endpoint: peer identity and a per-installation secret are both
verified. Local IPC frames are bounded and versioned.

## Phase gates

### P0 - architecture and baseline

- Pin the Avalonia and .NET SDK versions from official sources.
- Record the existing UI CPU, memory, thread, handle, and navigation implementation baseline.
- Add architecture, dependency, security, performance, and support-matrix checks.

Exit: the ADR, package lock, support matrix, and machine-readable baseline are committed.

### P1 - portable contracts and presentation

- Add immutable PC status/session/device/command-capability contracts.
- Add platform interfaces for secrets, IPC, terminal hosting, filesystem, commands, autostart, updates,
  notifications, and process supervision.
- Add one coalesced Agent status stream and differential observable collections.
- Ensure Presentation has no Avalonia or platform dependency.

Exit: architecture validation and presentation state tests pass on `net8.0`.

### P2 - Avalonia Windows preview

- Add a cached three-page shell: connection, sessions, and settings.
- Use virtualized item controls, immutable snapshots, async commands, and theme resources.
- Add headless view creation/navigation tests and measured navigation telemetry.

Exit: a Windows preview can run beside the released application without changing production ownership.

### P3 - independent Agent Host

- Ship the Agent executable instead of embedding it in the UI.
- Replace UI in-process dispatch with authenticated local IPC.
- Add Agent supervision, single-instance behavior, crash recovery, and a UI-independent autostart entry.
- Preserve Windows device identity, DPAPI data, registration index, buffers, and session hosts.

Exit: killing and restarting the UI does not interrupt Relay or managed sessions.

### P4 - Windows parity preview release

- Complete login, account, devices, sessions, remote-access, command settings, update, tray, diagnostics,
  localization, accessibility, and Windows Terminal integration.
- Meet the performance gates below and retain the WinForms rollback binary for one preview cycle.
- Publish a signed Windows preview artifact to a non-stable channel; do not overwrite stable automatically.

Exit: real Windows/Android end-to-end validation passes and the preview update/rollback path is proven.

### P5 - generic PC Agent server model

- Add `PC_AGENT` plus `platform`, `architecture`, `platform_version`, and bounded capabilities.
- Accept legacy `WINDOWS_PC` throughout the compatibility window.
- Make shell, file, and update eligibility capability driven.
- Update Android labels/icons/actions without changing existing PC/device IDs.

Exit: new and legacy clients coexist against the same server and migration rollback is tested.

### P6 - Linux Agent

- Implement PTY, process groups, resize, signals, terminal discovery, filesystem rules, Secret Service,
  systemd-user autostart, AppImage update, and rollback.
- Validate bash, zsh, fish, Unicode, colors, resize, disconnect, resume, files, and Android control.

Exit: Ubuntu LTS x64 and arm64 CI plus a real desktop E2E pass.

### P7 - macOS Agent

- Implement PTY, process groups, Keychain, launchd, command discovery, package update, rollback, signing,
  hardened runtime, and notarization automation.
- Validate zsh/bash, Apple Silicon and Intel, sleep/wake, resume, files, and Android control.

Exit: notarized artifacts and real arm64/x64 E2E evidence exist. Windows cannot substitute for this gate.

### P8 - unified release and GA

- Extend release manifests and AppShip mappings to explicit platform/architecture/package/signature policy.
- Add preview-to-stable promotion, staged rollout, health telemetry, rollback, and release resumption.
- Run protocol, server, Android, Windows, Linux, macOS, installer, updater, migration, security, and recovery
  suites before stable promotion.

Exit: all supported artifacts are reproducibly identified, signed under their platform policy, published,
and independently install/update/rollback verified.

## Performance gates

| Metric | Gate |
| --- | --- |
| Page navigation P95 | <= 100 ms |
| Longest UI-thread operation | <= 50 ms |
| Idle desktop CPU | <= 0.5% of one logical core average |
| Desktop private memory | <= 120 MiB target |
| Desktop plus Agent private memory | <= 200 MiB target |
| Unchanged snapshot | no page/list rebuild |
| List scale | 100 sessions and 100 devices scroll without visible stalls |
| UI restart | no Relay/session interruption |
| Remote input latency | no regression from the published baseline |

Measurements must include hardware, OS, build identity, sampling duration, and whether diagnostics were
enabled. A single idle sample is evidence, not a universal benchmark.

## Compatibility and rollback

- Stable Windows keeps its executable names, install root, registry marker, Terminal Fragment, account,
  DPAPI identity, AppShip stable application, and CLI contract through P4.
- Preview artifacts use a separate update channel and never replace stable without explicit promotion.
- Server migrations are expand/contract: add new columns/types first, dual-read/write next, retire legacy
  values only after the minimum-supported client fence advances.
- Every installer keeps the last verified package until the new Agent reconnects and reports its build.
