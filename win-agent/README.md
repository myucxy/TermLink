# Windows Agent

正式 Windows Agent 将使用 C# / .NET 8。阶段 0 已在
[`prototypes/conpty-probe`](../prototypes/conpty-probe) 验证和固定原始 ConPTY 录制格式，
并把已验证的生命周期代码迁入独立 `SessionHost` 进程。

目录边界：

- `src/Agent`：会话目录、Relay、加密缓冲与访问控制；
- `src/Protocol`：生产 Protobuf Envelope、设备证明、X25519/HKDF 和 ChaCha20-Poly1305；
- `src/Tray`：当前用户托盘和设置 UI；
- `src/SessionHost`：每会话一个 ConPTY Owner；
- `src/Updater`：签名更新和回滚；
- `tests`：生命周期、IPC、句柄和 Golden Stream 测试。

正式 Windows 工程固定 .NET SDK 8.0.303，包含 Agent、SessionHost、共享本机控制契约、
WinForms Tray 和单文件 Updater：

```powershell
.\Build-Windows.ps1
.\tests\Validate-Phase1WindowsApp.ps1
.\tests\Validate-ProductionProtocol.ps1
.\tests\Validate-ProductionRuntime.ps1
```

当前机器若只有 .NET 8 Runtime 而没有 SDK，可以通过 `-DotNetPath` 指向临时或独立安装的
8.0.303 SDK。`global.json` 会拒绝意外使用不兼容的 SDK feature band。

原有系统 C# 编译器入口继续保留，用于验证同一源码在阶段性 .NET Framework Harness 中的
回归兼容：

```powershell
.\src\SessionHost\Build-SessionHost.ps1
.\src\Agent\Build-Agent.ps1
.\src\SessionHost\New-WindowsTerminalProfileFragment.ps1
.\tests\Test-SessionHost.ps1
.\tests\Test-LocalAgentLoop.ps1
.\tests\Test-LocalAgentGap.ps1
.\tests\Test-SessionRegistry.ps1
```

Profile 生成器输出 CMD、Windows PowerShell、PowerShell 7 和 WSL 四个固定 GUID 的
Windows Terminal fragment，Shell 命令以 UTF-8 Base64 传给 Host。测试把 Host 放入真实
外层 ConPTY，验证双 PTY、Unicode Agent pipe、控制键、resize、EOF、timeout、强杀、
2 MiB 输出、持续运行与 20 轮句柄回收。结果在
`docs/validation/phase0-session-host.json`。

Session Host 不调用 `SetStdHandle` 或 `SetConsoleCP`。控制台输入使用
`ReadConsoleInputW`；Agent 输入使用原始 UTF-8 pipe。Windows 10 19045 已知的 conhost
handle 问题通过每会话独立 Host 进程隔离，不能在 Agent 内嵌多个 ConPTY Owner。

## 阶段 1 本地 IPC

`src/Common/LocalIpc.cs` 定义仅供当前用户本机 Host-Agent 使用的 V1 帧：4 字节大端长度、
版本、类型、16 字节 Session ID、64 位序号和 Payload，单帧上限 1 MiB。它不是公网协议，
也没有冻结生产 Protobuf。

每个会话使用当前用户 SID ACL 的独立 Named Pipe。Host 和 Agent 用 32 字节一次性 Token、
Session ID 及操作系统报告的对端 PID 相互认证。Host 在 Agent 断开时保留 4 MiB 有界队列，
重连后按序重发；溢出时发送覆盖准确序号范围的 `Gap`。输出批次最大 64 KiB，输入写入与
本地 Windows Terminal 输入通过锁串行化。

验证 Agent 使用 Windows DPAPI CurrentUser 保护每个事件 chunk，并用单独的 DPAPI 文件
保存最近 4096 个 `input_id`。输入和握手帧禁止进入事件 Buffer；终端自身回显的输入仍属于
加密输出，不能把“无输入帧”误写成“输出中永远没有输入字符”。

两条集成测试分别覆盖：

