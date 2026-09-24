# Windows 终端显示还原修复（2026-09-24）

## 根因和范围

`tmp/1.png` 两侧都是 Windows Terminal 中的 Codex CLI 0.156.1。TermLink 原先调用系统
`kernel32!CreatePseudoConsole`，使输出额外经过本机旧版 ConPTY（系统 conhost
10.0.19041.4522）。实测它丢弃 SGR 2（淡化），而 RGB 前景、RGB 背景、斜体和下划线可保留。
Codex 通过 OSC 10/11 探测外层终端颜色；旧链路无法在其 250 ms 时限内返回颜色，Codex 因此
不绘制依赖背景色计算的输入区背景。简单强制 `TERM` 或颜色变量不能解决这两项损失。

SessionHost 改用固定版本的微软官方 `Microsoft.Windows.Console.ConPTY 1.24.260710001`，
随包携带 `conpty.dll` 和架构目录中的 `OpenConsole.exe`；创建、调整尺寸、关闭均使用同一后端。
不依赖用户安装的 Windows Terminal 路径。包最低系统要求为 Windows 10 17763。
NuGet 精确版本和内容哈希写入 lock；构建和 Setup 独立复核检查原文件 SHA-256、微软
Authenticode 签名和 MIT 许可声明。自有二进制签名流程不重签微软文件。

进程环境 `TERMLINK_CONPTY_BACKEND=system` 可显式回退排障；默认使用 bundled，完整缺失的
旧开发布局允许 system 回退，部分缺失拒绝启动并提示修复安装。正式打包禁止缺失组件。
DLL 仅从安装目录的绝对路径加载，依赖搜索限定 DLL 所在目录和 System32。

现代 OpenConsole 启动会输出 `CSI 1 t`（恢复窗口提示）。共享 terminal-core 精确接受此操作
为无状态、无回复指令；其他未支持窗口操作仍拒绝。原始输出、ACK、快照和控制租约边界保持原设计。
**后续发布应先更新 Android，再更新 Windows，作为一组兼容升级。旧 Android 116 对包含
此指令的实时输出或历史重放可能拒绝解析，不能仅发布 Windows 并假定旧安卓兼容。**
本轮未修改 Server，也未执行正式发布或覆盖本机安装。

截图的约 31 px / 23 px 行距属于外层 profile 外观设置。TermLink profile 是独立 profile，
不会自动继承另一个 PowerShell profile 的专属外观覆盖。要获得相同布局，应在 Windows Terminal
设置中让两侧使用相同字体、字号、行高、颜色方案、背景和缩放，或设置公共 profile defaults。
本修复不改写用户的全局配置；无法保证不同 profile 或不同渲染器逐像素一致。

## 参考与适配边界

- 微软 [ConPTY 官方包](https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY/1.24.260710001)：
  MIT；随包保留 `ConPTY-NOTICE.txt`。原包 NuGet 作者签名和仓库签名验证通过。
- Windows Terminal [winconpty.cpp](https://github.com/microsoft/terminal/blob/v1.24.11911.0/src/winconpty/winconpty.cpp)
  和 `src/cascadia/TerminalConnection/ConptyConnection.cpp`：参考官方 DLL 导出 API 与 host 查找方式。
- Codex [terminal_probe/windows.rs](https://github.com/openai/codex/blob/rust-v0.156.1/codex-rs/tui/src/terminal_probe/windows.rs)、
  `terminal_probe.rs`、`style.rs`：参考 Windows 输入记录颜色查询、250 ms 探测期限和无颜色时的样式降级。
- Termux [TerminalEmulator.java](https://github.com/termux/termux-app/blob/3df69d1da197dd9bd71a3bafd902dffd720576b4/terminal-emulator/src/main/java/com/termux/terminal/TerminalEmulator.java)
  的 CSI `t` 分支：窗口恢复不改变单元格。已对照仓库固定版本与查询时的上游实现，并核对
  `terminal-forks/termux-terminal/UPSTREAM.md`、补丁队列、同步脚本和 Android 适配层。
  Termux 为 GPLv3-only，**仅参考行为，未复制代码**；不照搬其全部忽略策略、本地 PTY 或进程架构。

## 验证与复现

验证记录在 ignored 目录 `artifacts/validation/display-*` 和 `terminal-fidelity*.json`。

| 检查 | 结果 |
| --- | --- |
| Rust workspace | 461 + 10 + 48 项通过，含启动流逐字节拆分、淡化、快照和严格拒绝无效窗口操作 |
| Android native 模块 | 四种 ABI 构建成功；模拟器 Android 16 的 14 项 JNI 测试全部通过；JVM 单测通过（复用未变更缓存） |
| Windows | SessionHost、resize、NativeTerminalCore、WindowsSetup validation 通过；真实 create/resize/close 通过 |
| 实际 ConPTY 探测 | 受管及非受管均通过；淡化、RGB 前景/背景、OSC 往返、数字与中文输入无重复；受管 ACK 和 authority 正常 |
| Codex 0.156.1 | 新建隔离受管会话，仅启动和退出，不提交 prompt；system 无淡化/无 RGB 背景，bundled 两者均有，二者正常退出 |
| 打包 | win-x64 self-contained LockedMode publish 成功；完整包、回退、缺失/篡改拒绝检查通过 |

颜色探测使用观察到 OSC 请求后才响应的确定性外层模拟器，新版受管探测约 72 ms，非受管约
24 ms；旧版超过 250 ms 没有取得颜色。这验证真实 ConPTY、子进程和受管链路，但不代替所有
物理键盘、Windows 版本或远端网络条件。真实 Codex smoke 也采用此外层响应器，未做更新后
Windows Terminal GUI 的逐像素截图验收。正式安装包签名、上传和双端覆盖升级留待发布流程。

复现入口（先用现有开发环境构建 native DLL 和 SessionHost）：

```powershell
& ./win-agent/scripts/Build-TerminalCoreNative.ps1
& ./win-agent/src/SessionHost/Build-SessionHost.ps1
& ./win-agent/tests/Test-TerminalFidelity.ps1 `
  -SessionHostPath ./win-agent/src/SessionHost/build/TermLink.SessionHost.exe -Managed
& ./win-agent/tests/Test-ConPtyPackaging.ps1 `
  -SessionHostDirectory ./win-agent/src/SessionHost/build
# Managed 探测需要已有本地 Agent；省略 -Managed 可验证非受管链路。
```

`cargo fmt --check` 另发现原有 `input.rs` 两处格式差异；本次新增代码已符合格式，不混入无关格式改动。
旧 `Test-LocalAgentLoop.ps1` 使用 Framework csc 编译现有 Agent 失败，本轮以当前 .NET 验证项目
和实际受管 ConPTY/Codex 探测验证改动，不把该旧脚本标为通过。
