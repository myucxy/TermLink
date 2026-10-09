# 01 · 项目总览

> 本文保留原阶段技术盘点。2026-10-09 起，应用、network-core 与独立 terminal-core 的自有代码均采用 `GPL-3.0-only`；下文 Proprietary/Apache-2.0 描述为历史声明，当前以[授权说明](../../LICENSE-STATUS.md)为准。Termux 退役材料已迁出，见[清理记录](../archive/TERMUX_CLEANUP_20261008.md)。

## 定位

TermLink 是一个远程终端产品：Android 手机端安全、低延迟地查看和控制自己账号下多台 Windows PC 上的受管终端会话，支持断线恢复、可选加密历史、设备撤销、审计和会员套餐。

**生产运行时只有两个容器**：`外部 HTTPS 反向代理 → TermLink Server → PostgreSQL`。Server 内置身份（OIDC）/账号/设备发现/会话控制/Relay/Direct-NAT 信令/文件任务/Agent 升级任务/Web 管理页；**Server 不执行 Windows 命令、不建 ConPTY、不存终端历史、不存文件对象**（这些归 Windows Agent 与客户端）。商业套餐、支付、MinIO、服务端历史存储、Keycloak/Valkey/Caddy 容器均已移除。

## 组件清单

| 目录 | 用途 |
| --- | --- |
| `android/` | Android 客户端（Gradle 多模块）。`terminal-engine-api`（引擎稳定边界）、`terminal-engine-native`（terminal-core JNI + 自研 View，**生产渲染权威**）、`terminal-engine-replay`（Golden Stream 严格回放）、`terminal-engine-termux`（Termux Fork 适配层，Harness/差分用，非生产权威）、`core-session`（`RemoteTerminalSession` 状态机：Live/Reconnecting/ReadOnly/HistoryGap/租约）、`core-network`（HTTPS/OIDC/Protobuf realtime）、`core-crypto`（X25519/HKDF/ChaCha20-Poly1305，Keystore 包装）、`core-database`/`core-model`（Room 加密历史索引）、`app`（Compose 产品页 + Harness，入口 `dev/termlink/{product,harness}`） |
| `server/` | Go 单二进制 `cmd/termlink-server`（另有 lan-test、production-e2e-probe、identity-acceptance-bootstrap）。`internal/` 约 35 个包：auth、identity(Fosite OIDC)、realtime/realtimewire(Relay)、kv(Valkey，测试用)、store、history、filetransfer、agentupdate、billing/commerce（存量）、lanreplay/phase1test（测试夹具）、presence（新增，见 05）、webportal、outbox、migrate 等；`migrations/` 48 个 SQL 迁移 |
| `pc-client/` | 跨平台桌面客户端。ADR 0001：从 WinForms 迁到 Avalonia（2026-08-26 起，基线 tag `pc-avalonia-baseline-v0.1.32`）。分层依赖 `Contracts ← Platform.Abstractions ← Presentation ← Desktop`（架构测试强制）；`src/Agent.Host`（Linux/macOS 独立 Agent 进程，Unix-domain IPC）；`native/posix/termlink_pty.c`（POSIX PTY）；AppImage/PKG 打包 |
| `win-agent/` | Windows Agent（C#/.NET 8，SDK 锁 8.0.303）。`src/Agent`（会话目录、Relay、加密缓冲）、`src/SessionHost`（每会话一个 ConPTY Owner，`TerminalCoreAuthority.cs` 在 durable ACK 后有序 apply terminal-core）、`src/TerminalCore`（Rust FFI 绑定）、`src/Protocol`（Protobuf Envelope、设备证明、E2EE）、`src/Tray`、`src/Updater`、`src/Setup`（MSIX/单文件安装器）、`tests/` 58 项验证脚本 |
| `terminal-core/` | **完全自研** Rust 跨平台终端状态引擎（edition 2024，零依赖，Proprietary）。crates：`termlink-terminal-core`（确定性状态机、VT 子集、有界 scrollback、Snapshot V2.0-V2.10）、`termlink-terminal-ffi`（panic-contained C ABI，`include/termlink_terminal.h`，abi_version/struct_size 防漂移）。另有 `UNIMPLEMENTED_CAPABILITIES.md`、`DEVELOPMENT_ROADMAP.md` |
| `network-core/` | Rust 直连传输核心（transport-only，"shadow direct"）。crates：`termlink-direct-wire`（`TLD1\n` 数据报、HMAC、PROBE）、`termlink-direct-nat`（单 UDP socket 全程持有 + RFC 8489 STUN）、`termlink-direct-quic`（Quinn 0.11.11/rustls，TLS1.3，ALPN `termlink-direct/1`，MTU 1200）、`termlink-direct-path`（直连失败同步回 RelayOnly）、`termlink-direct-ffi`（C ABI v2→v5）。设备私钥不进此核心 |
| `protocol/` | 跨组件协议与测试向量。`protobuf/termlink_realtime.proto`（生产 Envelope，冻结）、`termlink_transport.proto`（直连信令）、`termlink_file_transfer.proto`、`PRODUCTION_REALTIME_V1.md`（权威契约）；`test-vectors/`：GOLDEN_STREAM_V1、LAN_REPLAY_V1、TERMINAL_SNAPSHOT_V1、BOUNDED_HISTORY_V1、E2EE_SESSION_V1 等（Kotlin 与 C# 共同消费） |
| `terminal-forks/` | 固定上游 Termux 源码：`termux-terminal/`（固定 commit `3df69d1da197dd9bd71a3bafd902dffd720576b4`，GPLv3，排除本地 PTY/JNI）、`termux-patches/`（remote-session-port、phase0-conpty-compat 两个补丁）、`Sync-TermuxTerminal.ps1`。详见 [02](02-architecture.md) 的 Termux 边界 |
| `terminal-reference/` | `xterm-harness`：离线 xterm.js 6.0.0 Golden Stream 对照 Oracle（Node `--test`）。是开发对照器，**不是**生产 WebView 回退引擎 |
| `scripts/` | 根级发布与验收 PowerShell：`Build-Release.ps1`、`Invoke-TailFirstProductE2E.ps1`（真实 Windows→Relay→Android E2E）等 |
| `release/` | 发布验证模块：`Verify-LocalRelease.ps1`、`UnifiedRelease.psm1` 等 |
| `technical-validation/` | `e2e/`（内存态 Go Relay loopback E2E）、`direct-e2e/`（Direct 传输跨运营商故障矩阵，发布门禁） |
| `ui-prototypes/` | 生产级纯 HTML/CSS/JS 交互原型，三端 15 页面，设计语言 "Quiet Focus" |
| `prototypes/` | 仅 `conpty-probe/`：ConPTY 技术验证 + Phase 0 Golden Stream 录制器 |
| `artifacts/` | 构建产物：`release/` 有 0.2.32-90 至 0.2.44-100 共 11 个版本目录 |
| `docs/` | 设计基线与验收台账（见下） |

