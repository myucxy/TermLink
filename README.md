# TermLink

TermLink 是一个面向个人和小型团队的多 PC 远程终端系统。Windows 端 Agent 负责创建、采集和控制受管终端会话；Android 客户端使用同一账号发现不同 PC，并实时查看终端输出、恢复历史记录及在授权后输入命令；云端 Relay 负责认证、设备发现和加密数据转发。

阶段 0 当前环境可自动化验收已完成。现有独立 ConPTY Session Host、Golden Stream V1、
`TerminalEngine` API、Android Compose Harness、固定 Termux emulator/view Fork、完全离线
xterm.js 6.0.0 Oracle，以及只读 Docker LAN Replay 测试闭环。四种 Shell、ANSI/Unicode、
Terminal Features、Vim、less、fzf 和 htop 共 10 个固定流，在 Termux/xterm.js 间达到
122/122 checkpoint match，并在 API 36 模拟器 10/10 冷启动回放通过。arm64 真机、厂商
IME、外接键鼠与 Android WebView 回退引擎仍待后续验证或实现。

阶段 1 已完成可重复的测试闭环：正式 .NET 8 Agent/SessionHost/Tray/Updater 工程已经建立；
Session Host 与 Agent 之间具备当前用户 Named Pipe、双向 PID/Token 认证、序号、ACK、
重连重发、有界 Gap、输入去重和 DPAPI Buffer。DPAPI 注册索引已在 Agent 强制结束和受控
重启后两次恢复 20/20 个真实 ConPTY SessionHost，远程停止不影响 20 个本地 Shell。Android 的
`RemoteTerminalSession` 已通过独立 `core-network` 接到 Docker 临时双向测试 Relay，并驱动
真实 Termux View。API 36 模拟器已验证累计 ACK、断线续传、输入 accepted、测试租约、
History Gap 和 exited。该链路使用隔离的明文测试协议和固定非敏感夹具，不是生产 Relay、
Protobuf、设备授权或 E2EE，也没有冻结可移植 Snapshot，因此当前版本仍不是可发布产品。
Windows 端现已增加签名 MSIX、登录自启、Tray 单实例和受控更新器；开发证书下已验证独立
签名更新清单、包身份/版本/SHA-256、篡改拒绝、原子缓存、可恢复事务日志及健康检查失败
回滚。生产受信证书下的真实安装/卸载和 `Add-AppxPackage` 更新/降级仍待后续验收。

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
- [Android 回放 Harness](android/README.md)
- [离线 xterm.js 对照 Harness](terminal-reference/xterm-harness/README.md)
- [LAN Docker 测试服务](server/README.md)

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
