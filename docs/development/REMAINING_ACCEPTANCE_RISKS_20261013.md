# TermLink 剩余验收风险与产品影响评估（2026-10-13）

## 结论摘要

- 当前候选在 **Relay Only、基础 Android CLI、基础协议、Wheel、Delta restore、Android 音频/剪贴板主路径**上有新增通过证据。
- 但发布组级账本仍不能把 22 组中任何一组标成完整关闭。
- 当前最需要关注的是 `r398 Frozen lifecycle / open_session`：如果该问题可复现到真实路径，用户会看到终端“正在重连”且 marker 无法稳定显示，影响会话可用性。
- `r393 Shell command location`、`r405 Broker WAL` 是功能性/恢复性缺口；`r404 Kitty` 更像验收竞态，尚不能证明产品图像路径失败。
- `r389 Effects/F` 的 Android 主路径证据良好；剩余两个 unverified 属于独立 Windows / interactive runner 范围，不等于 Android 功能不可用。

## 已记录 Run 状态

| Run | 范围 | 状态 | 关键结果 / 失败码 |
|---|---|---|---|
| `release-r389-android-effects` | H01/H02/H03 Android Effects | partial/complete | 8 个 UI phase passed；BEL、DECPS、muted、rate-limit、replay、host-exit audio passed；H01 interactive marker 与 H02 Windows native audio 未覆盖 |
| `release-r390-protocol-basic` | B 基础协议 | passed | C03/C04/C06/C09/C10/C11/C12/C13/C14 semantic passed |
| `release-r391-protocol-extended-snapshot` | B+ extended + snapshot | passed | extended semantic 与 snapshot restore passed |
| `release-r392-diagnostic-unsupported` | C02 unsupported | passed | fail-closed diagnostic 通过 |
| `release-r392-diagnostic-cursor-restore` | C02 cursor | passed | cursor restore diagnostic 通过 |
| `release-r392-diagnostic-tab-restore` | C02 tab | passed | tab restore diagnostic 通过 |
| `release-r393-shell` | S / H01 Shell | failed | `pwd` / `throw` / exit code passed；command location failed |
| `release-r394-wheel` | M / H04 wheel | passed | protocol replies + wheel passed |
| `release-r398-frozen-lifecycle` | T / E02 Frozen lifecycle | failed | `SURFACE_TIMEOUT`；Android realtime transport `FAILED` / remote `RECONNECTING` |
| `release-r403-delta-restore` | Δ / E03 Delta restore | passed | Delta restore 与独立 inspector 对齐通过 |
| `release-r404-kitty` | K / G01 Kitty | failed | `f` 和 `mutate-f` passed；`t` 阶段 `KITTY_TRANSFER_NOT_VISIBLE` |
| `release-r405-broker-wal` | L / G01 WAL fault | failed | child 报 `BROKER_FIXTURE_NOT_IDLE`；parent 记为 `UNEXPECTED_FAILURE` |

其他环境失败：`r387` 因 SessionHost 路径取错失败；`r395/r396/r397` 是 AVD 选择、AVD home、boot timeout 环境问题，不是产品逻辑失败。

## 失败项评估

### 1. S / Shell command location — `SHELL_COMMAND_LOCATION_FAILED`

**证据**
- `pwd` ACK 成功，`throw` ACK 成功，退出码分别为 0 / 1。
- `shell_semantics_verified=true`。
- 点击“命令定位”中的 `#2` 后，“命令定位”弹窗仍保持打开。
- 15 秒内 terminal viewport signature 变化，但 semantic probe 中看不到 `pwd`，仍可见 `H01_LOCATION` filler。
- 最终 `command_location_verified=false`。

**判断**
- 不是 Relay、Windows Host 或基础命令执行失败。
- 更可能是 native shell command anchor 过期、history capture 无法定位旧命令，或 Compose clickable 事件未正确触达。
- 属于 H01 高级导航 / 命令定位能力缺陷，不是“无法打开 shell”。

