# TermLink 知识库

> 生成于 2026-09-17。来源：仓库现状通读 + 最后一次 Codex 会话完整解析
> （`rollout-2026-09-16T16-01-58-01a0a93c-a3ac-73a2-aa47-c40249257887.jsonl`，2026-09-16 16:01–16:34 本地时间）。

## 文件索引

| 文件 | 内容 |
| --- | --- |
| [01-project-overview.md](01-project-overview.md) | 项目定位、组件清单、技术栈、开发状态 |
| [02-architecture.md](02-architecture.md) | 连接层 / E2EE / ConPTY / ACK-Gap-Snapshot / 直连传输 / 部署形态 / Termux 参考边界 |
| [03-history-sessions-analysis.md](03-history-sessions-analysis.md) | "历史会话"功能数据流 + 4 个已核实风险（最后一次会话的核心产出） |
| [04-session-log-2026-09-16.md](04-session-log-2026-09-16.md) | 最后一次 Codex 会话全程纪要，含被 429 中断的登录诊断与待验证假设 |
| [05-current-wip.md](05-current-wip.md) | 当前 92 处未提交变更全景、主题归纳、与会话发现的交叉风险 |
| [06-project-audit.md](06-project-audit.md) | 全项目深度审计（2026-09-18）：53 项发现（P0×1/P1×15/P2×16/P3×21）、四域详析、修复路线图 |

## 一分钟摘要

- **TermLink** 是远程终端产品：Android 端安全查看/控制自己账号下多台 Windows PC 的受管终端会话；生产运行时只有 `反向代理 → TermLink Server → PostgreSQL` 两个容器，终端正文永不落服务端。
- 两大自研 Rust 核心：`terminal-core`（终端状态权威，Windows/Android 生产在用）与 `network-core`（STUN/QUIC 直连传输，Relay 永远兜底）。
- 上次 Codex 会话完成了**历史会话功能分析**，确认架构合理但发现 4 个风险（目录无界增长、Presence 混淆 PC/会话在线、Web 门户与 Android 状态不一致、离线历史不可见），均已对照当前代码核实。
- 会话第二轮（"本地 Docker 上安卓模拟器无法登录"）**刚起步即因 429 限流中断，未得出结论**；现场证据与两个主要假设记录在 [04](04-session-log-2026-09-16.md)。
- 当前工作区有 92 处未提交变更，几乎全部集中在 `server/`，主题是"运行时简化 + 安全加固"（运行时密钥入库、默认关闭注册、设备禁用、内存 presence、去 Caddy）。

## 维护约定

- 会话记录类文件（04）是历史快照，不随后续代码演进而改写；分析结论类文件（03、05）中的代码锚点已按 2026-09-17 工作树核对。
- 修改 Android 端前仍须遵循根目录 [AGENTS.md](../../AGENTS.md) 的 Termux 参考政策。
