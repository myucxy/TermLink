# 终端兼容核心与标准传输 V1

本文是 2026-08-25 P0–P6 的实现基线，解决两个直接问题：Android 重连后不能安全恢复断联前
内容，以及 Codex CLI 的动态终端输出在远端缺字、缺行或停在半个刷新帧。它同时规定后续接入
Windows Terminal TerminalCore、其他终端内核或二开传输时必须遵守的接口和协议边界。

## 当前生产架构结论

生产链路仍采用 `SessionHost + ConPTY + 当前用户 Named Pipe`。这不是回退方案：ConPTY 负责真实
Shell 生命周期和输入输出，Named Pipe 提供本机认证、序号、ACK、重放与 Gap；本轮新增的兼容核心
位于 ConPTY 输出之后、网络加密之前，不改变 Shell 的所有权。

`termForward/terminal-main` 的 Windows Terminal 1.26 源码可作为解析和呈现语义参考，但该外部目录
没有 Git commit，也没有清单内可复现的 MSVC/MSBuild 工具链，所以没有把它复制进发布包或宣称已
切换原生 TerminalCore。正式切换必须先建立带 commit、MIT LICENSE/NOTICE、固定编译器版本和
可重复测试的 fork；切换后仍应实现本文接口，而不是让 TerminalCore 类型渗透到 Relay/Android。

## P0–P6 完成矩阵

| 阶段 | 实现 |
| --- | --- |
| P0 安全恢复与 Codex 修复 | Android 删除 TailOnly 空屏差量启动；快照/完整历史失败时不创建实时 socket、不推进 ACK。Windows portable model 支持 Codex 实际使用的 BS/HT/VT/FF、256 色、RGB、光标形状、bracketed paste、focus reporting 和 synchronized output。 |
| P1 标准协议 | `OutputChunkPayload` 增加 `source_event_seq` 与 `presentation_revision`；定义 `TerminalPresentationUpdateV1` Snapshot/Delta/Resync 和 `TerminalInputV1`。所有新增字段保持 protobuf 向后兼容。 |
| P2 核心接口 | Windows `TermLink.TerminalCore` 与 Android `terminal-engine-api` 均提供 `ITerminalSessionSource`、`IOrderedTerminalJournal`、`ITerminalPresentationProvider`、`ITerminalPresentationConsumer`、`ITerminalInputEncoder`、`ITerminalTransport`。 |
| P3 生产接入 | Windows 输出把本机 journal sequence 同时发布为 source sequence 和 V1 raw presentation revision；Android 验证锚点后才喂给 renderer。快照仍通过现有 recipient-bound E2EE REST 历史对象恢复。 |
| P4 能力与兼容 | Hello 可声明 `terminal_presentation_metadata_v1`、`terminal_typed_input_v1`；Relay Welcome 返回有界、按服务器顺序排列的协商交集。旧客户端忽略新增字段，新客户端接受旧发送端的零值并回退到 `Envelope.seq`。 |
| P5 typed input | Android 同时发送语义输入和旧字节；Windows 用默认 VT 编码器重新编码并做固定时间一致性比较，只有一致才写入 SessionHost。文本覆盖完整 UTF-8，RAW_BYTES 保留全部 0x00–0xff，常用键、F1–F12 和 Ctrl/Alt/Shift/Meta 有确定编码。 |
| P6 shadow、回滚与验收 | portable model 在原始输出旁路生成快照，synchronized output 未结束时不发布中间 checkpoint；生产实时 raw VT 始终保留。Windows 可用 `TERMLINK_ENABLE_TERMINAL_SNAPSHOT_V1=0` 或 `TERMLINK_ENABLE_TYPED_INPUT_V1=0` 进程变量回滚，Android config 可关闭 typed input；两者均不改变本机 Shell。 |

## 序号与恢复不变量

`source_event_seq` 是 SessionHost ordered journal 的权威位置；`presentation_revision` 是某个 renderer
projection 的单调版本。当前 raw-VT compatibility provider 使用两者相等。未来 normalized delta 可以
在同一个 source event 上产生新的 presentation revision，但必须声明 `base_revision`。