**用户影响**
- 基础 shell 会话、命令执行、输出、退出码不受本次证据否定。
- 用户使用“命令定位”时可能点击后无反应或菜单不关闭。
- “打开当前目录”和目录跳转验收未能继续执行。
- 影响等级：**中**。核心远程终端可用，但 H01 产品体验不完整。

**修复方案**
1. `NativeTerminalEngine.showShellCommand()` 失败时返回/记录具体原因：command missing、anchor stale、history unavailable、alternate/page unsupported。
2. Android UI 在定位失败时关闭或明确提示，不能只保留菜单。
3. 对长输出后的旧 anchor 做 stale-anchor remap，或明确滚动到最近可定位位置。
4. E2E 点击 clickable ancestor；等待弹窗关闭、`pwd` semantic marker 可见、viewport hash/signature 变化；最多三次重试并保存每次 UI/logcat。

**完成标准**
- 新 RunId 中 command location 关闭弹窗且 `pwd` semantic 可见。
- `command_location_verified=true`。
- `打开当前目录`、返回 CLI、`cd ..` 后续目录检查全部通过。

---

### 2. T / Frozen lifecycle — `SURFACE_TIMEOUT`

**证据**
- Windows Host 已启动并产生输出。
- Android 会话已创建，截图显示终端已经有部分 fixture 内容，但状态栏为“正在重连”。
- `failed-open-inspect.json`：
  - `realtime_socket_phase=CONNECTED`
  - `realtime_socket_state=CONNECTED`
  - `realtime_transport_state=FAILED`
  - `realtime_remote_state=RECONNECTING`
  - `realtime_subscriber_ready=false`
  - first/last/live marker 均不可见。
- `failed-open-hook-response.json` 的 `open_session` 返回 `SURFACE_TIMEOUT`。

**判断**
- 不是 Windows Host 完全未启动。
- 是 Android realtime session 在 subscribe/bootstrap 后进入 transport failed / reconnecting，导致 terminal surface fixture 未完成。
- 目前 evidence 缺少 realtime failure reason、最后 event acceptance/rejection reason、server close reason，因此不能安全归因到单一模块。
- 需要区分真实产品缺陷与旧 AVD 状态污染。

**用户影响**
- 如果真实用户可复现，影响较高：CLI 顶部显示“正在重连”，会话 marker / 稳定订阅无法 ready，终端体验不可靠。
- 但同时候选在其他 run 中多次建立稳定 Relay 会话，说明不一定是所有环境必现。
- 在拿到 failure reason 前应视为 **高风险未关闭项**，不能用于证明 Frozen lifecycle 可发布。

**修复方案**
1. Android inspect 增加：
   - `realtime_transport_failure_reason`
   - final websocket close code/reason
   - last realtime event type
   - last event acceptance/rejection reason
   - 相关异常 stack excerpt
2. E2E 失败时自动保存 Android logcat 片段。
3. 固定 AVD 环境：
   - `ANDROID_AVD_HOME=D:\Dev\Env\Android\avd`
   - `TermLinkReleaseUpgrade_API36`
   - boot timeout 提高到 600 秒。
4. 复测前 force-stop Android 应用、确认测试账号登录、不复用旧会话。
5. 拿到 reason 后分别修 subscribe cursor、event ordering、认证刷新或 reconnect bootstrap。

**完成标准**
- 新 RunId 中：
  - `open_session` 成功；
  - `realtime_transport_state=CONNECTED`；
  - `realtime_subscriber_ready=true`；
  - `realtime_remote_state` 不为 `RECONNECTING`；
  - frozen lifecycle 全链路 marker / history / reconnect 断言通过。

---

### 3. K / Kitty transfer — `KITTY_TRANSFER_NOT_VISIBLE`

**证据**
- `f` phase：529 visible pixels，semantic and visual passed。
- `mutate-f` phase：snapshot restore 后仍 529 visible pixels，passed。
- 失败发生在 `t` phase。
- 失败当时 `kitty-t.png` 曾为 0 字节。
- 运行结束后 `kitty-t.png` 为 98,686 字节；重新统计 magenta pixels 约 538，已超过脚本要求的 100。

