# TermLink

TermLink 是一个面向个人和小型团队的多 PC 远程终端系统。目标结构由 `termForward/terminal-main` 的异步通道把受管会话输入输出交给 Agent；Windows Agent 与 Android 登录同一 OIDC 账号后，Android 直接发现并使用该账号下的 PC 和受管会话，不要求用户先做设备配对、自授信或逐 PC Grant。远程输入仍必须取得短时会话租约，云端 Relay 负责认证、账号隔离、设备发现和加密数据转发。当前发布的 `ProductionAgentRuntime` 仍使用旧 ConPTY/SessionHost 链路；新通道和桥接仅完成本地隔离实现，待注册、IPC、持久化序号和运行时切换完成后再切生产。

阶段 0 当前环境可自动化验收已完成。旧独立 ConPTY Session Host、Golden Stream V1、
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
TLT1 和 Harness 不是生产 Relay、生产 Protobuf、账号隔离或 E2EE。生产账号、同账号设备直接发现、
设备撤销、兼容配对/Grant API、Protobuf/E2EE Relay、历史、计费、产品页面和 Compose 接线已经实现并通过自动化验证；
第 7 项的生产 TLS 配置、备份恢复、镜像供应链、监控告警和发布策略已经实现并完成本地验收。
Windows 端当前以签名的单文件 EXE 安装器为首发发布格式，包含 Tray、Agent、SessionHost、
Updater 和自包含 .NET 运行库；开发证书下已验证安装、Terminal Profile 生成、原地升级和
卸载。MSIX 仅保留为历史兼容验证路径，不作为本版本对外发布格式。生产发布仍需真实代码
签名证书链、RFC 3161 时间戳、AppShip 发布凭据及外部告警接收器验收。

本轮已在 `termForward/terminal-main` 的 `TerminalConnection` 项目中加入
`AsyncTerminalChannel`：它只订阅 `ITerminalConnection` 的 UTF-16 输出/状态事件，
并在独立有界 worker 上执行输入、resize 和关闭；Windows Agent 同时增加了
`TerminalMainChannelBridge` 合同适配器，在本地提交后把 UTF-16 块连续编码为现有 UTF-8 本机帧并累计 ACK。
两部分已有隔离测试，但桥接的 terminal-main 进程注册、认证 IPC、可重放源、幂等持久化 sink
和 ProductionAgentRuntime/Android/Relay 切换尚未实现；当前生产载荷仍沿用旧 UTF-8 格式，
不把新通道宣称为公网端到端已切换。

## 开发项 1-7 完成矩阵

| 开发项 | 范围 | 状态 | 验证与边界 |
| --- | --- | --- | --- |
| 1 | 生产账号/OIDC、同账号设备直连与撤销 | **已实现并验证** | Windows/Android 设备按同一 OIDC subject 归属账号并自动注册、直接发现 PC/Session；账号隔离、逐设备密钥和撤销测试已覆盖。旧 pairing/grant/trust 表与 API 仅作兼容层保留，不是首版产品前置流程；真实外部 OIDC enrollment/provider 部署不在本次验收范围。 |
| 2 | 生产 Protobuf 实时协议、X25519 + ChaCha20-Poly1305、Relay | **已实现并验证** | `protocol/protobuf/termlink_realtime.proto`、Go `realtimewire`/Relay、Android `core-network`、Windows `src/Protocol`；向量、协议和本地 Relay 集成通过，Relay 保持密文不透明，公网 TLS 部署未验收。 |
| 3 | terminal-main 异步通道到 Android 的实时输入输出、ACK/Gap/重连、租约与本地输入优先 | **本地通道与 Agent 适配已实现，生产切换待完成** | `termForward/terminal-main/src/cascadia/TerminalConnection/AsyncTerminalChannel.*`、`win-agent/src/Agent/TerminalMainChannelBridge.cs` 及隔离测试已加入；现有 ProductionAgentRuntime/Android/Relay 保持兼容旧 UTF-8 载荷，公网生产端到端尚未切换。 |
| 4 | 每 Android 设备单独加密的有界历史、S3/MinIO 预签名上传/下载/校验/删除 | **已实现并验证** | Android `core-crypto`/`feature-history`、Windows History Uploader、Go history store；SHA-256、密文元数据、预签名和删除集成测试通过。 |
| 5 | 服务端 Catalog、计费验证边界、权益与功能状态 | **核心已实现并验证（Provider 待配置）** | Go `billing`/Catalog/entitlement API、账号级不限 PC/Session、可注入 verified-purchase 契约与 Android `feature-billing` 已测试；真实 Google/Web verifier 未配置时生产入口返回 `BILLING_UNAVAILABLE`。 |
| 6 | Android 产品页面、Windows Tray、Compose PostgreSQL/Valkey/MinIO/OIDC 接线及自动化验收 | **已实现并验证** | Android product UI、Windows Account/Devices Tray、`core/history/integration.compose.yml` 与生产运行时；Go/Android/.NET/Compose 自动化验收通过。 |
| 7 | 生产 TLS/受信发布证书真实安装更新、备份恢复演练、镜像签名、监控告警、生产 CA/发布加固 | **已实现并本地验证（外部发布待验收）** | Caddy/ACME、隔离指标与告警、PostgreSQL+MinIO 备份恢复、摘要固定、SBOM/provenance/Cosign 工作流及生产 Compose 策略已验证；开发信任下 EXE 安装/升级/卸载通过，MSIX 仅作兼容验证。生产 CA、真实发布证书和 AppShip release tag 仍需外部执行。 |

