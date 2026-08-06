# TermLink Protocol

此目录保存跨组件协议和测试向量。生产实时 WebSocket Envelope、ACK/恢复、方向 ACL 与
E2EE 载荷外层契约已冻结在 `protobuf/`；`TerminalSnapshotV1` 仍未冻结。

- `protobuf/`：生产实时 Envelope 与载荷契约；
- `test-vectors/GOLDEN_STREAM_V1.md`：本地固定流容器；
- `test-vectors/LAN_REPLAY_V1.md`：阶段 0 只读 WebSocket 回放协议，保持不变；
- `test-vectors/PHASE1_TEST_SESSION_V1.md`：使用独立 `TLT1` Magic 的临时双向测试协议，
  仅验证 ACK、恢复、输入 accepted、测试租约、背压和 Gap，不是生产 Protobuf、Relay、
  E2EE 或 Snapshot 格式。
