# TermLink

TermLink 是一个面向个人和小型团队的多 PC 远程终端系统。Windows 端 Agent 负责创建、采集和控制受管终端会话；Android 客户端使用同一账号发现不同 PC，并实时查看终端输出、恢复历史记录及在授权后输入命令；云端 Relay 负责认证、设备发现和加密数据转发。

阶段 0 当前环境可自动化验收已完成。现有独立 ConPTY Session Host、Golden Stream V1、
`TerminalEngine` API、Android Compose Harness、固定 Termux emulator/view Fork、完全离线
xterm.js 6.0.0 Oracle，以及只读 Docker LAN Replay 测试闭环。四种 Shell、ANSI/Unicode、
Terminal Features、Vim、less、fzf 和 htop 共 10 个固定流，在 Termux/xterm.js 间达到
122/122 checkpoint match，并在 API 36 模拟器 10/10 冷启动回放通过。arm64 真机、厂商
IME、外接键鼠与 Android WebView 回退引擎仍待后续验证或实现。

阶段 1 的本机与生产基础已完成可重复验收：正式 .NET 8 Agent/SessionHost/Tray/Updater 工程已经建立；
Session Host 与 Agent 之间具备当前用户 Named Pipe、双向 PID/Token 认证、序号、ACK、
重连重发、有界 Gap、输入去重和 DPAPI Buffer。DPAPI 注册索引已在 Agent 强制结束和受控
重启后两次恢复 20/20 个真实 ConPTY SessionHost，远程停止不影响 20 个本地 Shell。Android 的
`RemoteTerminalSession` 已通过独立 `core-network` 接到 Docker 临时双向测试 Relay，并驱动
真实 Termux View。API 36 模拟器已验证累计 ACK、断线续传、输入 accepted、测试租约、
History Gap 和 exited。该链路使用隔离的明文测试协议和固定非敏感夹具；LAN Replay V1、
TLT1 和 Harness 不是生产 Relay、生产 Protobuf、设备授权或 E2EE。生产账号、设备配对、
Protobuf/E2EE Relay、历史、计费、产品页面和 Compose 接线已经实现并通过自动化验证；
第 7 项的生产 TLS 配置、备份恢复、镜像供应链、监控告警和发布策略已经实现并完成本地验收。
Windows 端现已增加签名 MSIX、登录自启、Tray 单实例和受控更新器；开发证书下已验证独立
签名更新清单、包身份/版本/SHA-256、篡改拒绝、原子缓存、可恢复事务日志及健康检查失败
回滚，并通过临时 `LocalMachine/TrustedPeople` 开发信任完成真实 `Add-AppxPackage` 安装、
包身份启动、更新器升级、强制降级和卸载。开发信任在演练后已精确清理；生产发布仍需真实
发布证书链、域名 ACME 签发、外部告警接收器及 GitHub OIDC 镜像发布验收。

## 开发项 1-7 完成矩阵

| 开发项 | 范围 | 状态 | 验证与边界 |
| --- | --- | --- | --- |
| 1 | 生产账号/OIDC/设备注册与配对 | **已实现并验证** | Go `auth`、设备注册/配对 API、Android OIDC/API 客户端、Windows 配对流程；Go/Android/.NET 契约与自动化验收通过，真实外部 OIDC enrollment/provider 部署不在本次验收范围。 |
| 2 | 生产 Protobuf 实时协议、X25519 + ChaCha20-Poly1305、Relay | **已实现并验证** | `protocol/protobuf/termlink_realtime.proto`、Go `realtimewire`/Relay、Android `core-network`、Windows `src/Protocol`；向量、协议和本地 Relay 集成通过，Relay 保持密文不透明，公网 TLS 部署未验收。 |
| 3 | Windows ConPTY 到 Android 真实输入输出、ACK/Gap/重连、租约与本地输入优先 | **已实现并验证** | Windows `ProductionAgentRuntime`、Android 生产 Session Client、服务端租约/撤销；本机 ConPTY 与跨端契约自动化覆盖有界重放、ACK/Gap、输入去重、租约和本地输入 epoch，未宣称公网生产端到端。 |
| 4 | 每 Android 设备单独加密的有界历史、S3/MinIO 预签名上传/下载/校验/删除 | **已实现并验证** | Android `core-crypto`/`feature-history`、Windows History Uploader、Go history store；SHA-256、密文元数据、预签名和删除集成测试通过。 |
| 5 | 服务端 Catalog、计费验证边界、权益与配额 | **核心已实现并验证（Provider 待配置）** | Go `billing`/Catalog/entitlement API、可注入 verified-purchase 契约与 Android `feature-billing` 已测试；真实 Google/Web verifier 未配置时生产入口返回 `BILLING_UNAVAILABLE`。 |
| 6 | Android 产品页面、Windows Tray、Compose PostgreSQL/Valkey/MinIO/OIDC 接线及自动化验收 | **已实现并验证** | Android product UI、Windows Account/Devices Tray、`core/history/integration.compose.yml` 与生产运行时；Go/Android/.NET/Compose 自动化验收通过。 |
| 7 | 生产 TLS/受信发布证书真实安装更新、备份恢复演练、镜像签名、监控告警、生产 CA/发布加固 | **已实现并本地验证（外部发布待验收）** | Caddy/ACME、隔离指标与告警、PostgreSQL+MinIO 备份恢复、摘要固定、SBOM/provenance/Cosign 工作流及生产 Compose 策略已验证；开发信任下真实 MSIX 安装/升级/降级/卸载通过。生产 CA、真实发布证书和 GitHub OIDC release tag 仍需外部执行。 |