统一边界：`LAN Replay V1`、`TLT1`（Phase 1 Test Session V1）和 Android Compose
Harness 都是测试夹具，不是生产协议；Android 历史客户端只接受 HTTPS。Integration
Compose 中 MinIO 的 HTTP 端口仅供本机 loopback 集成测试，不能当作生产历史传输。1-7
的本地自动化结果不代表 arm64 真机、厂商 IME、外接键鼠、平板/折叠屏或外部生产发布已经验证。

## 商业模式

- 同一账号可注册不限数量的 Windows PC，并同时订阅不限数量的实时会话；
- Free/Pro 计划和购买验证接口仍保留，用于兼容既有客户端和后续功能开关，但 PC/Session 数量不再由套餐额度限制；
- 套餐名称、周期、功能标记、排序、上下架状态及渠道 Product ID 均由服务端配置，客户端不硬编码套餐；
- Android 使用 Google Play Billing 展示本地化价格，Windows/Web 使用服务端购买渠道；
- 会员状态变化不会断开本地 Shell、删除设备或强制下线已有会话；
- E2EE、账号隔离、逐设备密钥、设备撤销、本地用户优先和紧急停止等安全能力不进入付费墙。

## 文档

- [终端兼容核心、标准传输与 P0–P6 实现](docs/TERMINAL_COMPATIBILITY_CORE_V1.md)
- [重新打开会话时的开发交接](DEVELOPMENT_HANDOFF.md)
- [完整产品与系统设计（开发基线）](docs/COMPLETE_SYSTEM_DESIGN.md)
- [Android ↔ Server ↔ Windows 文件操作 E2E](docs/REMOTE_FILE_OPERATIONS_E2E.md)
- [终端信息获取与服务端转发需求基线](docs/TERMINAL_INFORMATION_FORWARDING_REQUIREMENTS.md)
- `termForward/terminal-main/doc/async-terminal-channel.md`（异步通道契约与实现说明）
- [Android / Windows Agent / Server Console UI 布局设计稿](docs/UI_LAYOUT_DESIGNS.md)
- [生产级多端交互 UI 原型](ui-prototypes/index.html)
- [产品需求与技术方案](docs/PRODUCT_REQUIREMENTS_AND_SOLUTION.md)
- [阶段 0 技术验证与 Windows Terminal Android 移植评估](docs/TECHNICAL_VALIDATION.md)
- [内存 Relay 与生产 Protobuf E2E 技术验证模块](technical-validation/e2e/README.md)
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

## 服务端访问与首个管理员

生产验收部署的公开入口是 <https://termlink.kget.cc:8910/>。根路径始终先显示
TermLink 产品首页；点击“登录”后才进入 Keycloak，所有账号默认先进入 `/account` 个人
中心，拥有 `SUPER_ADMIN`、`ADMIN` 或 `AUDITOR` 数据库角色的账号再从个人中心进入受保护
的 `/admin`。首页、登录页、个人中心、身份与安全页和管理台共用同一个深浅主题偏好，不会
在页面切换时黑白跳变；身份与安全页始终提供“返回 TermLink 个人中心”。

