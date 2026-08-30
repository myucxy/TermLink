# Terminal Core 通用 CLI/TUI 开发路线图

更新日期：2026-08-30
目标：将 `terminal-core` 建设为可供 TermLink Windows、Android 及后续平台共同使用的通用终端状态核心，稳定兼容主流 CLI/TUI 应用。

## 1. 文档定位

本文是后续开发的主入口，用来记录优先级、依赖、完成标准和接续位置。协议级缺口的详细设计继续记录在
[`UNIMPLEMENTED_CAPABILITIES.md`](UNIMPLEMENTED_CAPABILITIES.md)，已实现 ABI、事件和 Snapshot 契约以
[`README.md`](README.md) 及公共头文件为准。

维护规则：

- 开始工作前先读“当前接续点”，再检查工作区状态，不覆盖其他人尚未提交的修改。
- 一个项目只有在 core、FFI、绑定、渲染/宿主和测试形成闭环后才能标记为完成。
- 每次完成工作后更新状态、验证证据和“下一步”，让下一次会话可以直接继续。
- 跨目标 `cargo check` 只代表可编译，不代表对应设备或平台已通过生产验收。
- 对外提示和错误遵守产品双语要求；core 保持稳定状态码/消息键，平台负责中英文呈现，不能把本地化文本写进协议字节。

状态标记：`[ ]` 未开始、`[~]` 进行中、`[x]` 已验证完成、`[!]` 阻塞或需要决策。

## 2. 目标边界

`terminal-core` 不负责启动或执行命令。shell、程序和 PTY/ConPTY 由平台 runtime 管理；core 的职责是：

- 解析 PTY 输出字节并维护确定性的终端状态。
- 将语义化键盘、鼠标、粘贴和焦点事件编码为输入字节。
- 生成需要宿主执行的有序 Host Effect，例如 PTY query reply。
- 导出可版本化的 Snapshot、Render Frame、History Page 和 Delta。
- 为 Windows、Android 及后续平台提供一致的状态、字符宽度和资源生命周期语义。

因此“支持所有命令行工具”的工程定义是：主流文本 CLI/TUI 在声明的兼容 profile 下行为正确；超出能力矩阵的协议必须明确拒绝或安全降级，不能静默假装支持。

首批必须持续回归的应用包括：

- Shell：PowerShell、`cmd.exe`、bash、zsh、fish。
- 编辑器/分页器：Vim/Neovim、nano、less、more。
- 全屏 TUI：tmux、screen、fzf、top/htop、Git TUI。
- 常用输出：Git、编译器、测试框架、进度条、彩色日志、OSC 8 hyperlink。

## 3. 当前基线

### 已进入生产主链

- `[x]` Windows `SessionHost` 使用 terminal-core authority，有序应用 durable ACK 后的事件，并将 Host Effect 写回 PTY。
- `[x]` Android 使用 native terminal engine 和 Render Frame V3。
- `[x]` 确定性事件序列、gap fail-closed、批量原子应用、Snapshot V2 系列、Render Frame 和语义输入已有生产基础。
- `[x]` TL-H115 的长历史产品需求由 Windows durable Transcript archive 与服务端分页解决。

注意：TL-H115 的产品方案不等于 core 已具备公共 History Page API；独立复用 core 时，内部 runtime scrollback 仍然有界。

### 当前工作区中的开发批次

- `[~]` Snapshot V2.9。
- `[~]` XTVERSION canonical reply。
- `[~]` `CSI 14t` 有界兼容处理。
- `[~]` private modes `2027`、`2031`。
- `[~]` OSC `default` reset。

这些改动目前位于 `host_effect.rs`、`lib.rs`、`modes.rs`、`osc.rs`、`snapshot.rs`、`tests.rs`。在完成审查和完整测试前，不得写成已发布能力。

## 4. 当前接续点

下次打开仓库时，从工作项 `W0` 开始：