恢复必须遵守以下顺序：

1. 读取 durable renderer ACK A；
2. 获取并认证一个锚定 S 的 recipient-bound snapshot，或从 1 连续重放到目标 S；
3. 在离屏 renderer 完成校验和安装；
4. 原子提交 renderer generation 与 ACK S；
5. 发送 `SUBSCRIBE(S)`，首个新事件只能是 S+1；
6. source anchor 不符、revision 回退或 suffix 缺失时进入 Resync/HistoryGap，禁止把差量应用到空屏。

下载、解密或“看起来像最后一屏”都不能单独授权 ACK。Transcript page 只用于上滑浏览，不能推进
实时 ACK，也不能插入实时 VT parser。

## Snapshot、Delta 与 Resync

`TerminalPresentationUpdateV1` 是 capability-gated 的 normalized contract：

- Snapshot 包含完整 `TerminalSnapshotV1`；
- Delta 包含 `base_revision`、改变的 screen row、cursor/modes 以及追加的 style/hyperlink；
- ResyncRequired 给出期望 revision、最新 source sequence 和稳定 reason code。

本版本冻结 schema 和跨端接口，但生产默认只协商 presentation metadata，不协商
`terminal_presentation_v1`，因此不会把尚未完成原生 TerminalCore 等价验证的 delta 送给客户端。
这是明确的灰度门禁，不妨碍 P0 的快照恢复和 Codex 内容完整性修复。

## typed input 兼容窗口

V1 `InputEventPayload.input` 仍必须存在。新 Android 额外填充 `typed_input`；新 Windows 重编码并比较
两者，防止 UI/插件语义和实际执行字节不一致。旧 Windows 忽略 typed 字段并执行旧字节；新 Windows
接受没有 typed 字段的旧 Android。兼容窗口结束前不得只发送 typed input。

Paste 是否包裹 bracketed-paste marker 取决于当前终端 mode。现有 Termux engine 已根据收到的 VT 状态
生成正确旧字节，因此此类输入用 RAW_BYTES 携带；以后 provider 能提供权威 mode 时再提升为语义 Paste。

## 二开规则

新终端源只负责产生严格递增 source events；journal 负责先持久化后公布；presentation provider 不得
越过缺失 source event；consumer 只能在 revision 连续时应用 delta；input encoder 必须是确定函数；
transport 只路由能力协商后的对象，Relay 不解析 E2EE 正文。

第三方实现可替换上述任一接口，但不得绕过账号隔离、recipient-bound E2EE、累计 ACK、输入租约、
本地键盘优先或 Gap/Resync fail-closed 规则。

## 验证入口

```powershell
& 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' run `
  --project .\win-agent\tests\TerminalCoreValidation\TermLink.TerminalCore.Validation.csproj `
  -c Release

# 可选：只复制所选 DPAPI WAL 到临时隔离目录进行真实 Codex 流验证；不输出正文、不修改原 WAL
& 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' run `
  --project .\win-agent\tests\TerminalCoreValidation\TermLink.TerminalCore.Validation.csproj `
  -c Release -- `
  --history-root '<production-history\pc-id root>' `
  --pc-device-id '<pc UUID>' --session-id '<session UUID>'

# 当前登录 Agent 可自动读取受保护的 device identity，仍不输出 Token、密钥或正文
& 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' run `
  --project .\win-agent\tests\TerminalCoreValidation\TermLink.TerminalCore.Validation.csproj `
  -c Release -- --current-production-session '<session UUID>'

& .\win-agent\tests\Test-PortableTerminalModel.ps1 `
  -DotNetPath 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe'

$env:JAVA_HOME='D:\Dev\Env\Jdk\jdk-21'
$env:ANDROID_SDK_ROOT='D:\Dev\Env\Android\Sdk'
.\android\gradlew.bat -p .\android testDebugUnitTest --no-daemon

Set-Location .\server
go test ./internal/realtime ./internal/realtimewire
```