首个版本已开放自助注册：从服务端登录页点击“创建账号”即可注册，当前不要求邮箱验证。
Android 通过固定公网地址的 HTTPS OIDC 端点完成原生登录，只有身份提供方要求交互时才进入受控浏览器流程；
Windows Agent 使用其 OIDC 登录流程。两端都不提供可编辑的服务器地址。账号安全管理仍可从服务端
个人中心进入。首版客户端不展示首次授信、配对或逐 PC 授权入口；同账号设备自动注册并发现
自己的 PC/Session，控制输入仍受短时租约和本地键盘抢占保护。不同 OIDC subject 的设备不能跨账号发现或路由。
部署启动时公开入口会保持关闭，
直到受管 `admin` 创建或校验成功，随后初始化任务才开放注册并启动公开上游，防止匿名注册抢占
初始管理员用户名。

Android 会加密保存去除一次性授权请求信息后的 OIDC 会话，以便进程重启后恢复登录；Windows 端也保存受保护的生产登录凭据并轮换 Refresh Token。普通“退出登录”清理当前 Token、实时连接和本地会话缓存，但不等于撤销/解绑服务端设备身份，也不删除其他设备。设备解绑必须走显式撤销；首版各端独立“解绑此设备”入口及跨端退出/撤销验收仍需确认，不能把退出按钮描述成已完成解绑。

首个版本的全新受管部署会创建固定用户名 `admin`，授予唯一初始
`SUPER_ADMIN`，并设置随机 24 位临时密码。密码不写入仓库、镜像、README 或普通启动
日志。完成部署后，只在可信本机终端执行下面的领取脚本；它使用互斥锁，先写入已领取
标记并轮换宿主机引导 Secret，再只输出一次原临时密码：

```powershell
.\server\ops\Start-ProductionAcceptance.ps1 -Reset
.\server\ops\Get-InitialAdminPassword.ps1
```

不要把领取命令的输出重定向到文件、CI 日志、聊天或剪贴板历史。使用该临时密码首次
登录时，Keycloak 会强制修改密码；改密后请在身份中心配置 MFA。领取标记一旦写入即按
已领取处理；如果领取过程被中断，脚本会拒绝再次输出该值，必须走下面的安全重置流程。
普通重启不会重新创建管理员，也不会再次显示原临时密码。

如果临时密码已领取但遗失、宿主机引导文件丢失，或无法确认领取状态，不要重新运行普通
启动来“找回”密码：持久化 Keycloak 中已经存在 `admin` 时，新生成的宿主机文件不一定是
已安装的凭据。使用受管的重置脚本在 Keycloak 内为 `admin` 签发一个新的临时密码，再
单次领取；脚本通过容器内文件型 Secret 调用身份管理工具，不会在命令行或日志中放入
密码值：

```powershell
.\server\ops\Reset-InitialAdminPassword.ps1
.\server\ops\Get-InitialAdminPassword.ps1
```

`admin` 进入管理台后可查看真实账号、设备、连接、会话、Schema、订阅计数和安全审计。
角色与账号停用操作只允许最近登录的 `SUPER_ADMIN` 执行，并受同源 CSRF、数据库二次
授权和“最后一个活动超级管理员不可降级/停用”的事务约束。控制台永不显示终端正文、
Token 或 Secret；尚未实现的兑换码能力明确标为未启用，不展示原型假数据。

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

## 私有局域网产品 E2E / Private-LAN product E2E

在当前仓库根目录运行下面的命令，可把一次性生产验收栈暴露到指定的本机局域网 IPv4；
run this command from this repository root to expose the disposable production
acceptance stack on the selected host LAN IPv4:

```powershell
.\server\ops\Start-ProductionAcceptance.ps1 -LanAddress 192.168.9.28
```

对应的 API/OIDC 地址为 `https://termlink.192.168.9.28.sslip.io:9443`；一般形式是
`https://termlink.<LAN-IP>.sslip.io:9443`。脚本把一次性 CA 导出到
`server\secrets\acceptance\acceptance-root-ca.crt`。Android Debug 构建通过
`-PtermlinkServerUrl=<LAN-origin>` 选择该地址，Windows Agent/Tray 通过
`--server-url <LAN-origin>` 选择该地址；Release 和未覆盖的客户端仍使用公开地址
`https://termlink.kget.cc:8910`。