**判断**
- 当前证据更支持“截图 / 渲染竞态”，不是 Kitty 传输本身失败。
- `f` 和 durable `mutate-f` 已证明图像可显示，且 snapshot restore 后仍可见。
- 不应据此断言产品 Kitty 图像不可用；但也不能把 K 记为通过。

**用户影响**
- 正常图形显示主路径未被打断。
- 风险主要是验收无法证明第三种 medium/temp transfer 的稳定显示；真实使用中极端情况下可能出现图像延迟显示。
- 影响等级：**低到中**，偏向验收脚本稳定性。

**修复方案**
- 为所有 Kitty phase 增加有界截图重试：
  - `f`
  - `mutate-f`
  - `t`
  - `mutate-s`
- 每次重试确认 PNG 非 0 字节，再统计 magenta pixels。
- 单次达标后短暂稳定等待，或要求连续两次达标。
- 可同时检查 terminal semantic probe 的 image/resource 状态，避免只依赖像素。

**完成标准**
- 新 RunId 中四种 phase 都记录 visible pixels。
- 若真实未渲染仍必须失败；不能通过无证据重试掩盖产品缺陷。

---

### 4. L / Broker WAL fault — `BROKER_FIXTURE_NOT_IDLE`

**证据**
- `broker-wal-fault.json` 停在 `preflight`。
- `wal_sequence_before`、`ack_before`、`sidecars_before` 全为 null。
- child 直接报 `BROKER_FIXTURE_NOT_IDLE`。
- parent 丢失具体诊断，记录为 `UNEXPECTED_FAILURE`。

**判断**
- `Assert-Baseline()` 要求多个条件同时满足，但 child 没保存 baseline，无法确定具体字段。
- 最可疑是 Android `live_ack` 与 WAL last sequence 瞬时不一致，或前序测试残留 broker capture / 会话状态。
- 目前不能证明 WAL 恢复逻辑失败，也不能证明其健康。

**用户影响**
- 正常无故障路径不受本次 run 直接否定。
- 该项覆盖的是 WAL 阻塞、broker source retry、durable ACK 恢复等异常恢复能力。
- 若真实存在缺陷，可能在 WAL 写入故障后导致图形 capture 延迟、重复或不完整；影响等级：**中到高**，但发生概率取决于故障场景。

**修复方案**
1. `Assert-Baseline()` 失败前写入完整 evidence：
   - Android session id；
   - `realtime_subscriber_ready`；
   - `live_marker_visible`；
   - `broker_capture_count`；
   - `broker_capture_last_sequence`；
   - `live_ack`；
   - WAL last sequence；
   - WAL 文件列表。
2. parent 不应把明确 child failure 压缩成 `UNEXPECTED_FAILURE`。
3. baseline 判断从瞬时 `live_ack == wal_last_sequence` 改为稳定窗口：
   - WAL 连续 1–2 秒不变；
   - `live_ack >= wal_last_sequence`；
   - Android ACK 稳定；
   - `broker_capture_count=0`。
4. 运行前 force-stop/restart Android 应用，保留账号但清理视图状态，或使用独立 Agent / history root。
5. baseline 稳定后再创建 next WAL blocker。

**完成标准**
- 新 RunId baseline evidence 完整。
- WAL block 期间 durable ACK 不丢失。
- 移除 blocker 后 broker source sequence 正确补发。
- 最终 `broker_capture_count=1`，无重复、无丢失。

---

### 5. r389 剩余 unverified

**原因**
- H01 automatic PowerShell marker injection 需要独立 interactive PowerShell session。
- H02 Windows native-window audio 需要独立 WASAPI / 实窗 parent run。

**用户影响**
- Android Effects 主路径、剪贴板、音频已通过。
- 缺口主要是 Windows 本地音效与自动 marker 注入验收，不代表 Android 功能不可用。
- 影响等级：**低到中**，属于发布证据缺口而非当前 Android 使用阻断。

**修复方案**
- 建独立 interactive PowerShell fixture 验证 H01 marker。
- 跑 Windows native-window effects / WASAPI 套件验证 H02。
- 两项通过后才能把 Effects 整组关闭。