### W0：收口当前开发批次

- `[ ]` 复核上述六个已修改 Rust 文件的 diff，确认没有覆盖交叉工作。
- `[ ]` 检查 Snapshot V2.9 的 canonical bytes、feature bits、旧版本 byte stability 和损坏输入拒绝。
- `[ ]` 检查 XTVERSION、OSC `default`、`CSI 14t`、modes `2027/2031` 的协议行为及 Host Effect 顺序。
- `[ ]` 为成功、分块输入、非法参数、状态回滚、Snapshot round-trip 增补测试。
- `[ ]` 运行格式、静态检查和 workspace 测试，并记录实际命令、平台和结果。
- `[ ]` 同步 README、公共 C 头文件及绑定说明；确认是否需要 Android/.NET decoder 变更。

完成标准：当前批次所有测试通过，无 ABI 破坏，旧 Snapshot/Render Frame 的既有字节保持稳定，Windows/Android 需要的绑定变更均已验证。

建议验证命令（构建前按 `D:\Dev\DEV_ENV_MANIFEST.json` 和项目约束确认使用既有固定版本工具）：

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

## 5. 分阶段实施计划

### P0：主流文本 CLI/TUI 通用兼容

这是把 terminal-core 定义为“通用文本终端核心”的最低完成线。

#### P0.1 终端身份与能力矩阵

- `[ ]` 建立 xterm、VT220/VT320、VTE、Windows Terminal/ConPTY、Kitty 和 tmux/screen 的行为对照矩阵。
- `[ ]` 定义少量、明确、可版本化的 terminal profiles，禁止把互相矛盾的能力拼成一个 profile。
- `[ ]` 记录每个 profile 的 DA1/DA2、XTVERSION、XTGETTCAP、键盘、鼠标、颜色和 OSC 能力。
- `[ ]` 由 runtime 根据 profile 一致设置 `TERM`、`COLORTERM` 和 terminfo；core 只上报真实实现的能力。
- `[ ]` 未支持能力采用稳定的 unsupported/fail-closed 行为。

完成标准：每个 profile 都有机器可读能力表和探测测试，应用根据 `TERM`/query 得到的能力与实际行为一致。

#### P0.2 Query 与 Host Effect 完整性

- `[ ]` 实现 `XTQMODKEYS` 的真实 canonical reply。
- `[ ]` 补齐目标 profile 所需的 DECRQSS、XTGETTCAP、window/title/icon query。
- `[ ]` 定义 OSC clipboard、通知、打开链接和 window manipulation 的授权策略。
- `[ ]` 为未知 query、拒绝、超限、超时和重复请求定义确定行为。
- `[ ]` 保证所有 PTY reply 与输入、输出事件按 source key 串行，恢复重放不会重复产生不可幂等外部操作。

完成标准：能力矩阵中的 query 全部有成功/拒绝/分块/恢复测试；有权限影响的操作只能通过受控 Host Effect。

#### P0.3 Unicode、grapheme 与字符宽度

- `[ ]` 实现完整 Unicode grapheme cluster segmentation。
- `[ ]` 正确处理 combining mark、variation selector、emoji ZWJ、skin tone 和区域旗帜。
- `[ ]` 固定 Unicode/emoji/width 数据版本，并在 Snapshot/profile 中记录影响恢复的版本信息。
- `[ ]` 定义 CJK ambiguous-width 策略，确保 Windows、Android 和服务端使用相同结果。
- `[ ]` 保证宽字符和 grapheme 在 wrap、erase、insert/delete、resize/reflow 中不会被拆坏。
- `[ ]` 评估并实现 bidi；在完成前明确能力边界和降级显示规则。

完成标准：Unicode corpus、随机编辑和跨平台 golden frame 通过，主流 prompt、表格、emoji 和 TUI 边框无光标漂移。

#### P0.4 输入协议

