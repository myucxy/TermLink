# TermLink

[简体中文](README.md) · **English**

TermLink lets you use **CLI terminals, AI agent conversations, and files on a Windows PC** from Android.
Work runs on the computer; the phone provides input and viewing. You can deploy your own relay server.

Current deliverables are Android, Windows x64, and a Docker server. Linux/macOS desktop code has not passed
complete product acceptance. Project-owned code uses [GPL-3.0-only](LICENSE); third-party material retains
its own terms. See [license and source delivery](LICENSE-STATUS.md).

## Product and feature demos

[![Windows Terminal and Android TUI preview](docs/media/feature-demos/previews/tui.gif)](docs/media/feature-demos/English/00_Full_demos/01_TUI_on_Windows_and_Android.mp4)

- [English catalogue](docs/media/feature-demos/English/README.md) · [中文目录](docs/media/feature-demos/中文/README.md)
- [Product videos](docs/media/feature-demos/English/06_Product_videos/README.md)
- [TUI demo](docs/media/feature-demos/English/00_Full_demos/01_TUI_on_Windows_and_Android.mp4): Windows Terminal
  with pwsh 7.6.6, running Codex, Claude Code, and OpenCode. Android sends real `hi` prompts and receives real replies.
- [Feature overview](docs/media/feature-demos/English/00_Full_demos/02_Feature_overview.mp4): CLI, AI, model/permission
  options, files, and appearance.

Screen activity is edited at 2x while narration and subtitles keep their normal pace. Both terminal panels share
one retimed source. English titles, narration and captions are localized; recorded UI and replies retain their
original language. Each language has two full demos, fifteen feature clips and five previous product videos.
All final assets live in this repository. [Recording notes](docs/media/feature-demos/README.en.md).

## Download and start

