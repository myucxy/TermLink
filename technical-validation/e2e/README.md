# E2E 技术验证模块

本目录保存从旧 `TermLink-E2E` 独立工作副本中迁移出的、仍具有独立验证价值的最小闭环。它是技术验证夹具，不是生产服务端、发布包或产品 E2E 结论。

## 保留内容

- `cmd/` 与 `internal/`：只在内存中保存账号、设备、PC、会话和租约的 Go Relay；进程退出后数据全部消失。
- `protocol-client/`：引用当前仓库 `win-agent/src/Protocol` 的 .NET 协议替身客户端，不复制 Protobuf 或 Windows 产品代码。
- `Test-E2ERelay.ps1`：启动真实 loopback HTTP/WebSocket Relay，验证签名设备注册、目录、租约、两个会话、输入、resize、ACK、密文透传和重连。
- `Dockerfile` 与 `compose.yml`：可选的本机容器运行方式，默认只绑定 `127.0.0.1:18080`。

## 明确未迁移内容

- 旧副本中的 Android、Windows Agent、Termux Fork、协议和测试数据副本；这些内容以当前仓库对应目录为唯一事实来源。
- `.git`、`.termlink-dev`、`local.properties`、Gradle/.NET/Go 构建输出、截图、日志和历史验收结果。
- 绑定旧 Android UI、旧 Agent 进程结构和旧本机目录的产品验收编排；它们不能证明当前版本的端到端能力。
- 固定开发配置、运行状态和任何凭据。

## 验证边界

本模块实际覆盖：

```text
.NET 协议替身客户端
        ↕ production Protobuf Envelope
loopback HTTP/WebSocket
        ↕
内存 E2E Relay
```

它不启动 Android APK、不连接真实 Windows ConPTY、不执行真实 Shell，也不解密产品 E2EE 正文，因此测试成功只能说明协议和 Relay 行为成立，不能作为 Android → Server → Windows 产品 E2E 或发布门禁。

## 本机运行

工具版本以 `D:\Dev\DEV_ENV_MANIFEST.json` 为准：Go 1.26.2、.NET SDK 8.0.303 和 PowerShell 7。

```powershell
pwsh -NoProfile -File .\technical-validation\e2e\Test-E2ERelay.ps1
```

测试证据写入已被 Git 忽略的 `artifacts/technical-validation/e2e/<run-id>/`。

## Docker 运行

```powershell
docker compose -f .\technical-validation\e2e\compose.yml up --build
```

健康检查：`http://127.0.0.1:18080/readyz`。该容器没有数据库、持久化卷、OIDC、对象存储和生产账号隔离，不应暴露为公网服务。

当前产品级 E2E 入口仍以仓库根目录的 `scripts/Invoke-TailFirstProductE2E.ps1`、`scripts/Invoke-FileOperationsE2E.ps1` 以及 `server/compose/acceptance.compose.yml` 为准。
