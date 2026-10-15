# 终端产品发布验收状态

保留 2026-10-11 r230 历史快照，并追加 2026-10-15 模拟器/MI 6/WAL 复验。范围固定为 C01–C14、H01–H04、E02–E04、G01，共 **22 组**。本文是持续更新的证据账本；矩阵要求以工作区兄弟仓库 `../terminal-core/PRODUCT_ACCEPTANCE_MATRIX_20261010.md` 为准。当前只验收 **Relay Only**，LAN/NAT/直连不在本轮范围。

## 判定规则

- **R 子项**：独立 Android、HTTPS Relay、Windows SessionHost 的真实闭环有完整 evidence，且仅计入已完成的 phase。**W** 是 Windows 本机实窗/ConPTY/音频，**U** 是单测/FFI/绑定，**S** 是脚本自检；W/U/S 均不能替代 R。
- 仅当该组矩阵所列真实场景、失败/恢复、双端表现和清理均有证据，才把整组标为通过。`running`、空日志、截图、失败 run 的前置 phase、Android ACK 发送值，不能当作完成或 Windows peer ACK。
- 每次复测使用新 RunId，保存 parent/child evidence、候选 APK/Windows/core 哈希、origin、设备和 recipient 身份、UTC 时间。已知失败保留；新成功只覆盖相应子项。

## 最新证据快照

2026-10-15 追加独立复验（不改写下方 r230 历史快照）：

| 新证据 | 结果 | 边界 |
| --- | --- | --- |
| `artifacts/broker-product-e2e/review-20261015-wal-r2/{evidence,broker-wal-fault}.json` | termlink2 真实 Relay WAL 故障恢复 passed；阻塞期间 ACK 45，解除后 WAL/source/ACK 46、capture 1、图片像素通过，Host exit 0。 | 仅本故障场景，r412 历史 hook 静默未复现；不关闭完整组。 |
| 工作区 `.workspace-validation/device-validation-20261015/mi6-relay-r5/` | MI 6/Android9 可见 Relay 会话、输出、IME 两帧布局和输入返回 passed，ACK 46→94。 | 使用独立 recovery 包的 fixture；通知/音频/剪贴板和全触摸矩阵未验。 |
| 同目录 `mi6-instrumentation-final.log`、`emulator-instrumentation-final.log` | 真机/模拟器各 6/6 通过，包括旧 Android GCM 修复后的真实 JNI Delta、Frozen/Broker Capture 解密及 tag 拒绝。 | 设备代码测试，不等价于所有产品 UI/网络场景。 |
| `artifacts/tail-first-e2e/review-20261015-frozen-r3/evidence.json` | Frozen begin/PAGE/并发 live/离线保留通过；完整 parent 失败 FROZEN_RELAY_RECONNECT_NOT_OBSERVED。 | 系统 HTTPS 联网探测超时，网络未 VALIDATED、available count=0；实际 socket generation 4/ready 不代替重连后 PAGE，E02 保持未完成。 |

完整报告见工作区 `.workspace-validation/device-validation-20261015/REPORT.md`；保留失败批次和候选哈希。本轮未提交或发布，22 组的整组通过数不因此增加。

2026-10-16 更新：`artifacts/tail-first-e2e/review-20261016-frozen-r6/evidence.json` 已 `passed/complete`，44 项断言全部通过，覆盖离线保留、Relay 新代次订阅、原 capture 重连后分页、截断历史边界、释放、300 秒 TTL 不被 PAGE 续期及过期槽复用。脚本改为检查同一会话的实际 Relay socket/transport/订阅恢复；公网联网验证计数保留为诊断，不能作为访问专用 Relay 的前置条件。测试中 available count=0、连接 generation=4，实际分页成功。r3/r4/r5 失败仍保留；本轮不修改系统 DNS/联网探测或产品网络验证策略。E02 此 Frozen 生命周期场景完成，不等价于全部 22 组关闭。