## 技术栈

| 组件 | 语言/框架 | 关键依赖 |
| --- | --- | --- |
| server | Go 1.26 | gorilla/websocket、pgx/v5、ory/fosite、go-oidc、prometheus、protobuf |
| android | Kotlin 2.1.21、AGP 8.11.1、Compose、Room | Bouncy Castle、protobuf 0.9.5 插件 |
| win-agent | C#/.NET 8（SDK 8.0.303） | WinForms Tray、P/Invoke ConPTY/Named Pipe/DPAPI |
| pc-client | C#/.NET 8 + Avalonia 12.1.1 | C（posix PTY）、AppImage/PKG |
| terminal-core | Rust edition 2024（1.97.1） | 零依赖，C ABI FFI |
| network-core | Rust edition 2024（Apache-2.0） | Quinn 0.11.11 + rustls 0.23.43 |

## 开发状态（截至 2026-09 中旬）

依据 `DEVELOPMENT_HANDOFF.md`：

- 开发项 1–6（账号/OIDC、Protobuf+E2EE Relay、ConPTY 输入输出+ACK/Gap/租约、主机本地加密历史、计费边界、产品 UI/接线）**均已实现并通过自动化验收**。
- 开发项 7（生产 TLS 受信证书、真实安装更新、备份恢复演练、镜像签名、监控告警、发布加固）**未开始**。
- 2026-09 上中旬主线：内置身份服务迁移、单节点部署简化、存储剥离、Direct transport 收尾（详见 [05](05-current-wip.md)）。

## 关键文档索引（docs/）

| 文档 | 说明 |
| --- | --- |
| `COMPLETE_SYSTEM_DESIGN.md` | 权威完整产品与系统设计 |
| `TERMLINK_TERMINAL_CORE_V2.md` | 自研 Rust 终端核心 V2 定位与边界 |
| `TERMINAL_COMPATIBILITY_CORE_V1.md` | 2026-08-25 P0-P6 基线（断线恢复 + Codex CLI 兼容） |
| `UI_LAYOUT_DESIGNS.md` | 三端 UI 布局与交互规则 v1.3 |
| `NETWORK_CONNECTION_AND_DIRECT_TRANSPORT.md` | 网络连接层与直连传输设计 |
| `HOST_OWNED_TERMINAL_HISTORY.md` | 主机持有终端历史的决策与边界 |
| `REALTIME_FILE_TRANSFER_V2.md` | 存储无关 E2EE 文件传输 V2 协议 |
| `EMBEDDED_IDENTITY_MIGRATION.md` | Ory Fosite 内置 OAuth2/OIDC 身份服务 |
| `PC_AVALONIA_MIGRATION.md` | PC 客户端 Avalonia 迁移 P0-P8 |
| `TAIL_FIRST_E2E.md` | 真实 Windows→Relay→Android 产品级 E2E 验收入口 |
| `VALIDATION_BASELINE_2026-09-03.md` | 已通过验证的持久台账（避免重复回归） |
| `RELEASE.md` / `RELEASE_RUNBOOK.md` | 统一发布构建入口与现场发布/故障恢复手册 |
| `docs/adr/0001` / `0002` | Avalonia PC 客户端 + 进程外 Agent；Windows 稳定机器标识 |
| `docs/validation/` | 25+ 份 JSON 验收证据（phase0/phase1、Avalonia P2-P8、MSIX 生命周期） |
