# TermLink 技术验证与 Windows Terminal Android 移植评估

文档版本：0.2
验证日期：2026-08-03
结论状态：阶段 0 当前环境可自动化验收完成

## 1. 结论摘要

阶段 0/1 得到四个明确结论：

1. **TermLink 的 Windows 受管会话方案可行。** 独立 Session Host、外层/内层双
   ConPTY、四种 Shell Profile、输入输出桥、resize、控制键、退出、断管、超时、强杀和
   句柄回收均已在真实 Windows 10 ConPTY 上通过。
2. **Windows Terminal 不能直接构建或简单移植成 Android APK。** MIT 许可证允许修改和派生源码，但现有应用深度绑定 Windows 平台。若目标是“在 Android 上获得 Windows Terminal 类似体验”，技术上可以重写 Android 外壳和渲染层，并选择性改造少量终端解析逻辑；这实际上是新建 Android 终端客户端，不是常规意义上的跨平台编译。
3. **固定 Termux Fork 可作为 Android 默认终端引擎。** 10 个 Golden Stream 在 Termux
   与固定 xterm.js Oracle 间完成 122/122 中间网格检查，匹配率 100%；同一 10 个用例
   在 API 36 模拟器独立冷启动后全部显示 `Verified`。
4. **Android 双向会话的网络与状态组合可行。** 独立 `core-network` 已把
   `RemoteTerminalSession`、真实 Termux View 和 Docker 临时测试 Relay 连通；API 36
   模拟器通过累计 ACK、断线续传、输入 accepted、测试租约、History Gap 和 exited 验证。
   这只证明临时明文测试链路，不证明生产 Relay、设备授权、E2EE 或 Snapshot。

对 TermLink 的推荐是：保留 Windows ConPTY 作为 Shell 主机，在 Android 上实现独立的 VT/ANSI 终端渲染与控制客户端，**不要把完整 Windows Terminal 移植作为 MVP 前置条件**。

GitHub 候选引擎、Windows Terminal 配色映射和 Android 端最终架构见 [Android 全功能 Terminal 引擎调研与技术方案](ANDROID_TERMINAL_ENGINE_RESEARCH.md)。

## 2. Windows ConPTY 实机验证

### 2.1 验证环境

| 项目 | 实际环境 |
| --- | --- |
| 操作系统 | Windows 10 专业版，10.0.19045，x64 |
| Windows Terminal | 安装包 1.24.11911.0，`wt.exe` 文件版本 1.24.2607.10001 |
| Windows PowerShell | 5.1 / 文件版本 10.0.19041.3996 |
| PowerShell | 7.6.4 |
| WSL | WSL 2，默认 Ubuntu 24.04；另有 docker-desktop |
| .NET | .NET 8 Runtime；本机未安装 .NET SDK |

原型与阶段 0 Session Host 不依赖 .NET SDK。PowerShell 使用 `Add-Type` 或系统 C#
编译器编译 P/Invoke 代码，直接调用 Windows API。

### 2.2 原型位置

- `prototypes/conpty-probe/ConPtyNative.cs`：ConPTY、管道、扩展进程属性和进程生命周期封装；
- `prototypes/conpty-probe/ConPtyProbe.ps1`：单个 Shell 验证入口；
- `prototypes/conpty-probe/Run-All.ps1`：全部 Shell 回归入口。
- `win-agent/src/SessionHost`：独立 ConPTY Owner 与 Windows Terminal Profile fragment
  生成器；
- `win-agent/tests/Test-SessionHost.ps1`：双 ConPTY 和生命周期回归入口。

运行：

```powershell
cd prototypes\conpty-probe
.\Run-All.ps1

cd ..\..\win-agent\tests
.\Test-SessionHost.ps1
```

### 2.3 验证方法

每个测试执行以下流程：