| 证据 | 可确认的结果 | 边界 |
| --- | --- | --- |
| `artifacts/validation/release-acceptance-20261011-audio-r228.json` 与同批 `production-offer.wav` | waveOut 生产音效实录 `passed`：BEL 单次完整时长、DECPS 三音频率/顺序、静音/限流及 Dispose 后静音通过。r228 构建和频谱自检通过。 | r223 保留旧窗口误报失败，r226 的 C# 编译错误和 r227 项目路径错误不计成功；音频正常路径实测不替代 Android 通知或驱动故障恢复。 |
| `artifacts/broker-product-e2e/release-r229-delta-restore/{evidence,delta-restore}.json` | 真实 Delta 恢复 `passed/complete`；独立 Windows 与 Android 基线 sequence/hash 完全相等，3/3 分片安装，Windows peer ACK、request release 及预算释放均确认，Host 正常退出 0。 | E03 正向多片子项通过，乱序/损坏、弱网及内存压力仍需相应证据。 |
| `artifacts/broker-product-e2e/release-r230-delta-retired/{evidence,delta-restore}.json` | 真实基线淘汰及完整快照回退 `passed/complete`；Windows 独立证实旧基线淘汰，Android 恰安装一次目标完整快照，sequence/hash 匹配，Host 正常退出 0。 | 子 helper 明确只确认 full fallback，基线淘汰由 parent 独立证实；不把旧 r219 对齐失败改写为通过。 |
| `artifacts/validation/release-acceptance-20261011-surface-r222.log`、`release-acceptance-20261011-desktop-dev-r224/` | 输入队列近 8MiB 合并边界、长文本/中文 FIFO/Enter、图形/告警/剪贴板/Skia 全套本机验证通过；r224 raw Desktop 为 0.3.3/132。 | r221 首次边界 fixture 算错 item overhead，修正后 r222 通过；不是正式安装包，普通客户端未替换。 |
| `artifacts/broker-product-e2e/release-r225-shell/shell-integration-evidence.json` | 快速 Windows 长命令与 Enter 已到达真实 PS7，启动/live marker 和完整 shell command#1 语义可见；parent 仍失败 `STATUS_MENU_NOT_OPEN`，Host 正常退出 0。 | 截图证明菜单已打开，旧 uiautomator XML 漏掉非焦点 Popup；Android `pwd`/`throw` 尚未到达，正补多窗口取证。 |
| `artifacts/tail-first-e2e/release-r220-frozen-lifecycle/evidence.json` | 保留认证的 warm_existing 前置通过，真实 32 列、ACK 39；parent 仍失败 `SURFACE_TIMEOUT`。 | 压力输出超 20,000 历史行，正在补 debug 专用 150 秒等待和严格同 run marker allowlist；普通产品 45 秒超时不改。 |
| `artifacts/broker-product-e2e/r108/evidence.json` | 真实 Relay `passed/complete`；C03/04/06/09–14 九组 semantic smoke、几何/查询/状态/主题回复和滚轮子项；Host 正常退出。 | 不含完整 extended、恢复故障、多 recipient、音频/剪贴板等门禁。 |
| `artifacts/broker-product-e2e/release-r172-diagnostic-unsupported/{evidence,effects-evidence}.json` | C02 unsupported 子项真实 UI 通过；诊断导致 Host 以 125 结束是该用例预期。 | 不覆盖其他错误族和输入诊断。 |
| `artifacts/broker-product-e2e/release-r182-diagnostic-cursor/{evidence,effects-evidence}.json` 与 `release-r184-diagnostic-tab/` 同名文件 | 两个 DCS malformed 子项均 `passed/complete`，Android 详情 UI 和截图已确认；各自 Host 125 是预期 fail-closed。 | 只证明这两个合成向量。r177 XML 显示 DCS 只公开 5 字节头、其余隐藏；不能要求泄露 payload。 |
| `artifacts/validation/release-acceptance-20261011-c02-current-r177.xml` | 实际 UI 报告 `sequence=<ESC>P1$t`、`hex=1B 50 31 24 74`、`observed_bytes=29`、`hidden_bytes=24`、`truncated_bytes=0`、末字节/码点 redacted。 | 是 UI 现场证据，不单独证明整个 parent run 完成。 |
| `artifacts/broker-product-e2e/release-r178-shell/shell-integration-evidence.json`、`release-r180-shell/` | H01 分别失败于 `ANDROID_COMMAND_ACK_TIMEOUT` 和 `SURFACE_TIMEOUT`；r178 已取得 session ID，但尚无 command ACK。 | H01 未通过；不能把会话打开当作 shell 命令闭环。 |
| `artifacts/validation/native-window-effects-r181/evidence.json` | W 实窗在 runtime 截图文件被占用，`status=failed`；此前 r169 `OpenClipboard` 失败，r151/143 也失败。 | H02/H03 Windows/Android 产品均未由这些 run 通过。 |
| `artifacts/tail-first-e2e/r4frozen9/evidence.json` | E02 旧真实 Relay BEGIN、PAGE×2、RELEASE 与 live 恢复，21/21 断言通过。 | 缺断线、TTL、最老页、重新浏览等完整生命周期。 |
| `artifacts/validation/release-acceptance-20261011-dev-android-r171.json` | 旧 DEV 0.3.3/132、APK/core 锁匹配，app 457 与 network 418 项单测零失败，当时已装两个模拟器。 | 后续候选已变化；该旧安装状态不能代表当前 5558。 |
| `artifacts/validation/release-acceptance-20261011-dev-android-r201.json` | 当前 DEV 0.3.3/132 的 app 459/459、`core-network` 422/422，共 881 项单测零失败；APK SHA-256 `d6a7ff5bc213a3db68faa5f135dcfa8bd5eb84f7bfa574f0f81b752962c6734a`，已装主模拟器 5556。 | r192/r194 的候选单测失败被新 run 修复，但旧失败保留；`core_source_dirty=true`，5558 仍停在旧 r171，产品 Relay 和正式发布门禁仍未通过。 |
| `artifacts/broker-product-e2e/release-r186-shell/shell-integration-evidence.json`、`release-r189-shell/` | H01 均 `status=failed`：r186 已打开会话但 `android-pwd` 等 ACK 超时；r189 已取得 session ID，但 `STARTUP_MARKER_NOT_VISIBLE`。两次 Host 均正常退出 0。 | 没有完成 `pwd`/`throw`、命令定位、目录跳转的闭环。r186 的截图不改变失败状态。 |
| `artifacts/broker-product-e2e/release-r196-shell/shell-integration-evidence.json` | H01 `status=failed`，`stage=android-open`，`failure_code=SURFACE_TIMEOUT`。 | 尚未进入 Shell 命令与目录验收；不能据此推断 r186/r189 问题已修复。 |
| `artifacts/broker-product-e2e/release-r203-shell/shell-integration-evidence.json`、`release-r205-shell/` | r203 最终 marker 可见且 Host 正常退出 0，但 `STATUS_MENU_NOT_OPEN`；r205 在 Android 打开阶段 `SURFACE_TIMEOUT`，现场命令尚未输入完成/回车，Host 被强制停止。 | 两次 parent 均失败、命令 ACK 为空；marker 成功不等于 Shell 语义或目录入口通过。 |
| `artifacts/validation/native-window-effects-r193/evidence.json`、`native-window-effects-r195/` | W 实窗均 `status=failed`。r193 已证明 cancel 保留哨兵，但 accept 提示未出现且剪贴板恢复失败；r195 首次 `OpenClipboard` 和清理恢复均遇 `CLIPBRD_E_CANT_OPEN`。 | r193 之前的 accept 请求可能落入剪贴板授权的 3 秒频率门禁；当前 fixture 增加 >3 秒等待，仍须新 run 验证。r195 的 OS 剪贴板占用未消失，不能算 H03 通过。 |
| `artifacts/validation/native-window-effects-r197/evidence.json`、`native-window-effects-r198/` | W 实窗 cancel/accept/后台拒绝与原剪贴板恢复均成功，DECPS 与音频文件已录制。r197 child 为 `audio_pending_analysis`，parent 因 dot-source 的 evidence path 为空失败；r198 完成分析后整体失败 `H02_REAL_BEL_SOUND_MISSING`。 | r198 离线 WAV 的短铃声约 5.25 ms，设计为 93.75 ms；音频产品问题尚在修复，不能把 W/F 或 H02/H03 整组标为通过。 |
| `artifacts/validation/release-acceptance-20261011-android-r192.log`、`release-acceptance-20261011-android-r194.log` | 候选 Android 单测门禁失败：r192 `core-network` 420 项中 1 项失败；r194 421 项中 2 项失败，涉及并发移除 actor/共享 lane 生命周期。 | 这是候选构建阻塞；尽管失败用例位于 direct 模块，Relay Only 的产品 LAN/NAT 测试仍不适用。修复后要重新跑单测、装入新 APK，再做产品 E2E。 |
| `artifacts/broker-product-e2e/release-r206-protocol-full/evidence.json` | B 的 full protocol 首轮 `android-open/SURFACE_TIMEOUT`；截图显示 PowerShell fixture 使用了错误的 `if(false)` 布尔字面量。 | 属于 fixture 故障，待修复后重跑；未产生 full protocol 吞吐或协议通过证据。 |
| `artifacts/validation/release-acceptance-20261011-audio-r212.log` 与同批 `audio-r212/terminal-audio-*/production-offer.wav` | 生产 BEL 已恢复完整约 93.5 ms，DECPS 音阶可听；整个 probe 仍失败：10.75 s Dispose 后长声继续播放到约 12.533 s。 | 同步 PlaySound 的跨线程 Stop 不可靠，不能放宽 Quiet 断言；正在改为 waveOut 完成事件和 reset，须新实测。 |
| `artifacts/validation/native-window-effects-r211/evidence.json` | Windows effects 在剪贴板哨兵设置阶段失败，尚未启动 Host；原剪贴板恢复成功。 | 小米电脑管家占用 OS 剪贴板；未获准关闭该用户程序，不能算产品通过。 |
| `artifacts/broker-product-e2e/release-r214-protocol-full/evidence.json` | `passed/complete`，9 个基础 semantic、24 个 extended、10 个逐字节回复阶段均通过；9 个基础阶段关闭/重建 Android engine 后语义一致，Host 正常退出 0、未强杀。 | 当前 r201 Android/r210 Desktop/r130 Host/core DEV。extended 尚未逐阶段恢复；跨会话、完整历史/复制、真机和正式 clean 源码门禁仍需补齐。 |
| `artifacts/tail-first-e2e/release-r217-frozen-lifecycle/evidence.json` | Frozen warm_existing 路径已验证包名、版本和 APK SHA-256 一致，登录/历史/prefs 未重置；`tail-first-open/SURFACE_TIMEOUT`。ConPTY 宽度 32，Android ready=true，但末 marker 不可见。 | 最后 marker 后的 20 行填充将其挤出可视区，是 fixture 问题；保留前面大历史填充，仅修末行后新 RunId 重测。尚未开始 Frozen 生命周期。 |

