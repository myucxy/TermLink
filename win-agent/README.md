# Windows Agent

正式 Windows Agent 将使用 C# / .NET 8。阶段 0 已在
[`prototypes/conpty-probe`](../prototypes/conpty-probe) 验证和固定原始 ConPTY 录制格式，
并把已验证的生命周期代码迁入独立 `SessionHost` 进程。

目录边界：

- `src/Agent`：会话目录、Relay、加密缓冲与访问控制；
- `src/Tray`：当前用户托盘和设置 UI；
- `src/SessionHost`：每会话一个 ConPTY Owner；
- `src/Updater`：签名更新和回滚；
- `tests`：生命周期、IPC、句柄和 Golden Stream 测试。

正式 Windows 工程固定 .NET SDK 8.0.303，包含 Agent、SessionHost、共享本机控制契约和
WinForms Tray：

```powershell
.\Build-Windows.ps1
.\tests\Validate-Phase1WindowsApp.ps1
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

“停止远程访问”只关闭 Agent 侧 Pipe 并禁用后续注册。Session Host 会继续桥接本地
Windows Terminal 与内层 ConPTY，不收到终止 Shell 的命令。Host 异常退出或断管后未在
限定时间重连时，worker 自动结束并从 Registry 删除。

停止结果包含请求数、完成数和超时数。单会话超时返回稳定码 `STOP_TIMEOUT`；全局停止
返回 `REMOTE_ACCESS_STOP_TIMEOUT`；关闭返回 `REGISTRY_SHUTDOWN_WITH_TIMEOUTS`；受控
重启返回 `AGENT_RESTART_WITH_TIMEOUTS`。超时 worker 会保留在不对摘要公开的 retiring
集合，禁止相同 Session ID 重注册，并在后续关闭中再次 best-effort 等待。批量停止会对
全部 worker 先发停止，再逐一收集结果，不因首个超时跳过其余 worker；除关闭/重启命令外，
控制服务返回错误码后继续运行。

`Test-SessionRegistry.ps1` 使用真实 Session Host 验证 3 个同时会话、摘要脱敏、单会话和
并发重复停止、异常退出 Host 回收；另用独立 PID 完成认证后主动失联并保持存活，验证
断管超时回收。测试也覆盖全局停止、新注册拒绝、Registry 关闭及 6 个本地 Shell 在远程
停止后的存活与输入。内部 worker 工厂还可重复验证单会话、批量和关闭超时，不增加生产
测试开关；测试会实际启动构建后的 `TermLink.Agent.exe --registry` 并通过控制 Pipe 列表和
优雅关闭。证据位于
`docs/validation/phase1-session-registry.json`。

## .NET 8 Agent、Tray 与重启恢复

`TermLink.Windows.sln` 现在构建四个正式工程：

- `TermLink.AgentControl`：Agent 与 Tray 共用的本机 Pipe V2 结构化契约；
- `TermLink.Agent`：长驻当前用户 Registry 和每会话 worker；
- `TermLink.SessionHost`：独立 ConPTY Owner；
- `TermLink.Tray`：系统托盘、脱敏会话列表、停止单会话、停止全部远程访问及重新启用。

Tray 退出不会关闭 Agent 或本地 Shell。默认控制 Pipe 名包含当前用户 SID 的 SHA-256
截断值；Tray 只消费 `SessionSummary`，不会获得 Pipe 名、Token、Buffer 路径或终端正文。
`--status-json` 仅供诊断输出同一份脱敏摘要；`--restart-agent` 为受控更新保留注册索引，
`--shutdown-agent` 按停止远程访问语义清除索引。

Registry 在 Buffer Root 保存 `registrations.dpapi`。文件载荷使用 DPAPI CurrentUser，写入时
原子替换；只包含恢复路由所需的 Session ID、Shell 类型、Host PID 与启动时间、注册时间、
Pipe 名和握手 Token。启动时必须同时匹配 PID 与进程启动时间才会恢复，以防 PID 重用；
正常 Session 结束、停止单会话、停止全部远程访问或受控关闭都会删除对应索引。该索引不
包含事件、输入、标题或工作目录，也不是终端历史或跨设备 Snapshot。

`Validate-Phase1WindowsApp.ps1` 使用正式 .NET 8 构建产物启动 20 个真实 ConPTY
SessionHost，分别在强制结束和受控重启 Agent 后从加密索引恢复全部 20 个连接，再由 Tray
核对 20 条脱敏摘要。最终停止远程访问后，20 个本地 Shell 均继续接收本地输入并正常退出。证据位于
`docs/validation/phase1-windows-app.json`。

## 签名 MSIX 与登录自启

`package/Build-Msix.ps1` 使用固定 .NET 8 SDK 发布 `win-x64` 自包含 payload，分别签名 7 个
TermLink 自有 EXE/DLL，再由 Windows SDK MakeAppx 生成并签名完整 MSIX。非开发构建必须
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

验证使用不落库的临时开发证书，覆盖 7 个嵌入签名、完整 MSIX 签名、476 文件自包含包、
Manifest/StartupTask、解包 payload、Tray 单实例、Agent 状态与关闭；不会修改系统证书库或
注册测试包。当前机器关闭 Developer Mode，开发根证书安装会触发 Windows 安全确认，因此
尚未把开发证书自动加入 Root，也没有把该结果写成真实 `Add-AppxPackage` 成功。生产证书链
可用后必须补当前用户安装、登录后启动和卸载验收。证据位于
`docs/validation/phase1-windows-installer.json`。

当前仍没有受控更新/失败回滚、生产证书下的真实安装验收、生产 Relay、账号与设备授权、
设备密钥或 E2EE。阶段性本机控制 Pipe V2 和 DPAPI 注册索引都不等于生产 Protobuf 或跨设备
协议。