---

## 对当前产品使用的影响分级

| 等级 | 项目 | 当前可用性影响 |
|---|---|---|
| 高风险 | T Frozen lifecycle / realtime reconnect | 若真实复现，CLI 会话显示“正在重连”，marker/history 不稳定，影响核心可用性。需先取证复测。 |
| 中 | S Shell command location / directory navigation | 基础 shell 可用，但“命令定位”和目录入口体验不可靠。 |
| 中到高 | L Broker WAL recovery | 正常路径不一定受影响；异常恢复路径未证明，可能影响故障后的图形 capture/durable ACK。 |
| 低到中 | K Kitty `t` | `f` 与 durable restore 已通过；更像验收竞态。不阻断基础图形显示，但第三 medium 未证明。 |
| 低到中 | r389 H01 interactive marker / H02 Windows audio | Android 主路径已通过；Windows 本地效果与自动 marker 证据不足。 |
| 验收缺口 | A theme authority、W Windows effects、真机 H02/H03/H04 | 不能宣布完整发布；实际手机通知、剪贴板、触摸/IME、双 recipient authority 仍需证据。 |

## 发布建议

1. **短期可继续使用范围**
   - 基础 Relay CLI、基础协议、Wheel、Delta restore、Android 音频/剪贴板主路径有较强证据。
   - 不应因本轮验证脚本环境失败直接判定产品不可用。

2. **不宜据此放量的范围**
   - 不能宣布 Frozen lifecycle / E02 通过。
   - 不能宣布 Shell / H01 完整通过。
   - 不能宣布 Kitty / WAL / G01 完整通过。
   - 不能宣布 Effects 整组完整关闭。

3. **下一轮顺序**
   - 先修 Kitty 截图重试并复测，成本最低。
   - 再修 WAL baseline diagnostics / stable wait 并复测。
   - 然后补 Shell command location 诊断和产品 anchor 修复。
   - 最后处理 Frozen lifecycle realtime failure reason；这是当前最高风险未关闭项。
   - W、A、真机复核继续按发布门禁补齐。

## 记录位置与证据

- 本记录：`termlink/docs/development/REMAINING_ACCEPTANCE_RISKS_20261013.md`
- Effects：`termlink/artifacts/broker-product-e2e/release-r389-android-effects/`
- Protocol：`termlink/artifacts/broker-product-e2e/release-r390-protocol-basic/`
- Extended/Snapshot：`termlink/artifacts/broker-product-e2e/release-r391-protocol-extended-snapshot/`
- Diagnostics：`termlink/artifacts/broker-product-e2e/release-r392-diagnostic-*/`
- Shell：`termlink/artifacts/broker-product-e2e/release-r393-shell/`
- Wheel：`termlink/artifacts/broker-product-e2e/release-r394-wheel/`
- Frozen：`termlink/artifacts/tail-first-e2e/release-r398-frozen-lifecycle/`
- Kitty：`termlink/artifacts/broker-product-e2e/release-r404-kitty/`
- WAL：`termlink/artifacts/broker-product-e2e/release-r405-broker-wal/`
- 会话流水：`.workspace-backups/termlink-covered-fix-20261012.md`

## 发布切分（按产品功能方向）

### 判定原则

1. **发布阻断**：影响核心远程终端建立、稳定会话、命令输入输出、剪贴板/音频主路径，或会在真实用户常见路径中造成误操作/数据丢失。
2. **可延后**：高级导航、故障恢复边界、第二/第三协议 medium、验收脚本竞态、非本次宣传能力。
3. **证据不足不等于产品不可用**：例如 Frozen lifecycle 只有一个 AVD run；Kitty `t` 的事后截图已达到视觉阈值。

### 本轮可发布评估