统一边界：`LAN Replay V1`、`TLT1`（Phase 1 Test Session V1）和 Android Compose
Harness 都是测试夹具，不是生产协议；Android 历史客户端只接受 HTTPS。Integration
Compose 中 MinIO 的 HTTP 端口仅供本机 loopback 集成测试，不能当作生产历史传输。1-7
的本地自动化结果不代表 arm64 真机、厂商 IME、外接键鼠、平板/折叠屏或外部生产发布已经验证。

## 商业模式

- 首发预置 Free：每个账号最多启用 1 台 PC 的远程访问，同时远程显示 3 个实时会话；
- 首发预置 Pro Monthly：按月自动续费、可取消，解锁更多 PC 和实时会话；
- 套餐名称、周期、额度、排序、上下架状态及渠道 Product ID 均由服务端配置，客户端不硬编码套餐；
- Android 使用 Google Play Billing 展示本地化价格，Windows/Web 使用服务端购买渠道；
- 会员到期只暂停超出免费额度的远程能力，不终止本地 Shell，不删除设备或历史；
- E2EE、设备撤销、本地用户优先和紧急停止等安全能力不进入付费墙。

## 文档

- [重新打开会话时的开发交接](DEVELOPMENT_HANDOFF.md)
- [完整产品与系统设计（开发基线）](docs/COMPLETE_SYSTEM_DESIGN.md)
- [Android / Windows Agent / Server Console UI 布局设计稿](docs/UI_LAYOUT_DESIGNS.md)
- [生产级多端交互 UI 原型](ui-prototypes/index.html)
- [产品需求与技术方案](docs/PRODUCT_REQUIREMENTS_AND_SOLUTION.md)
- [阶段 0 技术验证与 Windows Terminal Android 移植评估](docs/TECHNICAL_VALIDATION.md)
- [Android 全功能 Terminal 引擎调研与技术方案](docs/ANDROID_TERMINAL_ENGINE_RESEARCH.md)
- [ConPTY 可运行原型](prototypes/conpty-probe/README.md)
- [.NET 8 Windows Agent、SessionHost 与 Tray](win-agent/README.md)
- [Golden Stream V1 格式](protocol/test-vectors/GOLDEN_STREAM_V1.md)
- [LAN Replay V1 测试协议](protocol/test-vectors/LAN_REPLAY_V1.md)
- [Phase 1 临时双向测试协议](protocol/test-vectors/PHASE1_TEST_SESSION_V1.md)
- [生产 Protobuf 实时协议](protocol/protobuf/PRODUCTION_REALTIME_V1.md)
- [E2EE Session V1 测试向量](protocol/test-vectors/E2EE_SESSION_V1.md)
- [有界历史 V1 测试向量](protocol/test-vectors/BOUNDED_HISTORY_V1.md)
- [Android 回放 Harness](android/README.md)
- [离线 xterm.js 对照 Harness](terminal-reference/xterm-harness/README.md)
- [LAN Docker 测试服务](server/README.md)
- [生产运行时与 Compose](server/README-runtime.md)

## 阶段 0/1 快速验证

```powershell
# 在临时目录重录并严格校验全部 10 个 ConPTY Bundle
.\prototypes\conpty-probe\Record-Phase0GoldenStreams.ps1 `
  -CaptureRoot "$env:TEMP\termlink-phase0-recording"