- `[ ]` 验证并补齐 Kitty Keyboard、CSI-u、modifyOtherKeys、application cursor/keypad。
- `[ ]` 验证 bracketed paste、focus events、SGR/URXVT/X10 mouse。
- `[ ]` 统一 Ctrl/Alt/Meta、Win32 key event、UTF-16 surrogate、IME 和组合输入。
- `[ ]` 为大段粘贴、repeat、make/break、无效语义事件和 backpressure 建立边界。
- `[ ]` profile 只能声明已经通过编码和真实应用验证的输入协议。

完成标准：Windows/Android 对同一语义输入产生相同字节；Vim、tmux、fzf 和 shell 编辑操作的实测矩阵通过。

#### P0.5 PTY/ConPTY runtime 契约

该项横跨 core、Windows agent、Android transport 和未来 Unix runtime，不把平台职责错误塞入 Rust parser。

- `[ ]` 固化 byte-clean output、stdout/stderr 顺序、resize、input、PTY reply 和 EOF/exit/signal 的顺序契约。
- `[ ]` 固化 durable ACK、source sequence、gap recovery、断线重连和 backpressure 规则。
- `[ ]` Windows ConPTY 与 Unix PTY 的差异由 adapter 明确处理。
- `[ ]` 禁止中间层按行拆分、隐式转码或把二进制 PTY 数据当普通 JSON 文本处理。
- `[ ]` 建立进程退出、异常断开、快速 resize、慢消费者和大输出 soak test。

完成标准：相同 event journal 在所有平台恢复到相同 canonical Snapshot；不会因传输层改变字节或事件顺序。

#### P0.6 兼容与差分测试体系

- `[ ]` 引入公开规范和行为样本作为测试依据，不复制外部实现源码。
- `[ ]` 建立 vttest、真实应用脚本、golden stream 和跨终端差分测试。
- `[ ]` parser 对任意字节、任意事件分块、CAN/SUB/ST、中断与截断进行 fuzz/property 测试。
- `[ ]` 为每个修复保存最小复现流，避免兼容性回归。

完成标准：P0 应用矩阵在 Windows x64、Android ARM64 真机至少各通过一次完整 E2E；模拟器结果单独记录，不能代替真机。

### P1：长会话、历史与恢复

#### P1.1 History Page V1

- `[ ]` 定义 immutable capture token、`as_of_revision` 和 exclusive anchor。
- `[ ]` 以 sealed logical line 分页，定义 before/after、`has_more`、head truncated、eviction 和 stale token。
- `[ ]` 定义 resize/reflow、alternate screen、ED3、Snapshot 恢复和并发输出的历史语义。
- `[ ]` 设置页行数、cell、UTF-8 字节、活跃 token 和 capture 生命周期硬上限。
- `[ ]` 提供 additive C FFI、严格 .NET/Android decoder 和 buffer ownership。
- `[ ]` 明确产品 Transcript archive 与 core runtime History Page 的分工、拼接和降级路径。

完成标准：同 token/anchor 重试得到 byte-identical 页面；多页拼接无重复、遗漏或孤立 wide continuation。详细标准见 `UNIMPLEMENTED_CAPABILITIES.md`。

#### P1.2 Resize/Reflow 与锚点

- `[ ]` 分离 logical line 与 visual row。
- `[ ]` 在连续 resize 中稳定迁移 cursor、selection、viewport 和 history anchor。
- `[ ]` alternate screen 不污染主屏历史。
- `[ ]` 覆盖 margins、origin mode、scroll region、wide cell 和 pending grapheme。
- `[ ]` 明确应用自行重绘与 core reflow 的边界。

完成标准：Vim、less、tmux 和长软换行文本在连续 resize、恢复及分页后保持确定结果。

#### P1.3 Snapshot 完整恢复

