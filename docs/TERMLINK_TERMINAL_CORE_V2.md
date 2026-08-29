# TermLink 自研跨平台终端核心 V2

## 定位与边界

TermLink Terminal Core V2 是仓库内完全自研的终端状态机。实现位于 `terminal-core/`；它不复制
Windows Terminal（包括 `terminal-main/TerminalCore`）、termForward 或其他平台终端的代码，不链接
这些实现，编译和运行也不依赖它们。Windows Terminal、xterm 等外部实现可以作为公开行为的差分
测试对象，但差分测试不构成完全兼容声明。

核心只接收宿主提交的有序 source event，不直接拥有 ConPTY、POSIX PTY、网络、账号或加密：

```text
Windows ConPTY / POSIX PTY / fixture
                  |
                  v
        durable ordered journal
                  |
                  v
        TermLinkTerminalCore
                  |
                  v
       Snapshot V2 / render adapter
```

宿主必须先把事件写入权威 journal，再把同一事件交给核心。核心不能在 journal 持久化位置之前
推进 `next_source_sequence`。核心不读取系统时钟；`?2026` 的一秒超时由 authority host 把单调
计时器转换为同一个可持久化、可重放的 `ADVANCE` event，Windows authority、POSIX authority 和
mirror 重放相同事件，不能由 Android mirror 按本地时钟独立释放。

## 源码结构

```text
terminal-core/
  crates/termlink-terminal-core/src/
    parser.rs       ESC/CSI/OSC/DCS/SOS/PM/APC 状态机
    charset.rs      G0-G3、GL/GR、SS2/SS3 和字符映射
    osc.rs          title、palette、default color、hyperlink 状态
    screen.rs       双屏、cell、reflow、运行时 scrollback 和矩形操作
    snapshot.rs     sectioned Snapshot V2.0..V2.8 编解码
    render_frame.rs renderer-only Render Frame V1/V2/V3 编码
    host_effect.rs  有序 Host Effect Frame V1 编码
    input.rs        语义输入编码
  crates/termlink-terminal-ffi/src/lib.rs
                    panic boundary、opaque handle 和稳定 C ABI
  include/termlink_terminal.h
                    C ABI 声明
```

`win-agent/src/TerminalCore/NativeTerminalCoreV2.cs` 是 Windows SafeHandle/P/Invoke 绑定，已经真实接通
Config V3/output profile、Render Frame V2/V3、Host Effect Frame V1 与 Input V2 的 ABI discovery、
effect-aware apply、Win32 key 编码、buffer free 和严格 .NET decoder。`SessionHost` 现在只在 Agent
durable ACK 后按 common sequence apply，并把 Host Effect PTY reply 与用户输入放入同一串行顺序域。
Android 各 ABI JNI `.so`、严格 native bridge、自研 View 与连续历史重建也已接入 production。

Windows 历史帧链仍保留按会话隔离的 terminal-core 影子重放，作为非权威差分诊断。影子在有界的异步 per-session 队列上导出 Snapshot V2
和 Render Frame V3，不在 production history 锁内执行 native checkpoint/export。每会话上限为 8 个
pending work item 和 16 MiB conservative managed-retention estimate；后者不是 payload、wire、native
heap 或精确 CLR heap 上限，而是显式估入 cloned payload/frame/array 与排队 continuation 持有的
immutable Portable snapshot graph。超限只退休该会话影子，不给 production history 施加反压。

差分覆盖 sequence、dimensions、active-screen 标识，以及当前活动屏的 cursor/style、可见
grapheme/width/soft-wrap、rendition、palette override 与可见 hyperlink。`SHADOW_MATCH` 只表示这组
活动可见呈现和已列 metadata 相符，不代表整个 terminal state 等价；hidden state、hidden scrollback
和 inactive screen 仍未完成等价比较。结构化 Title frame 是 REST metadata authority，不能伪装成
OSC title，因此报告 `SHADOW_MATCH_LIMITED/TITLE_OUT_OF_BAND`；native frame 无法解析 Windows
Terminal theme palette 的默认色或槽位时，即使已知部分相符，也报告
`SHADOW_MATCH_LIMITED/PALETTE_THEME_UNRESOLVED`。

影子异常、缺失 DLL、ABI/decoder 失败或 queue overload 均不会影响 ConPTY、Relay 或旧历史上传。
retire 会停止接受新 work、把 native dispose 排在已有 per-session tail 后，并保留 dictionary tombstone，
防止 late history retry 创建第二个 native generation；只有最终 history durable release 才删除 dispatcher
tombstone 和 Portable projection，producer-lifetime released-session tombstone 仍阻止迟到任务。由于
native call 与 observer 都在进程内，若其中之一永久阻塞，排在 tail 后的 release 也无法执行，只能在
进程退出时由 OS 回收；若要提供可强制终止的 hard isolation，后续必须改为 out-of-process worker。
Android live production 产品链使用 native terminal-core engine；Termux-derived engine 仅保留 Harness、
旧归档 CHUNK 查看与差分测试，不承担 live renderer、ACK 或恢复 authority。

Windows 影子默认启用，可把进程变量 `TERMLINK_ENABLE_TERMINAL_CORE_SHADOW` 设为 `0` 或 `false`
关闭。状态变化写入
`%LOCALAPPDATA%\TermLink\logs\TermLink-terminal-core-shadow.jsonl`。Setup 与 MSIX 构建会使用固定
Rust 1.97.1 编译、签名并携带 `TermLink.TerminalCore.dll` 与 `termlink_terminal_ffi.dll`；安装验证同时
检查两层 DLL 的存在、签名集合和签名计数。

rollback 变量按 trim 后的值解析：`0` 或大小写不敏感的 `false` 关闭；未设置、空值和其他值均保持
默认启用。该开关只控制诊断影子，不改变 SessionHost terminal-core production authority。

## Windows Phase 2 authority（已实现）

Phase 2 把 `NativeTerminal` owner 放在拥有对应 ConPTY 的 `SessionHost`，而不是 Agent
history worker。每个 common `LocalFrame` 必须先由 Agent 写入 durable journal 并得到 durability ACK；
SessionHost 只能在收到该 ACK 后，按同一个 sequence 把同一 frame apply 到 native terminal。authority
apply 使用 Host Effect Frame，并把其中有序的 PTY reply 写回同一个 ConPTY。

当前实现已经完成并验收以下门禁：

- 明确定义并 crash-test 以 `Flush(true)` 为边界的 journal durable ACK，不能把“已入内存队列”当作
  durable；
- 解决 Windows Terminal 与 terminal-core 对 DA/DSR/其他 query 同时响应造成的双回复，确保只有一个
  PTY reply authority；
- 为 common sequence 的缺失定义 `Gap` fail-closed 后的 checkpoint/resync/重建策略，不能让有毒实例
  继续驱动 live ConPTY；
- 把用户/远程 input write 与 Host Effect V2 reply write 放进同一个 per-session 串行顺序域，避免字节
  交错或逆序。

`PortableTerminalModelV1` 和 Phase 1 shadow 继续保留为兼容/差分工具，但不再推进 durable ACK，
也不再决定 Snapshot/Transcript 或实时终端状态。

## 当前实现矩阵