失败事实继续保留：r85/r96 extended、r99 主题、r100 查询、r110/r112/r117/r120 Delta、r114 Host 提前退出、r143/r151/r169/r181/r193/r195/r197/r198 effects、r174 旧 DCS oracle、r175 Android open、r178/r180/r186/r189/r196/r203/r205 Shell、r192/r194 Android 单测、r206 full protocol fixture。r174 是测试 oracle 期待完整 DCS payload 的假阴性，不能改写为当时 run 通过；r182/r184 是修正后的新成功证据。`artifacts/validation/release-acceptance-status-r167.md` 保留 r147–r166 的逐条来源。

## 22 组状态

下表的命令代号在下一节定义。**已关闭整组 0/22；仍有未完成发布门禁 22/22。**“部分 R”只表示列出的真实子项通过，不能把单测、合成向量或某一次成功 phase 合并计算为整组通过。

| 组 | 当前证据与状态 | 尚需真实验收 | 最小入口 | 真机 |
| --- | --- | --- | --- | --- |
| C01 几何/窗口 | r108 几何回复部分 R；本机 resize 为 W。 | PS7/Claude 14/15/16/18/19t、IME/dock/fit、DPI、顺序和 renderer authority。 | B、S | 目标手机复核 DPI/IME |
| C02 错误诊断 | r172 unsupported、r182 cursor、r184 tab 为 R 子项；r174/r175 失败保留。 | 真实 CLI 输出及输入错误、精确字符/偏移、跨帧、重连、其他会话隔离和提示。 | D | 否，模拟器可先闭环 |
| C03 Unicode/宽度 | r108 semantic smoke 部分 R。 | Unicode 16/UAX#29、ConPTY 分块、复制/历史及重连双端一致。 | B+ | 否 |
| C04 行呈现/滚动 | r108 semantic smoke 部分 R。 | `ESC #3/#4/#5/#6/#8`、裁剪/滚动、备用屏、resize/Snapshot 像素。 | B+ | 可选视觉复核 |
| C05 传统协议 | r85/r96 extended 失败；r206 full protocol fixture 失败；仅 U/W。 | 主/备用屏、tab/margin/wrap、软复位、历史/输入不误删不双发。 | B+ | 否 |
| C06 SGR/颜色栈 | r108 semantic smoke 部分 R。 | 深/满栈、truecolor/下划线、reset、Snapshot 双端像素。 | B+ | 可选视觉复核 |
| C07 查询/去重 | r108 查询 166 bytes 部分 R；r100 失败。 | 分块/重复/延迟、跨 session 归属、一次性逐字节 Host 回复。 | B | 否 |
| C08 状态报告 | r108 状态 222 bytes 部分 R。 | 报告→修改→恢复、取消/超时、坏参数原子拒绝、断线和私有 Snapshot 隔离。 | B+ | 否 |
| C09 页面 | r108 semantic smoke 部分 R。 | 多页 PTY、跨页复制、页上限、resize、资源/历史恢复。 | B+ | H04 手势另用真机 |
| C10 字符集 | r108 semantic smoke 部分 R。 | 高位字节分块、UTF-8/legacy 切换和历史恢复。 | B+ | 否 |
| C11 宏 | r108 semantic smoke 部分 R。 | 定义/替换/删除、非法编码、取消/reset、Relay 重试无半提交。 | B+ | 否 |
| C12 DRCS | r108 像素 1050 部分 R。 | designation、覆盖/擦除、DPI/resize、资源释放和恢复。 | B+ | 可选视觉复核 |
| C13 SIXEL | r108 红色像素 9 部分 R。 | 流式/截断/超大 repeat、alpha/margin/备用屏和重连恢复。 | B+ | 可选视觉复核 |
| C14 OSC/颜色/标题 | r108 单 recipient 主题回复部分 R。 | 选择区/DECAC、标题、两 authority、断线后有序回复/画面。 | B、A | 第二 recipient 必需，可用第二模拟器 |
| H01 Shell | r178/r186 命令 ACK 超时；r180/r196/r205 Android open 的 `SURFACE_TIMEOUT`；r189 启动 marker 不可见；r203 菜单未打开，均失败。 | 真实 PS7 cwd/prompt/input/output/end/退出码、中文路径、目录入口、重连。 | S | 否 |
| H02 Effects/音频 | r197/r198 W 剪贴板子项成功，但 r198 音频分析 `H02_REAL_BEL_SOUND_MISSING`；无完整 R。 | 短铃声时长修复、前后台通知、权限/静音/限流、WASAPI 音频及 Host 退出停声。 | F、W | **是**，小米及通用 Android |
| H03 剪贴板/租约 | r197/r198 W cancel/accept/后台拒绝及恢复子项成功；旧 r193/r195 失败保留。 | Android Relay 系统剪贴板、两 recipient 控制切换、重试幂等仍缺。 | F、A | **是**，系统权限/后台行为 |
| H04 触摸/滚轮 | r108 wheel UI+ConPTY 字节部分 R。 | 非鼠标模式单指原操作、鼠标模式不双发、左右滚轮、DPI/键盘组合。 | M | **是**，触摸和输入法 |
| E02 Frozen History | r4frozen9 21/21 基础生命周期部分 R。 | 并发输出、Relay 断线/重连、TTL/最老页/截断、rebrowse、旧 Host 拒绝。 | T | 否 |
| E03 Delta Restore | r110/r112/r117/r120 均失败；只有 U/S。 | 两片真实 Relay、Windows peer ACK、乱序/损坏、journal 淘汰、fallback/WAL 和重同步。 | Δ | 否 |
| E04 主题 authority | r108 单 recipient 主题阶段部分 R。 | 两个不同主题 recipient、authority 迁移、退订、同色静默、断线重订阅。 | A | 第二 recipient 必需，可用第二模拟器 |
| G01 Kitty/Broker/WAL | r108 file 图像 6804 像素和 durable ACK 部分 R。 | file/temp/shared-memory 修改后恢复、WAL 故障重试、未登记映射拒绝、peer ACK。 | K、L | 否 |

