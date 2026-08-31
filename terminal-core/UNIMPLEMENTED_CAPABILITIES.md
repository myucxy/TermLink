# TermLink 自研终端核心：未实现能力与实施路线

更新日期：2026-08-28

本文记录 `terminal-core` 当前明确未实现的能力、没有采用“半实现”的原因、实现依赖和完成标准。
它是工程路线记录，不表示这些能力已经进入产品，也不能替代对应协议的测试和验收证据。

> 状态说明（2026-08-30）：Windows 与 Android 的生产 authority/render 主链已经接入 native core。
> 本文尾部仍存在 Windows-first/shadow 阶段的历史平台描述，统一清理工作已记录在
> [`DEVELOPMENT_ROADMAP.md`](DEVELOPMENT_ROADMAP.md) 的 W0；本文所列 History Page、Delta、
> DRCS、SIXEL 和 Kitty graphics 技术缺口仍然有效。

## 基本原则

- 完全使用 TermLink 自研代码，不复制、链接或构建依赖 Windows Terminal、termForward、Termux
  或其他终端核心。外部实现只能作为公开行为的对照样本。
- 所有输入、缓存、图片、字形、分页和增量数据必须有明确上限，并在失败时保持事件原子性。
- 不能把“识别 introducer”或“解析部分参数”声明为协议支持。缺少状态、持久化、渲染或生命周期
  管理时，应明确 fail closed 或返回 unsupported。
- 新状态必须同时考虑 Snapshot、C ABI、.NET、Android contract、恢复重放和旧版本 byte stability。
- 跨平台 `cargo check` 只证明源码可编译，不等同于平台运行、渲染或产品接入已完成。

## 当前缺口概览

| 能力 | 当前已有基础 | 仍缺少的完整能力 | 当前行为 |
|---|---|---|---|
| History Page | 有界 runtime scrollback、行 ID/revision、Snapshot V2.10 retained primary scrollback | immutable capture、分页 token、anchor、sealed logical line、截断和 stale 语义 | 检查点可恢复历史，但无对外分页历史 API |
| Delta | 有 presentation revision、Snapshot 和 Render Frame | dirty journal、稳定资源 ID、tombstone、base/target revision、gap/resync | 预留入口返回 unsupported |
| DRCS | DCS 分派、G0–G3、NRC/charset 状态、Snapshot | DECDLD/DRCS 解析、字形仓库、映射、持久化、跨平台 glyph 渲染 | 已知有状态序列 fail closed |
| SIXEL | DCS 边界、流式 parser 基础、Host Effect 通道 | SIXEL rasterizer、颜色寄存器、画布、图片资源和合成 | SIXEL dispatch fail closed |
| Kitty graphics | APC 边界；Kitty Keyboard Input V3 已实现 | graphics chunk、压缩/传输、image/placement ID、查询/删除、合成和回复 | APC payload 不作为 graphics 执行 |

## 1. History Page

### 为什么当前 scrollback 不等于 History Page

当前 scrollback 是终端运行时内部状态。Snapshot V2.10 会有界、canonical 地冻结 retained primary
scrollback，用于检查点恢复；旧 V2.1..V2.9 仍保持 visible-only 字节语义。但 Snapshot 不是可分页的
durable transcript。直接把内部数组作为在线分页 API 仍会在 reflow、淘汰、宽字符修复和并发输出时
产生不稳定结果。

### 需要实现

- 在某个 `as_of_revision` 创建 immutable history capture/token。
- 以 sealed logical line 为分页单位，不能在宽字符、组合字符或软换行中间切断。
- 定义 exclusive anchor、`has_more_before`、head-truncated/gap 和 history eviction 语义。
- 定义 token 过期、错误 terminal、错误 revision、重复请求和并发写入时的确定行为。
- 对单页行数、cell 数、UTF-8 字节、token 数和 capture 生命周期设置硬上限。
- C ABI 使用 additive versioned DTO 和同库 buffer ownership；.NET/Android 采用严格 decoder。
- 明确 history capture 与 resize/reflow、alternate screen、ED3、Snapshot 恢复的关系。

### 完成标准

- 同一 token 与 anchor 重试得到 byte-identical 页面。
- 新输出不会改变已有 capture；过期 capture 返回明确状态而不是部分页面。
- 多页拼接无重复、无遗漏，且不会产生孤立 wide continuation。
- history 淘汰、截断、resize/reflow、导入 Snapshot 后的行为均有边界测试。

## 2. Delta

### 为什么不能只导出“变化的格子”

终端变化不仅包含 cell，还包含行结构、滚动、删除、cursor、mode、title、hyperlink、rendition、charset、
图片和未来 DRCS 资源。仅发送当前 dirty cells 无法表达删除、资源回收、reflow 或丢失 revision 后的恢复。

### 需要实现

- 定义 `base_revision`、`target_revision` 和严格连续性；base 不匹配时要求完整 Snapshot/resync。
- 为行、rendition、hyperlink、DRCS/image 等可引用资源定义稳定 ID 和 tombstone。
- 建立有界 dirty journal；一次事件或 batch 的状态与 delta 必须原子提交。
- 定义滚动、行替换、resize/reflow、alternate screen 切换和 hard reset 的 canonical operation。
- 支持重复 delta 幂等处理，严格拒绝乱序、跨 terminal、未知 feature 和引用缺失。
- 限制记录数、payload、资源创建/删除和保留 revision 窗口。
- Snapshot + 后续 Delta 必须形成自闭合恢复链，并有 gap fallback。

