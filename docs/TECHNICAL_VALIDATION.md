# TermLink 技术验证与 Windows Terminal Android 移植评估

文档版本：0.1
验证日期：2026-08-02
结论状态：第一轮验证完成

## 1. 结论摘要

本轮得到两个明确结论：

1. **TermLink 的 Windows 受管会话方案可行。** 已在真实 Windows ConPTY 上验证 CMD、Windows PowerShell、PowerShell 7 和 WSL 的输出捕获、输入写入、窗口 resize 与退出状态，四组用例全部通过。
2. **Windows Terminal 不能直接构建或简单移植成 Android APK。** MIT 许可证允许修改和派生源码，但现有应用深度绑定 Windows 平台。若目标是“在 Android 上获得 Windows Terminal 类似体验”，技术上可以重写 Android 外壳和渲染层，并选择性改造少量终端解析逻辑；这实际上是新建 Android 终端客户端，不是常规意义上的跨平台编译。

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

原型不依赖 .NET SDK。PowerShell 使用 `Add-Type` 即时编译 C# P/Invoke 代码，直接调用 Windows API。

### 2.2 原型位置

- `prototypes/conpty-probe/ConPtyNative.cs`：ConPTY、管道、扩展进程属性和进程生命周期封装；
- `prototypes/conpty-probe/ConPtyProbe.ps1`：单个 Shell 验证入口；
- `prototypes/conpty-probe/Run-All.ps1`：全部 Shell 回归入口。

运行：

```powershell
cd prototypes\conpty-probe
.\Run-All.ps1
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

### 2.5 验证过程中发现的实现约束

#### 标准句柄继承

当前自动化测试宿主会重定向父进程 stdin/stdout。Windows 创建子进程时可能沿用这些有效句柄，导致 Shell 绕过 ConPTY 管道。原型在创建 Shell 的短暂窗口内清空并随后恢复测试进程标准句柄，成功让子进程使用 ConPTY 句柄。

正式实现不能在多线程 PC Agent 中修改进程级全局标准句柄，否则并发创建会话时存在竞态。推荐：

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

### 2.6 尚未完成的 Windows 验证

本轮没有验证以下内容，不能把它们视为已通过：

- Ctrl+C、Ctrl+Break、Alt/Shift 组合键和逐键输入；
- Vim、less、htop、fzf 等长时间全屏 TUI；
- 终端尺寸连续快速变化；
- 每秒数 MB 输出下的背压、内存上限和丢包恢复；
- Session Host 与 Windows Terminal 自定义 Profile 的本地透明桥接；
- Windows 服务、UAC、不同用户会话和锁屏状态；
- 网络 Relay、序号/ACK、端到端加密和 Android 真机渲染。

第一轮已经消除了最核心的“ConPTY 能否同时读写 Shell”风险，下一轮应优先验证 TUI、控制键和 Android 渲染兼容性。

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
6. 下一轮技术验证先做“捕获的 ConPTY 录制数据 → Android 渲染器离线回放”，再接入 WebSocket Relay。

## 6. 下一轮建议验收项

1. 录制 PowerShell、Vim、fzf 和 WSL `htop` 的原始 ConPTY 流与 resize 事件。
2. Android 真机离线回放，逐项对比 Windows Terminal 的最终屏幕状态。
3. 验证中文输入法、emoji、宽字符、组合字符、True Color 和鼠标模式。
4. 验证 Ctrl+C、Esc、方向键、Home/End、PageUp/PageDown 和软键盘辅助栏。
5. 以 2 MB/s 持续输出压测 10 分钟，观测帧率、内存、耗电和丢帧策略。
6. 暂停 30 秒后按序恢复，确保终端状态不重复、不乱序；数据缺口时从快照恢复。
