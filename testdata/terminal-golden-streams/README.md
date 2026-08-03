# Terminal Golden Streams

每个子目录是一个
[`termlink.golden-stream` V1 Bundle](../../protocol/test-vectors/GOLDEN_STREAM_V1.md)。

阶段 0 固定 10 个用例：

| 类型 | 用例 |
| --- | --- |
| Shell smoke | CMD、Windows PowerShell、PowerShell 7、WSL |
| 字符与协议 | ANSI/Unicode、Terminal Features |
| WSL TUI | Vim、less、fzf 0.44.1、htop 3.3.0 |

Terminal Features 覆盖 SGR 属性、16/256/True Color、OSC 4/8/10/11/12/52、滚动区、
光标保存恢复、alternate screen、鼠标 1000/1002/1003/1006、Focus 1004、bracketed paste
2004，以及超长 OSC/CSI 后的解析恢复。OSC 52 内容不会进入 Clipboard。

从仓库根目录录制和校验单个 PowerShell 冒烟用例：

```powershell
.\prototypes\conpty-probe\ConPtyProbe.ps1 `
  -Shell powershell `
  -CaptureRoot .\testdata\terminal-golden-streams `
  -CaseName smoke-powershell-v1

.\prototypes\conpty-probe\Test-GoldenStream.ps1 `
  -BundlePath .\testdata\terminal-golden-streams\smoke-powershell-v1
```

在不覆盖仓库固定数据的临时目录重录并严格校验全部用例：

```powershell
.\prototypes\conpty-probe\Record-Phase0GoldenStreams.ps1 `
  -CaptureRoot "$env:TEMP\termlink-phase0-recording"
```

同一 10 个 Bundle 已输入固定 Termux Fork 与 xterm.js 6.0.0 + Unicode 11；122/122
事件后网格检查点匹配。结果在 `docs/validation/phase0-grid-report.json`。

提交新 Bundle 前必须检查输出中是否存在用户名、绝对路径、Token 或其他敏感内容。
`normalized_grid` 与 `reference_screenshot` 当前有意为空；草案网格不能作为生产 Snapshot
或跨端协议。