| 功能方向 | 本轮证据 | 发布决策 | 理由 |
|---|---|---|---|
| Relay 会话 / 基础 CLI | Effects、Protocol、Extended/Snapshot、Diagnostics、Wheel、Delta 多个 run 均成功建会话 | **可发布基础范围** | 核心 relay + terminal text/protocol 路径有充分通过证据。 |
| Shell 命令执行 | `pwd`/`throw`/exit code passed | **可发布基础范围** | 基础命令执行与退出码路径通过；失败在高级命令定位。 |
| Android 剪贴板 / 音频 / 效果主路径 | r389 H01/H02/H03 UI 和实录音频 passed | **可发布基础范围** | 当前 Android 主路径已验证；H01 interactive marker、H02 Windows native audio 属补充证据。 |
| 图形 Kitty | `f` / `mutate-f` passed；`t` 疑似截图竞态 | **不阻断基础版本** | 已证明主图形显示与 durable restore 可见；`t` 需脚本重试后复测。 |
| Delta restore | r403 passed | **可发布** | restore 与 Windows inspector 对齐通过。 |
| Frozen lifecycle / reconnect | r398 open_session transport FAILED/reconnecting | **发布前必须补验证；暂不宣布该特性** | 可能命中核心 reconnect，但仅一例且缺失败原因；若 release notes 不宣传 frozen lifecycle，可带 known issue 发布基础版本。 |
| Shell command location | r393 menu 未关闭、pwd 不可见 | **延后优化** | 高级导航缺陷，不阻断 shell 基础使用；可后续修复。 |
| Broker WAL fault recovery | r405 baseline evidence 缺失 | **延后优化；先修验收诊断** | 覆盖异常恢复边界，不代表正常写入路径失败；需要新 RunId 取证。 |
| Theme authority / dual recipient | 本轮未跑 | **按 release notes 决定** | 若不宣传跨设备主题 authority，可延后。 |
| 真机通知 / 系统剪贴板 / 触摸 / IME | 本轮仅模拟器 | **内部/灰度可发；公网全量前应补齐** | 模拟器无法替代 OEM 通知、系统剪贴板、触摸和输入法行为。 |

### 建议的最小发布动作

1. **必须做：Frozen lifecycle 复验**
   - 给 Android inspect/log 增加 `realtime_transport_failure_reason`、最终 websocket close code/reason、最近 event acceptance/reason。
   - force-stop 应用后复测同一 APK/账号/Agent。
   - 若仍 transport FAILED，则将其视为 P0 产品缺陷；若复测稳定，可标记为环境/旧状态问题。
   - 该步骤优先级最高，因为它最接近核心会话可用性。

2. **建议做：Kitty / WAL 验收脚本修正**
   - Kitty：所有 phase 加截图非 0 字节与 mag pixels 有界重试。
   - WAL：保存完整 baseline；把瞬时 `live_ack == wal_last_sequence` 改成稳定窗口与 `>=` 判断。
   - 这两项主要是验收脚本修正，不阻塞基础产品代码，但可避免误报。

3. **不需要为本次版本阻塞的产品项**
   - Shell command location。
   - H01 interactive PowerShell marker。
   - H02 Windows native-window audio。
   - Theme authority（若本次不宣传）。
   - WAL 故障恢复的产品级深修（先拿到完整 evidence）。

### 发布说明建议

- 若发布基础版本，不要在 release notes 中宣布以下完整能力：
  - Frozen lifecycle 全量通过；
  - Shell command location；
  - Kitty 全 medium/restore 矩阵；
  - Broker WAL fault recovery；
  - 双设备 theme authority。
- 建议标记为 “Relay Only 基础远程终端已验证；高级恢复/导航能力持续加固”。

## 2026-10-13 处理轮结果补充

### 已完成代码/脚本改动

1. `Invoke-BrokerProductE2E.ps1`
   - Kitty 每个 phase 的截图改为最多 10 秒有界重试。
   - Broker fixture 在非 Delta 模式下，5200 个 marker 后显式输出 live marker，供 WAL baseline/recovery 使用。
2. `Invoke-BrokerWalFaultProductE2E.ps1`
   - baseline 拆分为具体失败码。
   - evidence 保存完整 baseline、WAL sequence list。
   - baseline 改为连续 2 秒稳定：WAL last 不变、Android ACK 不变、ACK >= WAL last、broker capture 为 0。
   - inspect 显式传入 live marker。
