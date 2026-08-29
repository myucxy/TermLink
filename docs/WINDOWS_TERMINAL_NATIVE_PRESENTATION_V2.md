# Windows Terminal 原生展示状态 V2（已停止实施）

> 2026-08-28 架构决定：产品尚未发布，TermLink 改为完全自研跨平台终端核心，不再引入、链接或
> 构建依赖 Windows TerminalCore。本文件保留为历史差距分析，不再是实施方案。当前实施基线见
> [TermLink 自研跨平台终端核心 V2](TERMLINK_TERMINAL_CORE_V2.md)。

## 结论

TermLink 当前生产输出来自 `SessionHost` 的 ConPTY 原始字节；
`PortableTerminalModelV1` 是独立的 C# 安全子集状态机，并不是 Windows TerminalCore 的状态导出。
它只能在全部状态可无损表达时生成 V1 快照，否则必须退回从事件 1 开始的连续回放。

`terminal_snapshot_v1` 在 Windows Agent 中默认关闭，只有显式实验开关才允许启用。本次
Windows-first 提交不改变或声明 Android 的 V1 发布策略；任何客户端都不得从任意 VT Chunk
中段启动解析或据此推进 ACK。

## 为什么不能继续复制转义序列实现

Windows Terminal 的完整状态分散在 TerminalCore、StateMachine、AdaptDispatch、TerminalInput
和 TextBuffer/ImageSlice 中，包括：

- 主屏、副屏、保存光标、delayed wrap、全局 margins 和 reflow；
- G0-G3、GL/GR、single shift、94/96 字符集、DRCS 和宏；
- DECSACE、矩形属性操作和保存的 DEC mode；
- 鼠标/焦点/粘贴/Win32 input 等输入模式；
- 256 色 palette、默认颜色、样式、OSC 8；
- SIXEL palette、像素层、滚动、擦除和 resize 后位置。

TerminalCore 当前是进程内静态 C++ 库，没有稳定序列化 ABI。读取 active TextBuffer 只能得到
一部分可见文字，无法恢复后续字节依赖的解析和输入状态。

## 已废弃方案的数据路径

以下内容只记录 2026-08-28 之前评估过的路径，用于保留差距分析。不得据此新增
`terminal-main`、Windows TerminalCore 或 C++ exporter 产品依赖。

```text
(已废弃) terminal-main connection callback
  -> durable ordered raw journal
  -> Terminal::Write（TerminalCore 唯一解析器）
  -> 同一写锁、同一 source sequence 下导出 presentation revision
  -> authenticated local IPC
  -> Windows Agent 持久化并按设备密封 V2 snapshot/delta
  -> Server 只存储/路由密文
  -> Android detached validator/renderer 原子安装
  -> 从 S+1 接续实时事件
```

当时评估的原生 exporter 挂点是 `ControlCore::_connectionOutputHandler`。该挂点不再实施。新的
目标路径已经从 durable ordered journal 向自研 Rust 核心提交完整事件：`SessionHost` 只在 Agent
返回 durable ACK 后按序 apply，Gap fail closed，并由同一串行域写入核心 PTY reply 与用户输入。
Phase 1 诊断 shadow 仍可用于差分回归，但不再承担 production authority。

以下内容仅保留为非权威的历史差距清单；现行需求、状态与门禁以
[TermLink 自研跨平台终端核心 V2](TERMLINK_TERMINAL_CORE_V2.md) 为准。

## V2 最小状态

| 状态 | V2 要求 |
| --- | --- |
| Screen | 两个 buffer 的 cells、cursor、saved cursor、style、pending wrap、last grapheme |
| Margins | 明确 `GLOBAL`/`PER_SCREEN` 所有权和半开区间；V2 provider 接入前必须固定目标所有权 |
| Charset | G0-G3 designator、94/96、GL/GR、single shift、DRCS 资源引用 |
| Modes | 输出模式、独立 input/mouse flags、saved DEC mode presence/value |
| Rectangle | DECSACE 及后续属性操作依赖状态 |
| Palette | 256 色、默认前景/背景/光标色及 profile baseline |
| Hyperlink | table、cell/current/saved rendition 引用和 Android 交互语义 |
| SIXEL | 解析后的 RGBA slice、cell anchor、像素尺寸、持久 palette 和资源上限 |
| Parser boundary | 只允许完整 source event 后的 quiescent Ground；无 UTF/CSI/OSC/DCS 残留 |
| Projection | lifecycle、exit code、local input epoch、正序 transcript line ID |

V2 必须使用独立的 `TerminalSnapshotV2`、`TerminalTranscriptPageV2`、capability、密钥域和
container version，不能给冻结的 V1 添加“覆盖字段”，也不能嵌入 V1 后用 V2 修正其语义。

## 无损与故障规则

1. raw journal 不允许有损队列。容量不足应背压或先落 DPAPI/WAL；无法恢复时停止 source 并产生
   精确 Gap，禁止丢字节后继续解析。
2. 只在 parser quiescent event-end 生成快照；未完成 SIXEL/DCS 不得保存原始前缀后重放。
3. 未知 charset、mode、关键 enum、DRCS 或 raster 资源必须拒绝，protobuf 忽略未知字段不代表
   可安全恢复。
4. Android 必须先在 detached renderer 中校验和恢复，绑定 snapshot/ACK 后一次性安装；失败不
   修改 live renderer 或 durable ACK。
5. 没有无损 snapshot 时只允许连续 `1..N` 回放。任意中段 tail replay 不能成为终端状态。

## 开启 capability 前的验收门槛

- `replay(1..N) == restore(V2@S) + replay(S+1..N)`，并比较字符、样式、光标、双屏、modes、
  charset、margins、palette、hyperlink、raster 和输入编码状态；
- 覆盖 resize/reflow、alternate 活动时 resize、saved auto-wrap/DEC bank、全部字符集、矩形、
  mouse flag 组合、SIXEL 滚动/擦除/resize；
- 覆盖队列饱和、并发 callback、close-during-backpressure、进程崩溃和 journal 恢复；
- Windows/Android 跨语言 golden vectors、对象尺寸/解压/像素上限和未知扩展拒绝全部通过；
- 完成真实自研核心 -> Agent -> Server -> Android 的 S/S+1 E2E 后，才允许发布端默认声明
  `terminal_snapshot_v2`。
