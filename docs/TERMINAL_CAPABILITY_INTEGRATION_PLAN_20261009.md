# 全量终端能力：应用集成与联合验收计划

日期：2026-10-09。状态：S1 首批已编码并完成本地 DEV 验证；未升级主客户端或发布。

进展与剩余项见 [S1 诊断与编辑接入记录](TERMINAL_S1_DIAGNOSTICS_EDITING_20261009.md)。
新增查询、SGR 栈、1007 和实际 ConPTY 检查见 [S1/S2 接入记录](TERMINAL_S1_S2_QUERY_STACK_SCROLL_20261009.md)。
诊断 V2、DEC 报告恢复、性能及 Unicode 数据验证见 [core 后续开发记录](../../terminal-core/S1_S4_REPORTS_UNICODE_BASELINE_20261009.md)。
双宽/双高行及 32 项 JNI/View 验证见 [C04 接入记录](../../terminal-core/S2_LINE_RENDITIONS_20261009.md)；后续 opt-in Windows 原生展示、手机尺寸调整、颜色与 33 项 JNI/View 验证见 [最新开发记录](../../terminal-core/S2_COLORS_NATIVE_DISPLAY_20261009.md)。产品 Relay 验收仍待完成。
VT52/C1/字符集、DEC 宏、混合来源诊断、实际 ConPTY 输入修复及 35 项 JNI/View 验证见
[S4 开发记录](../../terminal-core/S4_TRADITIONAL_MACROS_20261010.md)。真实 Windows 原生窗口已复验输入及手机尺寸调整/还原；生产协商和 Relay 验收仍待完成。
DEC 页及选定宿主真实尺寸、Snapshot 2.18、38 项 JNI/View、真实窗口/PTY 应答及加密事件局部验证见
[页与有序尺寸开发记录](../../terminal-core/S4_PAGES_HOST_GEOMETRY_20261010.md)。本轮新增服务端消息 71 白名单，未部署；混合版本门禁和公网 Relay 验收仍未完成。

用户要求全部完成上一轮列出的未实现/部分实现能力。协议范围、22 组任务、S0–S8 依赖和逐项标准以
[core 全量实施计划](../../terminal-core/FULL_CAPABILITY_IMPLEMENTATION_PLAN_20261009.md) 为主；
本文件记录应用仓库的接入和验收。相对链接按同级双仓库工作区解析。

## 1. 当前事实与不能混用的状态

- [Claude 查询修复](QUERY_COMPATIBILITY_FIX_20261009.md) 已有代码与本地实测，包括真实故障 WAL 和隔离真实 Claude 启动/退出。
- 五个 DEV 库及 `terminal-core.lock.json` 已更新，`source_dirty=true`；不能据此标记正式发行库。
- 运行中的 Windows 与 Android 主 App 尚未使用这批修复；模拟器此前跑的是独立 instrumentation 测试。
- 手机主 App → 公网 Relay → 更新后 Windows 的产品联测尚未完成。
- 制定计划后已开始 S1 开发并重新导出 DEV 五库及执行本地测试；在线配置和正式版本未变。

## 2. 模块职责与修改边界