| 能力 | 当前实现 | 明确边界 |
| --- | --- | --- |
| 事件合同 | `OUTPUT`、`RESIZE`、`ADVANCE` 每次成功提交恰好消费一个连续 source sequence；单事件与 1..256 事件 batch 都原子提交；`GAP` 和向前跳号 fail closed | 旧序号只拒绝、不毒化实例；单个 Output 上限 1 MiB，batch Output 总量上限 4 MiB |
| Parser | UTF-8、ESC、CSI、OSC、DCS 可跨任意事件分块；7-bit SOS/PM/APC 以不缓存 payload 的常量状态忽略到 ST；显式 standalone-C1 profile 在 UTF-8 scalar boundary 识别 13 个 one-byte alias（IND/NEL/HTS/RI/SS2/SS3/DCS/SOS/CSI/ST/OSC/PM/APC） | 默认 UTF-8 profile 不解释 raw standalone C1；其他 C1 control fail closed；UTF-8 编码的 C1 scalar 不会被重解释；BEL 只终止 OSC，在 DCS/SOS/PM/APC 内保持 inert；DCS/SIXEL 等能力仍受各自实现边界约束 |
| Screen / scrollback | 主/副屏、cursor/saved cursor、宽字符和 continuation cell、有限 combining/ZWJ、primary reflow、alternate crop/pad；DECLRMM 激活时 primary resize 改用 crop/pad；主屏可配置有界运行时 scrollback，存储布局为 `[history prefix][visible suffix]`；row metadata 保留 logical line ID/offset、sealed revision 和 head-truncated | history 容量单位是物理行，不是 bytes；副屏、局部 margin、IL/DL/RI/`CSI T` 不进入 history；尚无对外 History Page API |
| 编辑与 mode | 常用 cursor/edit/erase/scroll CSI；ED3 只清 hidden runtime history；标准 erase blank 只保留当前前景/背景色；ED/EL/ECH/ICH/DCH/IL/DL 与 DECAWM reset 取消 delayed-wrap；REP `CSI Ps b`、DECSCUSR `CSI Ps SP q`、DEC synchronized output `?2026h/l`、DECSTBM、DECLRMM `?69h/l`、DECSLRM `CSI Pl;Pr s`、IRM、DECOM、DECAWM、BCE、动态 tab；`?47/?1047/?1048/?1049` alternate/save/restore；application cursor/keypad、newline、bracketed paste、focus；xterm `modifyOtherKeys` level 0..3；mouse tracking 9/1000/1002/1003 及 encoding 1005/1006/1015/1016 | ED2 只清 visible display，尚无 Windows 特有的 history 清理/viewport 语义；`?2026` 只延迟 presentation，必须由宿主提交 journaled `ADVANCE` 驱动超时；DECSCUSR 只接受单个缺失值或 `0..6`；DECLRMM 关闭时带参数的 `CSI s` fail closed；mouse pointer/held 状态由调用方在每个语义事件中完整提供，不进入 Snapshot |
| Rendition | SGR 有序支持 bold/faint、italic、blink、inverse、conceal、strikethrough、overline，underline 有 none/single/double/curly/dotted/dashed 六态，foreground/background/underline color 均支持 indexed/RGB；38/48/58 同时接受分号与 colon 颜色形式；BCE、矩形属性、Snapshot V2.4 与 Frame V3 保真保存 | V1/V2 Frame 对扩展属性执行确定性降级；未知、非法或不完整 SGR fail closed，整事件/批次原子回滚，不做近似 |
| Charset | G0-G3 designation、GL/GR invocation、SI/SO、SS2/SS3、ASCII、DEC Special Graphics、ISO Latin-1 supplemental，以及 12 个 Western NRC；`?42h/l` 控制 NRC replacement；活动及 saved charset 状态进入 Snapshot V2 | designation/invocation 与 `?42` 独立，mode off 时 NRC designation 按 ASCII 映射；普通输出仍按 UTF-8 解码；GR 目前不作为 `0xA0..0xFF` 的 8-bit print 通道；DRCS 与其他未知 designation fail closed |
| OSC / title | OSC title、palette、default foreground/background/cursor color、OSC 8 hyperlink，以及 CSI 22/23 `t` 的统一 10 槽 title stack 均为有界持久状态并进入 Snapshot V2 | 查询只分类、不合成 host reply；OSC 22/23 不是 title stack；未知 selector 不改变状态；格式错误或有状态资源上限不能静默近似 |
| DEC 矩形 | DECERA、DECSERA、DECFRA、DECCRA、DECCARA、DECRARA、DECSCA 和 DECSACE 子集 | DECCRA 当前只支持单页复制；DECCARA/DECRARA 只处理当前支持的 rendition 位，未列出的矩形命令不应据此推断为已实现 |
| Snapshot V2.0..V2.8 | canonical little-endian、固定 header/critical directory、双 CRC32C、完整双屏/charset/OSC/mode/margin/tab 状态；NRC 状态选择 feature `0x8_0000` 的 V2.7（64 profiles），standalone-C1 output profile 选择 feature `0x10_0000` 的 V2.8（128 profiles）；当前源码推导上界为 44,568,090 bytes，导入硬上限 64 MiB | 默认 UTF-8 profile 继续选择最低可保真的 V2.0..V2.7；所有 history-enabled profile 只投影可见双屏，不携带隐藏 history cells 或 sealed revision；parser/UTF-8 非 quiescent 或 `?2026` active 时不可导出；不是 V1 DTO 的封装 |
| Render Frame V1/V2/V3 | 三者均为 renderer-only canonical little-endian、96-byte header、6 sections、双 CRC32C；V1/V2 API/bytes 冻结；V3 使用 `TLRFRM3\0`、required features `0x7f`、rendition section v2 和不扩大的 16-byte record 保真保存扩展 SGR；硬上限均为 16 MiB，精确最坏值均为 13,395,208 bytes | V1/V2 丢弃 faint/overline/underline color，并把所有非 none underline 降为 single 后重新 canonical sort/dedup/remap；隐藏 history 不进入 table/row/cell；三版严格隔离，不能导入恢复；当前每次导出 full frame，没有 damage/delta |
| C ABI / 输入 | native ABI 版本仍为 1；Config V1/V2 保持 UTF-8 profile，88-byte `TlTerminalConfigV3`/`tl_terminal_create_v3` 同时选择 history 与显式 output profile；Frame/Host Effect/输入保持 additive versioned surface 与同库 buffer free | Config V3 要求 exact `struct_size == 88`、全部 reserved 为 0、history 不超过 `u32::MAX`，且 profile 只能是 UTF-8 或 UTF-8-with-standalone-C1；raw-input 占位入口与 Delta 导出入口返回 `TL_STATUS_UNSUPPORTED` |
| .NET binding | `ValidateAbi()` 增加 Config V3 的 88-byte discovery；显式 overload 通过 `tl_terminal_create_v3` 选择 history 与 `NativeTerminalOutputProfile`，旧 overload 继续走 V1/V2 UTF-8；真实 DLL 验证覆盖未知 profile 拒绝、两种 profile 对 raw C1 OSC/ST 的差异及 Snapshot 恢复 | `SessionHost` authority 复用该严格 binding；诊断 shadow 和 Portable V1 仍不得据此推进 durable ACK |
| 平台接入 | Rust core、C ABI、Windows P/Invoke/SessionHost authority 与 Android JNI/native View 已接入；win-x64 release DLL 和四个 Android ABI `.so` 均已从制品加载并通过运行门禁，Linux/macOS aarch64 通过 non-host `cargo check` | Linux/macOS 仍未发布产品 runtime；Android/Windows 遇到 ABI、连续性、decoder 或 Gap 异常时 fail closed，不回退成有损 authority |

“实现”只表示上表所列的有界子集，不表示完整 VT、xterm 或 Windows Terminal 兼容。仓库内自有
ConPTY capture 已全部进入默认测试并能完整解析/应用而不 fail closed；它们是 acceptance regression，
不是像素或全状态差分 oracle。

## 运行时 scrollback

`TerminalOptions.history_capacity_rows` 和 C ABI `TlTerminalConfigV2.history_capacity_rows` 配置主屏
运行时 history。该字段统一表示“物理终端行数”，不是内存 bytes、allocator 容量或逻辑行数。默认值
为 0，因此旧 Rust constructor、40-byte `TlTerminalConfigV1` 和 `tl_terminal_create` 保持原行为。
单屏最多请求 10,000 个 history rows，同时必须满足含可见区在内的 `MAX_CELLS = 262,144` cell
上限；有效容量为请求值、10,000 行和当前列数推导的 cell 上限三者中的最小值。FFI 先拒绝大于
`u32::MAX` 的输入，超过 10,000 但仍可表示的请求由核心和 Snapshot wire 归一化为 10,000。

主屏内部存储满足：

```text
stored rows = [history prefix][visible suffix]
viewport_top = history rows
visible cells = rows * columns
```

公开 `Terminal::cell(row, column)` 仍按可见 viewport 坐标访问。只有处理 `OUTPUT` 时由全 viewport
向上滚动离开的主屏行才会进入 history，包括 LF/autowrap 和覆盖全 viewport 的 `CSI S`。副屏始终
没有 history 容量；局部 vertical margin、IL/DL、RI、`CSI T` 以及其他可见区编辑既不追加也不
改写 history prefix。达到容量时先淘汰再 append，不产生超容量瞬时峰值。

