# Protobuf

生产实时契约由以下文件定义。实时 Envelope 已冻结 V1；快照对象的 Phase 1 字段与校验
契约已冻结供实现和互操作测试使用，但运行能力仍关闭：

- `termlink_realtime.proto`：稳定 Envelope、现有加密载荷，以及纯追加的
  `TerminalSnapshotV1`/`TerminalTranscriptPageV1` REST 对象定义；
- `PRODUCTION_REALTIME_V1.md`：握手、设备证明、AEAD、ACK/恢复、方向 ACL、流控和
  快照恢复边界；
- `../test-vectors/TERMINAL_SNAPSHOT_V1.md`：Phase 1 快照/历史页的规范性字段校验、
  E2EE domain、大小上限、原子 ACK 状态机和启用门禁。

V1 的主屏/transcript line ID 共用正数递增命名空间；副屏 producer-canonical ID 为零。
旧 V1 对象中的副屏非零 ID 仅为 importer 兼容并在导入时归零，绝不参与 cursor。分页使用固定
snapshot S 和 exclusive `before`，跨端无符号数值统一限制在 `Int64.MaxValue`。

临时 Phase 1 `TLT1` 协议仍只存在于 `../test-vectors/`。生产 `/v1/realtime`
不得接受它。当前运行时仍只启用有界事件回放恢复；`terminal_snapshot_v1` 不得宣告，
现有 `SNAPSHOT_OFFER`/`SNAPSHOT_CHUNK` 也不承载新对象。只有 C#/Kotlin E2EE 向量和
producer/importer 等价性测试通过后，才能另行启用能力。