1. 创建 ConPTY 输入和输出管道；
2. 以 80×25 创建伪控制台；
3. 通过 `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 启动目标 Shell；
4. 将终端调整为 100×30；
5. 写入测试命令，模拟远端 Android 输入；
6. 从输出管道读取原始 VT/ANSI 字节流；
7. 检查输出标记和输入计算结果；
8. 检查 Shell 是否返回预期退出码 17。

输入验证值由多段字符串在 Shell 内拼接，最终检查：

```text
__INPUT_CAPTURED__:android-input-simulation
```

因此验证不只是匹配被终端本地回显的命令文本，还确认 Shell 实际消费并执行了输入。

### 2.4 实测结果

| Shell | 输出捕获 | 输入往返 | resize | 退出码 | 结果 |
| --- | --- | --- | --- | --- | --- |
| CMD | 通过 | 通过 | HRESULT `0x00000000` | 17 | 通过 |
| Windows PowerShell | 通过 | 通过 | HRESULT `0x00000000` | 17 | 通过 |
| PowerShell 7 | 通过 | 通过 | HRESULT `0x00000000` | 17 | 通过 |
| WSL / bash | 通过 | 通过 | HRESULT `0x00000000` | 17 | 通过 |

捕获内容中实际出现了：

- ANSI 清屏、光标、颜色和窗口标题控制序列；
- PowerShell 行编辑器产生的光标定位及颜色序列；
- WSL bash 的 bracketed-paste 模式序列；
- CMD 中文系统信息的 UTF-8 输出；
- Shell 提示符、输入回显、命令结果和退出状态。

这些结果证明网络层应传输**原始有序字节流**，Android 必须使用真正的终端模拟器解析状态，不能只把内容追加到普通文本框。

阶段 0 又固定了 10 个 Bundle：四种 Shell smoke、ANSI/Unicode、Terminal Features，以及
Vim、less、fzf、htop。录制脚本在临时目录重新生成后可由严格校验器 10/10 读取；Termux
与 xterm.js 在 122 个事件后检查点均匹配。机器可读结果见
[`phase0-grid-report.json`](validation/phase0-grid-report.json)。

### 2.5 验证过程中发现的实现约束

#### 标准句柄继承

自动化测试宿主会重定向父进程 stdin/stdout。Windows 创建子进程时可能沿用这些有效
句柄，导致 Shell 绕过 ConPTY 管道。Probe 和正式 Session Host 现均使用
`STARTF_USESTDHANDLES` 配合空标准句柄启动 ConPTY 子进程，不再临时修改父进程的全局
标准句柄。

正式实现不能在多线程 PC Agent 中修改进程级全局标准句柄，否则并发创建会话时存在竞态：

- 每个会话使用独立、无继承标准句柄的 `termlink-session-host.exe`；
- PC Agent 只负责管理 Session Host 和网络连接；
- 对继承句柄使用白名单，不让令牌、日志、Relay socket 等敏感句柄进入 Shell；
- 增加“从 Windows 服务启动”和“从托盘程序启动”两种上下文的回归测试。

#### PowerShell 交互模式

PowerShell 使用 `-NonInteractive` 时会主动拒绝 `Read-Host`。TermLink 会话本质上是交互终端，正式 Profile 不应添加该参数。无 Profile 与非交互是两个不同概念，可继续使用 `-NoProfile`，但不应错误关闭交互能力。

#### 关闭顺序

ConPTY、管道和 Shell 关闭顺序会影响 EOF 与读线程退出。正式实现必须并行读取输出，并在退出、超时和强制终止路径中统一管理：

1. 停止接受远端输入；
2. 关闭输入写端；
3. 等待或终止 Shell 进程树；
4. 关闭 ConPTY；
5. 读取剩余输出并结束输出管道；
6. 发布唯一一次生命周期结束事件。

### 2.6 Session Host 阶段 0 结果

`Test-SessionHost.ps1` 在真实外层 ConPTY 中运行独立 Session Host，并验证：

- CMD、Windows PowerShell、PowerShell 7、WSL 四个 Windows Terminal Profile fragment
  使用固定 GUID 和 Base64 Shell 命令；外层 resize 能传播到内层 ConPTY；
- Ctrl+C、Alt+B、UpArrow 转为正确的控制台输入；Android 侧另覆盖文本、Unicode code
  point、特殊键、Paste、SGR Mouse、Focus 和只读拒绝；
- Agent UTF-8 stdin pipe 经 PowerShell 7 `ReadKey` 完成 CJK 与 emoji 往返；
- 输入 EOF、600 ms timeout、外层强制终止和 Job Object `KILL_ON_JOB_CLOSE` 均能回收
  Shell 进程树；
- 快速 resize `120×40 → 60×20 → 132×43` 最终传播成功；
- 2 MiB 输出无截断，耗时 1477 ms；10 秒持续任务完成；
- 20 轮 Session Host 生命周期后父测试进程句柄 `619 → 601`，没有正增长。

完整证据见 [`phase0-session-host.json`](validation/phase0-session-host.json)。Session Host
使用 `ReadConsoleInputW` 读取 Unicode `KEY_EVENT_RECORD`，不依赖控制台代码页；因此已
移除无效且会修改 Console 共享状态的 `SetConsoleCP(65001)`。

### 2.7 已知 Windows 限制

- Windows 10 19045 的 `ClosePseudoConsole` 存在已知 conhost Process handle 问题，见
  [microsoft/terminal#17903](https://github.com/microsoft/terminal/issues/17903)。生产架构
  用独立 Session Host 隔离每个 ConPTY，并让 OS 在 Host 退出后回收残留内核对象。
- 本机 CP936/Windows 10 下，外层双 ConPTY 的 Unicode `ReadLine` 路径不可靠；这不是
  生产 Agent 输入路径。Agent UTF-8 pipe → Session Host → 内层 ConPTY → PowerShell 7
  `ReadKey` 的 CJK/emoji 路径已通过。
- Windows 终端语义下，关闭外层输入 pipe 不会自动让交互 Shell 产生 EOF。阶段 0 使用
  `Ctrl+Z` 加 Enter 验证 Windows EOF；生产 Session 生命周期必须发送显式关闭/终止意图。
- Ctrl+Break、Windows 服务/UAC、锁屏和多用户桌面属于 Agent 集成阶段，不由阶段 0
  Session Host Harness 宣称通过。

## 3. Windows Terminal 官方源码审计

### 3.1 审计基线

审计对象为微软官方仓库：

- 仓库：<https://github.com/microsoft/terminal>
- 分支：`main`
- Commit：[`fbda436dc654cf551dd196b2667ef95d3e0a7262`](https://github.com/microsoft/terminal/commit/fbda436dc654cf551dd196b2667ef95d3e0a7262)
- Commit 时间：2026-07-31T23:14:39Z
- 审计时最新正式版本：[`v1.24.11911.0`](https://github.com/microsoft/terminal/releases/tag/v1.24.11911.0)

### 3.2 许可证结论

根目录 [`LICENSE`](https://github.com/microsoft/terminal/blob/fbda436dc654cf551dd196b2667ef95d3e0a7262/LICENSE) 为 MIT License，允许使用、复制、修改、合并、发布、分发、再许可和销售副本，但必须保留版权与许可声明。仓库还包含 [`NOTICE.md`](https://github.com/microsoft/terminal/blob/fbda436dc654cf551dd196b2667ef95d3e0a7262/NOTICE.md) 以及第三方组件各自的声明。

因此：

- 从开源许可证角度，创建 Android 派生版本是允许的；
- 发布时必须保留 MIT 和适用的第三方许可声明；
- MIT 代码许可不等于自动获得 Microsoft、Windows Terminal 名称和 Logo 的商标授权，TermLink 应使用自己的名称与视觉资产；
- 本文不是法律意见，正式商业发布前仍应完成依赖和商标审查。

### 3.3 平台依赖证据

#### 官方当前没有跨平台/Android 计划

官方仓库的 [Issue #19815](https://github.com/microsoft/terminal/issues/19815) 在 2026-01-29 明确提出 Linux、macOS，甚至 Android 的跨平台与同步诉求。该 Issue 被关闭为 `not planned`，Windows Terminal 维护者说明项目“深度依赖 Windows 平台，一直到使用 Direct3D 11 的渲染器”，因此不太可能带到其他平台。

较早的 [Issue #14956](https://github.com/microsoft/terminal/issues/14956) 同样因跨平台请求被关闭为 `not planned`，回复引用项目 FAQ：Windows Terminal 需要大量 Windows 专有技术，近期不会支持其他平台。

这不是“许可证禁止移植”，而是“官方不提供跨平台实现，现有代码也没有可直接使用的 Android 平台层”。

#### 应用与构建系统是 Windows 专用

官方 [`README.md`](https://github.com/microsoft/terminal/blob/fbda436dc654cf551dd196b2667ef95d3e0a7262/README.md#prerequisites) 明确要求 Windows 10 2004 以上、Windows 11 SDK、Visual Studio、C++ 与 WinUI 工作负载。Windows Terminal 项目文件声明：

- `ApplicationType` 为 `Windows Store`；
- 使用 C++/WinRT；
- 引用 Microsoft.UI.Xaml；
- 通过 MSIX/AppX 打包与部署；
- 依赖多个 Visual C++ `.vcxproj` 项目。

仓库当前约有 76 个 `.vcxproj`、2 个 `.wapproj`、48 个 `.xaml` 和 111 个 `.idl` 文件；未发现 Android、Gradle、AndroidManifest 或 JNI 构建目标，也没有 Kotlin/Java 源文件。

#### UI 控件依赖 WinUI/XAML 与 WinRT

[`TerminalControl/pch.h`](https://github.com/microsoft/terminal/blob/fbda436dc654cf551dd196b2667ef95d3e0a7262/src/cascadia/TerminalControl/pch.h) 直接包含 Windows.Foundation、Windows.UI.Xaml、Microsoft.UI.Xaml、Windows Storage 和 Windows 输入相关 WinRT 接口。标签页、设置页、窗口管理、辅助功能、IME、拖放和快捷键均需要 Android 侧重新实现。

#### GPU 与文字渲染依赖 DirectX

[`renderer/atlas/pch.h`](https://github.com/microsoft/terminal/blob/fbda436dc654cf551dd196b2667ef95d3e0a7262/src/renderer/atlas/pch.h) 直接依赖 Direct2D、Direct3D 11、DirectWrite 和 DXGI。Android 不提供这些 API，必须改写为 Android Canvas、OpenGL ES、Vulkan 或 Compose 渲染。

文字塑形、字体回退、emoji、连字、宽字符和光标绘制并不是替换几个函数即可完成，属于移植工作量最大的部分之一。

#### TerminalCore 也不是开箱即用的跨平台库

[`TerminalCore`](https://github.com/microsoft/terminal/tree/fbda436dc654cf551dd196b2667ef95d3e0a7262/src/cascadia/TerminalCore) 虽然包含有价值的状态机、缓冲区、选择与终端输入逻辑，但当前仍依赖：

- C++/WinRT 接口和生成代码；
- Windows 输入类型，例如虚拟键、扫描码和 `WORD`；
- Windows 项目系统及仓库内部 `til`/`wil` 类型；
- Atlas 渲染器及其他 Windows 工程引用。

更底层的 [`terminal/parser`](https://github.com/microsoft/terminal/tree/fbda436dc654cf551dd196b2667ef95d3e0a7262/src/terminal/parser) 相对更有复用价值，但预编译头仍直接包含 `windows.h`、SAL 和 Windows tracing。若要通过 Android NDK 使用，仍需拆分接口、补兼容层、移除 Windows tracing，并建立 CMake/NDK/JNI 工程。

#### ConPTY 本身不能移植到 Android

ConPTY 是 Windows Kernel/Console Host 提供的 API。Android 客户端既不需要也不能本地运行 ConPTY；它只需要接收 PC Agent 发送的 VT/ANSI 字节流。若 Android 要运行本机 Shell，则应使用 Android/Linux PTY，这是另一个主机实现，与远程 Windows 会话无关。

### 3.4 可移植性分层结论

| 目标 | 结论 | 说明 |
| --- | --- | --- |
| 把官方 Windows Terminal 直接编译成 APK | 不可行 | 没有 Android 构建目标，UI、打包、输入和渲染均为 Windows API |
| 用 NDK 移植整个 Windows Terminal | 理论可行但不经济 | 需要替换大部分平台层与构建系统，接近重写应用 |
| 只复用 TerminalCore | 有条件可行 | 仍需剥离 WinRT、Windows 类型与 Atlas 依赖，维护长期上游合并成本 |
| 只改造 VT parser/状态机 | 可研究 | 范围更小，但仍不是直接编译，需要平台兼容层和独立测试 |
| Android 独立实现终端 UI，接收 ConPTY 字节流 | 可行且推荐 | 与 TermLink 架构吻合，交付风险最低 |
| 仿照 Windows Terminal 的主题和交互 | 可行 | 重做交互与视觉，不复制名称/Logo，并遵守实际复用代码的许可证 |

## 4. Android 推荐实现路线

### 4.1 路线选择

#### 路线 A：WebView + 成熟 VT 渲染器，作为兼容基线与回退

Android 使用 Kotlin/Compose 承担账号、PC/会话列表、连接、密钥和生命周期；终端页面在受限 WebView 中运行成熟的 VT/ANSI 渲染器，通过本地 Bridge 接收解密后的二进制流并发送按键。

优点：

- 快速覆盖 ANSI、True Color、光标、滚动区和常见 TUI；
- 可先验证协议和移动端键盘体验；
- 与 Windows Terminal 源码解耦，升级成本可控。

注意事项：

- WebView 禁止任意网络访问、文件访问和动态脚本注入；
- Web Bridge 只暴露最小接口并校验消息大小；
- 原始终端内容不写入 WebView 调试日志或可被第三方页面访问的存储；
- 必须实测中文 IME、硬件键盘、组合键、复制选择和高频输出。

#### 路线 B：Android 原生终端内核，完整度优先时作为默认路线

当前默认选择 Termux `terminal-emulator` + `terminal-view` 派生 Fork，通过 Compose 承载原生 View；xterm.js 离线 WebView 保持兼容对照和回退。该路线能获得更好的触控、输入法、无障碍和性能控制，但需要补齐 Windows/ConPTY 差异并维护 Golden Stream 回归。

#### 路线 C：移植 Windows Terminal Core，不建议用于 MVP

先把 parser、buffer、selection 与 renderer interface 提取为标准 C++，移除 WinRT/WIL/Windows tracing，再通过 Android NDK + JNI 接入 Kotlin，并重写 Android 渲染器。

仅当产品后续明确要求与 Windows Terminal 在大量边缘 VT 行为上高度一致，且团队愿意长期维护微软上游差异时，才值得进入此路线。

### 4.2 推荐组件边界

```text
Android Compose UI
├── Account / Device / Session screens
├── Realtime WebSocket client
├── E2EE and Android Keystore
├── Input lease and mobile key toolbar
└── TerminalEngine interface
    ├── TermuxNativeEngine  # 默认
    └── XtermWebViewEngine  # 兼容对照与回退