物理 row 通过单调、checked、不可复用的 logical line ID 和 cell offset 关联。若淘汰切断了一个
soft-wrapped logical line，首个保留 fragment 继续使用原 ID/offset 并设置 `HEAD_TRUNCATED`。一个
硬终止 logical line 完全退出 viewport 后，所有仍驻留 fragment 使用触发输出事件提交后的 source
revision 完成 seal。单个事件和 batch 失败会一起回滚 history、seal revision 和 logical-ID frontier。
resize/crop/reflow 在容量允许范围内保留 history 边界、cursor/saved cursor 和 logical anchor。
DECLRMM 激活时，活动主屏 resize 使用确定性的 crop/pad，不把局部左右边距产生的 wrap 错当成整行
logical line 参与 reflow；副屏始终使用 crop/pad。若主屏冻结在 alternate screen 后方，则切回时按届时的
DECLRMM 状态选择 crop/pad 或 reflow。resize 会截断自定义边距；任一自定义轴被截到不足两个 cell 时，
该轴规范化为 resize 后的全屏范围。resize、mode/margin 改变、restore 和主副屏切换后都会按对应行的
有效右边界重新校验 current/saved pending-wrap。

这是核心内部的有界运行时存储，不是 durable transcript。当前 checkpoint 没有冻结隐藏 history，
也没有 immutable history token、`as_of_revision`、exclusive anchor、每页最多 200 个完整 sealed
logical lines、`has_more_before` 或 stale-token 语义；这些属于尚未实现的 History Page V1。

## C ABI Config V2/V3 与 .NET binding

增加 scrollback 没有提升 `TL_TERMINAL_ABI_VERSION`，其值仍为 1。Config V2 是新的 additive create
入口，不是改变旧结构含义：

| 结构/入口 | 固定已知大小 | history / output profile 行为 |
| --- | --- | --- |
| `TlTerminalConfigV1` / `tl_terminal_create` | 40 bytes | history 固定为 0；output profile 固定 UTF-8 |
| `TlTerminalConfigV2` / `tl_terminal_create_v2` | 64 bytes | 显式 `history_capacity_rows`；output profile 固定 UTF-8 |
| `TlTerminalConfigV3` / `tl_terminal_create_v3` | 88 bytes | 同时显式选择 history 与 UTF-8/standalone-C1 output profile |

`TlTerminalConfigV2` 的 ABI layout 为：`abi_version@0`、`struct_size@4`、`columns@8`、`rows@12`、
`initial_source_sequence@16`、`reserved_v1_compat@24`、`history_capacity_rows@40`、`reserved@48`。
两个 reserved 区域都必须全为 0。调用方可通过 `tl_terminal_config_v2_struct_size()` 做 runtime
layout handshake；和其他 versioned config 一样，短于已知 prefix 会被拒绝，未知尾部可以被忽略。

Windows binding 使用显式 64-byte DTO 和相同 offset 门禁，并同时检查 native V1=40、V2=64。
`NativeTerminalCoreV2.Create(..., historyCapacityRows = 0)` 在参数为 0 时调用 V1，在非 0 时调用
`tl_terminal_create_v2`。公开参数为 `ulong`，binding 不先行截断；超过 native `u32::MAX` 的值由
FFI 明确返回 invalid argument。

Config V3 layout 为 `abi_version@0`、`struct_size@4`、`columns@8`、`rows@12`、
`initial_source_sequence@16`、`reserved_v1_compat@24`（16 bytes）、`history_capacity_rows@40`、
`reserved_v2_compat@48`（16 bytes）、`output_profile@64`、`reserved0@68`、`reserved@72`
（16 bytes），总计 88 bytes。V3 与 V1/V2 的 unknown-tail 兼容规则不同：
`tl_terminal_create_v3` 要求 `struct_size == 88`，全部 reserved 为 0，history 不超过 `u32::MAX`，
profile 只接受 `TL_OUTPUT_PROFILE_UTF8=0` 或
`TL_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1=1`。调用方通过
`tl_terminal_config_v3_struct_size()` 握手；V1/V2 入口仍固定创建 UTF-8 profile。

.NET binding 已有显式 88-byte DTO、`tl_terminal_config_v3_struct_size` discovery、
`tl_terminal_create_v3` P/Invoke 和带 `NativeTerminalOutputProfile` 的新 overload；旧 overload 保持
V1/V2 UTF-8 行为。真实 DLL validation 路径覆盖 unknown profile/非法 size/reserved 拒绝、默认
UTF-8 与 standalone-C1 对 raw C1 OSC/ST 的不同处理，以及 Snapshot import 后 profile 保持。
Windows production renderer 已由 `SessionHost` terminal-core authority 按 durable frame 驱动；
`PortableTerminalModelV1` 与 Agent shadow 只保留为非权威兼容/差分路径。

Android renderer contract、JNI/NDK `.so`、native exporter、自研 View 和产品接线已复用同一
Config/Frame ABI，并通过严格 decoder、设备仪器测试和公网产品 E2E 门禁。

Render Frame V2/V3 同样不提升 native ABI，也不改变 V1/V2 已有 symbol 或 bytes。V2 增加：

- `tl_terminal_render_frame_v2_format_version()`：返回 2；
- `tl_terminal_render_frame_v2_max_bytes()`：返回 16 MiB；
- `tl_terminal_checkpoint_export_render_frame_v2(...)`：从同一 immutable checkpoint 显式导出 V2。

V3 对应增加：

- `tl_terminal_render_frame_v3_format_version()`：返回 3；
- `tl_terminal_render_frame_v3_max_bytes()`：返回 16 MiB；
- `tl_terminal_checkpoint_export_render_frame_v3(...)`：从同一 immutable checkpoint 显式导出 V3。

.NET 的 `ValidateAbi()` 同时门禁 V1/V2/V3 format version 与 16 MiB hard limit；
`NativeTerminalCheckpoint.ExportRenderFrameV2()` 真实调用新增 export symbol，并把 DLL 产生的 bytes
交给 `NativeTerminalRenderFrameV2Decoder` 严格解码；V3 的 discovery/export/DTO/decoder 也走独立入口，
并验证扩展 rendition 与恶意 frame 拒绝。这个链路已由 real-DLL native validation 覆盖，并由
`SessionHost` production authority 在 durable ACK 后调用；`PortableTerminalModelV1` 只保留兼容投影。

DEC synchronized output 同样只增加 ABI surface，不提升 native ABI version。新增稳定状态
`TL_STATUS_PRESENTATION_DEFERRED = 13`；48-byte `TlTerminalPresentationInfoV1` 的 layout 为
`abi_version@0`、`struct_size@4`、`flags@8`、`reserved0@12`、`next_source_sequence@16`、
`logical_time_micros@24`、`synchronized_output_deadline_micros@32`、`reserved@40`。flags bit 0/1/2
分别表示 failed closed、parser/UTF-8 incomplete 和 synchronized output。调用方通过
`tl_terminal_presentation_info_v1_struct_size()` 做 layout handshake，再调用
`tl_terminal_get_presentation_info_v1()`；getter 即使在 failed closed 后也成功返回组合 flags。
Windows binding 对该结构做 48-byte/offset/reserved/flag 一致性门禁，并把 checkpoint status 12 和
13 都作为临时不可发布。Android JNI getter 和各 ABI native `.so` 复用同一 discovery 与失败关闭规则。

## Host Effect Frame V1

DA/DSR/CPR 是权威 PTY 必须收到回复的查询，不能继续只做 mirror state no-op。旧
`tl_terminal_apply_event`/`tl_terminal_apply_event_batch_v1` 仍明确丢弃 effects，确保 Android mirror
或旧调用方不会把回复误写回 Windows。authority host 使用新增入口：

- `tl_terminal_host_effect_frame_v1_format_version()`：返回 1；
- `tl_terminal_host_effect_frame_v1_max_bytes()`：返回精确上限 65,710,862；
- `tl_terminal_apply_event_with_host_effects_v1(...)`；
- `tl_terminal_apply_event_batch_with_host_effects_v1(...)`。

Frame 为 canonical little-endian：magic `TLHEF1\0\0`、64-byte header、40-byte record、body/header
双 CRC32C。header 固定记录 count、total length、records/payload absolute offset、required feature bit 0
和零 reserved。每个 V1 record 的 kind=1 (`PTY_REPLY`)、flags=0、ordinal=0、reserved=0，携带完成
query final byte 的 `source_sequence`、exclusive `source_offset_end` 和 1..11 bytes 连续 payload。
records 按 `(source_sequence, source_offset_end, ordinal)` 严格递增，无 gap、overlap、alias 或尾部垃圾。
成功但无 effect 仍返回一个 owned 64-byte empty frame。