| 位置 | 计划改动 | 必须保留的边界 |
|---|---|---|
| 独立 core 仓库 | parser/state/width/页/资源、Snapshot/Render/Host Effect/History/Delta、C ABI/JNI 和五库构建 | 不将 Rust、JNI C++ 或库编译脚本移回应用 |
| `win-agent/src/TerminalCore/` | versioned P/Invoke、严格帧解码、新能力发现、资源与历史接口 | 不放宽旧版本结构校验，不自行计算另一套字符宽度 |
| `win-agent/src/SessionHost/` | geometry/theme 输入、宿主事件执行、外层回复/副作用归属、滚轮策略、ConPTY 透传验证 | 同一个 authority、PTY 和会话；durable ACK 后有序应用；普通输入不被过滤 |
| `win-agent/src/Agent/` | 新 frame/resource/history/delta 的有界生产、恢复及加密传送，shell 元数据接入现有目录/历史 | Windows 为设备会话/历史权威；不回退到 Server 推断运行状态 |
| `pc-client/src/Desktop/`、`Presentation/`、`Windows/` | 利用既有 Avalonia 增加 core 帧/资源展示窗口及宿主指标/主题接入 | 不另起解析器或 shell；保留现有窗口工作流；不把外层终端能力当所有 Windows 都支持 |
| `android/terminal-engine-api/` | versioned DTO/严格 decoder、能力发现与输入/宿主事件契约 | 冻结旧契约；数据层不执行响铃或剪贴板动作 |
| `android/terminal-engine-native/` | native 调用、Render/资源消费、字素/行属性/图像绘制、稳定历史视口 | 继续使用自有 JNI/View；保留选择复制、IME、单指滚动和 dock 原布局 |
| Android 产品会话与目录层 | cwd/命令定位、授权剪贴板、进度/通知、生命周期去重、新旧版本提示 | UI 紧凑、中文/英文；旧历史不触发即时动作；不新增“每按钮独立协议” |
| 应用协议与 Server（按实际需要） | capability、加密 envelope/资源分块/限额、背压及路由兼容 | 不解析终端正文，不新增内容权威；无需变更则不重建/部署 Server |

## 3. 三条链路分别实施

**状态链路**：进程 → ConPTY → SessionHost → durable event/core authority → Snapshot/Render/资源 →
Windows 展示及 Relay 加密传输 → Android 校验/原子安装。资源与状态同 revision；缺资源不得先推进展示或 ACK。

**输入链路**：Windows/Android 语义输入 → 现有控制与排序 → 唯一 core 编码 → PTY。
H04 的滚轮策略顺序要覆盖鼠标报告、备用屏幕 1007、主屏历史；不影响非鼠标模式既有触摸操作。

**即时动作链路**：core 生成有 ID 的宿主请求 → 校验会话/控制 epoch/目标端/当前授权 → 至多一次执行记录 → 平台动作。
回放仅恢复 cwd、命令标记、进度等状态，不复制、不响铃、不通知。外层 Windows 原始 OSC 若仍被投射，
须阻止它绕过宿主授权重复执行；只有过滤自动回复并不足以保证 OSC 52 安全。

Shell 标记不仅需要接收端解析，还要验证启动端实际发出。优先为默认 PowerShell 7 提供仅当前会话的
幂等初始化，不改用户全局 profile；已自行输出标记的 shell 不重复注入。不支持注入的 shell 保留普通终端行为，
其自动标记能力按实际范围记录，不用创建时 cwd 冒充实时目录。

新 Delta 模式与现有原始事件回放必须有单一的消费权威：在有序检查点/序号边界协商切换，
不能同一输出既 replay 一次又 apply Delta 一次。保留现有原始日志以供恢复和兼容；
Delta 的 revision 连续性不能代替 transport sequence、durable ACK 或 E2EE 验证。

### 不支持协议的错误展示（C02/S1，用户补充）

Windows 和 Android 统一消费 core 的结构化诊断，具体字段、源坐标和转义规则见 core 计划 3.1.1。
错误应显示准确原因及对应的不支持序列/字符，不能统一翻译成“服务器不可用”或只展示 TL-H122。