# 构建并验证独立 Session Host
.\win-agent\tests\Test-SessionHost.ps1
.\win-agent\tests\Test-LocalAgentLoop.ps1
.\win-agent\tests\Test-LocalAgentGap.ps1
.\win-agent\tests\Test-SessionRegistry.ps1
.\win-agent\Build-Windows.ps1
.\win-agent\tests\Validate-Phase1WindowsApp.ps1
.\win-agent\tests\Validate-WindowsInstaller.ps1
.\win-agent\tests\Validate-WindowsUpdater.ps1
.\win-agent\tests\Validate-ProductionProtocol.ps1
.\win-agent\tests\Validate-ProductionRuntime.ps1

# 双向模拟器验收依赖运行中的本地测试容器
docker compose -f server\compose\lan-test.compose.yml up --build -d

# 构建和测试 Android Harness
cd android
$env:JAVA_HOME = 'D:\Dev\Env\Jdk\jdk-17.0.17'
$env:ANDROID_HOME = 'D:\Dev\Env\Android\Sdk'
.\gradlew.bat testDebugUnitTest assembleDebug lintDebug
.\Validate-Phase1Session.ps1
.\Validate-Phase1Network.ps1
.\Validate-Phase1RelayEmulator.ps1
.\Validate-Phase0Emulator.ps1 -DeviceSerial emulator-5554

# 验证临时双向测试 Relay；脚本同时运行现有 LAN Replay 回归
cd ..\server
.\Validate-Phase1TestRelay.ps1
D:\Dev\Env\Go\bin\go.exe test ./...
D:\Dev\Env\Go\bin\go.exe vet ./...

# 生产 Compose 配置（需要本机 .env 和 secrets）
docker compose --env-file .env -f compose\core.compose.yml config

# 真实 PostgreSQL/Valkey/MinIO 集成（一次性基础设施）
docker compose -f compose\integration.compose.yml up -d --wait postgres valkey minio
$env:TERMLINK_INTEGRATION_DATABASE_URL='postgres://termlink:integration-only-password@127.0.0.1:55432/termlink?sslmode=disable'
$env:TERMLINK_INTEGRATION_REDIS_URL='redis://127.0.0.1:56379/0'
$env:TERMLINK_INTEGRATION_S3_ENDPOINT='http://127.0.0.1:59000'
$env:TERMLINK_INTEGRATION_S3_PUBLIC_ENDPOINT='http://127.0.0.1:59000'
go test ./internal/api -run TestProductionAccountRelayAndRevocation -count=1
docker compose -f compose\integration.compose.yml down -v

# 生成 Termux 草案网格后，验证离线 xterm.js Oracle
cd ..\terminal-reference\xterm-harness
npm ci
npm test
```

## 手机连接局域网 Docker

```powershell
# 在仓库根目录启动只提供固定测试流的容器
docker compose -f server\compose\lan-test.compose.yml up --build -d

# 把 <PC-LAN-IP> 替换为 ipconfig 显示的当前 Wi-Fi/以太网 IPv4
Invoke-RestMethod http://<PC-LAN-IP>:8080/healthz
```

把 `android/app/build/outputs/apk/debug/app-debug.apk` 安装到手机，在 Harness 中切到
“LAN 固定回放”，输入 `ws://<PC-LAN-IP>:8080` 和默认测试令牌 `termlink-lan-test`，
再点击“连接 Docker”。预期显示
`Verified · LAN Docker WebSocket · 14 events · 702 bytes · 100×30 · exit 17`。

在 API 26+ 测试设备上也可以切到“双向会话”，连接同一地址与令牌，选择断线恢复、
History Gap、慢消费者或 exited 场景。该模式把 Android `RemoteTerminalSession`、真实
Termux View 与 `/v1/test/session/` 连成测试闭环，并显式显示
`TEST RELAY · NO E2EE`；它不会连接真实 Windows Shell。

手机与 PC 必须在同一可互访局域网。该服务使用明文 `ws://`；LAN Replay 只读，双向测试
入口会接收 Harness 主动发送的测试输入，两者都禁止传输敏感内容或暴露到公网。完整排查与
停止步骤见 [Android 回放 Harness](android/README.md)。

## 一句话目标

让用户可以从 Android 安全地查看和控制自己多台 Windows PC 上正在运行的终端会话，同时保留可恢复、可审计、端到端加密的会话记录。
