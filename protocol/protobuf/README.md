# Protobuf

生产实时契约由以下文件定义（实时 Envelope 已冻结 V1；跨引擎快照仍未冻结）：

- `termlink_realtime.proto`：仅携带路由元数据与 E2EE 密文的稳定 Envelope；
- `PRODUCTION_REALTIME_V1.md`：握手、设备证明、AEAD、ACK/恢复、方向 ACL 与流控规则。

临时 Phase 1 `TLT1` 协议仍只存在于 `../test-vectors/`。生产 `/v1/realtime`
不得接受它。跨引擎 `TerminalSnapshotV1` 尚未冻结；当前只启用有界事件回放恢复。