### 完成标准

对全部受支持事件满足：

```text
apply(snapshot@S, delta(S..N)) == snapshot@N
```

还必须覆盖重复、乱序、缺失、截断、CRC/版本损坏、资源 tombstone 和 journal 淘汰后的 full-resync。

## 3. DRCS

DRCS 是主机动态下载字形，不只是新增 charset tag。完整实现至少包括：

- 自研 DECDLD/DRCS DCS 语法和严格参数/像素解码。
- 有界 glyph bank：字符集、字形尺寸、擦除/替换、generation 和内存预算。
- 与 G0–G3 designation、GL/GR、SS2/SS3、DECNRCM 和保存/恢复 charset 的明确交互。
- Snapshot feature/section，保证下载字形和当前 designation 可确定恢复。
- Render Frame 或独立资源协议中的 glyph 定义、引用、更新和删除。
- Windows/Android 自研 renderer 的位图字形绘制、缩放和 DPI 行为。
- 非法/超限 DCS 的事件与 batch 原子回滚，不能留下半个字形集。

只有 parser、state、Snapshot、ABI、renderer 和恢复测试全部闭环后，才可声明 DRCS 支持。

## 4. 共用图像资源层

SIXEL 和 Kitty graphics 应先共用一个 renderer-neutral 图像基础层，避免各自维护图片缓存和合成逻辑：

- 有界 image store、稳定 image ID、generation、引用计数和确定回收策略。
- placement 的 cell/pixel anchor、尺寸、裁剪、z-order、滚动与 alternate-screen 生命周期。
- 图片资源 create/update/delete 与 terminal state 的同 revision 原子提交。
- Snapshot 是否携带图片、何时只允许 full resync，以及 Delta 如何传输资源。
- Render Frame/资源帧的 chunk、CRC、压缩协商、最大像素/字节和解压炸弹防护。
- Windows 与 Android 的一致 alpha/compositing、缩放和 DPI 规则。

在该层完成前，只解析 SIXEL/Kitty graphics 会制造“状态成功但画面缺失”的错误兼容性。

## 5. SIXEL

需要在共用图像资源层之上实现：

- 流式 DCS SIXEL decoder，可跨任意事件分块。
- SIXEL data、repeat、carriage return、new line、raster attributes 和 color register。
- 画布增长、透明/背景处理、调色板、裁剪、像素及内存硬上限。
- 光标移动、滚动、margin、alternate screen 与 erase/reset 的明确语义。
- 生成 image resource/placement，并由 Windows/Android renderer 实际显示。
- malformed、超限、取消 CAN/SUB、ST 分割和 allocation failure 的原子性测试。

当前 SIXEL 被识别为已知有状态 DCS 并 fail closed；这比静默忽略后继续产生错误网格更安全、确定。

## 6. Kitty graphics

Kitty Keyboard Input V3 已实现，但它与 Kitty graphics 是两套独立协议。Graphics 仍需：

- APC command/control-data 分离和跨事件 chunk assembly。
- Base64、可选压缩、direct/file/shared-memory 等传输介质的明确支持矩阵。
- image ID、image number、placement ID、cursor placement、cell/pixel 尺寸、z-index 与 crop。
- transmit/display/query/delete、quiet mode、回复格式和 Host Effect 顺序。
- chunk 超时/取消、重复 ID、替换、删除、screen/reset/terminal-close 的生命周期。
- 路径和共享内存介质的宿主授权边界；portable core 不直接打开任意宿主文件。
- 与共用 image store、Snapshot/Delta、Windows/Android renderer 的闭环。

未选择支持的传输介质必须返回明确错误；不能因为 APC 能正确找到 ST 就声明 Kitty graphics 兼容。

## 推荐实施顺序

1. History Page V1：先建立稳定、可分页的历史读取语义。
2. Delta V2：建立 revision、dirty journal、gap 和 full-resync 机制。
3. DRCS：先贯通小型 renderer resource、Snapshot 和 glyph 生命周期。
4. 共用图像资源/placement/合成层。
5. SIXEL：在图像基础层上实现 raster DCS。
6. Kitty graphics：复用 image store、placement、Delta 和 renderer。

依赖关系：History Page 与 Delta 共用 revision/capture 基础；DRCS 先验证通用资源协议；SIXEL 与
Kitty graphics 共用图像存储、placement、合成和回收。按此顺序可避免形成三套不兼容的资源通道。

## 产品与平台边界

- 当前 Windows 生产 renderer/journal 尚未切换到自研核心。
- Android renderer contract、JNI/NDK `.so`、自研 terminal View 和设备运行验收均未纳入本次 Windows-first 提交。
- 因此上述能力即使先在 Rust 中完成 parser，也必须等到平台渲染、真实 binding 和 E2E 恢复链通过后，
  才能标记为产品已支持。