唯一允许的 V1 payload 是：`ESC[?1;2c`、`ESC[>0;0;0c`、`ESC[0n`、`ESC[?0n`、
`ESC[{row};{column}R`、`ESC[?{row};{column}R`。坐标是无前导零的 canonical 十进制，范围
1..512。DECOM 关闭时 CPR/DECXCPR 的行列都相对整个 viewport；DECOM 开启时行相对 vertical
margin，只有 DECOM 与 DECLRMM 同时开启时列才相对 left margin。只 clamp 报告值，不修改真实
cursor 或 wrap-pending。

effect-aware Rust batch 最多 256 events、总 Output 4 MiB。最坏 effect 数为
`1 + floor((4 MiB - 1) / 3) = 1,398,102`；首个 11-byte DECXCPR 加后续 7-byte DA1 得出
65,710,862-byte 精确最大 frame。C ABI 先验证完整 event/buffer，再 prepare candidate。RAII pending
buffer guard 先分配并注册 token，publisher 成功后核心才做不可失败 commit，最后写 caller output 并
disarm；publisher error/panic 会注销 token、释放 allocation 且不消费 sequence。

.NET 公开四类 effect-aware 单事件与 batch API，严格检查 discovery 1/65,710,862，copy/free native
buffer 后再 decode；buffer 契约或 decoder 失败会退休 wrapper。Android 只有 immutable DTO 和 strict
decoder，没有 JNI exporter、PTY writer、execute 或 feed-back API，不能成为 Windows authority。

## Parser 控制字符串与 C0 边界

