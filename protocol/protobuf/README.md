# Protobuf

生产实时契约由以下文件定义。实时 Envelope 已冻结 V1；快照对象的 Phase 1 字段与校验
契约已冻结供实现和互操作测试使用，但运行能力仍关闭：

- `termlink_realtime.proto`：稳定 Envelope、现有加密载荷，以及纯追加的
  `TerminalSnapshotV1`/`TerminalTranscriptPageV1` REST 对象定义；
- `termlink_transport.proto`：独立连接层信令、LAN/STUN 候选、短期路径票据、切换栅栏与
  QUIC 直连帧。直连只搬运原始 `Envelope` 字节，不建立第二套终端序列或 ACK；
- `PRODUCTION_REALTIME_V1.md`：握手、设备证明、AEAD、ACK/恢复、方向 ACL、流控和
  快照恢复边界，以及可选直连数据路径与 Relay 回退边界；
- `../../docs/NETWORK_CONNECTION_AND_DIRECT_TRANSPORT.md`：连接层拆分、候选优先级、
  NAT 打洞、make-before-break 切换、安全上限、失败回退和验收矩阵；
- `../test-vectors/TERMINAL_SNAPSHOT_V1.md`：Phase 1 快照/历史页的规范性字段校验、
  E2EE domain、大小上限、原子 ACK 状态机和启用门禁。
- `../test-vectors/direct-transport-ticket-v1.json`：服务端 Ed25519 路径票据的规范 protobuf
  与 UUID network-order 向量；
- `../test-vectors/direct-transport-crypto-v1.json`：Android、Windows 与 Rust 必须共同验证的
  signalling HKDF/AEAD、probe HKDF/HMAC、peer-proof transcript、TLS exporter context 和
  direct path binding 向量。

V1 的主屏/transcript line ID 共用正数递增命名空间；副屏 producer-canonical ID 为零。
旧 V1 对象中的副屏非零 ID 仅为 importer 兼容并在导入时归零，绝不参与 cursor。分页使用固定
snapshot S 和 exclusive `before`，跨端无符号数值统一限制在 `Int64.MaxValue`。

临时 Phase 1 `TLT1` 协议仍只存在于 `../test-vectors/`。生产 `/v1/realtime`
不得接受它。当前运行时仍只启用有界事件回放恢复；`terminal_snapshot_v1` 不得宣告，
现有 `SNAPSHOT_OFFER`/`SNAPSHOT_CHUNK` 也不承载新对象。只有 C#/Kotlin E2EE 向量和
producer/importer 等价性测试通过后，才能另行启用能力。

`termlink_transport.proto` 不修改冻结的 Realtime V1 wire。生产端只有在独立信令端点、
双方设备认证、QUIC 路径、栅栏切换、原字节回退重发和故障注入全部通过后，才可宣告
`direct_transport_v1`。无论是否启用该能力，`/v1/realtime` WSS 都保持已认证、心跳活跃且
可立即承载原有 Envelope；候选收集、STUN、QUIC 或渲染器失败都不得关闭 Relay。