## 最短剩余执行顺序

这是一条按依赖排序的**最短 runner 路线**，不是“跑完一次即通过 22 组”。每一步都要检查完整 parent/child evidence 与失败阶段，失败时先修复并用新 RunId 重跑；后续步骤不能借失败 run 的前置阶段充数。

1. 以 r201 的 881/881 单测及已装 5556 APK 为候选起点，核对同一候选 APK、Windows/core 哈希及隔离 Agent/SessionHost，先过各脚本自检。r192/r194 历史失败保留；5558 需装同一候选后才能用于双 recipient。当前不添加 LAN/NAT 产品验收。
2. 修复 r206 fixture 的 `if(false)` 后跑 **B → B+**：先验证基础几何/查询/状态/协议，再验证 extended 和 Snapshot。失败的 r85/r96/r100 保留；r206 不算协议通过。接着补 **D** 的真实 Claude/PS7 输出与输入错误诊断；r172/r182/r184 的 unsupported、cursor、tab 三个合成向量已有成功子项，但仍缺真实 CLI、跨帧和隔离。若候选哈希变化，应在该候选重跑相应向量。
3. 跑 **S**，从 Android 打开 PS7 到 `pwd`/`throw`、退出码与目录跳转全链路；r203/r205 尚未验证 Shell 命令闭环。
4. 修复 r198 短铃声后重跑 **W** 的 Windows 实窗剪贴板、截图和 WASAPI，再跑 **F** 的 Android Relay 效果/剪贴板。r197/r198 的 W 剪贴板子项已越过 3 秒请求门禁并通过；不能替代完整 W/F 或 H02/H03。
5. 跑 **M → T → Δ → K → L → A**：滚轮；Frozen 并发/断线/TTL/最老页/rebrowse；Delta 多片、错误基线与 peer ACK；Kitty 三来源；Broker WAL 故障；最后用两个独立 recipient 验证主题 authority。T 的 r4frozen9 只通过旧基础 21/21，新 lifecycle 未有成功产品 run；Δ、K、L 的不同故障/恢复向量仍需各自独立证据。
6. 汇总每组尚缺的异常、恢复、权限、像素与清理证据，再在**小米及通用 Android 真机**完成 H02/H03/H04 的通知、系统剪贴板、前后台、触摸与输入法复核。W/F 需要可用 WASAPI loopback 端点；需要独立测试账号/Agent 和第二 recipient，不复用普通会话。只有矩阵中 22 组各自的完整场景都通过，才可把 0/22 改为完成数。