```

`TerminalEngine` 应只接收和产生以下跨平台事件：

- `feed(bytes, seq)`：输入 PC 输出字节；
- `resize(columns, rows)`：改变视口；
- `sendInput(bytes)`：发送 UTF-8/控制序列；
- `snapshot()/restore()`：重连画面恢复；
- `selection/copy/search`：本地能力，不把明文交给 Relay。

这样可以先用 WebView 验证产品，再在不改变网络协议和业务 UI 的前提下替换为原生引擎。

## 5. 对项目方案的最终调整

基于本轮验证，TermLink 应正式采用以下决策：

1. Windows PC 端由独立 Session Host 拥有 ConPTY 和 Shell 生命周期。
2. 普通 Windows Terminal 只作为可选的本地前端/启动入口，不作为远程捕获 API。
3. Android 不移植完整 Windows Terminal，首版实现独立 TerminalEngine。
4. PC 与 Android 之间传输原始、有序、端到端加密的 VT/ANSI 字节事件以及 resize/lifecycle 事件。
5. 快照应来自终端解析状态或规范化屏幕状态，不能简单截取最后若干行纯文本。
6. 阶段 0 已完成“ConPTY 录制 → Termux/xterm.js 同流对照 → Android 离线/LAN 测试回放”；
   生产协议仍按后续阶段实现。

## 6. 阶段 0 兼容性结论

当前 Windows 10/API 36 环境中，阶段 0 可自动化项已完成。明确的不兼容或未证明项为：

1. OSC 52 Clipboard 内容被有意阻断，只验证解析器可恢复；生产是否允许 OSC 52 必须经过
   权限和泄露风险评审。
2. OSC 8 当前只验证控制序列后的解析恢复，尚未提供安全链接元数据/点击语义；Mouse
   1000/1002/1003/1006 与 Focus 1004 已覆盖模式解析和输出编码，但未经过真实鼠标硬件。
3. 复杂 grapheme、更多 ZWJ/字体回退和全部恶意控制序列不可能由 10 个固定流穷尽；
   Termux/xterm.js 差异报告必须继续作为回归门槛。
4. `termlink.terminal-grid-draft` v0 只是引擎对照格式，不能冻结为跨端 Snapshot；
   `normalized_grid` 和参考截图仍有意为空。
5. API 36 x86_64 模拟器完成 10/10 本地回放和 LAN WebSocket 回放，但它不能替代 arm64
   真机、Gboard/Samsung/搜狗 IME、蓝牙键鼠、平板/折叠屏、TalkBack 和耗电测试。当前
   只有 API 24 与 API 36 AVD，而 app `minSdk` 为 26，无法补做 API 26 模拟器验证。
6. 固定 Termux 上游完整网络同步两次遇到 GitHub `git fetch` connection reset；检入源码
   的两项 patch 已通过 `git apply --reverse --check`，但一次全新在线同步仍应在网络恢复后
   重跑。
7. LAN Replay 是只读固定测试设施，不接收输入，也不证明生产 Protobuf、ACK、背压、
   断线恢复、设备授权或 E2EE。

Android 模拟器证据见
[`phase0-android-emulator.json`](validation/phase0-android-emulator.json)。真机与外设矩阵是
阶段 0 唯一外部硬件阻塞；在这些结果补齐前，不能把“当前环境阶段 0 通过”表述为完整的
生产兼容认证。

## 7. 阶段 1 本地闭环进度

### 7.1 Windows Host-Agent IPC

当前验证实现为每个 Session 建立当前用户 SID ACL 的 Named Pipe，并使用 Session ID、
32 字节一次性 Token、声明 PID 和 Windows 返回的实际 Pipe 对端 PID 做双向认证。本机帧
采用长度前缀、类型、Session ID、64 位序号和 Payload，最大 1 MiB；该格式不用于公网，
也不是生产 Protobuf Envelope。

Host 对未 ACK 事件保留生产默认 4 MiB 有界内存队列。连接断开后继续服务本地 ConPTY，
重连时由独立写循环按序重发，读循环可同时处理 ACK 和 Agent 命令。队列溢出会丢弃旧队列
并产生一个覆盖准确缺失范围的 `Gap`；Agent 只在 `gap_start == last_seq + 1` 且
`gap_start <= last_seq + 1` 且 `gap_end == gap_frame_seq - 1` 时接受；小于号允许 ACK 在旧连接
关闭前已落盘、但尚未被 Host 处理的安全重叠前缀。输出突发最多合并 2 ms，帧最大 64 KiB。

Agent 使用 DPAPI CurrentUser 保护每个落盘事件，并以 DPAPI 保护的去重文件保存最近 4096
个输入 ID。输入正文帧、握手和 resize request 不进入事件 Buffer。Agent 在事件持久化后才
ACK，重启时从现存加密 chunk 文件名恢复最后序号。

`Test-LocalAgentLoop.ps1` 在真实外层 ConPTY 中运行 Session Host，主动制造一次 Agent 断管，
验证重发、最终 ACK、输入 ID 去重、远端 resize、exit 17 和 Buffer SHA-256。
`Test-LocalAgentGap.ps1` 用诊断阈值触发与生产队列相同的 Gap 算法，验证缺口持久化及缺口后
的连续 ACK。证据分别为：

- [`phase1-local-agent.json`](validation/phase1-local-agent.json)；
- [`phase1-local-agent-gap.json`](validation/phase1-local-agent-gap.json)。

### 7.2 Win-Agent Session Registry

`SessionRegistry` 在一个长驻 Agent 进程内为每个 Host 启动隔离的 `SessionAgent` worker，
每个 worker 继续独占原有认证 Pipe、序号/ACK、DPAPI Buffer 和 `input_id` 去重状态。活动
目录和全局远程开关由单锁保护，停止动作先原子移除目录项，再在锁外关闭 Agent Pipe 并
等待 worker 回收，避免停止路径阻塞其他摘要和注册操作。

长驻入口使用当前用户 SID ACL 的本机控制 Pipe，支持注册、列出脱敏摘要、停止单会话、
停止全部远程访问、显式重新启用及关闭 Registry。Host 握手除原有 Session ID、Token、
声明 PID 和 OS 实际 PID 外，还核对 Registry 注册时记录的 Host PID。摘要只包含路由所需
元数据，不包含终端正文、标题、工作目录、Token、Pipe 名或 Buffer 路径。

单会话和全局停止只关闭远程 Agent Pipe，不向 Host 发送终止指令。真实 Host 测试确认
本地标准输入仍能进入内层 ConPTY 并让 Shell 正常退出。Host 异常退出或断管超时后，worker
结束并从 Registry 原子移除；全局停止同时禁用新注册，只有显式重新启用后才能恢复。

停止 API 结构化返回请求、完成和超时数。单会话、全局和 Registry 关闭超时分别返回
`STOP_TIMEOUT`、`REMOTE_ACCESS_STOP_TIMEOUT` 和 `REGISTRY_SHUTDOWN_WITH_TIMEOUTS`。
超时 worker 进入内部 retiring 集合，相同 Session ID 在清理完成前不能重注册；后续关闭
再次 best-effort 等待。批量路径先向所有 worker 发出停止，再对每个 worker 收集结果，
首个超时不会跳过后续清理，控制服务也不会因可恢复的停止超时退出。

`Test-SessionRegistry.ps1` 已覆盖 3 个同时会话、摘要脱敏、重复/并发停止只有一个成功者、
异常退出 Host 回收，以及独立 PID 完成认证、主动断管但保持存活时的超时回收；测试还覆盖
2 个会话全局停止、禁用期间拒绝注册、2 个会话 Registry 关闭，以及 6 个本地 Shell 不受
远程停止影响。内部 worker 工厂稳定制造了单会话、批量和关闭等待超时，验证稳定错误码、
控制循环存活、全部 worker 均被等待以及 Dispose 重试 retiring worker；同时实际启动了
构建产物 `TermLink.Agent.exe --registry`，完成 List 和优雅 Shutdown。证据为
[`phase1-session-registry.json`](validation/phase1-session-registry.json)。该控制格式只用于
本机 Agent/Tray IPC；当前为阶段性 V2，不是生产 Relay、Protobuf 或 Snapshot。

在此基础上已建立固定 SDK 8.0.303 的 `TermLink.Windows.sln`，分别构建 AgentControl、
Agent、SessionHost 和 WinForms Tray，Release 构建为 0 warning/0 error。Tray 使用当前用户
控制 Pipe 展示脱敏摘要，支持停止单会话、停止全部远程访问和重新启用；退出 Tray 不结束
Agent 或本地 Shell。

Registry 新增 `registrations.dpapi` 恢复索引。索引使用 DPAPI CurrentUser 保护并原子替换，
只保存 Session ID、Shell 类型、Host PID 与启动时间、注册时间、Pipe 名和握手 Token；不
保存终端正文、标题、工作目录、输入或事件。Agent 启动时只有 PID 与进程启动时间同时匹配
才恢复，避免 PID 重用；正常结束或远程停止会删除对应条目。

`Validate-Phase1WindowsApp.ps1` 使用正式 .NET 8 产物启动 20 个真实 ConPTY SessionHost，
分别在强制结束和受控重启 Agent 后从加密索引恢复 20/20 个会话，注册时间保持不变；Tray
读取 20 条脱敏摘要。
停止全部远程访问后索引被清除，20 个本地 Shell 均继续接收本地输入并正常退出。证据为
[`phase1-windows-app.json`](validation/phase1-windows-app.json)。

### 7.3 Windows 签名 MSIX 与登录自启

`package/Build-Msix.ps1` 已使用 .NET 8.0.303 生成 `win-x64` 自包含 payload，签名 8 个
TermLink 自有 EXE/DLL，再使用 Windows SDK 10.0.26100 MakeAppx 生成并签名完整 MSIX。
Manifest 使用稳定身份 `TermLink.Terminal`、`runFullTrust` 和
`windows.startupTask`，登录后启动 Tray；Tray 以当前用户控制 Pipe 派生的本地 Mutex 保证
重复启动只留下一个实例。

`Validate-WindowsInstaller.ps1` 使用隔离身份和不落库的临时开发证书，验证自包含包、8 个
嵌入签名、包签名、固定更新发布者证书、Manifest、StartupTask、解包后的 Tray/Agent 启动、
双启动单实例、Agent 状态与关闭。验证前后测试包、证书库和 TermLink 进程均无残留。证据为
[`phase1-windows-installer.json`](validation/phase1-windows-installer.json)。

当前系统关闭 Developer Mode，临时开发根证书加入 CurrentUser Root 会触发 Windows 安全
确认；自动化没有点击或绕过该警告，也没有把解包运行写成真实安装成功。生产受信代码签名
证书可用后仍须执行 `Add-AppxPackage`、实际登录启动和卸载验收。

### 7.3.1 受控更新与失败回滚

新增单文件 `TermLink.Updater.exe`、独立更新清单生成器和 PowerShell MSIX 部署后端。更新
清单使用包签名证书对原始 UTF-8 JSON 做 RSA PKCS#1 v1.5 SHA-256 签名；Updater 严格绑定
固定证书、MSIX Authenticode、Package Identity、Publisher、目标/当前版本、有效期和包
SHA-256。目标与回滚包先原子缓存，再由包外 runner 通过可恢复事务日志执行更新；健康检查
失败会以 `ForceUpdateFromAnyVersion` 回装已验证的当前版本。

`Validate-WindowsUpdater.ps1` 使用两个真实开发签名 MSIX，验证清单和包篡改拒绝，以及成功、
部署失败保留旧版、健康失败回滚、回滚失败和回滚重入 5 条路径。部署状态使用故障注入后端，
未注册 AppX、未改证书库，也未把结果写成真实系统更新成功。证据为
[`phase1-windows-updater.json`](validation/phase1-windows-updater.json)。

### 7.4 Android RemoteTerminalSession

新增 `android/core-session`，只依赖 `terminal-engine-api` 和 Kotlin Coroutines，不依赖
Termux、Compose、OkHttp 或 LAN Replay。状态机包含 `Live`、`Reconnecting`、`ReadOnly`、
`Controlling`、`Exited` 和 `HistoryGap`。

当前实现保证：

- Output/Resize/Title/Lifecycle 共享严格连续序号，重复事件直接丢弃；
- 只有 `TerminalEngine.feed` 返回后才更新最高连续 ACK；
- ACK 发送失败仍保留已应用序号，重连提交该序号，不重复 feed；
- 输入必须持有未过期租约，每次生成唯一 `input_id`，发送失败后不自动重试；
- exited、无租约和 History Gap 状态阻止输入；
- `sizeEvents` 去重并在 100～200 ms 内防抖；
- 未冻结跨端 Snapshot 前，只允许从序号 1 清空并回放有界原始流，拒绝中段 reset。

11 个协程单元测试全部通过，新增回归确认 Ended 已应用后即使最终 ACK 发送失败，状态仍
保持 `Exited`。证据为
[`phase1-android-session.json`](validation/phase1-android-session.json)。全量 Android/Termux
构建、单元测试与 Lint 通过；API 36 模拟器再次完成 10/10 固定流和 LAN Docker 回放。

### 7.5 Android 网络、测试 Relay 与真实 Termux View

新增 `android/core-network`，通过 OkHttp WebSocket 串行处理独立的 `TLT1` 测试帧。适配器
在事件完成 `TerminalEngine.feed` 后才发送累计 ACK；断线后从本地最高连续序号恢复且不
重复 feed。输入每次使用唯一 UUID，只在收到匹配 `InputAccepted` 时完成，网络失败不会
自动重发。测试租约、释放、Flow Control 与明确 History Gap 均绑定到
`RemoteTerminalSession`。5 个单元测试全部通过，证据为
[`phase1-android-network.json`](validation/phase1-android-network.json)。

Go 测试服务新增 `/v1/test/session/<case>?scenario=...`，与现有只读
`/v1/test/replay/` 路由、帧 Magic 和处理器完全隔离。`steady`、`disconnect-once`、
`history-gap`、`slow-consumer` 和 `exited` 场景覆盖 suffix-only resume、匹配输入确认、
测试租约、慢消费者 Flow Control 后有界关闭，以及明确缺口。Go test/vet 与既有 LAN Replay
回归均通过，证据为 [`phase1-test-relay.json`](validation/phase1-test-relay.json)。

Compose Harness 新增“双向会话”，把 Live、Reconnecting、ReadOnly、Controlling、Exited
和 HistoryGap 直接绑定到真实状态机，同时显示 ACK、输入确认数和
`TEST RELAY · NO E2EE`。连接后收起地址、令牌和场景表单，把主要高度留给真实
`TerminalView`。

API 36 模拟器经 `ws://10.0.2.2:8080` 实测：`disconnect-once` 恢复到 ACK 13 并取得测试
租约，13 个 code point 输入事件均收到匹配确认；`history-gap` 停在 ACK 4 并报告缺失
5..6；`exited` 到达 ACK 14 与 exit 17；0 crash/ANR。证据为
[`phase1-android-relay-emulator.json`](validation/phase1-android-relay-emulator.json)。