- Android / Windows: [GitHub Releases](https://github.com/myucxy/TermLink/releases) (APK, Setup and full corresponding source) or [AppShip](https://appship.kget.cc:8910), with [release records](docs/releases/README.md).
- 0.3.14 / build 144: [Android APK](https://appship.kget.cc:8910/api/v1/artifacts/0696ce01-bb0a-4b4b-9853-4837118ce3a0/download) · [Windows Setup](https://appship.kget.cc:8910/api/v1/artifacts/89d25f4a-a8ae-4c1a-8505-d676af793cb0/download) · [full corresponding source](https://appship.kget.cc:8910/api/v1/artifacts/857db4f8-15c6-4c10-814a-04b7c6221e11/download). [Results](docs/RELEASE_0.3.14_144.md).
- Server: [Docker Hub](https://hub.docker.com/r/myucxy/termlink), with [deployment instructions](server/README.md).
- [Quick start](docs/guides/QUICK_START.md) · [Build guide](docs/development/BUILDING.md).
- [Matching source for 0.3.5 / build 135](https://appship.kget.cc:8910/api/v1/artifacts/ca7763b8-ae84-4a24-9c78-d0e5698bbca6/download):
  application `8503acb`, core `1c907c5`. [Hashes and release results](docs/RELEASE_0.3.5_135.md).

Windows currently uses the existing self-signed publisher identity. Signature integrity is separate from public
CA trust and SmartScreen reputation. Agent availability, login and usable account capacity are separate checks.

## Capabilities and architecture

- CLI: managed terminals, input, shortcuts, mouse reports, explicit window fit, and history browsing.
- AI: create or resume conversations from capabilities actually reported by the PC; model, reasoning,
  attachments, tools, approvals and interruption depend on the agent protocol.
- Files: browse and transfer PC files, preview results, and start AI conversations from a directory.
- Recovery: authenticated device discovery, encrypted relay, reconnect and host-owned terminal history.

Android connects through the relay server to Windows Agent and its managed sessions. Server/PostgreSQL handle
identity, authorization, metadata, routing and task coordination. They do not execute PC commands or persist
complete terminal/AI content or file objects. Windows owns execution and history; the separate Rust core owns
terminal state. ZCode desktop remote uses its official web entry point and a separate network boundary.

Current CLI and AI use/acceptance scope is **Relay Only**. Retained LAN/NAT code is not a claim of current
deployment or acceptance. See [current architecture](docs/architecture/CURRENT.md).

| Directory | Role |
| --- | --- |
| [android](android/README.md) | Android UI, crypto/network integration, native terminal bindings and view |
| [pc-client](pc-client/README.md) | Avalonia desktop UI and platform adapters |
| [win-agent](win-agent/README.md) | Agent, AI adapters, ConPTY SessionHost, Setup and Updater |
| [ai-web](ai-web/README.md) | Application-owned AI conversation UI embedded in Android |
| [server](server/README.md) | Go server, identity, metadata, relay and administration |
| [protocol](protocol/README.md) | Versioned messages, crypto envelopes and shared vectors |
| native/terminal-core | Locked prebuilt libraries; source/builds live in the separate [core repository](https://github.com/myucxy/terminal-core) |
| [docs](docs/README.md) | User guides, development, architecture, releases and provenance |

## References and acknowledgements

Thanks to [Happier](https://github.com/happier-dev/happier), [HAPI](https://github.com/tiann/hapi),
[CC Pocket](https://github.com/K9i-0/ccpocket) and [Happy](https://github.com/slopus/happy) for references in
agent capability descriptions, mobile chat, tool/approval presentation, host-owned synchronization and recovery.
These are behavioral/architecture references, not copies of their product implementations.
[Agent Client Protocol](https://github.com/agentclientprotocol/agent-client-protocol) and
[Codex](https://github.com/openai/codex) inform actual protocol adapters and compatibility work.

Terminal research includes [Windows Terminal](https://github.com/microsoft/terminal),
[Termux](https://github.com/termux/termux-app), [xterm.js](https://github.com/xtermjs/xterm.js),
[ConnectBot termlib](https://github.com/connectbot/termlib), [xterm](https://invisible-island.net/xterm/) and
[kitty](https://github.com/kovidgoyal/kitty). Termux-derived Android modules were retired; legacy Windows width
data retains its documented provenance. xterm.js remains in an independent reference harness, not the Android
product engine. Windows uses bundled Microsoft ConPTY components. [Migration boundaries](docs/archive/TERMUX_CLEANUP_20261008.md).

Actual components/resources include Avalonia, React and its documented dependencies, CommonMark Java,
LobeHub/ZCode icons, Nerd Fonts/Cascadia Code and legacy wcwidth data. See the
[full Chinese reference table](README.md#参考项目与致谢), [NOTICE](NOTICE),
[dependency inventory](docs/licenses/dependency-inventory.json), [AI Web notices](ai-web/THIRD_PARTY.md),
[server notices](server/THIRD_PARTY_NOTICES.md), [icon provenance](android/app/src/main/assets/licenses/ai-agent-icons-NOTICE.md)
and [font provenance](android/terminal-engine-api/src/main/assets/termlink-fonts/NOTICE.txt).
Project names and product marks do not imply endorsement. This overview is not a complete license audit.

## Contributing, security and releases

[Contributing](CONTRIBUTING.md) · [Security reports](SECURITY.md) · [Open-source readiness](docs/development/OPEN_SOURCE_READINESS.md)

GitHub checks documentation, committed-source secrets, Server/Web tests and source packaging. The separate
[client release pipeline](docs/development/GITHUB_RELEASE.md) verifies locally signed clients before the local command publishes them;
signing keys stay local. AppShip remains available; Docker is built locally and pushed to Docker Hub.
Separate Docker CI checks both image architectures and amd64 startup, database migrations, readiness and restart without pushing images.
Follow the [release runbook](docs/RELEASE_RUNBOOK.md) for signatures, source identity, tests and rollback.