- 真实双 ConPTY、主动断管、重连重发、最终累计 ACK、同一 `input_id` 只接受一次、
  111×37 resize、exit 17 和 DPAPI Buffer 检查；
- 诊断用 64 KiB 队列触发与生产 4 MiB 队列相同的溢出算法，验证 `Gap` 持久化、缺口后的
  连续序号和最终 ACK。

证据位于 `docs/validation/phase1-local-agent.json` 和
`docs/validation/phase1-local-agent-gap.json`。

## 阶段 1 Session Registry

`src/Agent/SessionRegistry.cs` 在一个长驻 Agent 进程内管理每会话独立的 `SessionAgent`
worker。Registry 的并发模型是：一个锁保护活动会话目录和远程访问总开关；每个 worker
独占自己的 Host Pipe、DPAPI Buffer、序号与输入去重状态；停止时先从目录原子移除，再在
锁外关闭对应 Agent Pipe 并等待 worker 回收。

Registry 提供当前用户 SID ACL 的独立本机控制 Pipe，支持注册、脱敏摘要、停止单个会话、
停止全部远程访问、显式重新启用和关闭 Registry。可执行文件的长驻模式为：

```powershell
.\src\Agent\bin\Release\net8.0-windows\TermLink.Agent.exe --registry `
  --control-pipe termlink-agent-control `
  --buffer-root "$env:LOCALAPPDATA\TermLink\chunks"
```

控制 Pipe 只承载 Agent/Tray 本机命令，不是 LAN Replay、生产 Relay 或公网协议。注册仍为
每个 Host 分配独立 Session ID、32 字节 Token 和 Pipe，并在原有双向身份校验之外核对
Registry 登记的 Host PID。摘要只包含 Session ID、白名单 Shell 类型、生命周期、Host PID、
最后序号和时间；不包含终端正文、标题、工作目录、Token、Pipe 名或 Buffer 路径。

“停止远程访问”只关闭 Agent 侧 Pipe，不向本地 Shell 发送终止命令。单会话停止会把
Session ID、Host PID 和进程启动时间写入 CurrentUser DPAPI 保护的
`remote-access-policy.dpapi` 墓碑；同一 Host 不能在 worker 回收、历史发布或 Agent 重启后
静默重新注册。Host 收到 `SESSION_STOPPED` 后停止注册重试，但继续桥接本地 Windows
Terminal 与内层 ConPTY。Host 退出后墓碑可安全清理，新开的 Profile 使用新的 Session。

“停止全部远程访问”把全局关闭状态持久化到同一策略文件。Agent/系统重启不会自动开放；
只有本机控制面的显式“恢复远程访问”成功持久化后才重新接受注册。策略文件截断、篡改或
无法用当前 Windows 用户解密时按 fail-closed 处理。全局恢复不会删除仍存活 Host 的单会话
墓碑。

停止结果包含请求数、完成数和超时数。单会话超时返回稳定码 `STOP_TIMEOUT`；全局停止
返回 `REMOTE_ACCESS_STOP_TIMEOUT`；关闭返回 `REGISTRY_SHUTDOWN_WITH_TIMEOUTS`；受控
重启返回 `AGENT_RESTART_WITH_TIMEOUTS`。超时 worker 会保留在不对摘要公开的 retiring
集合，禁止相同 Session ID 重注册，并在后续关闭中再次 best-effort 等待。批量停止会对
全部 worker 先发停止，再逐一收集结果，不因首个超时跳过其余 worker；除关闭/重启命令外，
控制服务返回错误码后继续运行。

若停止已经在当前进程生效、但策略文件无法持久写入，控制面返回
`STOPPED_NOT_DURABLE` 或 `REMOTE_ACCESS_STOPPED_NOT_DURABLE`，不会把非持久结果误报为
成功；恢复写入失败返回 `REMOTE_ACCESS_ENABLE_NOT_DURABLE` 并继续保持关闭。