- `[ ]` 审计所有影响后续解析的状态是否进入 Snapshot：parser、双屏、cursor、margins、modes、tabs、charset、palette、hyperlink、pending grapheme 和输入协议。
- `[ ]` 后续资源能力加入后覆盖 DRCS/image store/placement。
- `[ ]` 建立版本迁移、旧版本读取、未知 required feature 和损坏输入策略。
- `[ ]` 建立 Snapshot + 连续 journal 重放的跨版本测试。

完成标准：任意支持状态在导入后继续输入，与不中断执行产生 byte-identical canonical Snapshot 和 Render Frame。

### P2：高效远程同步

#### P2.1 Delta V2

- `[ ]` 定义 `base_revision`、`target_revision` 和严格连续性。
- `[ ]` 建立有界 dirty journal。
- `[ ]` 为 line、rendition、hyperlink、DRCS/image 等资源定义稳定 ID 和 tombstone。
- `[ ]` canonical 表达 scroll、replace、resize/reflow、alternate screen 和 hard reset。
- `[ ]` 重复 delta 幂等，乱序、跨 terminal、缺失引用和 gap 必须拒绝并要求 full resync。
- `[ ]` 提供 Snapshot + Delta 自闭合恢复链和 additive FFI/binding。

完成标准：对所有受支持事件满足：

```text
apply(snapshot@S, delta(S..N)) == snapshot@N
```

并通过重复、乱序、缺失、CRC/版本损坏、journal 淘汰和 full-resync 测试。

#### P2.2 弱网与性能验收

- `[ ]` 测量全量 frame、Snapshot、Delta 在典型 shell/TUI/日志负载下的大小、CPU 和延迟。
- `[ ]` 验证断线、重复包、乱序、慢客户端和多次 resync。
- `[ ]` 设置 revision window、payload、记录数、缓存和内存硬上限。

完成标准：Delta 在不牺牲确定性和恢复正确性的前提下，相对全量传输取得明确收益；无收益的场景允许安全回退全量。

### P3：现代图像和动态资源

#### P3.1 共用资源层

- `[ ]` 设计 renderer-neutral image/glyph store、稳定 ID、generation、引用计数和确定回收。
- `[ ]` 定义 placement、cell/pixel anchor、crop、z-order、scroll、erase、alternate-screen 生命周期。
- `[ ]` 资源 mutation 与 terminal revision 原子提交。
- `[ ]` 定义 Snapshot、Delta、Render resource frame 的 chunk、CRC、压缩和 resync。
- `[ ]` 建立像素、尺寸、压缩比、总内存和解压预算。

#### P3.2 DRCS/DECDLD

- `[ ]` 完成严格 DCS parser、glyph bank、charset 映射、Snapshot/Delta 和跨平台 glyph renderer。
- `[ ]` 覆盖替换、擦除、超限、分块、取消和 batch 原子回滚。

#### P3.3 SIXEL

- `[ ]` 实现流式 rasterizer、repeat、raster attributes、color register、画布增长和 placement。
- `[ ]` 覆盖透明/背景、cursor、scroll、margin、erase/reset 和 malformed/超限输入。

#### P3.4 Kitty graphics

- `[ ]` 实现 APC chunk assembly、image/placement ID、transmit/display/query/delete 和 reply。
- `[ ]` 明确 direct/file/shared-memory 支持矩阵；portable core 不直接读取未经宿主授权的路径或共享内存。
- `[ ]` 覆盖 base64、可选压缩、quiet mode、重复 ID、取消、超时和删除生命周期。

P3 完成标准：协议、状态、Snapshot/Delta、Windows/Android renderer、资源回收和安全边界全部闭环。只识别 introducer 不算支持。

### P4：扩展及 Legacy 兼容

- `[ ]` OSC 7、8、9/99、52、shell integration marks、progress、palette/default-color 的目标能力矩阵。
- `[ ]` 完整 8-bit C1/GR 和需要支持的 ISO-2022 charset family。
- `[ ]` DEC protected cells、selective erase 及目标 VT3xx/VT4xx 行为。
- `[ ]` 调研低使用率 printer/controller/Tektronix 等模式，依据真实产品需求决定实现或明确不支持。

