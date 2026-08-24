# TermLink Protocol

此目录保存跨组件协议和测试向量。生产实时 WebSocket Envelope、ACK/恢复、方向 ACL 与
E2EE 载荷外层契约已冻结在 `protobuf/`。`TerminalSnapshotV1` 与独立
`TerminalTranscriptPageV1` 的 Phase 1 对象字段和校验契约也已冻结，但运行能力仍关闭，
不能据此声称生产端已生成或恢复快照。

- `protobuf/`：生产实时 Envelope 与载荷契约；
- `test-vectors/GOLDEN_STREAM_V1.md`：本地固定流容器；
- `test-vectors/LAN_REPLAY_V1.md`：阶段 0 只读 WebSocket 回放协议，保持不变；
- `test-vectors/TERMINAL_SNAPSHOT_V1.md`：REST E2EE 快照/历史页的规范、边界、原子
  `ACK S → SUBSCRIBE(S) → S+1` 状态机与启用门禁；共享
  `test-vectors/terminal-snapshot-v1.json` 已由 Kotlin 与 C# 测试共同消费并验证；
- `test-vectors/PHASE1_TEST_SESSION_V1.md`：使用独立 `TLT1` Magic 的临时双向测试协议，
  仅验证 ACK、恢复、输入 accepted、测试租约、背压和 Gap，不是生产 Protobuf、Relay、
  E2EE 或 Snapshot 格式。

Phase 1 快照锚定现有 `snapshot_seq=S`，不消耗 `Envelope.seq`，通过 REST 历史对象传输；
现有 `SNAPSHOT_OFFER`/`SNAPSHOT_CHUNK` 保留但不使用。快照只含主/副屏最后可见区域，
scrollback 必须为空；上滑历史使用独立分页模型，禁止向实时 VT parser 前插中段数据。
主屏和 transcript 共用正数递增 line-ID 命名空间，副屏新对象固定使用零 ID；历史页始终按固定
S 和 exclusive `before`（首个 cursor 为主屏首行 ID）向前读取，所有跨 Kotlin/C# 的
`uint64` 值不得超过 `Int64.MaxValue`。无更早行时不构造空 page，而是返回无对象结果。

Android 已具备完整对象解密/校验、protobuf 到终端 domain 的唯一 decoder、Termux detached
prepare/install importer，以及 Room 中 renderer checkpoint 与 ACK S 的原子绑定。产品接线仍由
debug-only `TERMLINK_TERMINAL_SNAPSHOT_RUNTIME` 门禁控制，默认关闭且 release 强制关闭。
Windows 的 `PortableTerminalModelV1` 仍只是未连接 durable `LocalFrame` 的受限 ASCII 模型，
`ProductionHistoryUploader` 也只上传 `CHUNK`，所以任何组件都不得宣告
`terminal_snapshot_v1`、上传生产快照或用快照推进 ACK。

当前 wire representation 还不能无损表示 saved autoWrap、saved DEC private-mode bank、
DECSACE state 和 title stack。生产端必须在这些状态会影响后续输入时拒绝生成快照，直至严格
producer profile 或后续兼容契约解决；共享向量通过不等于运行能力通过。