## 最小真实运行命令映射

在 `termlink/` 仓库目录运行。以下变量必须先绑定到**同一候选**的绝对路径及隔离测试身份；不要复用普通用户正在使用的 Agent/SessionHost。每条命令另取唯一 RunId，保存完整 evidence。脚本的参数自检、相应子 runner 与可执行依赖必须先通过。这里是运行入口，不表示一次运行就覆盖表中全部缺口。

```powershell
$origin = [uri]'https://termlink2.kget.cc:8910/'
$base = @{
    AcceptanceOrigin = $origin
    AgentControlPipe = $isolatedPipe
    SessionHostPath = $hostExe
    NativeDisplayPath = $displayExe
    EmulatorSerial = 'emulator-5556'
    AndroidPackageName = 'dev.termlink.harness.recovery'
}

# B: 几何、查询、状态及基础协议；B+: extended + Snapshot。
& scripts/Invoke-BrokerProductE2E.ps1 @base -RunId 'accept-protocol-001' -ProtocolAcceptance -ProtocolReplyChildPath $replyChild
& scripts/Invoke-BrokerProductE2E.ps1 @base -RunId 'accept-extended-001' -ExtendedProtocolAcceptance -SnapshotRestoreAcceptance -ProtocolReplyChildPath $replyChild

# D: C02 三个向量必须各自独立运行。
foreach ($vector in @('unsupported', 'cursor-restore', 'tab-restore')) {
    & scripts/Invoke-BrokerProductE2E.ps1 @base -RunId "accept-diagnostic-$vector-001" -DiagnosticFailureAcceptance -DiagnosticVector $vector
}

# S / M / F: Shell、滚轮、效果/剪贴板。
& scripts/Invoke-ShellIntegrationProductE2E.ps1 @base -RunId 'accept-shell-001'
& scripts/Invoke-BrokerProductE2E.ps1 @base -RunId 'accept-wheel-001' -WheelAcceptance -ProtocolReplyChildPath $replyChild
& scripts/Invoke-BrokerProductE2E.ps1 @base -RunId 'accept-effects-001' -EffectsAcceptance -AudioRecorderPath $wasapiRecorder

# A: 两个独立 Android recipient 的主题 authority。
& scripts/Invoke-ThemeAuthorityProductE2E.ps1 -AcceptanceOrigin $origin -AgentControlPipe $isolatedPipe -SessionHostPath $hostExe -NativeDisplayPath $displayExe -ProtocolReplyChildPath $replyChild -FirstSerial 'emulator-5556' -SecondSerial 'emulator-5558' -AndroidPackageName 'dev.termlink.harness.recovery' -RunId 'accept-authority-001'

# T: 当前 HTTPS Relay 的 Frozen lifecycle；明确选 ExternalHttps，避免默认 ManagedAcceptanceCompose。
& scripts/Invoke-TailFirstProductE2E.ps1 -AvdName $avdName -EmulatorPort 5556 -AcceptanceOrigin $origin -TargetMode ExternalHttps -FrozenGraphicsMode -FrozenLifecycleAcceptance -AndroidApkPath $apk -AgentControlPipe $isolatedPipe -SessionHostPath $hostExe -NativeDisplayPath $displayExe -ArtifactRunId 'accept-frozen-001'

# Δ: Delta 与 retired-base/fallback 需要独立 run，Windows inspector/hash/peer ACK 必须独立取证。
& scripts/Invoke-BrokerProductE2E.ps1 @base -RunId 'accept-delta-001' -DeltaRestoreAcceptance -DeltaHistoryRoot $deltaHistory -DeltaPcDeviceId $pcDeviceId -DeltaAgentPid $isolatedAgentPid -DeltaSnapshotInspectorPath $snapshotInspector

# K / L: Kitty 三来源与 WAL 故障分开记录。
& scripts/Invoke-BrokerProductE2E.ps1 @base -RunId 'accept-kitty-001' -KittyTransferAcceptanceChildPath $kittyChild
& scripts/Invoke-BrokerProductE2E.ps1 @base -RunId 'accept-wal-001' -BrokerWalFaultAcceptance -HistoryRoot $isolatedHistory

# W: Windows 本机实窗/音频前置，不代替 F 的 Android Relay 结果。
& scripts/Invoke-WindowsTerminalEffectsProductE2E.ps1 -HostPath $hostExe -DisplayPath $displayExe -AgentControlPipe $isolatedPipe -AudioRecorderPath $wasapiRecorder -TestArtifactRoot $validationRoot -RunId 'accept-win-effects-001'
```

`$replyChild`、`$kittyChild`、`$snapshotInspector`、`$wasapiRecorder` 均为当前候选配套 EXE；`$deltaHistory`、`$isolatedHistory`、`$validationRoot` 为独立测试目录。T 会操作所选 AVD，需保证其登录同一测试账号、使用当前 APK，并在运行前核对它是否会安装/重启该模拟器。F/W 需要可用 WASAPI loopback 音频端点；H02/H03/H04 最终需要小米及通用 Android 真机做系统通知、剪贴板、前后台、触摸/输入法验收。其他协议组可先用模拟器，但 Windows 端必须是真实 ConPTY/SessionHost，不能用 mock 管道代替产品结果。Relay 断线重连、第二 recipient、异常/超时和视觉差异须在相应 run 外补录独立证据。

## 下次更新

追加新 run 时，先核对 parent 与子 evidence 都已最终落盘且 `status/stage` 一致，再写到对应组的“当前证据”；失败码和未到达阶段保留。只有全部缺口和横向门禁满足后才改变整组状态。正式发布还需另行通过 `docs/RELEASE_RUNBOOK.md` 的 clean core/对应源码、版本、签名、制品哈希、回滚和 AppShip/Docker 门禁；本账本不授权发布。