- 在现有会话状态/错误区域给出紧凑摘要，例如“窗口控制参数未支持：<ESC>[999t”（合成示例）。
  复用原有错误入口，不新增常驻工具栏或改变原 CLI 布局。
- 点击“查看详情”展示原因、协议族、具体失败参数/字符、可读转义序列、十六进制字节、
  起止事件/字节位置、来源层与 core/profile 版本；提供“复制诊断”按钮。
- 按真实影响显示“已忽略，继续运行”或“当前会话/镜像已停止”等结果；网络错误、权限拒绝、
  数据损坏和协议不支持分别处理。普通字形缺失不能误标成未知协议。
- 后续断开/FailedClosed 保留原错误详情入口；同类错误聚合计数，正常可选探测降级不连续弹窗。
- 界面、Windows 控制台日志和复制结果均使用纯文本转义，不把 ESC/C1/换行控制字节原样写回终端；
  无关敏感载荷在文本和 hex 中同时隐藏。诊断经现有授权/E2EE 链路传递，不明文上传 Server。
- 字节预览截断或信息缺失时明确标注，不猜补原始字符；中英文说明共用稳定错误类别，原始协议字节不本地化。

联合测试须在模拟器和 Windows 实际触发合成不支持指令，核对两端摘要、详情、复制结果、
跨帧位置与首次根因一致，并确认错误文案与实际继续/停止行为相符。

## 4. 关键产品验收场景

| 场景 | 操作 | 必须观察到的结果 |
|---|---|---|
| 原故障 | 安卓新建 PowerShell 7 → 输入 claude → 回车 → 退出到 shell | 不发生原 16t/exit 125/TL-H122；shell 后续命令正常；PC 与手机状态一致 |
| 不支持协议诊断 | 发送合成未知指令及参数，跨事件分块、同帧混合，查看/复制双端详情 | 准确显示失败项、转义原序列、hex 与源位置；保留首次根因；敏感载荷隐藏；诊断不执行控制字符、不误报网络故障 |
| 双向输入 | 手机输入文本/快捷键，再 Windows 本地输入 | 每次只输入一次，控制权按既有规则切换，程序应答不被当成人工输入 |
| 字素 | 输出中文、国旗、组合字符、Emoji/ZWJ、Indic；选择复制、缩放和换行 | 两端字符列、光标、原文本一致；不割裂字素，不重排旧 profile 历史 |
| 尺寸 | 云朵菜单“按手机窗口调整”，切换 IME、字体和一/两行 dock | 按收起 IME 后的有效窗口计算；geometry 查询自洽；没有尺寸反馈循环 |
| 滚动 | 普通主屏、备用屏幕、1007、mouse reporting 与触摸鼠标开关组合 | 普通单指操作不变；滚轮不会同时滚历史和向程序发送方向键；本机滚轮正常 |
| Shell | PowerShell 中 cd 中文/空格目录，执行成功和失败命令 | 实时目录正确；命令/输出/退出状态可定位；目录打开沿用 Windows 路径权限 |
| 剪贴板/提醒 | 输出 OSC 52、BEL/进度；授权/拒绝、后台/前台、重连回放 | 权限与系统限制明确；无旧输出重复复制或通知、无后台越权读取 |
| 状态扩展 | 双宽/双高、SGR 栈、页切换、跨页复制、DEC 恢复、VT52 和宏 | 两端实际呈现与核心状态一致；重置不残留，宏受预算限制 |
| 图形 | 真实进程输出 DRCS、SIXEL、Kitty；滚动、resize、覆盖、删除 | ConPTY 确实传到核心；Windows/Android 实际绘制；资源释放，恢复无丢图/错位 |
| 历史分页 | 长输出后前翻、并发新输出、resize、token 过期 | 无重复遗漏；固定 capture 稳定；失效锚点有明确反馈，不闪回已关闭 CLI |
| 增量恢复 | 模拟 Relay 暂断、重复消息、错 base、缺资源、慢消费者 | 幂等或明确 resync，无半帧、重复输入、错误 ACK；其他会话和设备刷新仍可用 |
| 生命周期 | 关闭窗口、批量关闭失效会话、Agent 离线、重新登录 | 设备列表真实更新，旧终端不一闪出现；历史/AI 与现有产品规则保持一致 |
| 旧版混用 | 新 Windows+旧 Android、旧 Windows+新 Android | 老功能继续工作；无法表达的新 profile 提前明确提示，不能运行到中途才损坏 |
| 升级/回滚 | 覆盖升级后恢复旧会话/数据，再演练回滚 | 账号/密钥/原始历史保留；新状态的旧版不可读边界明确，不清库规避 |

## 5. 测试入口与执行顺序

以下是可复用的入口及应扩展位置，不是本轮已执行记录。先核验 D:\Dev 工具清单、各目录的 SDK 固定要求，
再运行 Ensure-DevEnv；DEV 版本/build 当次查询最近正式发布，不复制旧说明中的数字。

1. core：Rust 1.97.1 的 `fmt --all -- --check`、`clippy --workspace --all-targets --locked -- -D warnings`、
   `test --workspace --locked`；已有 golden、FFI 和新增属性/模糊/资源测试一起保留。依赖缓存完整时使用 offline。
2. 从 core `scripts/Build-ToTermLink.ps1 -Development` 生成本地 DEV 五库，再运行应用
   `scripts/Verify-NativeArtifacts.ps1`；正式库另需 clean 源码及 `-RequireClean`。
3. Windows 串行运行 `NativeTerminalCoreValidation`、`SessionHostValidation`；按变动运行
   `ProductionTerminalHistoryValidation`、`ProductionSubscriberRecoveryValidation`、
   `ProductionSessionFaultIsolationValidation`、`ProtocolValidation` 和文件/输入相关验证。
   项目位于 `win-agent/tests/`；不得并行构建共享 `obj` 的项目。
4. PC 新展示扩展 `pc-client/tests/Desktop.HeadlessTests/`、`WindowsAdapterValidation/`，
   再执行真实 Windows 窗口的绘制/IME/输入/辅助功能基础回归。Headless 不代替实际窗口验收。
5. Android 运行 `:terminal-engine-api:testDebugUnitTest`、`:terminal-engine-native:testDebugUnitTest`、
   `:terminal-engine-native:connectedDebugAndroidTest`，再跑受影响产品逻辑测试与真实 App 操作。
   明确指定模拟器序列号，ARM64 真机另记录，不将四 ABI 编译成功当四 ABI 设备测试。
6. 新建隔离产品会话做第 4 节 Relay 联测。既有 `technical-validation/e2e/Test-E2ERelay.ps1`
   只能提供测试 Relay 辅助证据，不能自动等同于主 App 公网全链路通过；执行前审查脚本是否仍适配当前架构。
7. 每阶段候选完成适用压力/兼容检查；最终候选按 core 计划执行 24 小时 soak、模糊和资源回收测试。
   改动后重跑受影响验证；文档-only 不重复全量运行。

正式签名制品还要按发布手册复核哈希、版本、签名连续性、安装/正常更新路径和数据保留。
DEV 联测通过不能自动视为正式制品通过；最终实际安装的制品要有独立证据。

## 6. 复核与验收记录

每阶段按“设计 → 实现与局部测试 → 调用链复核 → 必需回归 → 实机/Relay → 最终 diff 复核”执行。
复核人/执行者据实记录，同一执行者复核称 self-review，不冒称独立人员已审批。

每项最少保存：任务 ID、core/app revision/dirty、锁文件及库 hash、测试命令、测试/失败/跳过数量、
设备、实际版本、协议 profile、截图/日志、资源/性能指标、未完成项及回滚办法。
原始记录使用 `artifacts/validation/terminal-capabilities/<阶段>/<运行标识>/`，摘要进 docs，
不得提交真实用户会话、账号 Token 或可恢复私密文本的未经处理 WAL。

阶段关闭前对照 22 组核对：没有遗漏子项，没有仅 parser 通过就标完整，没有旧客户端静默误读，
没有新增 Server 内容权威，没有重放宿主动作，没有全局故障扩大，没有 core 源码/构建脚本回流应用。
当前 C02 诊断及 C05 编辑按 S1 实现记录标记；其余新能力保持“计划”，不能将本批视为全量完成。

## 7. 发布准备与授权边界

仅在后续明确执行发布时，按 [RELEASE_RUNBOOK.md](RELEASE_RUNBOOK.md)、[RELEASE.md](RELEASE.md) 走完整门禁：

- 固定 clean core/app 提交，重建并核验五库、锁文件，使用已测试的精确源码。
- DEV 沿用最近成功正式版本；正式版本现场分配，不复用未上传或带旧 core 的候选包。
- 核验签名连续性、匹配的 GPLv3 对应源码/构建材料、升级与回滚证据，再进入实际上传步骤。
- Android/Windows 为本计划主要涉及平台；Server 按实际协议改动决定是否构建与部署。
- Docker 仍按用户指定本机编译、直推 Docker Hub；不借此开启 GitHub 镜像发布或 LAN/NAT。
- 每一步分开记录测试通过、构建成功、上传成功、安装成功、部署验收成功。失败项保持未完成。

这份计划不执行 Git 提交/推送、替换运行中的客户端、上传 AppShip 或部署 Docker，也不变更正式发布状态。