`Test-SessionRegistry.ps1` 使用真实 Session Host 验证 3 个同时会话、摘要脱敏、单会话和
并发重复停止、异常退出 Host 回收；另用独立 PID 完成认证后主动失联并保持存活，验证
断管超时回收。测试也覆盖全局停止、新注册拒绝、Registry 关闭及 6 个本地 Shell 在远程
停止后的存活与输入。内部 worker 工厂还可重复验证单会话、批量和关闭超时，不增加生产
测试开关；测试会实际启动构建后的 `TermLink.Agent.exe --registry` 并通过控制 Pipe 列表和
优雅关闭。证据位于
`docs/validation/phase1-session-registry.json`。

## .NET 8 Agent、Tray 与重启恢复

`TermLink.Windows.sln` 现在构建六个正式工程和一个本机控制契约验证程序：

- `TermLink.AgentControl`：Agent 与 Tray 共用的本机 Pipe V4 结构化契约；
- `TermLink.Protocol`：生产 Protobuf 与 E2EE/历史密码封装；
- `TermLink.Agent`：长驻当前用户 Registry 和每会话 worker；
- `TermLink.SessionHost`：独立 ConPTY Owner；
- `TermLink.Tray`：系统托盘、脱敏会话/账号/设备状态与本机控制；
- `TermLink.Updater`：签名清单校验、部署事务和失败回滚；
- `TermLink.AgentControl.Validation`：Pipe V4 请求/响应往返验证，不进入产品包。

Tray 退出不会关闭 Agent 或本地 Shell。默认控制 Pipe 名包含当前用户 SID 的 SHA-256
截断值；Tray 只消费 `SessionSummary` 和脱敏 `ProductionStatus`，不会获得 Pipe 名、Token、
密钥、Buffer 路径或终端正文。
`--status-json` 仅供诊断输出同一份脱敏摘要；`--restart-agent` 为受控更新保留注册索引，
`--shutdown-agent` 按停止远程访问语义清除索引。

Registry 在 Buffer Root 保存 `registrations.dpapi`。文件载荷使用 DPAPI CurrentUser，写入时
原子替换；只包含恢复路由所需的 Session ID、Shell 类型、Host PID 与启动时间、注册时间、
Pipe 名和握手 Token。启动时必须同时匹配 PID 与进程启动时间才会恢复，以防 PID 重用；
受控重启保留活动会话路由；正常 Session 结束、停止单会话、停止全部远程访问或受控关闭
会把条目标记为仅恢复终端 Buffer。Agent 重启时此类条目只恢复已结束的 DPAPI 事件流，绝不
重新连接仍在运行的本地 Host。只有该 Session 面向全部当前有效 Grant 的 E2EE 历史均已持久
提交后，Agent 才删除恢复索引和事件 chunk；History 权益或服务不可用时继续保留本地有界
Buffer。该索引不包含终端正文、输入、标题或工作目录，也不是跨设备 Snapshot。

`Validate-Phase1WindowsApp.ps1` 使用正式 .NET 8 构建产物启动 20 个真实 ConPTY
SessionHost，分别在强制结束和受控重启 Agent 后从加密索引恢复全部 20 个连接，再由 Tray
核对 20 条脱敏摘要。最终停止远程访问后，20 个本地 Shell 均继续接收本地输入并正常退出。证据位于
`docs/validation/phase1-windows-app.json`。

生产 Agent 由 `src/Agent/ProductionAgentRuntime.cs` 接入 HTTPS API、生产 Protobuf
realtime Relay、设备配对/撤销、租约和每 Android 设备历史上传；`src/Protocol` 的
E2EE 实现与 `protocol/protobuf/termlink_realtime.proto` 保持同一 Envelope。生产 runtime
验证脚本覆盖无配置拒绝、DPAPI 有界重放、Gap、发送背压、刷新失败隔离、生命周期释放和
配置密钥边界；它不把本机 V3 控制 Pipe 或 Phase 1 测试帧冒充公网协议。

## 开发项 1-7 完成矩阵