完成标准：每项均由兼容矩阵和真实使用场景驱动；不能为了宣称“全协议”引入未经闭环验证的半实现。

## 6. 横向生产要求

以下要求适用于每个阶段，而不是最后再补：

### ABI 与兼容

- 公共结构 additive versioning，保留旧结构大小、符号和既有 canonical bytes。
- 新 required feature 必须被旧 decoder 明确拒绝，optional feature 必须能够安全忽略。
- FFI panic containment、同库 buffer ownership、空指针/短结构/未知枚举测试保持完整。

### 安全与资源边界

- 所有参数、字符串、行、cell、历史、图片、字形、chunk 和压缩输入都有硬上限。
- 防止 OSC/escape injection、OSC 52 越权、任意文件访问、共享内存越权和压缩炸弹。
- 分配失败、超限、malformed 和 panic 不得提交半个事件或产生部分 Host Effect。

### 性能与稳定性

- 保留 parser throughput、frame/snapshot 大小、峰值内存、长历史和 resize 基准。
- 建立大输出、慢消费者、长时间 TUI、频繁 Snapshot/Delta 的 soak test。
- 优化不能改变 canonical 状态或绕过 fail-closed、revision 和原子性约束。

### 平台验收

- Windows x64：ConPTY、native FFI、renderer、恢复链 E2E。
- Android ARM64 真机：JNI/NDK、native FFI、renderer、触摸/IME、恢复链 E2E。
- Windows ARM64、Linux、macOS：构建通过后仍需真实 runtime/renderer 验收，分别记录状态。
- 字体和平台绘制差异不能反向污染 core 的 cell、width 或资源语义。

### 双语提示与错误

- core 公共接口优先返回稳定 status、错误类别、消息键和可安全显示的参数。
- Windows/Android 将用户可见提示同时提供中文和英文，且两端使用相同消息键。
- PTY reply、Snapshot、Delta 和 Render Frame 不携带本地化诊断文本。
- 每个新增错误路径至少验证状态码、中文、英文和无敏感信息泄漏。

## 7. 里程碑与发布门槛

| 里程碑 | 范围 | 可对外描述 | 发布门槛 |
|---|---|---|---|
| M0 | W0 | 当前兼容批次完成 | core/FFI 测试、ABI 审查、双端影响复核完成 |
| M1 | P0 | 主流文本 CLI/TUI 通用生产核心 | 能力矩阵、真实应用、Windows x64 与 Android ARM64 E2E 通过 |
| M2 | P1 | 可独立承载长会话和分页恢复 | History Page、reflow、Snapshot 恢复闭环通过 |
| M3 | P2 | 高效可靠的远程增量核心 | Delta 连续性、幂等、resync、弱网和性能验收通过 |
| M4 | P3 | 支持现代终端图像/动态字形 | 共用资源层和双端 renderer 全闭环 |
| M5 | P4 | 扩展与 Legacy 兼容 | 每项由能力矩阵和真实用例独立验收 |

M1 是“通用文本终端核心”的最低完成线；M4/M5 决定现代图像和长尾协议的覆盖程度。任何里程碑都不使用“支持所有终端协议”这种无法验证的表述，应发布具体 profile、能力矩阵和平台测试证据。

## 8. 每次开发后的记录模板

在本文件“当前接续点”或单独的提交/验证记录中追加：

```text
工作项：
状态：[ ] / [~] / [x] / [!]
修改范围：
协议/ABI/Snapshot 影响：
Windows/Android 影响：
验证命令与结果：
真实设备/应用证据：
已知限制：
下一步：
```

若某项只完成 parser，但尚未完成状态、Snapshot、FFI、renderer 或 E2E，应保持 `[~]`，并明确未闭环的层级。