3. Android realtime diagnostics
   - `ProductionRealtimeSessionDiagnostics` / `ProductRealtimeDiagnostics` 新增 `transportFailureReason`。
   - E2E inspect 输出 `realtime_transport_failure_reason`。
   - 所有直接进入 transport FAILED 的路径记录 reason。
   - Debug recovery APK：`7e75331af88de23765c7436e4592612488caf3f6e566cc7f67207e0ba36a5d42`。

### 新复测结果

| Run | 结果 | 说明 |
|---|---|---|
| `release-r407-frozen-lifecycle` | failed / `SURFACE_TIMEOUT` | 新 diagnostics 已进入 projection，但 `realtime_transport_failure_reason=null`。 |
| `release-r408-frozen-lifecycle` | failed / `SURFACE_TIMEOUT` | 截图显示输出推进到约 marker 0679 后进入“正在重连”；reason 仍 null，说明失败路径/客户端映射仍未覆盖。 |
| `release-r409-kitty` | failed，但原竞态已消除 | `f`、`mutate-f`、`t` 全部 `semantic_and_visual_passed`；后续 `s` 阶段出现新的 `HOOK_RESPONSE_TIMEOUT`，Host exit code 1。 |
| `release-r410-broker-wal` | failed / `BROKER_LIVE_MARKER_NOT_VISIBLE` | 诊断生效：baseline 完整，WAL last=47，ACK=47，发现 fixture 缺 live marker。 |
| `release-r411-broker-wal` | failed / `BROKER_LIVE_MARKER_NOT_VISIBLE` | live marker 传入 inspect 后仍 false，确认 fixture 本身缺 live marker。 |
| `release-r412-broker-wal` | failed / waiting retry 阶段 `HOOK_RESPONSE_TIMEOUT` | baseline 已通过；WAL blocker proven（wal_before=46，block=47，ack_during=46，sidecar=1）；删除 blocker 后 Android inspect 30 秒未响应。 |

### 下一步根因排序

1. **Frozen lifecycle**
   - reason 仍为 null，不能再只加字段。
   - 需要确认 Android 是否有旧 client / new client 映射错位，或 FAILED 状态由非当前 diagnostics client 持有。
   - 在 transport state 设置点加结构化 log；失败时导出 client incarnation、endpoint incarnation、renderer incarnation。
2. **WAL recovery**
   - baseline 和 blocker proof 已通过；剩余是删除 blocker 后 Android hook/inspect 无响应。
   - 需要在 waiting loop 中每次 inspect 超时保存 hook 原始输出/logcat，并检查 Android是否 ANR、session 是否 exited、endpoint 是否被 retire。
3. **Kitty**
   - 原 `KITTY_TRANSFER_NOT_VISIBLE` 已消除。
   - 新失败集中在 shared registration / `s` phase；需检查 `KITTY_CHILD_FAILED`、child stderr、Host exit code 与 Android shared resource ACK。


## 追加（2026-10-14 · r425/r426 live-marker 修复复验）

**修复**：TailFirst Frozen lifecycle 的 `open_session` readiness 不再要求
live marker（`Invoke-TailFirstProductE2E.ps1`，receiver 早已支持 optional
live_marker）。此前 r398-r421 全部 `SURFACE_TIMEOUT` 的阻塞点已消除。

**复验结果**（ExternalHttps · termlink2 · recovery 包 · r130 SessionHost）：

| Run | 结果 |
|---|---|
| r425 | open/begin/page/并发实时输出全部断言通过（live_ack 78→79）；失败于 `frozen-history-relay-reconnect` 的 inspect `OPERATION_TIMEOUT` |
| r426 | 同 r425，同位置失败——确定性可复现，非瞬态 |