| 开发项 | 范围 | 状态 | 验证与边界 |
| --- | --- | --- | --- |
| 1 | 生产账号/OIDC/设备注册与配对 | **已实现并验证（预配置接入）** | Go `auth`、设备注册/配对 API、Android AppAuth/Keystore、Windows challenge/撤销；Windows runtime 当前读取本机预配置的 DPAPI 凭据，不包含浏览器 OIDC enrollment UI。 |
| 2 | 生产 Protobuf 实时协议、X25519 + ChaCha20-Poly1305、Relay | **已实现并验证** | `protocol/protobuf/termlink_realtime.proto`、Go `realtimewire`/Relay、Android `core-network`、Windows `src/Protocol`；向量、协议和 Relay 测试通过。 |
| 3 | Windows ConPTY 到 Android 真实输入输出、ACK/Gap/重连、租约与本地输入优先 | **已实现并验证（模块/契约级）** | Windows `ProductionAgentRuntime`、Android 生产 Session Client、服务端租约/撤销；真实 ConPTY、跨端向量和各端自动化覆盖有界重放、ACK/Gap、输入去重、租约和本地输入 epoch，尚未做公网生产部署验收。 |
| 4 | 每 Android 设备单独加密的有界历史、S3/MinIO 预签名上传/下载/校验/删除 | **已实现并验证** | Android `core-crypto`/`feature-history`、Windows History Uploader、Go history store；SHA-256、密文元数据、预签名和删除集成测试通过。 |
| 5 | 服务端 Catalog、计费验证边界、权益与配额 | **核心已实现并验证（Provider 待配置）** | Go Catalog/entitlement、可注入 verified-purchase 契约和 Android Play Billing 协调已测试；生产 main 在真实 Google/Web verifier 未配置时返回 `BILLING_UNAVAILABLE`，未冒充线上购买验证。 |
| 6 | Android 产品页面、Windows Tray、Compose PostgreSQL/Valkey/MinIO/OIDC 接线及自动化验收 | **已实现并验证** | Android product UI、Windows Account/Devices Tray、`core/history/integration.compose.yml` 与生产运行时；Go/Android/.NET/Compose 自动化验收通过。 |
| 7 | 生产 TLS/受信发布证书真实安装更新、备份恢复演练、镜像签名、监控告警、生产 CA/发布加固 | **已实现并本地验证（生产证书待验收）** | 开发信任下真实 MSIX 安装、包身份启动、Updater 升级、强制降级和卸载通过且无残留；生产发布者证书链和时间戳仍需外部验收。服务端 TLS、备份恢复、监控和镜像供应链也已完成本地验证。 |

统一边界：`LAN Replay V1`、`TLT1`（Phase 1 Test Session V1）和 Android Compose
Harness 都是测试夹具，不是生产协议；Android 历史客户端只接受 HTTPS。Integration
Compose 中 MinIO 的 HTTP 端口仅供本机 loopback 集成测试，不能当作生产历史传输。1-7
的自动化结果不代表 arm64 真机、厂商 IME、外接键鼠、平板/折叠屏、真实计费 Provider
或生产发布已经验证。

## 签名 MSIX 与登录自启

`package/Build-Msix.ps1` 使用固定 .NET 8 SDK 发布 `win-x64` 自包含 payload，分别签名 8 个
TermLink 自有 EXE/DLL，再由 Windows SDK MakeAppx 生成并签名完整 MSIX。Updater 以可复制
到包外执行的自包含单文件发布，包内同时固定同一签名者的 `TermLink.UpdatePublisher.cer`。
非开发构建必须
提供 HTTPS RFC 3161 时间戳；Manifest Publisher 必须与 PFX 证书 Subject 完全一致。

```powershell
$password = Read-Host 'PFX password' -AsSecureString
.package\Build-Msix.ps1 `
  -Version 0.1.0.0 `
  -Publisher 'CN=Your Trusted Publisher' `
  -PublisherDisplayName 'Your Publisher' `
  -CertificatePath C:\secure\termlink-signing.pfx `
  -CertificatePassword $password `
  -TimestampUrl https://your-rfc3161-timestamp.example
```

