# 02 · 架构与协议要点

## 连接层（Relay Realtime）

- WebSocket `/v1/realtime`，子协议 `termlink.realtime.v1`。
- 握手 = OIDC bearer + 设备证明头（`X-TermLink-Device/Timestamp/Nonce/Signature`，Ed25519 签名 `TERMLINK-DEVICE-PROOF-V1` 域，60s 时钟窗 + nonce 一次性消费）。
- 首帧为有界 JSON `ClientHello`/`ServerWelcome`，之后只收二进制 Protobuf `Envelope`（≤1 MiB）。
- `liveness_probe_v1` ping/pong 用于 Android 前后台切换前的存活探测。
- 权威契约：`protocol/protobuf/PRODUCTION_REALTIME_V1.md`。

## E2EE

- 应用消息 = nonce 12B + ChaCha20-Poly1305 密文（Envelope field 11），`key_id` 标识 X25519/HKDF key epoch。
- AD 是除 field 11 外所有字段的确定性 protobuf 编码；收发方向不同 HKDF key，`(key_id, direction, seq)` 永不复用。
- Relay 校验路由/授权/方向/序号但**永不解密** field 11；ACK 是路由可见的累计游标副本，密文游标必须与 `Envelope.ack` 一致。

## ConPTY 路径（Windows Agent）

- 每会话启动 `termlink-session-host.exe` 拥有 ConPTY 与 Shell 进程树（PowerShell/CMD/WSL）。
- Host↔Agent 走当前用户 SID ACL Named Pipe（V9，兼容 V5-V8）+ 32B 一次性 Token + 对端 PID 双向认证；长度前缀帧（1 MiB 上限）、64 位序号、4 MiB 未 ACK 有界队列、断管重连重发、溢出发 `Gap`。
- Agent durable ACK 后才把事件按序 apply 到自研 terminal-core（`win-agent/src/Agent/TerminalCoreAuthority.cs`）；TerminalCore 是渲染/ACK/Snapshot/Transcript 的唯一权威。

## ACK / History Gap / Snapshot

- 累计 ACK + suffix-only resume；Gap 显式覆盖缺失序号范围，不静默丢弃；Android `RemoteTerminalSession` 有 HistoryGap 状态。
- 输入去重：最近 4096 个 input_id，DPAPI 持久化。
- Snapshot V1 经 recipient-bound REST E2EE 历史对象传输，锚定 `snapshot_seq=S`，完整恢复并绑定 renderer checkpoint 后才可 ACK S，下一订阅 `SUBSCRIBE(S)`；scrollback 不进 snapshot，上滑历史走独立分页（`docs/TERMINAL_SNAPSHOT_V1.md`）。

## 控制租约

- 远端输入须设备授权 + 短时输入租约，本地用户优先级最高；服务端协调租约/撤销。
- 最近提交 `7dec0e4` 已把 PC agent 默认设为 collaborative control。

## 终端历史（主机持有）

- Windows 主机本地持有（WAL/checkpoint + DPAPI CurrentUser 加密落盘）；服务端不落历史对象。
- `host_history_v1` 实时请求按需提供；已结束会话由 Android 经临时零窗口 E2EE 通道从在线主机恢复（详见 [03](03-history-sessions-analysis.md)）。

## 直连传输（Direct / NAT）

- 独立信令 WebSocket `/v1/transport/signals`（`termlink.transport.v1`）。
- network-core 做 STUN/QUIC shadow direct；稳定性优先于直连命中率——**Relay 始终权威，直连失败同步回 `RelayOnly`**，失败不产生额外连接建立时延。

## 文件传输

- `REALTIME_V2` 存储无关协议，独立 WebSocket `/v1/file-transfers`（`termlink.file-transfer.v2`）；E2EE、断点续传，字节只在 Android 与 Windows 间流动，服务端不做对象存储。

## 单机部署形态

- Server `single-node` 模式内置 OIDC(Fosite)、迁移、内存协调器、Relay、Direct/NAT 信令、注册/邀请码/邮件/人机验证。
- PostgreSQL 是唯一外部长期服务；不需要 Keycloak/Valkey/MinIO/Caddy 容器。
- 本地开发：`server/compose.yml`（WIP 新文件）起 `termlink-local-server-1` + `termlink-local-postgres-1`，端口绑定由 `TERMLINK_HTTP_BIND_IP`（默认 0.0.0.0）+ `TERMLINK_HTTP_PORT`（默认 8080）控制。

## Termux 参考边界（AGENTS.md 政策的现状结论）

- 固定来源：`termux/termux-app` commit `3df69d1da197dd9bd71a3bafd902dffd720576b4`（见 `terminal-forks/termux-terminal/UPSTREAM.md`，GPLv3）。
- Termux 的 `TerminalBuffer`、滚动、换行、选择行为可作差分参考；其**本地 PTY、环形 scrollback、Bootstrap/APT 架构不能照搬**，因为 TermLink 的历史是跨设备协议而非本地缓冲。
- Android 端 `terminal-engine-termux` 仅作 Harness/差分用途；生产渲染权威是 `terminal-engine-native`（自研 terminal-core + 自研 View）。
- 独立 `TermuxTranscriptViewport` 方案被确认为正确适配（2026-09-16 会话复核）。
