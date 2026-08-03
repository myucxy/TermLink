# ConPTY Session Host 技术验证

这个原型直接调用 Windows ConPTY API，验证 TermLink PC 端最关键的数据通路：

- 创建伪控制台并启动 Shell；
- 从输出管道捕获终端字节流；
- 从输入管道模拟远程输入；
- 调整终端行列数；
- 捕获 Shell 退出码。

不需要安装 .NET SDK。PowerShell 会使用 `Add-Type` 即时编译同目录下的 C# P/Invoke 代码。

## 运行

```powershell
.\Run-All.ps1

# 或单独运行
.\ConPtyProbe.ps1 -Shell powershell
.\ConPtyProbe.ps1 -Shell cmd
.\ConPtyProbe.ps1 -Shell pwsh
.\ConPtyProbe.ps1 -Shell wsl
```

每个用例必须同时满足 `OutputCaptured`、`InputRoundTripCaptured`、`ResizeSucceeded` 和 `ExitCodeCaptured` 才会通过。脚本也会输出捕获到的原始终端内容，并将 ESC 控制字符显示成 `<ESC>`，便于观察 ANSI 序列。

## Golden Stream 录制

录制器可以把每次 `ReadFile` 得到的原始输出块、单调时间、resize 和 Shell 生命周期写成
[Golden Stream V1](../../protocol/test-vectors/GOLDEN_STREAM_V1.md)：

```powershell
.\ConPtyProbe.ps1 `
  -Shell powershell `
  -CaptureRoot ..\..\testdata\terminal-golden-streams `
  -CaseName smoke-powershell-v1

.\Test-GoldenStream.ps1 `
  -BundlePath ..\..\testdata\terminal-golden-streams\smoke-powershell-v1
```

仓库当前固定 10 个 Bundle：四个 Shell smoke、`ansi-unicode-pwsh-v1`、
`terminal-features-pwsh-v1`，以及 WSL 下的 Vim、less、fzf 和 htop。它们都只包含固定
非敏感标记，并验证输入往返、80×25 → 100×30 resize 与退出码 17。

PowerShell 7 还提供 ANSI/Unicode 场景，用于输出 16/256 色、True Color、CJK、emoji、
组合字符、备用屏幕和 bracketed-paste 控制序列：

```powershell
.\ConPtyProbe.ps1 `
  -Shell pwsh `
  -Scenario ansi-unicode `
  -CaptureRoot ..\..\testdata\terminal-golden-streams `
  -CaseName ansi-unicode-pwsh-v1
```

已有同名 Bundle 时默认拒绝写入；只有明确传入 `-OverwriteCapture` 且目标 Manifest 仍是
同名 V1 Bundle 时，才会覆盖 10 个已知文件。输入正文默认不写入 Bundle。

一次重录全部阶段 0 用例：

```powershell
.\Record-Phase0GoldenStreams.ps1 `
  -CaptureRoot "$env:TEMP\termlink-phase0-recording"
```

脚本先运行 `Prepare-Phase0WslTools.sh`，从固定 URL 下载并严格校验 fzf 0.44.1、htop
3.3.0 及依赖的大小和 SHA-256，不修改 WSL 系统包。然后逐个录制并运行严格校验器。

## 独立 Session Host

阶段 0 已把生命周期代码迁入
[`win-agent/src/SessionHost`](../../win-agent/src/SessionHost)。它独立拥有内层 ConPTY、
Shell 进程树和带 `KILL_ON_JOB_CLOSE` 的 Job Object，不修改父进程全局标准句柄。运行：

```powershell
.\..\..\win-agent\tests\Test-SessionHost.ps1
```

测试同时生成 CMD、Windows PowerShell、PowerShell 7 与 WSL 的 Windows Terminal
Profile fragment，并验证双 ConPTY、Agent UTF-8 pipe 输入、Ctrl+C、Alt+B、方向键、
EOF、timeout、强杀、快速 resize、2 MiB 输出、持续运行和 20 轮生命周期。证据写入
`docs/validation/phase0-session-host.json`。

## 验证边界

该原型证明 TermLink 可以可靠管理自己创建的 ConPTY 会话，不证明能够附加或捕获普通
Windows Terminal 已经打开的既有标签页。LAN Replay 仍是固定开发测试设施；生产网络
转发、端到端加密、ACK、背压和断线恢复属于后续阶段。

测试工具通常会重定向父进程标准句柄，而 Windows 会把有效的重定向句柄复制给子进程。
Probe 与 Session Host 都通过 `STARTF_USESTDHANDLES` 指定空子进程标准句柄，交由 ConPTY
接管，不再短暂修改父进程的全局标准句柄。

Windows 10 19045 的 `ClosePseudoConsole` 有已知 conhost Process handle 问题。生产方案用
每会话独立 Session Host 隔离，并由 OS 在 Host 退出后回收。Windows Shell 的 EOF 是
`Ctrl+Z` 加 Enter；单纯关闭外层输入 pipe 不会生成 Shell EOF。