MSIX 使用 `windows.startupTask` 在当前用户登录后启动 `TermLink.Tray.exe`；Tray 再确保同一
用户的 Agent 已运行。Tray 增加了基于当前用户控制 Pipe 名的本地 Mutex，从登录自启和开始
菜单重复启动时仍只保留一个托盘实例。包使用稳定生产身份 `TermLink.Terminal`，验证脚本
使用隔离身份 `TermLink.Terminal.Validation`。

```powershell
.\tests\Validate-WindowsInstaller.ps1
```

`Validate-WindowsInstaller.ps1` 继续使用不落库的临时开发证书验证 8 个嵌入签名、完整 MSIX、
自包含 payload、固定更新发布者证书、Manifest/StartupTask、Tray 单实例、Agent 状态与关闭，
且不注册测试包。第 7 项另由 `Validate-TrustedMsixLifecycle.ps1` 在显式确认和提升会话下，临时
把已有开发发布证书加入 `LocalMachine/TrustedPeople`，使用隔离包身份完成真实注册、包身份
启动、升级、降级和卸载；最后按指纹与证书原始字节哈希精确移除本轮证书，包和进程也无残留。
证据分别位于 `docs/validation/phase1-windows-installer.json` 和
`docs/validation/item7-windows-msix-lifecycle.json`。该结果只证明开发信任机制，不证明生产证书链。

## 受控更新与失败回滚

`package/New-UpdateManifest.ps1` 为已有 MSIX 生成单独的 UTF-8 JSON 清单，并使用包签名者的
RSA 私钥对清单原始字节签名。清单绑定 Package Identity、Publisher、四段版本、最低当前
版本、文件名、SHA-256 和最长 31 天有效期。Updater 还会独立检查 MSIX Authenticode，且
目标包、回滚包和固定发布者证书必须完全一致。

```powershell
$password = Read-Host 'PFX password' -AsSecureString
.\package\New-UpdateManifest.ps1 `
  -PackagePath .\artifacts\TermLink-0.2.0.0-x64.msix `
  -CertificatePath C:\secure\termlink-signing.pfx `
  -CertificatePassword $password `
  -MinimumCurrentVersion 0.1.0.0
```

`TermLink.Updater.exe launch` 在已安装包仍运行时完成两套更新输入校验，把目标包、当前版本
回滚包、公钥和单文件 runner 原子复制到 `%LOCALAPPDATA%\TermLink\updates`，再由包外 runner
调用 Windows `Add-AppxPackage`。事务日志可在进程中断后重入；目标部署失败时旧版本保持
不变，目标健康检查失败时使用 `ForceUpdateFromAnyVersion` 回装已验证的当前版本。健康检查
只验证 Agent 能返回成功状态，不会把用户主动关闭远程访问误判为故障。

```powershell
.\tests\Validate-WindowsUpdater.ps1
```

该测试用同一临时开发证书构建 0.1.0.0 和 0.2.0.0 两个真实 MSIX，覆盖清单/包篡改拒绝、
成功、部署失败、健康失败回滚、回滚失败和回滚重入。故障注入证据位于
`docs/validation/phase1-windows-updater.json`。第 7 项生命周期测试还验证了真实 PowerShell
MSIX 部署后端：目标事务进入 `Succeeded`，随后以 `ForceUpdateFromAnyVersion` 回到旧版并
再次通过 Agent 健康检查。生产模式仍要求 Authenticode `Valid` 证书链和 HTTPS RFC 3161 时间戳。

生产 Relay、账号/设备授权、设备密钥、E2EE、租约和历史上传已由生产 runtime 与跨端
自动化验收覆盖；阶段性本机控制 Pipe V4 和 DPAPI 注册索引仍不等于生产 Protobuf 或
跨设备协议。开发信任下的真实安装/卸载、包身份启动和 `Add-AppxPackage` 更新/降级已验收；
实际注销/登录周期、生产受信发布证书和时间戳服务仍需外部验收。不得把开发证书结果写成
生产发布验证。
