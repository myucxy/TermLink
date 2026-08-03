# TermLink Server

当前 `server` 仅包含阶段 0/1 的 Docker 测试服务。它从仓库内固定、非敏感的
Golden Stream Bundle 构造两条彼此隔离的 WebSocket 测试路径：

```text
LAN Replay V1（只读） → Android → TerminalEngine
Phase 1 Test Session V1（临时双向） ↔ Android RemoteTerminalSession → Termux View
```

第一条路径继续使用冻结且不变的 [LAN Replay V1](../protocol/test-vectors/LAN_REPLAY_V1.md)。
第二条路径使用独立 `TLT1` Magic，专门验证累计 ACK、断线续传、输入 accepted、测试租约、
慢消费者 Flow Control 和 History Gap；规范见
[Phase 1 Test Session V1](../protocol/test-vectors/PHASE1_TEST_SESSION_V1.md)。

整个服务都不是生产 Relay：它使用明文 `ws://`，不连接真实 Shell，也没有账号、设备批准、
生产租约、E2EE、可移植 Snapshot 或持久历史。Phase 1 入口只会接受 Harness 主动发送的
测试输入，`InputAccepted` 也只表示测试服务收到了匹配的 UUID。

## 启动局域网测试容器

在仓库根目录执行：

```powershell
docker compose -f server\compose\lan-test.compose.yml up --build -d
docker compose -f server\compose\lan-test.compose.yml ps
```

默认配置：

- HTTP/WebSocket 端口：`8080`，绑定所有本机接口；
- 测试令牌：`termlink-lan-test`；
- 测试用例：`smoke-powershell-v1`；
- 健康检查：`GET /healthz`；
- 只读 WebSocket：`GET /v1/test/replay/<case-name>`，使用 Bearer Token；
- 临时双向 WebSocket：
  `GET /v1/test/session/<case-name>?scenario=<scenario>`，使用相同 Bearer Token。

双向入口的 `scenario` 可取 `steady`、`disconnect-once`、`history-gap`、
`slow-consumer` 或 `exited`。

若要避免使用默认测试令牌，应在第一次 `up` 前设置至少 12 个字符的值，并在 Android
Harness 输入同一个值：

```powershell
$env:TERMLINK_TEST_TOKEN = 'replace-with-a-local-test-token'
docker compose -f server\compose\lan-test.compose.yml up --build -d
```

## 验证

先查找 PC 当前 Wi-Fi 或以太网 IPv4 地址，再从 PC 验证两个入口：

```powershell
ipconfig
Invoke-RestMethod http://127.0.0.1:8080/healthz
Invoke-RestMethod http://<PC-LAN-IP>:8080/healthz
```

预期响应：

```json
{"status":"ok","mode":"lan-replay-test"}
```

`mode` 是为既有 LAN Replay 自动化保留的兼容健康标识，不是当前路由能力清单。

手机需要与 PC 位于同一可互访局域网。先用手机浏览器打开
`http://<PC-LAN-IP>:8080/healthz`；只有该地址可访问后，才继续测试 Android WebSocket。

在 API 36 模拟器上验证完整 Android 双向路径：

```powershell
cd android
.\Validate-Phase1RelayEmulator.ps1
```

脚本使用 `ws://10.0.2.2:8080`，验证一次断线后从 ACK 续传、取得测试租约后的输入
accepted、明确的 `5..6` History Gap、exit 17，以及 0 crash/ANR。机器可读证据写入
`docs/validation/phase1-android-relay-emulator.json`。

## 安全边界

- `ws://` 和默认令牌只适合可信局域网内的非敏感固定测试流，禁止映射到公网；
- LAN Replay 路由保持只读；Phase 1 路由只接收主动测试输入，不得输入密钥、真实命令或
  其他敏感内容；Bundle 名称受限，浏览器 Origin 请求被拒绝；
- 容器以非 root 用户运行，根文件系统只读，并丢弃全部 Linux capabilities；
- Compose 不会修改 Windows 防火墙。如果手机访问失败，先检查当前 IP、同一 Wi-Fi、
  访客网络或 AP 客户端隔离；需要新增防火墙规则时必须单独评估和授权。

结束测试：

```powershell
docker compose -f server\compose\lan-test.compose.yml down
```

## Go 检查

```powershell
cd server
D:\Dev\Env\Go\bin\go.exe test ./...
D:\Dev\Env\Go\bin\go.exe vet ./...
.\Validate-Phase1TestRelay.ps1
```

验证脚本会生成 `docs/validation/phase1-test-relay.json`，并确认现有 LAN Replay 测试仍然
通过。当前实现没有把临时帧格式标记为生产 Protobuf，也没有改变 LAN Replay V1。
