# TermLink Protocol

此目录保存跨组件协议和测试向量。当前只冻结 Golden Stream 和 LAN Replay 测试格式，不提前
冻结生产实时 WebSocket Envelope、E2EE 载荷或 `TerminalSnapshotV1`。

- `protobuf/`：兼容性验证通过后再加入实时协议定义；
- `test-vectors/GOLDEN_STREAM_V1.md`：本地固定流容器；
- `test-vectors/LAN_REPLAY_V1.md`：阶段 0 只读 WebSocket 回放协议，保持不变；
- `test-vectors/PHASE1_TEST_SESSION_V1.md`：使用独立 `TLT1` Magic 的临时双向测试协议，
  仅验证 ACK、恢复、输入 accepted、测试租约、背压和 Gap，不是生产 Protobuf、Relay、
  E2EE 或 Snapshot 格式。