Parser 已实现 SOS `ESC X`、PM `ESC ^`、APC `ESC _` 三种 7-bit control-string introducer，以及
7-bit ST `ESC \`。三类字符串走有界状态忽略：payload byte 直接丢弃、不进入可增长 buffer，因此无论
跨多少个 source event，parser memory 都保持常量；在 ST 之前 parser 不是 Ground，Snapshot、Render
Frame 和 checkpoint 均不可发布。CAN/SUB 取消字符串；NUL、DEL 和普通内嵌 C0（包括 BEL）不产生
屏幕副作用，ESC 驱动 ST/escape handling，BEL 不终止 SOS/PM/APC。该能力只表示安全消费未知
7-bit control string，不表示解释 APC 内协议，尤其不表示实现 Kitty graphics。

OSC/DCS 的 C0 规则同样显式化。OSC selector/payload 与 DCS entry/passthrough/ignore 内的普通 C0 不会
执行 CR/LF/HT 等屏幕控制，也不会被保留到 OSC payload；CAN/SUB 取消当前字符串，`ESC \` 是 7-bit
ST。BEL 会终止 OSC（包括已经收到 tentative `ESC` 的边界），在 DCS 内则保持 inert。dispatchable DCS
已经收到 tentative `ESC` 后再遇 CAN/SUB 仍采用保守 fail-closed，避免把可能有状态的 DCS 静默截断成
等价状态。

output profile 是创建或 Snapshot 恢复时确定的 terminal property。默认 `UTF8` profile 保持旧行为，
不把 raw `0x80..0x9f` 解释为 standalone C1。显式 `UTF8_WITH_STANDALONE_C1` profile 只在 UTF-8
scalar boundary 把 13 个已支持的 one-byte alias 映射到等价 7-bit 形式：IND、NEL、HTS、RI、SS2、
SS3、DCS、SOS、CSI、ST、OSC、PM、APC；其他 C1 control 原子 fail closed。多字节 UTF-8 的
continuation 不会被误判，UTF-8 编码的 C1 scalar 也不会被重解释为 standalone control。RIS 重置
parser/model 但不改变 profile。语义 paste encoder 独立过滤 raw 与 UTF-8 编码的 C1，不能把 paste
清洗与 output parser profile 混为一谈；识别 DCS/APC introducer 也不表示实现 SIXEL/Kitty graphics。

## Extended SGR 与矩形属性

当前 `Attributes` 明确保存 `faint`、`overline`、`underline_style` 和 `underline_color`；underline 是
`None`、`Single`、`Double`、`Curly`、`Dotted`、`Dashed` 六种合法 enum，不再用单一 bool 表达。
普通 SGR 逐项按输入顺序应用，后项覆盖前项：

- `2` 开启 faint，`22` 同时清除 bold 与 faint；
- `4`/`21` 分别选择 single/double underline，`24` 清除 underline；
- `4:0..5` 分别选择 none/single/double/curly/dotted/dashed；
- `5` 与 `6` 都开启 blink，`53/55` 开关 overline；
- `58/59` 设置/复位 underline color；
- `38/48/58` 均接受 `P;5;n`、`P;2;r;g;b`、`P:5:n`、`P:2::r:g:b`。

parser 继续使用最多 16 个 scalar 参数的固定上限。颜色不完整、separator 混用错误、style 越界或
未知 SGR 都 fail closed；解析先生成完整有序 operation 列表，再修改 candidate attributes，因此失败
不会留下前半段样式，单事件/批次原子性保持不变。

DECCARA 复用同一套有序 SGR 解析与完整属性集合，只修改目标 cell attributes，不修改字符、保护位、
hyperlink、meaningful end、row metadata、current/saved presentation。DECRARA 对 bool 与合法 underline
enum 做可逆 toggle；颜色 selector 会被严格解析并消费，但在 reverse 操作中是定义好的 no-op，不生成
非法 underline 值。扩展状态同样覆盖 DECERA/DECSERA/DECFRA、ICH/DCH/IL/DL、RIS、保存恢复、
alternate screen 和 resize/crop/reflow 的继承或清理路径。

## 标准擦除属性、delayed-wrap 与 ED3

标准 blank-producing erase/edit/scroll 路径使用统一 erase attributes：blank 只保留执行时 current
foreground/background color；bold、faint、italic、六态 underline 与 underline color、blink、inverse、
conceal、strikethrough、overline 全部恢复默认，并清除 protected 与 OSC 8 hyperlink。ED/EL/ECH、
ICH/DCH、IL/DL、IRM insert 与 full-width scroll 产生的新 blank，以及 DECERA 都使用该规则。DECSERA
是有意的例外：选择性擦除保留原 cell attributes；DECFRA 则使用完整 current
rendition/protection/hyperlink。

`CSI 3 J`（ED3）只删除 active screen 的 hidden runtime scrollback prefix；visible cells、cursor、modes、
current rendition 和配置的 history capacity 不变，之后 full-primary output scroll 会继续捕获 history。
副屏没有 history，因此 ED3 在副屏不会凭空修改主屏历史。`CSI 2 J`（ED2）仍只擦除 visible display，
当前没有 Windows console/terminal 针对 ED2 的特殊 history 清理或 viewport reposition 语义，不能把
ED2 描述成 ED3。

ED 0/1/2/3、EL 0/1/2、ECH、ICH、DCH、IL、DL 都会取消当前 delayed-wrap；DECAWM reset
`CSI ?7l` 也会取消。命令之后的下一枚 printable character 从当前 cursor cell 继续，而不会因为擦除或
编辑之前已到右边界就先换行。其他 cursor/margin/restore/resize 路径仍按各自既有规则重校验
pending-wrap。

## REP 语义

`CSI Ps b` 已实现。`Ps` 缺失或为 0 时按 1 处理；没有 repeat source 时是 no-op。source 是最近一枚
完整的 printable grapheme，而不是原 cell 快照：新的 base character 替换 source，combining scalar
和 ZWJ joined continuation 可以跨 source event 分片继续扩展，最终只保存 character、combining string
和 width。

执行 REP 时通过普通打印路径重新输出 source，因此使用执行时的 current presentation：当前 rendition、
protected、OSC 8 hyperlink、IRM、DECAWM、vertical/horizontal margins 和本次 output revision 都生效；原 cell
的 attributes/protection/hyperlink 不随 source 捕获。宽字符、combining 与 ZWJ grapheme 作为一个完整
source 重复。

source 生命周期采用保守边界：NUL 和 DEL 不清除；其他 C0、ESC、非 REP CSI、OSC、DCS、CAN/SUB
abort 和 hard reset 会清除。一个完成的 REP 会消费 source，因此没有新 printable grapheme 时紧随的
第二个 REP 是 no-op。非法 private/intermediate/subparameter/多参数 REP 仍 fail closed。事件任意字节
切分保持同一结果；失败的 atomic batch 会把 source、grid 和 source sequence 一起回滚。

## DECSCUSR cursor style

`CSI Ps SP q` 已实现并支持跨 source event 任意字节切分。缺失 `Ps` 按 0 处理；唯一合法参数及状态为：

| `Ps` | shape | blinking |
| --- | --- | --- |
| 0 或 1 | block | 是 |
| 2 | block | 否 |
| 3 | underline | 是 |
| 4 | underline | 否 |
| 5 | bar | 是 |
| 6 | bar | 否 |

多个参数、subparameter、private/intermediate 变体或范围外参数不会被近似：它们按 unsupported
fail closed。hard reset 恢复 blinking block。cursor style 是 terminal-level presentation state，
Snapshot V2.3/V2.4/V2.5 与 Render Frame V2/V3 会保存它；既有 Render Frame V1 刻意忽略该状态以保持逐字节兼容。

## DEC synchronized output `?2026`

`CSI ?2026h` 开始 synchronized output，`CSI ?2026l` 结束。begin 不使用嵌套计数；重复 begin 保持
active，并把 deadline 重置为当前 `logical_time_micros + 1,000,000`，加法在 `u64::MAX` 饱和。
显式 end、RIS 或成功 `ADVANCE` 到达 deadline 都会释放。`ADVANCE` overflow 仍按可重试原子失败
处理，不改变 logical time、deadline 或 source sequence，调用方可用同一 sequence 重试。

active 期间核心继续原子提交 output、resize、alternate-screen、OSC 和其他终端状态，但禁止发布
Snapshot V2、Render Frame V1/V2/V3 或 checkpoint，C ABI 返回 status 13。gate 优先级固定为：
failed closed → parser/UTF-8 incomplete（status 12）→ synchronized output（status 13）。同步状态不进入
Snapshot；V2.0..V2.5 所有 profile 导入后都 inactive，因此既有 legacy inactive Snapshot 与 V1/V2
Frame bytes 不变。

一秒只是逻辑时间约束，不是核心内部 timer。authority host 必须使用单调时钟调度，并先 journal
一个 `ADVANCE` event，再提交给核心；超时释放因此也会消费一个连续 source sequence，断点恢复和
mirror replay 会在同一个 revision 发布。Android mirror 不能按自己的 wall clock 提前释放。

## Charset 语义

默认状态为 G0/G1=`ASCII`、G2/G3=`ISO Latin-1 supplemental`、GL=G0、GR=G2，且没有 pending
single shift。当前 designation 和 invocation 为：

| 序列 | 行为 |
| --- | --- |
| `ESC ( B` / `ESC ( 0` | 将 ASCII / DEC Special Graphics 指定到 G0 |
| `ESC ( A/4/5/C/R/Q/K/Y/E/6/%6/Z/7/H/=` | 将 British、Dutch、Finnish、French、French Canadian、German、Italian、Norwegian/Danish、Portuguese、Spanish、Swedish、Swiss 中对应的 94-character NRC 指定到 G0；同样的 designation 可用于 G1-G3 |
| `ESC ) ...`、`ESC * ...`、`ESC + ...` | 对 G1、G2、G3 执行受支持的 94-character designation |
| `ESC - A`、`ESC . A`、`ESC / A` | 将 96-character ISO Latin-1 supplemental 指定到 G1、G2、G3 |
| SI / SO | 把 G0 / G1 调入 GL |
| `ESC n` / `ESC o` | 把 G2 / G3 调入 GL |
| `ESC ~` / `ESC }` / `ESC \|` | 把 G1 / G2 / G3 调入 GR |
| `ESC N` / `ESC O` | SS2 / SS3；仅下一枚 7-bit graphic byte 使用 G2 / G3，C0、DEL 和非 ASCII byte 不消费 pending shift |

普通 `0x20..0x7e` graphic byte 通过 pending SS2/SS3 或当前 GL 映射；多字节 UTF-8 字符绕过 G-set
映射。DEC private mode `?42h/l` 只控制 NRC replacement 是否生效，不丢弃 designation/invocation；
mode off 时 NRC 按 ASCII 映射。DECFRA 的 Pch `32..126` 使用 GL，`160..255` 清除 bit 7 后使用 GR，且不消费 pending
SS2/SS3。活动 charset、主/副屏 saved charset、GL、GR 和 pending single shift 都写入
`CHARSET_STATE` section，`?42` 写入 mode bank，因而 checkpoint/suffix 恢复不会丢失 NRC 或 shift 状态。

## OSC、标题栈与查询边界

当前持久化以下 selector：

- OSC 0/1/2：同时、icon 或 window title。
- CSI `22;Pt;Ps t` / `23;Pt;Ps t`：保存/恢复 title。`Pt=0/1/2` 选择 both/icon/window；
  `Ps=0` 使用一个共享 LIFO position，`Ps=1..10` 直接访问固定槽位且不移动 position。缺失字段按 0
  处理，空栈 restore 是 no-op。OSC 22 是另一种协议，不再错误复用为 title stack。
- OSC 4/104：设置、查询或按 baseline 重置 256 色 palette。
- OSC 10/11/12 与 110/111/112：设置、查询或重置默认前景、背景和 cursor color。
- OSC 8：按规范化的完整 `(sorted params, URI)` tuple intern hyperlink；打开链接时写入后续 cell，
  payload `;`（params 和 URI 均为空）关闭当前链接，已关闭的表项仍保留稳定引用。

OSC/CSI 查询的边界是“识别但不回复”：`OSC 4;index;?` 和 `OSC 10/11/12;?` 返回内部
`OscEffect::Query` 分类，`Terminal` 只提交零状态变化，不产生 PTY 输出、回调或网络副作用。host
reply 必须由权威 PTY 侧的上层策略独占，Android mirror 不得生成会回流到 Windows 的回复。
默认 primary/secondary DA、DSR 5/6、DEC DSR 5 和 DECXCPR 6 已经通过 Host Effect Frame V1 产生
有序 PTY reply；资源 4 的 XTQMODKEYS 与 OSC color query 仍只识别分类，尚无对应 host-effect kind，
不能把“可重放 query”描述成“已发送协议回复”。
未知 OSC selector 被完整消费且不改变状态；已知 selector 的非法 UTF-8、控制字符、非法颜色、
畸形参数或超限输入不会被当成成功状态。

主要资源上限来自 `osc.rs`：OSC payload 64 KiB，单个 title 4 KiB，共享 title stack 10 槽，
hyperlink URI 4 KiB、参数区 1 KiB、`id` 256 bytes、每条最多 16 个参数、最多 1024 条记录，
hyperlink 存储总量 256 KiB。OSC 状态和 cell hyperlink reference 一起进入 Snapshot V2，导入会
校验引用、排序、重复 tuple、计数和总字节数。

## DECLRMM、DECSLRM 与边距矩形行为

`CSI ?69h/l` 分别开启/关闭 DECLRMM；每次执行（包括重复设置同一状态）都会把左右边距重置为当前
全宽。DECLRMM 开启时，`CSI s` 解释为 DECSLRM：左/右参数缺失或为 0 时分别默认 1/当前列数，合法
范围至少包含两列，设置成功后按 DECOM 的坐标原点 home cursor；反向、越界或不足两列的范围不修改
边距、cursor 或 pending-wrap。DECLRMM 关闭时，无参数 `CSI s` 保持 ANSI save-cursor 语义，带参数
形式按 unsupported fail closed，不把两个协议静默混用。DECSTBM 同样只接受至少两行的合法范围。

普通打印与 REP、delayed autowrap、LF/VT/FF/IND、RI、ICH/DCH/ECH、IL/DL 及 `CSI S/T` 都接入
`[top,bottom) x [left,right)` 路径。只有 cursor 同时落在纵向和横向边距内时才使用自定义左右范围；
否则保留对应的物理屏幕行为。自定义右边界的 delayed wrap 会从 `right-1` 回到 `left`；局部矩形滚动
不进入主屏 history、不生成整行 soft-wrap metadata，矩形外 cell 保持不变，左右边界切断的 wide
lead/continuation 会被修复而不遗留 orphan。全宽路径继续保留原有 history capture、row metadata 与
BCE 语义。CR、HT、CHT、CBT 也根据 cursor 所处边距轴选择自定义边界或物理边界。

CUP/HVP/CHA/VPA 在 DECOM 关闭时使用页面坐标；DECOM 开启时，行相对 DECSTBM，只有 DECOM 与
DECLRMM 同时开启时列才相对 DECSLRM。相对 cursor movement 仅在 cursor 与对应边距轴相交时 clamp。
mode/margin、restore 和主副屏切换后都会按所在行的有效右边界重新校验 delayed-wrap。主副屏共享
terminal-level mode/margins；RIS 回到主屏、关闭 DECLRMM/DECOM、恢复全宽边距并 home cursor。

## DEC 矩形与 DECSACE

当前 CSI intermediate dispatch 支持：

- `CSI ... $ z` DECERA：矩形擦除。
- `CSI ... $ {` DECSERA：保护属性感知的选择性矩形擦除；DECSCA `CSI Ps " q` 设置后续 cell
  的 protected 位。
- `CSI Pch;Pt;Pl;Pb;Pr $ x` DECFRA：使用当前 rendition 和 hyperlink 填充矩形，Pch 按上述
  GL/GR 规则解释。
- `CSI Pts;Pls;Pbs;Prs;Pps;Ptd;Pld;Ppd $ v` DECCRA：把源矩形复制到目标原点，最多八个参数；
  source/destination page 缺失或为 0 时按 1，当前只接受有效 page 1，其他 page fail closed。
- `CSI Pt;Pl;Pb;Pr;... $ r` DECCARA：复用完整有序 SGR parser，改变 faint、六态 underline、
  overline、underline color 以及其他受支持样式/颜色。
- `CSI Pt;Pl;Pb;Pr;... $ t` DECRARA：反转受支持 bool/underline enum；颜色 selector 严格消费但 no-op。
- `CSI Ps * x` DECSACE：`Ps=0/1` 选择 stream extent，`Ps=2` 选择 rectangle extent；该 mode
  进入 Snapshot V2 的 mode bank。

坐标是 1-based、边界包含；反向或空矩形被忽略，超出有效范围的坐标被截断。DECOM 激活时行坐标
相对 vertical margin；只有 DECOM 与 DECLRMM 同时激活时列坐标相对 horizontal margin。DECSACE
只影响 DECCARA/DECRARA 的 attribute-change extent，
不改变 DECERA、DECSERA、DECFRA 或 DECCRA 的坐标规则。

DECCRA 的源 top/left 与目标 top/left 缺失或为 0 时默认 1，源 bottom/right 缺失或为 0 时默认当前
有效页的末行/末列。反向源矩形是 no-op；源范围和目标落点超出有效页时执行 clip。实现先 staging
完整源 cell，再写入目标，因此横向、纵向或二维重叠不会被已经写入的目标污染。复制保留完整 cell
presentation，包括 base/combining grapheme、width/continuation、
rendition、protected 与 OSC 8 hyperlink；源或目标边界切断宽字符时会修复 lead/continuation，不留下
orphan。

DECCRA 不移动 current/saved cursor，不改变 pending-wrap、current attributes/protected/hyperlink，
也不修改 history、soft-wrap、logical line ID/offset、sealed/head-truncated 等 row metadata。它只编辑
既有 grid，因此不需要新增 Snapshot/Render Frame section、feature、版本、ABI 或 wire 上限；后续导出
仍通过既有 cell presentation 表达复制结果。当前实现是上述有界子集，不能扩展表述为完整 DEC
rectangle family 兼容。

## Snapshot V2.0 至 V2.8 格式

Snapshot V2 的 magic 为 `TLSNP2S\0`。文件采用 little-endian，包含 104-byte header、12 个
32-byte critical section directory entry，以及按 8 bytes 对齐且零填充的 section。当前 reader
严格接受格式/minimum-reader minor 同时为 `2.0/2.0` 至 `2.8/2.8` 的已定义 canonical profile，
不接受版本字段混用。section 按 tag 严格递增：

1. `CORE_STATE`
2. `MODE_BANK`
3. `MARGINS`
4. `TAB_STOPS`
5. `CHARSET_STATE`
6. `OSC_STATE`
7. `GRAPHEME_TABLE`
8. `RENDITION_TABLE`
9. `PRIMARY_META`
10. `PRIMARY_ROWS`
11. `ALTERNATE_META`
12. `ALTERNATE_ROWS`

DECLRMM 沿用 `MODE_BANK` 的 namespace 1 / parameter 69，DECSLRM 沿用既有 24-byte `MARGINS`
section（top/bottom/left/right 加纵横 follows-screen flags），因此没有新增 wire profile、feature bit、
版本或最大长度。importer 要求 DECLRMM 关闭时 horizontal margin 必须是规范全宽且
`horizontal_follows_screen=true`；自定义纵/横边距必须在屏内且至少跨两格，并且不能标记
follows-screen。active screen 的 current/saved pending-wrap 必须位于其所在行的有效右边界，否则按
`MalformedSnapshot` 拒绝。

exporter 在编码前也会自检双屏、margin 合法性、margin/mode 一致性，以及 active screen 的
current/saved pending-wrap 是否位于有效右边界，避免核心内部非规范状态被导出。alternate screen
active 且 DECLRMM active 时，冻结的 primary 后续按 crop 路径恢复，不适用 reflow target 约束，因此
importer 会跳过该 primary 的 reflow preflight；DECLRMM 关闭时仍执行原有检查。`Screen::resize_crop`
不独立猜测水平边距，crop 后由 `Terminal` 在更新 dimensions/margins 后按对应行的有效右边界统一复核
active current/saved pending-wrap。

header 固定总长度、directory offset/count、width profile 和 required-feature bitmap。每个 directory
entry 固定 tag、critical flag、section version、offset、length 和 logical count。当前 importer 要求
精确的 section 集合、顺序、连续对齐布局和零 padding；未知 critical tag/version 或 feature bitmap
不匹配归为 incompatible，长度、布局、引用、非规范表和尾随数据错误归为 malformed。

导出选择能够无损表达当前状态的最低 canonical profile。默认 cursor style 是 blinking block；没有
扩展 SGR 时，仅当 shape/blinking 为非默认组合才选择 V2.3。八种 legacy 组合是：

| requested history rows | REP source | cursor style | 导出版本 | required features | `CORE_STATE` / screen section version |
| --- | --- | --- | --- | --- | --- |
| 0 | 无 | 默认 | V2.0，保持既有逐字节格式 | `0x18ff` | 1 / 1 |
| 非 0 | 无 | 默认 | V2.1 | `0x38ff` | 1 / 2 |
| 0 | 有 | 默认 | V2.2 | `0x58ff` | 2 / 1 |
| 非 0 | 有 | 默认 | V2.2 | `0x78ff` | 2 / 2 |
| 0 | 无 | 非默认 | V2.3 | `0x98ff` | 3 / 1 |
| 非 0 | 无 | 非默认 | V2.3 | `0xb8ff` | 3 / 2 |
| 0 | 有 | 非默认 | V2.3 | `0xd8ff` | 3 / 1 |
| 非 0 | 有 | 非默认 | V2.3 | `0xf8ff` | 3 / 2 |

V2.1 添加 history feature `0x2000`，并让四个 screen meta/rows section 使用 version 2。V2.2 添加
REP feature `0x4000`，让 `CORE_STATE` 使用 version 2。V2.3 添加 cursor-style feature `0x8000`，
无论是否同时存在 history/REP 都让 `CORE_STATE` 使用 version 3；screen section 仍只由 history 决定
version 1 或 2。默认 blinking block 必须 canonical downgrade 到 V2.0/V2.1/V2.2，不允许仅为了使用
新 minor 而输出 V2.3。

任一扩展 SGR 状态都会选择 V2.4：检测范围包含主/副屏可见 cells、两屏 current attributes 和两屏
saved attributes；扩展状态包括 faint、overline、`None`/`Single` 以外的 underline style 或非 default
underline color。V2.4 添加 feature `0x1_0000`，并保留相同的八种精确 profile：

| requested history rows | REP source | cursor style | 导出版本 | required features | `CORE_STATE` / screen section version |
| --- | --- | --- | --- | --- | --- |
| 0 | 无 | 默认 | V2.4 | `0x118ff` | 1 / 1 |
| 非 0 | 无 | 默认 | V2.4 | `0x138ff` | 1 / 2 |
| 0 | 有 | 默认 | V2.4 | `0x158ff` | 2 / 1 |
| 非 0 | 有 | 默认 | V2.4 | `0x178ff` | 2 / 2 |
| 0 | 无 | 非默认 | V2.4 | `0x198ff` | 3 / 1 |
| 非 0 | 无 | 非默认 | V2.4 | `0x1b8ff` | 3 / 2 |
| 0 | 有 | 非默认 | V2.4 | `0x1d8ff` | 3 / 1 |
| 非 0 | 有 | 非默认 | V2.4 | `0x1f8ff` | 3 / 2 |

V2.4 只把 `RENDITION_TABLE` section 升为 version 2；其他 section version 仍完全由 history、REP、
cursor style 决定。rendition record 保持 48 bytes，不扩大 wire；它保真编码 faint、overline、六种
underline style 与 underline color。V2.0..V2.3 的 `RENDITION_TABLE` 仍是 version 1，解码后新增字段
恢复默认值，既有 legacy export/import bytes 保持逐字节兼容。

DEC private mode `?9001` active 时选择 V2.5，并添加 Win32 Input Mode feature `0x2_0000`。V2.5
对 requested history、REP source、non-default cursor style、extended SGR 四个独立布尔维度形成全部
16 种精确 profile；required features 是基础 `0x18ff | 0x2_0000` 再按状态分别并入
`0x2000/0x4000/0x8000/0x1_0000`。`CORE_STATE`、screen 与 `RENDITION_TABLE` 的版本继续由原四个
维度决定，只有 `MODE_BANK` 固定升级到 version 2，并新增按 namespace/parameter 排序的
`namespace=1, parameter=9001, enabled=1` record。V2.5 不接受 disabled 9001、伪造可选 feature、旧版
MODE_BANK 或向 V2.0..V2.4 注入 9001 record。`?9001l` 或 hard reset 后，writer 必须降回仍能保存其余
状态的最低 V2.0..V2.4 profile。

Kitty keyboard negotiation 对主/备用屏各维护最多 8 层、oldest→current 的 flags 栈，支持
`CSI = flags ; mode u` 的 replace/OR/AND-NOT、`CSI ? u` 查询、`CSI > flags u` push 和
`CSI < count u` pop；只保留 `flags & 0x1f`。任一栈非空时选择 V2.6，添加 feature `0x4_0000`，
并将 section count 从 12 增为 13。新增 critical tag `KEYBOARD_PROTOCOL_STATE=0x0000_0008`
位于 TAB_STOPS 与 CHARSET_STATE 之间，编码 main/alternate depth、零 reserved 与各层 u32 flags。
V2.6 对 history、REP、cursor、extended SGR、Win32 9001 五个可选维度形成全部 32 种组合。
两栈清空后 writer 必须降级并重现 V2.0..V2.5 对应的原始字节。

任一 NRC 状态选择 V2.7 与 feature `0x8_0000`：包括 `?42` active、活动 charset designation，
以及 main/alternate saved charset designation。V2.7 与 history、REP、cursor、extended SGR、Win32、
Kitty 六个维度组合成 64 个精确 profile；`MODE_BANK` 使用 version 3 记录 namespace 1 / parameter 42，
`CHARSET_STATE` 使用 version 2 和 NRC designator ID 3..14。没有 NRC state 的 UTF-8 profile 必须
canonical downgrade 到最低 V2.0..V2.6 profile。

显式 standalone-C1 output profile 无条件选择 V2.8 与 feature `0x10_0000`。它与上述六个维度及 NRC
组合成 128 个精确 profile。该 profile 不新增 section；minor/feature pair 本身持久化 profile，NRC
存在时沿用 V2.7 的 mode/charset section version，否则沿用原有维度规则。import 后 suffix replay 与
RIS 都保持 standalone-C1；默认 UTF-8 profile 继续选择最低可保真的 V2.0..V2.7 profile。

三代 `CORE_STATE` 共用 32-byte payload：columns/rows 位于 offset 0/4，source sequence 位于 8，
logical time 位于 16，active-alternate/cursor-visible 位于 24/25，flags 位于 26 的 `u16`，repeat
grapheme ID 位于 28 的 `u32`。V2.0/V2.1 的 flags 与 ID 必须为 0；V2.2 的 flags 必须精确为 1，
且 ID 必须引用完整 REP source 的 canonical grapheme。V2.3 的 flags bit 0 表示 REP source，bit 1
表示 non-blinking，bits 2..3 分别编码 block=0、underline=1、bar=2；其 cursor-style bits 不能表达
默认 blinking block，其他高位和 shape=3 均非法。没有 REP 时 ID 必须为 0，有 REP 时 ID 必须是
已使用且 canonical 的完整 source grapheme 引用。

所有 history-enabled profile 都不序列化 history prefix。screen meta 的 `stored_rows` 仍等于
`visible_rows`、`viewport_top` 仍为 0，row runs 只收集可见 cells；grapheme table 除可见 cells 外
最多再包含 REP source，rendition table 仍只收集现有 canonical presentation 状态。alternate
requested history 必须为 0。导入后 history prefix 为空，但主屏容量策略、visible anchors、
`next_logical_line_id` 保留，后续连续 output 可以重新开始捕获。history row 的 `sealed_revision` 也
不进入 wire；因此这些 Snapshot 是可恢复 viewport/checkpoint 格式，不是 durable History Page。

CRC 使用仓库内 `crc32c.rs`：payload CRC32C 覆盖 header 之后的全部字节（包括 directory 和
sections），header CRC32C 覆盖 header 的前 100 bytes，并包含已经写入的 payload CRC。CRC 用于
损坏检测，不提供认证或加密。

八种 legacy profile、八种 V2.4 profile、十六种 V2.5 profile、三十二种 V2.6 profile、
六十四种 V2.7 profile 与一百二十八种 V2.8 profile 的 minor、minimum-reader minor、
required features、CORE_STATE/screen/rendition/mode section version 必须形成上述精确组合。importer 还严格拒绝非法
cursor/REP flags、非法 underline enum/ColorSpec、非零 reserved、越界或未使用 reference、错误 width、
非法 UTF-8、非规范 table entry，以及 legacy minor 与扩展 rendition 混用。导出和导入都以
`MAX_SNAPSHOT_BYTES = 64 * 1024 * 1024` 为硬上限；当前合法 canonical encoding 的精确静态上界
为 44,568,090 bytes。导出仅允许 parser 为 Ground、没有 pending UTF-8 且 synchronized output
inactive；Snapshot 不序列化半截 ESC/CSI/OSC/DCS/SOS/PM/APC，也不持久化半个同步展示事务。导入先完成无持久分配的结构与
资源 preflight，再解码为独立 `Terminal` 并验证双屏；只有全部成功，C ABI 才把新 handle 写入
原本为 null 的输出。失败不会部分安装状态。所有合法 canonical profile 导入后、不应用事件直接
再导出，字节保持一致。

## Fail-closed 与原子提交

`Terminal::apply_event` 复用 `Terminal::apply_events_atomic`。后者整批只 clone 一次当前状态，在
candidate 上连续完成所有 `OUTPUT`、`RESIZE` 或 `ADVANCE`；只有整批成功才以一次赋值替换 live
state。C ABI 的 batch 入口先完整校验所有 event/payload，再进入该 core transaction，不以循环调用
单事件 API 冒充原子性：

- 事件内部任何位置失败都不会留下半个 CSI、部分屏幕编辑、部分 OSC batch 或半次 resize。
- `GAP` 或未来 sequence 立即令实例 fail closed；旧 sequence 只返回 mismatch，live state 不变。
- `Unsupported` 丢弃 candidate 并令 live 实例 fail closed，防止把不等价的近似状态继续导出。
- dimension、普通资源上限、logical time overflow 等可重试错误丢弃 candidate，但不消费 sequence。
- failed-closed 实例拒绝后续事件、语义输入和 checkpoint；恢复方式是销毁实例，从更早的合法
  checkpoint 加连续 journal suffix 重建。

不可变 checkpoint 在创建时冻结 Snapshot bytes、Render Frame V1/V2/V3 bytes、revision 和
`next_source_sequence`，后续 live terminal 更新不影响它。C ABI 的 checkpoint/import/buffer 输出
只在成功后发布所有权，所有入口均在 panic boundary 内返回稳定状态码。

## Render Frame V1 / V2 / V3

Render Frame V1/V2/V3 的 magic 分别为 `TLRFRM1\0`、`TLRFRM2\0` 与 `TLRFRM3\0`，major 分别为
1、2、3。三版都使用 96-byte header、6 个严格递增的 section 和按 8 bytes 对齐的零 padding，section 依次为
grapheme、rendition、palette/default colors、visible hyperlink、rows/cells、title。header 和 payload
各有 CRC32C；CRC 只用于损坏检测，不提供认证。V1/V2 的 API 与 bytes 完全冻结；V2/V3 必须通过各自
显式 discovery/export API 获取，不以协商或静默升级改变旧调用结果。

编码只从 active screen 的 `visible_cells()` 收集并 canonical 排序/去重 grapheme/rendition/link
table，row metadata 也通过 visible row 映射，保留 active visible screen、cursor、wrap、protection
和 visible hyperlink 引用。隐藏 history 中独有的字符、属性、链接或无效 hyperlink reference 不会
进入或破坏 frame；history 内容、容量和 anchor 不同但可见状态相同时，同一版本的 frame bytes
相同。导出要求 parser Ground、没有 pending UTF-8 且 synchronized output inactive。三版的 ABI 硬上限都是 16 MiB，当前
512x512 及其他资源上限共同推导出的精确合法 canonical 最坏值也都是 13,395,208 bytes。

V2 只改变 header：required features 从 V1 的 `0x1f` 变为 `0x3f`，offset 68 写入
shape（block=0、underline=1、bar=2），offset 69 写入 blinking 0/1，offset 70..71 保留为 0；六个
payload section 逐字节复用对应 V1 frame 的内容。因此 V1 decoder 必须拒绝 V2，V2 decoder 也必须
拒绝 V1。

V3 继承 V2 cursor header，required features 为 `0x7f`；只有 rendition section 升为 version 2，record
仍为 16 bytes。flags bit 0..6 保留旧属性，bit 7/8 表示 faint/overline，bit 4 继续表示 underline
present，bits 9..11 编码 `None=0`、`Single=1`、`Double=2`、`Curly=3`、`Dotted=4`、`Dashed=5`，
known mask 为 `0x0fff`；present 必须与 style 是否为 `None` 严格一致。record offset 4/8/12 分别为
packed foreground/background/underline color。未知 flags/style、非法 packed color、reserved、版本或
section 组合均拒绝。

V3 保真输出全部扩展 rendition；V1/V2 丢弃 faint、overline、underline color，把任意非 `None`
underline 降为 single，再重新 canonical sort/dedup/remap rendition IDs。这是确定性的 legacy 投影，
避免新增状态改变 V1/V2 bytes。Windows .NET V3 DTO/strict decoder 会检查版本隔离、offset/length、
padding、CRC、UTF-8、表排序与引用、宽字符 continuation、颜色、cursor style、保留位、revision 和
sequence，并覆盖恶意 frame mutation。Android renderer/JNI/exporter/`.so` 和产品接线也使用 V3
strict decoder，并通过设备仪器测试与产品 E2E。

三版 header flags bit 0 表示 alternate screen、bit 1 表示 DECSCNM global reverse video。V1/V2
rendition flags bit 5/6 分别表示 conceal/strikethrough，并严格拒绝扩展位；V3 使用上面的 `0x0fff`
known mask。

## 尚未实现

以下能力必须继续作为边界，而不能由现有 grid 或 V1 DTO 推断：

- History Page V1：尚无 immutable history capture/token、`as_of_revision`、exclusive anchor、每页
  最多 200 个完整 sealed logical lines、`has_more_before`、gap/head-truncated 或 stale-token 对外语义；
  运行时 scrollback 已实现，但所有 history-enabled Snapshot profile 都不冻结隐藏 cells，不能冒充
  durable transcript；
- Delta V2；`tl_terminal_checkpoint_export_delta` 只是返回 `TL_STATUS_UNSUPPORTED` 的预留入口；
- DCS DECUDK、DECDLD/DRCS、DECDMAC/macros、DECRSPS 和 SIXEL raster；这些已知有状态 DCS
  在 parser dispatch 时 fail closed，其他确认为未知且可安全忽略的 DCS 才被丢弃；
- DRCS、其他未支持 charset 与普通输出的 8-bit GR print 通道；standalone-C1 profile 只实现上文
  列出的 13 个 alias，SOS/PM/APC 的有界忽略不能外推为 APC 内协议支持；
- ED2 的 Windows console/terminal 特殊 history 清理与 viewport 语义；当前 ED2 只清 visible display，
  ED3 才清本核心的 hidden runtime history prefix；
- Kitty graphics 和一般化 query-reply 覆盖；Kitty keyboard negotiation、semantic Input V3 与 Snapshot
  V2.6 状态持久化已经实现，但 APC ignore 不是 Kitty graphics 实现；DEC private mode `?9001` 与
  ConPTY Win32 key record 仍是独立路径；
- XTQMODKEYS、OSC color query、OSC 52 clipboard 和 window report 的后续 host-effect kind；
  DA1/DA2、DSR 5/6、DEC DSR 5、DECXCPR 6 是当前已通过 Host Effect V1 实现的有序 PTY replies，
  不能据此声称已经覆盖一般化 query reply。

`terminal_snapshot_v1` 只用于独立迁移/对照路径。Snapshot V2.0..V2.8 是单独格式，
不嵌套、不修补 V1 DTO，也不继承 V1 的固定投影上限或兼容性承诺。

## 验证命令

本轮固定工具链门禁通过：Rust core 441/441、仓库自有 golden streams 10/10、FFI 46/46，且
`cargo fmt` 与 `clippy -D warnings` 均通过。Linux x64、macOS aarch64 以及 Android aarch64、armv7、
i686、x86_64 六个 non-host target 均完成 workspace `cargo check`。这些结果证明源码级可移植性，
不等同于对应平台已经生成或运行产品包。

当前 Rust 源重新构建的 win-x64 release FFI DLL 已由 .NET 8.0.303 validation 程序实际加载；验证覆盖
Config V3 profile 选择/拒绝、UTF-8 与 standalone-C1 行为差异、Snapshot V2.8 profile 恢复，以及既有
Frame、Host Effect、Input、严格 decoder、恶意输入拒绝与 owned-buffer release。

Android APK 已打包各 ABI NDK/JNI `.so`，自研 terminal View、设备仪器测试和产品 E2E 均已通过；
Windows 生产渲染与 native Snapshot/Transcript 门禁已切换到 SessionHost authority。Linux/macOS
仍只具备源码级可移植性验证，不能据此声明对应产品 runtime 已发布；易变的 DLL/SO 大小或 hash
也不是长期协议契约。

仓库已用 `terminal-core/rust-toolchain.toml` 固定 Rust 1.97.1。从仓库根目录执行：

```powershell
& D:\Dev\Scripts\Ensure-DevEnv.ps1 -Tool rust
Set-Location terminal-core
& D:\Dev\Env\Rust\cargo\bin\cargo.exe +1.97.1 fmt --all -- --check
& D:\Dev\Env\Rust\cargo\bin\cargo.exe +1.97.1 clippy --workspace --all-targets --all-features --locked --offline -- -D warnings
& D:\Dev\Env\Rust\cargo\bin\cargo.exe +1.97.1 test --workspace --all-targets --all-features --locked --offline
& D:\Dev\Env\Rust\cargo\bin\cargo.exe +1.97.1 build -p termlink-terminal-ffi --release --locked --offline --target x86_64-pc-windows-msvc
```

源码级验收应至少覆盖 split UTF-8/ESC/CSI/OSC/DCS/SOS/PM/APC、control string C0/CAN/SUB/ST、
标准 erase attributes、delayed-wrap cancel、ED3 history isolation、charset designation/invocation/SS、OSC
query 与有界失败、DECLRMM/DECSLRM、DECCRA 重叠/clip/page/呈现复制、矩形 wrap/scroll/edit 与宽字符边界修复、Snapshot
CRC/section/64 MiB 边界、failed import 原子性，以及
`replay(1..N) == import(snapshot@S) + replay(S+1..N)`。

`cargo test --workspace --all-targets --all-features --locked --offline` 也会默认运行
`tests/golden_streams.rs` 中 10 组仓库自有 ConPTY capture。
