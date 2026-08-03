# TermLink Phase 1 Test Session Protocol V1

状态：仅用于阶段 1 本地闭环，不是生产 Realtime、Protobuf 或 E2EE 协议。

## 1. 用途和边界

本协议把 Android `RemoteTerminalSession` 接到一个可重复的临时双向 Relay，用于验证：

- 原始 VT 输出、resize 和生命周期事件按序进入真实 Termux View；
- Android 在 `TerminalEngine.feed` 完成后发送累计 ACK；
- 重连只从最后连续 ACK 之后恢复；
- 每次输入携带唯一 `input_id`，且 `InputAccepted` 仅表示测试 Relay 已接受该输入；
- 慢消费者触发 Flow Control，永久缺失触发明确的 History Gap。

本协议不包含账号、设备批准、生产租约协调、加密或 Snapshot。它只能传输仓库内固定的
非敏感 Golden Stream 和用户在测试 Harness 中主动输入的测试数据，不得暴露到公网。
生产协议冻结时不得保持本协议的 Magic、Type 编号或 Payload 布局。

阶段 0 的 [LAN Replay V1](LAN_REPLAY_V1.md) 保持只读且格式不变。

## 2. 连接

```text
GET ws://<LAN-IP>:8080/v1/test/session/<case-name>?scenario=<scenario>
Authorization: Bearer <test-token>
```

`scenario` 允许：

| 值 | 行为 |
| --- | --- |
| `steady` | 发送固定流并保持 Live |
| `disconnect-once` | 初次连接在序号 6 后关闭；带 ACK 重连后继续 |
| `history-gap` | 序号 4 后声明 5..6 永久缺失 |
| `slow-consumer` | 最多允许 2 个未 ACK 事件，超时后关闭慢消费者 |
| `exited` | 包含固定流的 Ended 事件 |

## 3. 二进制帧

所有 WebSocket Data Message 都是二进制，多字节整数使用 Big Endian。公共 Header 为
24 字节：

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | ASCII Magic，固定为 `TLT1` |
| 4 | 1 | Version，固定为 `1` |
| 5 | 1 | Type |
| 6 | 2 | Flags，V1 固定为 `0` |
| 8 | 8 | 服务器事件 `sequence`；控制帧固定为 `0` |
| 16 | 8 | 客户端最高连续 `ack`；不适用时为 `0` |

### 3.1 Android 到测试 Relay

| Type | Name | Payload |
| ---: | --- | --- |
| `1` | Resume | 空；Header `ack` 为恢复点 |
| `2` | CumulativeAck | 空；Header `ack` 为已完成 feed 的最高连续序号 |
| `3` | Input | 16 字节 UUID + 原始输入字节 |
| `4` | ResizeRequest | `uint16 columns` + `uint16 rows` |
| `5` | LeaseRequest | 空 |
| `6` | LeaseRelease | UTF-8 lease ID |

### 3.2 测试 Relay 到 Android

| Type | Name | Payload |
| ---: | --- | --- |
| `129` | Welcome | UTF-8 case name |
| `130` | Output | 原始 ConPTY 字节 |
| `131` | Resize | `uint16 columns` + `uint16 rows` |
| `132` | Lifecycle | `uint8 state` + `int32 exit_code` |
| `133` | Gap | `uint64 missing_start` + `uint64 missing_end` |
| `134` | InputAccepted | 16 字节 UUID |
| `135` | LeaseGranted | `int64 expires_at_ms` + UTF-8 lease ID |
| `136` | LeaseRevoked | UTF-8 原因 |
| `137` | FlowControl | `uint32 max_unacked_events` |
| `138` | Error | UTF-8 稳定测试错误码 |
| `139` | Title | UTF-8 标题 |

Lifecycle state：`1 = Running`、`2 = Interrupted`、`3 = Ended`。Gap 帧的 `sequence`
必须等于 `missing_end + 1`，客户端不得越过缺口推进 ACK。

## 4. 行为约束

1. WebSocket 建立后，Android 必须先发送 Resume，Relay 才能发送事件。
2. Relay 只发送 `sequence > ack` 的事件；重复事件由 Android 丢弃。
3. Android 只能在事件完成 TerminalEngine 应用后发送 CumulativeAck。
4. Input 在收到相同 UUID 的 InputAccepted 前不算已接受；断线时不自动重试输入。
5. FlowControl 是测试级背压信号，不允许丢弃或重排 VT 字节。
6. History Gap 后保持只读；因跨端 Snapshot 尚未冻结，本 Harness 不从 Termux 私有状态恢复。
7. 日志不得记录输入 Payload、终端正文、Token 或完整客户端 IP。
