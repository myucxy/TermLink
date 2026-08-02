# Android 全功能 Terminal 引擎调研与技术方案

文档版本：0.2
调研日期：2026-08-02
适用项目：TermLink Android 客户端

## 1. 结论

GitHub 上有能够满足 TermLink Android 端需求的终端引擎，但没有一个项目能够把 Windows Terminal 应用本身直接搬到 Android。

在许可证不构成约束、终端完整度与 Android 原生体验优先的前提下，本项目调整为以下路线：

1. **最终默认引擎：[`termux/termux-app`](https://github.com/termux/termux-app) 的 `terminal-emulator` + `terminal-view` 派生 Fork。** 只复用终端核心与 View，不引入 Termux 本地 Shell、PTY 和包管理器。它已经具有成熟的 Android IME、触控选择、硬件键盘、SGR 鼠标、bracketed paste、备用屏幕、True Color、光标颜色和滚动缓冲。
2. **兼容基线和回退引擎：[`xtermjs/xterm.js`](https://github.com/xtermjs/xterm.js) 6.0.0，通过离线 WebView 嵌入。** 用它建立同一份 ConPTY Golden Stream 对照结果；Termux Fork 遇到尚未补齐的序列时，可在开发期或特定会话中切换回 xterm.js。
3. **原生对照引擎：[`connectbot/termlib`](https://github.com/connectbot/termlib) 0.1.0。** 它保持为 libvterm 兼容性对照，不再是默认原生路线，因为当前公开 API 的鼠标、独立光标颜色、搜索和快照缺口比 Termux Fork 更多。
4. 从第一天定义稳定的 `TerminalEngine` 接口，不让网络协议、账号、加密和会话页面依赖具体渲染器。

如果只选一个长期维护的 Android 引擎，选择 **Termux Terminal 派生 Fork**；如果要求最短时间得到最完整的首个可运行版本，xterm.js 仍然更快。

## 2. “全功能”的筛选标准

本次没有把“能显示彩色文本”视为全功能。候选引擎至少需要覆盖：

- VT100/VT220/xterm 常用 CSI、OSC 和 DEC 模式；
- 普通 ANSI 16 色、256 色及 24 位 True Color；
- 主/备用屏幕、滚动区、光标移动和窗口 resize；
- Vim、tmux、htop、fzf 等 curses/TUI 程序；
- 中文宽字符、emoji、组合字符和 Android IME；
- Enter、Esc、Tab、方向键、功能键、Ctrl/Alt/Shift；
- 复制、选择、粘贴、滚动历史和搜索；
- 鼠标点击、拖动、滚轮以及 SGR mouse；
- 可设置字体、前景、背景、光标、选择色和 16 色调色板；
- 可嵌入现有 App，并允许项目对核心行为进行长期修改；
- 仍有维护，并且存在测试或大型实际使用者。

## 3. GitHub 候选项目

以下数据是 2026-08-02 的快照，Star 和更新时间会继续变化。

| 项目 | 平台/许可证 | 活跃情况 | 优点 | 主要问题 | 结论 |
| --- | --- | --- | --- | --- | --- |
| [xterm.js](https://github.com/xtermjs/xterm.js) | TypeScript，MIT | 约 21k Star；2026-07-27 有提交；npm 6.0.0 | 成熟、TUI/鼠标/IME/Unicode/无障碍、插件齐全、主题字段完整、支持 ConPTY 特性 | Android 需要 WebView；需处理 Bridge、安全和软键盘 | 兼容基线与回退 |
| [connectbot/termlib](https://github.com/connectbot/termlib) | Kotlin/Compose/C++，Apache-2.0；libvterm 为 MIT | 2026-07-21 有提交；Maven Central 0.1.0；最新版 ConnectBot 正在使用 | 原生 Compose、True Color、CJK、组合字符、选择、缩放、resize、IME、四 ABI | 0.1.x 较新；鼠标 JNI、cursorColor、搜索/状态导出存在缺口 | 原生对照候选 |
| [termux/termux-app](https://github.com/termux/termux-app) | Java/Android，仓库 GPLv3-only | 约 58k Star；审计提交 `3df69d1da197`，2026-07-15 | 成熟的 VT 核心、Canvas 渲染、鼠标、IME、True Color、选择、粘贴和硬件键盘；约 145 个核心测试方法 | `TerminalView` 绑定本地 `TerminalSession`；缺 OSC 8、焦点事件、自定义选择色和稳定快照，需要派生 Fork | 完整度优先时的最终首选 |
| [xterm.dart](https://github.com/TerminalStudio/xterm.dart) | Flutter/Dart，MIT | 约 650 Star；pub.dev 4.0.0 | 移动/桌面、CJK/emoji/IME、主题、TUI 示例完整 | TermLink 已选 Kotlin/Compose；嵌入 Flutter 增加第二套 UI Runtime | 仅在改用 Flutter 时选 |
| [libapps/hterm](https://github.com/libapps/libapps-mirror) | JavaScript，BSD-3-Clause | 2026-07-24 有镜像提交 | Chrome OS/SSH 场景验证充分 | 生态和 API 便利性不如 xterm.js；Android 同样需要 WebView | Web 备选 |
| [jackpal/Android-Terminal-Emulator](https://github.com/jackpal/Android-Terminal-Emulator) | Java，Apache-2.0 | 已归档 | 早期 Android Terminal 基础 | VT 能力和 Android 技术栈陈旧，无持续维护 | 排除 |
| [wezterm/wezterm](https://github.com/wezterm/wezterm) | Rust，桌面 Terminal | 活跃 | 功能很强、渲染优秀 | 没有正式 Android UI/构建目标，移植成本过高 | 排除 |

## 4. 最终首选方案：Termux 原生派生 Fork

### 4.1 源码审计结论

本次针对 `termux/termux-app` 提交 `3df69d1da197dd9bd71a3bafd902dffd720576b4` 重新审计。适合复用的是两个独立 Android Library：

- `terminal-emulator`：`TerminalEmulator` 接受自定义 `TerminalOutput`，可通过 `append(byte[], length)` 直接输入 ConPTY 字节，并公开 `resize`、`sendMouseEvent`、`paste` 和屏幕文本访问能力；
- `terminal-view`：`TerminalView` 与 `TerminalRenderer` 提供 Android Canvas 网格渲染、IME、硬件键盘、选择手柄、缩放、滚动和鼠标交互；
- 已实现 DECSET 1000/1002、SGR mouse 1006、bracketed paste 2004、主/备用屏幕、256 色、True Color、OSC 4/10/11/12/52、三种光标形状及独立光标颜色；
- `terminal-emulator` 当前约有 145 个 JUnit 风格测试方法，覆盖颜色、Unicode、resize、历史、DEC/CSI/OSC 和矩形区域等行为。

因此不需要保留 Termux 的本地 Linux PTY。TermLink 可以直接把网络解密后的 ConPTY 原始字节输入 `TerminalEmulator`。

### 4.2 必需的远程会话改造

`TerminalView` 当前直接依赖本地 `TerminalSession`，但核心调用点很集中。Fork 后抽取以下接口：

```kotlin
interface TerminalSessionPort {
    val emulator: TerminalEmulator
    fun write(bytes: ByteArray)
    fun writeCodePoint(altDown: Boolean, codePoint: Int)
    fun resize(columns: Int, rows: Int, cellWidthPx: Int, cellHeightPx: Int)
}
```

`RemoteTerminalSession` 的职责是：

- `feed(seq, bytes)` 在单一解析线程调用 `emulator.append`，随后在主线程局部刷新 View；
- `write` 把键盘、鼠标、终端查询响应包装为带 `inputId` 的加密输入事件；
- `resize` 同时调整 Android 模拟器并通知 PC Agent 调用 ConPTY resize；
- 不创建 Android 本地 Shell，不加载 Termux JNI PTY；
- Compose 页面通过 `AndroidView` 承载 Fork 后的 `TerminalView`。

### 4.3 为“完整实现”必须补齐的能力

Termux 是最合适的原生基础，但原样代码仍不能称为完全兼容。Fork 第一阶段需要补齐：

1. Windows Terminal `selectionBackground` 和可选选择前景色；Termux 当前选择效果主要是反色；
2. OSC 8 超链接及安全点击回调；
3. DECSET 1004 焦点进入/离开事件；
4. DECSET 1003 任意鼠标移动，以及更完整的触控到鼠标策略；
5. Windows/ConPTY 特殊输入与 resize/reflow 差异；
6. Unicode 表更新、emoji ZWJ 和复杂 grapheme cluster Golden Test；
7. 搜索、结构化屏幕导出和版本化快照 DTO；
8. TalkBack/屏幕阅读器语义和高吞吐时的增量重绘；
9. OSC、链接、剪贴板和标题长度限制等远程输入安全策略。

### 4.4 xterm.js 的角色

xterm.js 不再是最终 UI 技术方向，但继续承担三个角色：

- 作为 ConPTY 输出的行为对照和 Golden Stream Oracle；
- 在 Termux Fork 尚未支持某个序列时提供可切换回退；
- 帮助定位问题属于 PC Agent、传输协议还是 Android VT 模拟器。

## 5. Web 兼容基线：xterm.js 离线 WebView

### 5.1 选择依据

[`xterm.js README`](https://github.com/xtermjs/xterm.js/blob/904ae935269e/README.md) 明确列出 bash、vim、tmux、curses、鼠标事件、CJK、emoji、IME 和无障碍支持。其公开 API 还提供：

- `write(Uint8Array)`：直接输入 ConPTY 原始字节；
- `onData`：输出键盘和鼠标生成的终端输入；
- `resize(columns, rows)`；
- `scrollback`、只读输入和屏幕阅读器模式；
- `windowsPty: { backend: "conpty", buildNumber }`；
- 可选 `win32InputMode` 扩展；
- Fit、Search、Unicode、WebLinks、Clipboard、WebGL 和 Serialize Addon。

其中 Serialize Addon 官方仍标为实验性，只适合 Android 本地缓存或调试，不能作为 TermLink 跨设备权威快照格式。

### 5.2 Android 组件结构

```text
TermLink Android App
├── Compose App Shell
│   ├── Account / Device / Session UI
│   ├── 输入租约与快捷键栏
│   └── Theme / Font / Accessibility settings
├── SessionTransport
│   ├── TLS WebSocket
│   ├── E2EE decrypt/encrypt
│   ├── seq / ACK / reconnect
│   └── output batching and backpressure
└── TerminalEngine
    ├── XtermWebViewEngine       # MVP 默认
    └── NativeVTermEngine       # termlib 实验实现
```

WebView 只负责终端网格、VT 状态和选择渲染；登录、网络、密码学、设备授权、剪贴板授权以及文件访问全部留在 Kotlin 层。

### 5.3 xterm.js 初始化建议

```ts
const terminal = new Terminal({
  convertEol: false,
  cursorBlink: true,
  cursorStyle: 'block',
  drawBoldTextInBrightColors: true,
  scrollback: 10_000,
  screenReaderMode: accessibilityEnabled,
  theme: mapWindowsTerminalScheme(sessionTheme),
  windowsPty: {
    backend: 'conpty',
    buildNumber: session.conptyBuildNumber,
  },
  vtExtensions: {
    win32InputMode: true,
  },
});
```

推荐加载：

- `@xterm/addon-fit`：根据 Android 容器计算行列；
- `@xterm/addon-search`：会话内搜索；
- `@xterm/addon-unicode11`：统一宽字符规则；
- `@xterm/addon-web-links`：链接识别，但点击必须回 Kotlin 层做安全确认；
- `@xterm/addon-webgl`：只在真机验证稳定后开启，并处理 Context Loss。

不要让 Clipboard Addon 直接无条件访问系统剪贴板。OSC 52 和粘贴必须交给 Kotlin 权限策略处理。

### 5.4 WebView 安全边界

终端会显示高度敏感的数据，WebView 必须按离线组件加固：

- JS、CSS、字体和 xterm.js 全部打包进 APK，不加载 CDN；
- 使用 `WebViewAssetLoader` 的固定本地域名，不使用任意 `file://` 页面；
- 禁止文件访问、Content Provider 访问、混合内容和任意外网请求；
- CSP 默认拒绝网络、外部脚本、frame、object 和表单；
- 使用带固定 Origin 白名单的 Web Message Channel，避免暴露通用 JavaScript Interface；
- Bridge 只允许 `feed`、`input`、`resize`、`theme` 和少量状态事件；
- 每个消息限制大小和频率，批量传输建议为 16～32 KiB；
- App 进入后台时停止绘制但继续有限缓冲，超过上限触发快照恢复；
- Release 包关闭 WebView 调试，崩溃日志不能包含终端正文；
- URL、OSC 52、下载和外部 Intent 必须由 Kotlin 层校验和确认。

## 6. 原生对照方案：connectbot/termlib

### 6.1 已确认能力

[`connectbot/termlib`](https://github.com/connectbot/termlib/tree/4b738d235581) 是独立 Android Library，不负责 PTY/SSH，恰好符合 TermLink“网络由 App 管理、组件只处理终端”的边界。

已在源码和 0.1.0 发布包中确认：

- `TerminalEmulator.writeInput(ByteArray/ByteBuffer)` 可输入 Relay 解密后的原始字节；
- `onKeyboardInput(ByteArray)` 可把按键序列送回 Relay；
- `resize(rows, columns)` 与 `onResize`；
- 256 色、True Color、CJK 双宽字符和组合字符；
- Compose Canvas 渲染、选择、放大镜、滚动、缩放和多字体；
- Android IME、硬件键盘、AltGr、Ctrl/Alt/Shift、Backspace 模式；
- OSC 8 链接、OSC 52 剪贴板回调和终端标题；
- `applyColorScheme`、16 色 ANSI Palette、默认前景和背景；
- `armeabi-v7a`、`arm64-v8a`、`x86`、`x86_64`；
- Maven Central：`org.connectbot:termlib:0.1.0`；
- 最新 ConnectBot 已依赖 `termlib 0.1.0`，说明它不只是演示工程。

### 6.2 当前缺口

在审计的 `4b738d235581` 提交中：

1. libvterm 内部有 mouse API，但 termlib 公开 Kotlin/JNI API 没有 `dispatchMouseMove/Button/Wheel`；
2. Compose `Terminal` 的光标绘制直接使用前景色，无法独立应用 Windows Terminal `cursorColor`；
3. 没有公开的稳定 terminal state 序列化/恢复接口；
4. 搜索和完整 transcript 导出不是稳定公共 API；
5. 粘贴有 UI 回调，但 bracketed-paste 的端到端行为需要单独验证；
6. 没有 xterm.js 的 Microsoft `win32-input-mode` 扩展；
7. 0.1.x API 仍年轻，需要锁版本和回归测试。

建议维护一个尽量小的 `TermLink/termlib` fork，并优先把补丁贡献回上游：

- 为 `Terminal()` 增加 `cursorColor`；
- 暴露 libvterm mouse move/button/wheel；
- 增加 `paste(text)`，由 libvterm 正确处理 bracketed paste；
- 提供只读 screen/transcript 导出或稳定快照 DTO；
- 添加 TermLink 的 ConPTY Golden Stream 回归测试。

## 7. Windows Terminal 配色兼容

### 7.1 可兼容的范围

Windows Terminal 官方配色 Scheme 包含：

- `foreground`、`background`；
- 可选 `cursorColor`、`selectionBackground`；
- 8 个普通 ANSI 色；
- 8 个 Bright ANSI 色。

官方格式见 [Windows Terminal Color Schemes](https://github.com/MicrosoftDocs/terminal/blob/main/TerminalDocs/customize-settings/color-schemes.md)。这些字段可以完整映射到 xterm.js。termlib 除独立 cursorColor 需要补丁外，其余核心字段可以映射。

True Color 输出，例如 `SGR 38;2;r;g;b` 和 `48;2;r;g;b`，直接使用输出指定颜色，不经过 16 色 Palette；xterm.js 和 termlib 都支持。

### 7.2 字段映射

| Windows Terminal | xterm.js `ITheme` | termlib |
| --- | --- | --- |
| `foreground` | `foreground` | `defaultForeground` + Compose `foregroundColor` |
| `background` | `background` | `defaultBackground` + Compose `backgroundColor` |
| `cursorColor` | `cursor` | 需要增加 `cursorColor` 参数；未修改时退化为 foreground |
| `selectionBackground` | `selectionBackground` | Compose `selectionBackgroundColor` |
| `black`～`white` | 同名，其中 `purple → magenta` | ANSI Palette 索引 0～7 |
| `brightBlack`～`brightWhite` | 同名，其中 `brightPurple → brightMagenta` | ANSI Palette 索引 8～15 |

Windows Terminal Scheme 没有单独定义 `selectionForeground`。xterm.js 中保持未设置即可；termlib 可默认使用终端前景色或根据背景计算高对比度颜色。

### 7.3 xterm.js 映射代码

```ts
function mapWindowsTerminalScheme(s: WindowsTerminalScheme): ITheme {
  return {
    foreground: s.foreground,
    background: s.background,
    cursor: s.cursorColor ?? s.foreground,
    cursorAccent: s.background,
    selectionBackground: s.selectionBackground,
    black: s.black,
    red: s.red,
    green: s.green,
    yellow: s.yellow,
    blue: s.blue,
    magenta: s.purple,
    cyan: s.cyan,
    white: s.white,
    brightBlack: s.brightBlack,
    brightRed: s.brightRed,
    brightGreen: s.brightGreen,
    brightYellow: s.brightYellow,
    brightBlue: s.brightBlue,
    brightMagenta: s.brightPurple,
    brightCyan: s.brightCyan,
    brightWhite: s.brightWhite,
  };
}
```

### 7.4 termlib 映射代码

```kotlin
val ansiPalette = intArrayOf(
    argb(scheme.black),
    argb(scheme.red),
    argb(scheme.green),
    argb(scheme.yellow),
    argb(scheme.blue),
    argb(scheme.purple),
    argb(scheme.cyan),
    argb(scheme.white),
    argb(scheme.brightBlack),
    argb(scheme.brightRed),
    argb(scheme.brightGreen),
    argb(scheme.brightYellow),
    argb(scheme.brightBlue),
    argb(scheme.brightPurple),
    argb(scheme.brightCyan),
    argb(scheme.brightWhite),
)

emulator.applyColorScheme(
    ansiColors = ansiPalette,
    defaultForeground = argb(scheme.foreground),
    defaultBackground = argb(scheme.background),
)
```

### 7.5 不属于 Scheme 的外观

以下 Windows Terminal 效果不能只靠 ANSI Palette 实现，应作为 TermLink Android 外观层配置：

- 背景图片；
- Opacity；
- Acrylic/Mica 模糊；
- 字体、字号、字重和行高；
- Tab、标题栏和 Pane 边框；
- 光标形状和闪烁策略；
- `intenseTextStyle` 对 Bold/Bright 的组合行为。

建议定义 TermLink 自己的 `TerminalAppearance`，其中 `colorScheme` 保持与 Windows Terminal JSON 兼容，其他外观字段独立扩展。

## 8. 配色同步流程

PC Agent 不应把整个 Windows Terminal `settings.json` 发到云端。推荐流程：

1. PC Agent 读取当前 Windows Terminal 稳定版/Preview 的用户配置；
2. 解析当前 TermLink Profile 的 `colorScheme`，并处理 light/dark 两套方案；
3. 从 `schemes` 中提取有效颜色；内置 Scheme 使用 TermLink 自带的兼容定义；
4. 规范化为 `TerminalThemeV1`，只保留颜色与显示字段；
5. 在 `SessionHello` 中端到端加密发送给 Android；
6. Android 验证全部颜色格式和字段大小后应用；
7. 用户切换主题时发送 `theme_changed`，不重建终端状态。

建议协议：

```text
TerminalThemeV1 {
  name
  foreground
  background
  cursor
  selection_background
  ansi_colors[16]
  bold_as_bright
  font_family_hint
}
```

主题值是会话表现数据，不应拥有执行脚本、加载 URL、加载字体文件或引用 PC 本地路径的能力。

## 9. 实时数据与终端引擎集成

### 9.1 输出方向

```text
ConPTY bytes
  → PC Agent: seq + timestamp + encrypted payload
  → Relay: opaque forwarding
  → Android SessionTransport: decrypt + reorder + deduplicate
  → TerminalEngine.feed(Uint8Array/ByteBuffer)
  → ACK highest contiguous seq
```

不要在 Relay 或 Kotlin 网络层把终端数据转换成普通字符串。输出可能包含分段 UTF-8、控制字符和二进制 OSC 数据；只有 TerminalEngine 负责解析。

### 9.2 输入方向

```text
Soft keyboard / hardware key / mouse / quick-key bar
  → TerminalEngine generates VT input bytes
  → Android checks input lease
  → encrypted InputEvent(inputId, bytes)
  → PC Agent deduplicates inputId
  → ConPTY input pipe
```

粘贴不能按“失败后自动重发整段文本”处理，否则可能重复执行命令。只有确认 PC Agent 从未接受该 `inputId` 时才允许重试。

### 9.3 快照与冷启动恢复

Android 本地引擎状态不能作为跨设备权威状态。建议分阶段实现：

- MVP：保存完整或有上限的加密原始事件流，Android 从会话开始快速批量回放；
- 稳定版：PC Session Host 同时维护一个轻量 libvterm 状态，周期性生成与行列尺寸绑定的规范化屏幕快照；
- Android 先应用快照，再应用 `snapshot_seq + 1` 之后的原始事件；
- xterm Serialize Addon 只用于同一手机的本地快速恢复，不作为 PC 与 Android 间协议。

## 10. 推荐的 `TerminalEngine` 接口

```kotlin
interface TerminalEngine {
    val inputEvents: Flow<ByteArray>
    val sizeEvents: Flow<TerminalSize>

    suspend fun feed(seq: Long, bytes: ByteBuffer)
    fun resize(columns: Int, rows: Int)
    fun applyTheme(theme: TerminalThemeV1)
    fun setReadOnly(readOnly: Boolean)
    fun clear()
    fun dispose()
}
```

搜索、选择、复制等渲染器能力通过单独的可选接口暴露，避免为了迁就某个引擎污染基础数据协议。

## 11. 实施顺序

### 阶段 A：兼容性回放测试

使用现有 ConPTY Probe 扩展录制以下会话：

- CMD 与 PowerShell 彩色表格；
- Windows PowerShell PSReadLine；
- WSL Vim、htop、fzf、less；
- 256 色表与 24 位渐变；
- 中文、emoji、组合字符、双宽字符；
- alternate screen、快速 resize 和鼠标模式。

将同一事件流分别输入 xterm.js 和 termlib，截取最终画面，与 Windows Terminal 参考截图做像素/网格状态对比。

### 阶段 B：Termux RemoteTerminalEngine

- Fork `terminal-emulator` 与 `terminal-view`，抽取 `TerminalSessionPort`；
- 实现 `RemoteTerminalSession`、单线程字节 feed 和增量刷新；
- Theme V1 与 Campbell/One Half Dark/Solarized 配色；
- 补选择色、Search、Unicode、OSC 8、焦点与任意鼠标移动；
- 输入租约、快捷键栏、复制和确认式粘贴；
- 断线重连、输出批处理和后台缓冲。

### 阶段 C：xterm.js 对照与回退

- 构建完全离线的 xterm.js WebView Engine；
- 用阶段 A 的录制数据运行同一套 Golden Test；
- 自动生成 Termux Fork 与 xterm.js 的网格、光标和颜色差异；
- 保留按会话切换引擎的开发开关和生产回退能力。

## 12. Termux Fork 默认上线门槛

Termux Fork 满足以下条件后成为生产默认；在此之前 xterm.js 作为回退：

1. Windows/WSL Golden Stream 的最终网格正确率不低于 99%；
2. Vim、htop、fzf、PowerShell PSReadLine 可连续操作 30 分钟；
3. 中文 IME、硬件键盘和 Ctrl/Alt 组合键无阻断问题；
4. 16 色与 True Color 测试完全通过；
5. 2 MB/s 输出持续 10 分钟无 OOM，P95 绘制延迟满足产品指标；
6. Mouse、bracketed paste、OSC 52 与链接策略符合安全要求；
7. 与 Termux 上游的差异有独立补丁集和回归测试，能够持续跟进上游更新。

## 13. 最终建议

TermLink 不需要等待或移植 Windows Terminal Android 版。最佳落地组合是：

```text
Windows: ConPTY Session Host
Transport: E2EE WebSocket byte stream
Android default: Kotlin/Compose + Termux terminal-emulator/terminal-view fork
Android compatibility oracle/fallback: offline xterm.js WebView
Android native comparator: connectbot/termlib
Theme: Windows Terminal Scheme → TerminalThemeV1 → renderer adapter
```

该方案以原生 Android 的长期完整度为主目标。Termux Fork 负责最终体验，xterm.js 负责兼容性校准与回退，从而避免单一引擎缺陷阻断远程会话。
