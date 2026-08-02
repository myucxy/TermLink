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

## 验证边界

该原型证明 TermLink 可以可靠管理自己创建的 ConPTY 会话，不证明能够附加或捕获普通 Windows Terminal 已经打开的既有标签页。网络转发、端到端加密、断线重传和 Android 渲染属于后续验证项。

测试工具通常会重定向父进程标准句柄，而 Windows 会把有效的重定向句柄复制给子进程。原型在创建 Shell 的极短窗口内清空并随后恢复测试进程的标准句柄，使子进程使用 ConPTY 提供的标准句柄。正式 PC Agent 不应在多线程进程中修改全局标准句柄；应让独立 Session Host 创建 ConPTY，或保证 Session Host 启动时没有继承重定向句柄。