该协议明确标记为临时明文测试格式，不是生产 Protobuf/Relay/E2EE；它不连接 Windows
Agent 或真实 Shell，不实现设备批准，也不使用 Termux 私有状态冒充跨设备 Snapshot。
[LAN Replay V1](../protocol/test-vectors/LAN_REPLAY_V1.md) 仍保持只读且格式不变。

### 7.6 尚未完成的生产边界

以上完成阶段 1 的本地 IPC、Session Registry 与客户端状态机核心，不证明生产可用。仍需
实现和验证：

- Agent/Tray 使用生产受信证书的真实安装/卸载、登录启动和 Updater 更新/降级；受控更新
  核心和故障回滚状态机已经验证；
- Android 正式设备/Session 页面，以及 `RemoteTerminalSession` 与生产网络、Crypto、
  设备授权的组合；
- Windows Agent → 生产 Relay → Android 的真实端到端输入、ACK、背压和恢复闭环；
- 通过兼容评审后再冻结 Protobuf Envelope 与可移植 `TerminalSnapshotV1`；
- OIDC、设备授权、Lease Coordinator、E2EE、Key Epoch 和无正文服务端可观测性；
- arm64 真机、厂商 IME、键鼠、平板/折叠屏、TalkBack 和后台生命周期。

LAN Replay 和 Phase 1 Test Session 都只能使用固定非敏感夹具，不能用来替代上述验收。
