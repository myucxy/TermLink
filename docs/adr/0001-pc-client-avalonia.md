# ADR 0001: Avalonia PC client and an out-of-process Agent

- Status: Accepted
- Date: 2026-08-26
- Baseline: `pc-avalonia-baseline-v0.1.32`
- Decision owners: TermLink maintainers

## Context

The released Windows control plane is a .NET 8 WinForms application. It builds and disposes complete
pages during navigation, periodically rebuilds list controls, and hosts the production Agent in the UI
process. The arrangement is functional, but it couples UI latency and lifetime to Relay, history, file
operations, terminal recovery, and update work. It also cannot provide a shared desktop UI on Linux and
macOS.

The protocol and terminal model projects already target portable `net8.0`. The Agent, local control
contract, terminal host, update system, installer, and server device model still contain Windows-specific
assumptions.

## Decision

TermLink will use Avalonia for the Windows, Linux, and macOS desktop control plane. Android remains a
native Kotlin/Compose client. The PC Agent becomes a separately supervised process and exposes a bounded,
authenticated local control contract. The UI consumes immutable status snapshots and never owns Relay,
terminal, history, update-worker, or file-operation lifetimes.

Migration proceeds in two compatibility fences:

1. reach feature and performance parity on Windows while preserving the existing identity, Setup,
   updater, CLI, Windows Terminal integration, and rollback path;
2. generalize the server/device contract and add Linux and macOS platform implementations.

The common UI and presentation layers must not reference a concrete platform adapter. Platform features
are accessed through small interfaces and are selected by the platform entry point.

## Consequences

- Windows can continue to ship a signed Setup while the preview UI is evaluated side by side.
- Linux and macOS require independent PTY, credential-store, autostart, update, package-signing, and
  end-to-end evidence; Avalonia does not make those platform services portable by itself.
- Existing `WINDOWS_PC` records remain readable during a compatibility period. A later migration adds a
  generic PC-agent category plus explicit platform, architecture, and capability fields.
- A UI crash or restart must not terminate the Agent, Relay, or a managed terminal session.
- The current WinForms application stays buildable until the Windows Avalonia release passes all gates.

## Rejected alternatives

- WinUI 3: Windows only and adds Windows App SDK deployment constraints.
- Electron/WebView2: materially higher idle memory for a resident tray application.
- .NET MAUI: no supported Linux desktop target.
- one UI implementation per desktop OS: duplicates product behavior and accessibility work.
