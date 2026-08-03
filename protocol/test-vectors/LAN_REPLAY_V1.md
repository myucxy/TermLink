# TermLink LAN Replay Protocol V1

状态：仅用于阶段 0/1 局域网开发 Harness，不是生产 Realtime/E2EE 协议。

## 1. 用途和边界

LAN Replay V1 让 Android 手机通过 Docker WebSocket 接收仓库内固定的非敏感 Golden
Stream，验证以下链路：

```text
Golden Stream Bundle → Docker WebSocket → Android → TerminalEngine
```

该服务不接受终端输入、不连接真实 Shell、不提供账号或设备发现，也不应暴露到公网。
生产 Relay 仍必须使用认证、设备批准和 E2EE，本协议不能直接升级为生产协议。

## 2. 连接

```text
GET ws://<LAN-IP>:8080/v1/test/replay/<case-name>
Authorization: Bearer <test-token>
```

- `case-name` 只允许小写字母、数字、点、下划线和连字符；
- 浏览器 `Origin` 请求被拒绝，Android/CLI 等无 Origin 客户端可以连接；
- 默认测试令牌仅保护非敏感样例，不能用于真实终端正文。

## 3. 二进制帧

所有 WebSocket Data Message 都是二进制。多字节整数使用 Big Endian。

公共 Header 为 18 字节：

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 1 | Version，固定为 `1` |
| 1 | 1 | Type |
| 2 | 8 | Golden Stream `seq`，无符号整数 |
| 10 | 8 | `elapsed_us`，无符号整数 |

Type：

| Value | Name | Payload |
| ---: | --- | --- |
| 1 | Started | UTF-8 JSON 元数据 |
| 2 | Resize | `uint16 columns` + `uint16 rows` |
| 3 | Output | 原始 ConPTY 字节，不做编码转换 |
| 4 | Ended | `int32 exit_code` |

Started JSON：

```json
{
  "case_name": "smoke-powershell-v1",
  "event_count": 14,
  "output_event_count": 10,
  "output_bytes": 702,
  "output_sha256": "lowercase hex"
}
```

## 4. 客户端校验

Android 必须在显示 `Verified` 前确认：

1. Version 和 Type 已知；
2. `seq` 从 1 开始严格连续；
3. `elapsed_us` 非递减；
4. Started 和 Ended 各出现一次且位于首尾；
5. 输出事件数、总字节数和 SHA-256 与 Started 元数据一致；
6. 至少收到一个合法 Resize；
7. WebSocket 只包含二进制帧。

任一校验失败都必须关闭连接并显示失败，不能把部分结果标记为通过。