**当前定性**：Frozen 打开与冻结浏览主链路已恢复；剩余缺陷收窄为
"Host 失联 + 冻结浏览叠加时的 inspect hook 超时"。普通（非冻结）会话的
Relay 重连恢复已被常规使用与历史 run 证明。该缺陷按产品方向决策记为
已知限制，发布说明不得宣传 Frozen lifecycle 全量能力，修复排入后续
（取证方向：Android logcat ANR/广播投递、hook 超时窗口）。

**发布门禁状态**：基础版本（Relay Only，不宣传 Frozen 全量 / Shell
command location / Kitty shared s / WAL retry / theme authority）可发布。


## 追加（2026-10-14 · 登录报 ACCOUNT_DEVICE_STATUS_UNAVAILABLE 的成因）

**现象**：部分电脑登录时提示「操作没有完成…ACCOUNT_DEVICE_STATUS_UNAVAILABLE」，
agent 侧 control-runtime 记录
`BeginAccountAuthorization failure=ProductionEnrollmentException site=IsDeviceRevokedAsync`。

**机制**：登录（BeginAccountAuthorization）时 agent 用保留的设备身份调
`GET /v1/devices/self`（带设备证明头）核验设备状态（ProductionEnrollment.cs
IsDeviceRevokedAsync）。仅两种结果被视为正常：200（未吊销）、401 且
`error.code == DEVICE_REVOKED`（已吊销）。**其余一律抛
ACCOUNT_DEVICE_STATUS_UNAVAILABLE**，包括：

- `401 UNAUTHENTICATED`：令牌无效/设备在该服务器数据库中不存在/设备证明不匹配
  ——典型场景：机器的保留身份是在**另一台服务器**（termlink2 旧库 vs 生产库）
  注册的，server-url 与身份归属不一致，或服务器端数据库被重建（termlink2 于
  10-10 切换为 recovery 库）
- `403 DEVICE_PENDING`：设备已注册但尚未 TRUSTED
- `401 DEVICE_DISABLED`：设备被禁用
- 边缘 502/超时（termlink.kget.cc / termlink2 经 NAS Lucky 转发，实测有抖动）

**两台服务器实测**：对未知设备均返回 `401 UNAUTHENTICATED`（非
DEVICE_REVOKED），因此任何"设备身份与所连服务器数据库不匹配"的机器都会
稳定复现此错误。

**按机处置**：
1. 查该机 `%LOCALAPPDATA%\TermLink\server-url.txt` 指向哪台服务器；
2. 到对应服务器核对 devices 表中该机器设备行（存在？trust_state？disabled/revoked？）；
3. 身份属于另一台服务器 → 改回对应 server-url，或清除保留身份后重新登录
   （产生新设备注册）；
4. DEVICE_PENDING → 完成设备信任批准；
5. 边缘 502 → NAS Lucky 上游稳定性问题，重试即可。

**产品改进待办**：agent 侧应将 DEVICE_PENDING / UNAUTHENTICATED /
DEVICE_DISABLED 与"状态不可用"区分呈现（分别给出"等待信任"、"请重新注册"、
"设备已禁用"指引），登录流程对边缘 502 应先重试再失败。


## 追加（2026-10-14 · 未识别拒绝的诊断日志增强，termlink 0050346）

按用户要求，未识别的服务端拒绝不再只留笼统错误码：

- `IsDeviceRevokedAsync` 对非 200/非 DEVICE_REVOKED 的响应解析
  `error.code` 与 `error.message_key`（有界、不含原始响应体与凭据），
  异常消息追加 `http_status=<n> server_code=<…> server_message_key=<…>`，
  例如 `… http_status=403 server_code=DEVICE_PENDING
  server_message_key=auth.devicePending`。
- 控制管道日志（TermLink-control-runtime.log）对
  ProductionEnrollmentException 追加 `message=<有界消息>`（其余异常类型
  维持 type+site-only，避免记录潜在敏感文本）。
- 网络失败保持原契约：以 `HttpRequestException` 原样传播（测试固化），
  日志记为 `failure=HttpRequestException site=IsDeviceRevokedAsync`。
- 该增强随下一个 Windows 构建发布；已发布 134 的机器遇到新报错时，
  升级后即可在日志中看到具体服务端拒绝码。