Android 测试设备必须显式信任该 CA，Windows 测试进程必须设置受限的
`TERMLINK_ACCEPTANCE_CA_FILE`。本流程只转发 TermLink 创建并管理的 Session，不能接管
任意已打开的 cmd、PowerShell 或 Windows Terminal 标签页。全部命令和产物都来自当前仓库，
不依赖同级或其他仓库。完整中英文步骤与限制见
[生产协议验收栈](server/acceptance/README.md)。

2026-08-21 已在上述 `192.168.9.28` LAN Origin 上完成一次真实 Android 16 模拟器到
Windows PowerShell 7 受管 Session 的产品链路验收；a live Android 16 emulator-to-Windows
PowerShell 7 managed-session run has completed against that LAN origin. Windows Agent 和 Android
均登录 `acceptance-probe`，数据库中的 Device Grant 与 Pairing Challenge 记录数均为 `0`。
Android 输入创建了 Windows marker `D:\Dev\Temp\TLANDROID1787315819158`；早期复验在手工执行
`Devices -> Sessions` 后重新取得租约，第二次输入创建了
`D:\Dev\Temp\TLRECONNECT1787316577617`。同持有者租约恢复实现后的后续复验已取代当时的
“必须切换 Tab”结论：Android PID 从 `8234` 经强停变为空，再冷启动为 `8360`，无需切换 Tab，
页面语义即为 `SessionsSelected=True`、`SessionTitle=True`、`EnterPresent=True`，上述两个历史
marker 也自动恢复；同一过程中租约 TTL 依次观测为 `27882 ms`、`25145 ms`、`20007 ms`。因此强停/冷启
后的 Session 页面、历史和同持有者租约恢复已经验证，证据截图为
`D:\Dev\Temp\termlink-fixed-cold-start-20260821-221034.png`。

Android 16 IME 修复和标准命令输入框已经实现，但本轮最新命令 marker
`tlcold20260821223551` 尚未确认在 Windows 执行成功；冷启后新命令执行仍是待验证项，不能标为
已通过。

Both clients used `acceptance-probe`; the database contained zero Device Grant
and zero Pairing Challenge rows. Earlier inputs created
`D:\Dev\Temp\TLANDROID1787315819158` and, after a manual reacquisition in the
earlier run, `D:\Dev\Temp\TLRECONNECT1787316577617`. A later run with same-holder
lease resume superseded the earlier tab-switch limitation: Android PID `8234`
was force-stopped, disappeared, and cold-started as PID `8360`. Without a tab
transition, the restored UI reported `SessionsSelected=True`,
`SessionTitle=True`, and `EnterPresent=True`, and both historical markers were
restored automatically. Lease TTL observations were `27882 ms`, `25145 ms`, and
`20007 ms`. Cold-start Session/UI, history, and same-holder lease restoration are
therefore verified; the screenshot is
`D:\Dev\Temp\termlink-fixed-cold-start-20260821-221034.png`.

The Android 16 IME fix and standard command input field are implemented, but
the newest command marker `tlcold20260821223551` has not been confirmed as
successfully executed on Windows. Fresh command execution after cold start
remains pending and is not recorded as passed.

本次验收使用 `TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT` 隔离 Windows 配置与 CurrentUser
DPAPI 凭据；该覆盖只有在同时设置 `TERMLINK_ACCEPTANCE_CA_FILE` 时才会启用，不改变默认
`%LOCALAPPDATA%\TermLink`。实机还发现用户系统代理 `127.0.0.1:7897` 会被 WebSocket 继承并
导致 WSS TLS EOF；显式 LAN acceptance CA 路径现在为 WebSocket 设置 `Proxy = null`，与已有
REST/OIDC 验收代理绕过保持一致。详细证据和仅存在本机、未进仓的截图位置见验收文档。

The Windows acceptance storage override is honored only together with
`TERMLINK_ACCEPTANCE_CA_FILE`, leaving normal `%LOCALAPPDATA%\TermLink` storage
unchanged. A user system proxy at `127.0.0.1:7897` caused the WSS TLS EOF; the
explicit LAN-acceptance CA path now also bypasses that proxy for WebSocket
connections. The acceptance document lists the local-only, untracked screenshots.

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
