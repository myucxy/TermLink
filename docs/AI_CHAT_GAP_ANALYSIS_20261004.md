# AI 会话体验差距核查（2026-10-04）

后续状态：本文记录 Web 实施前的差距，思考摘要、生成状态、静态 HTML、搜索、草稿持久化等随后已实现。当前剩余功能以 [重新盘点清单](AI_ENHANCEMENT_BACKLOG_20261004.md) 为准；不要将本页旧表直接当作当前待办。

结论：TermLink 已有多 Agent 聊天的主要控制能力，当前最明显的差距是生成过程可见性、输出成果查看、代码变更审阅和长会话管理。继续增加输入栏按钮，不能解决 Windows 没有转换上游事件或没有提供输出文件读取的问题。

本报告以当前工作区代码为准，包含尚未正式发布的改动；不是对旧正式包的功能保证。本轮只核查代码、既有验证记录及参考项目公开前端，未运行参考产品的完整端到端测试，没有修改产品代码、构建或发布。较早调研文档中的待办须以本报告和当前实现为准，不能再次把已实现的模型、授权、附件、历史恢复列为完全缺失。

## 已具备的基础

- 按设备和 Agent 分组的紧凑列表、搜索、置顶、重命名、真实标题删除确认。
- 原生 Codex、Claude、ZCode 适配，以及独立的 ZCode `desktop_remote` 网页策略。
- 默认管理工作目录、自选电脑目录、仅显示设备已支持 Agent 的创建入口。
- 模型选择、按 Agent/模型提供的推理强度、不同授权方式；能力不支持时不强行展示统一选项。Claude 推理强度目前仅创建时可设。
- 图片、拍照、手机文件上传、电脑文件引用，以及上传进度、取消和重试。
- 停止生成、支持时补充当前任务、手机待发队列、工具分组、单次批准/拒绝及问题回答。
- Markdown、表格、代码块复制、历史分页、返回最新、引用/编辑为草稿、导出 Markdown。
- TermLink 会话恢复、原生 Codex/Claude 历史导入、沿用历史授权；支持时从当前上下文分支。

上述“有功能”不代表每个 Agent 的能力、授权和每条网络路径都已经等价验证。

## 差距与实施优先级

| 能力 | 当前状态 | 具体缺口与参考 | 优先级 |
| --- | --- | --- | --- |
| 全程生成反馈 | 有静态“正在生成”及停止；顶部细进度条主要反映请求进行中 | 增加真正覆盖生成周期的轻量动画、耗时、最后活动、思考/工具/等待审批/重连阶段。参考 HAPI StatusBar；无事件时显示“等待 Agent”，不编造进度百分比 | P0 |
| 思考内容 | 未渲染；Windows 丢弃部分 reasoning/thinking 事件 | 只接入 Agent 协议公开返回的思考或摘要，按回合折叠；区分有摘要、未提供、生成中。HAPI 有 reasoning 组件，ZCode 有 reasoning 时间线 | P0 |
| 流式与断线恢复 | 活跃页约 1 秒轮询快照；Codex 文本有 delta，Claude 尚无完整局部流事件处理 | 补齐各适配器事件，先通过既有请求通道传带序号增量及 snapshot 回退，再评估推送。不能只把轮询调快；参考 CC Pocket seq/historyRevision | P0 |
| 任务计划与子任务 | 工具摘要可看，尚无专门计划/子任务展示 | 计划步骤、完成状态、子 Agent 父子关系、失败原因，默认压缩为一行；HAPI UpdatePlanView、ZCode plan/subagents 数据结构可参考 | P1 |
| 输出文件与图片 | 输入附件已有；Markdown 图片仅文字占位，电脑文件引用主要用于输入 | 生成文件卡片、真实文件路径/类型/大小、点开图片、下载/分享、可点击本地文件及行号引用。不能以“附件能上传”代表“成果能查看” | P1 |
| HTML/网页预览 | HTML 代码按文字显示；已有 WebView 专用于 ZCode 官方远控 | 首先支持静态 HTML 源码/预览，之后才做带 JS 或本地开发服务器的交互页面。CC Pocket 有实际静态 HTML 实现；React/Vue 源码不能直接当 HTML 加载 | P1 |
| 代码变更审阅 | 管理弹窗可看文本 Diff，部分来源受长度限制 | 按文件列出新增/修改/删除、增删行和语法着色、完整差异按需读取。HAPI CodexDiffView 可参考。撤销文件、应用补丁、Git 回滚另立能力，不能当纯 UI 按钮处理 | P1 |
| 上下文与用量 | 有原始 usage 和压缩状态，主要放在管理弹窗 | 紧凑用量入口、当前上下文占比、压缩提示；仅协议有窗口大小时显示百分比，仅有可靠价格信息时估算费用。ZCode 有用量和上下文分类 UI | P1 |
| 单次审批的范围 | 已有会话授权模式；具体请求只有“允许本次/拒绝” | 在 Agent 支持时加入本会话允许、指定工具/命令规则、拒绝原因。会话总体模式与一条审批的允许范围是不同能力；HAPI PermissionFooter 区分这些行为 | P1 |
| 消息操作、分支和恢复 | 可引用/编辑成草稿；Codex 可从当前上下文分支；原生历史可继续对话 | 缺少会话内全文搜索、跳转命中、从某条消息重试/分支、回退。导入历史目前是有界近期内容/摘要，不是完整原生日志浏览器；Happier 按 Agent 分开声明 fork/rollback 支持状态 | P2 |
| 草稿与队列可靠性 | 草稿/待发队列在内存，未知结果操作有独立的持久化去重机制 | 杀进程后恢复未发草稿/附件索引及队列；恢复后重新核对 host/session，不自动向新进程重放。不要把已存在的请求去重误写成“完全没有可靠性” | P1 |
| 后台通知 | 应用进程存活时提供尽力提醒 | 锁屏或进程回收后的可靠完成/审批提醒、点击直达具体会话。须保留不在通知泄露聊天正文的边界，不能仅凭前台测试保证后台到达 | P2 |
| 命令、技能和 MCP | 有部分 slash 命令建议、文件引用、MCP 工具消息 | 更完整的命令说明/参数提示、文件名补全、Skills 浏览与调用、MCP 连接/能力状态。参考 HAPI useSkills 和 ZCode 设置入口；能调用 MCP 工具不等于已有 MCP 管理界面 | P2 |
| 富内容阅读 | 基础 Markdown/表格/代码块已具备 | 代码语法高亮、图片查看优先；按实际需求再加公式、Mermaid、音视频、PDF/Office 预览。无需一次引入整个办公渲染平台 | P2/P3 |
| 工作区管理 | 能选择目录与输入文件，查看部分变更 | 完整项目文件树、Git 状态、检查点、撤销、worktree 隔离、本地服务预览等属于下一层工作区能力；不应承诺给所有 Agent 同时开放 | P3 |

P0 表示应先解决的核心反馈/同步问题，P1 表示紧随其后的日常使用能力，P2/P3 表示按需求扩展；这不是漏洞严重度或发版承诺。

## 与 ZCode 比较时的边界

### 公开前端中核实的内容

2026-10-04 读取官方 `https://zcode.z.ai/remote/v4` 及其入口脚本 `index-B-ilXaCQ.js`。公开脚本包含：

- reasoning 文本增量与折叠展示、generation/tool 阶段、plan/subagents 等时间线字段。
- 上下文 used/size、用量入口，以及消息、系统提示、Skills、MCP 等上下文分类。
- 文件树代码、按本轮改动生成 Markdown/HTML 卡片的逻辑、预览组件、Skills/MCP 设置动作。
- 文件变更与检查点相关结构，包括外部修改、检查点缺失等失败原因。

这些是静态实现证据。检查点结构、某个组件被打进包，不能独立证明当前远控路由、手机端、当前账号都能使用相应完整功能。本轮未在已配对的 ZCode 账户上进行操作测试。

**特别发现：**卡片生成逻辑把 `compactForRemoteControl` 传为 `suppressWebRemoteCards`；启用时跳过 website 及 html 卡片。因此“桌面有 HTML 预览”不能直接推导为“内嵌官方远控页面也有相同预览”。这也不能证明 ZCode 所有其他入口都不能预览 HTML。

### TermLink 的两条 ZCode 路径

| 路径 | 当前实际含义 | 剩余差距 |
| --- | --- | --- |
| `desktop_remote` | Windows 配置远控链接，通过 TermLink 交给安卓加载官方页面；网页实际通过 ZCode 自己的服务链路通信 | 页面功能由官方远控决定，不能按桌面全量功能计算；不自动融入 TermLink 原生工具卡片、模型控制和成果查看 |
| `native` | TermLink 单独启动 ZCode app-server，转换当前接入的文本、工具、交互和设置协议 | 桌面 Host 的模型授权不会自动继承；尚不能保证独立原生适配具备桌面登录、全部模型/工具/预览能力。需要协议和真实环境验证，UI 不能绕过这一步 |

本机桌面架构和授权边界见 [ZCODE_DESKTOP_REMOTE_ANALYSIS.md](ZCODE_DESKTOP_REMOTE_ANALYSIS.md)。其中较早的入口建议不是当前 UI 完成度清单。

## 推荐的落地方案

1. **先使执行过程清楚。**Windows 适配器按已有协议接入公开 reasoning、工具状态、计划和 usage；安卓用一条紧凑状态行及折叠块呈现。以真实状态结束动画，审批、失败、停止、重连各自处理。沿用已有消息/请求标识，补少量结构化字段和增量游标，不重写整套会话框架。
2. **补齐查看成果。**在现有 AI 页面增加按需打开的“文件/变更”入口；点开后才加载文件详情、Diff 或预览。先做图片、文本、静态 HTML。输出文件由 Windows 校验并读取，经现有加密通道按需返回；聊天里出现任意路径不能自动获得读取权。静态 HTML 限制脚本、外部请求、导航及本地文件访问；完整网站预览另作有授权和隔离的通道。
3. **完善长任务。**草稿持久化、全文搜索、准确通知、分支/回退、技能与工具管理依次补齐，按 Agent/模型/协议实际支持决定显示。对话分支不等于文件回滚，共享目录分支也不等于 worktree 隔离。

界面保持现有紧凑方向：附件/授权在左、模型/推理强度/发送在右；生成状态一行，思考和工具默认折叠，计划有数据才出现，文件和 Diff 用抽屉/全屏详情承载。CLI 页面布局及交互保持现状。

架构继续保持 Windows 为会话、历史和文件权威，服务端负责既有桥接；电脑离线隐藏正文的既定要求不变。不能为加入离线聊天缓存或后台通知，未经需求确认改变这些产品规则。

实施后的关键验收：不同 Agent 不支持能力时正确隐藏；推理块不重复、历史不重播动画；工具失败/取消/审批状态准确；增量断线续传和旧客户端回退；HTML 资源及脚本边界；长 Diff 不静默截断；草稿恢复不重复发消息；原 CLI 回归。此处是后续验收要求，不代表本轮已经执行。

## 证据入口

TermLink 当前实现：

- [AiPage.kt](../android/app/src/main/kotlin/dev/termlink/product/AiPage.kt)：轮询、生成状态、工具、审批、历史阅读。
- [AiReplyContent.kt](../android/app/src/main/kotlin/dev/termlink/product/AiReplyContent.kt)：HTML 原文、图片占位和代码块。
- [AiComposerOptions.kt](../android/app/src/main/kotlin/dev/termlink/product/AiComposerOptions.kt)：附件、授权、模型、队列及命令建议。
- [AiWorkspace.kt](../android/app/src/main/kotlin/dev/termlink/product/AiWorkspace.kt)：内存草稿/队列、持久化未知操作、快照请求。
- [AiSessionActions.kt](../android/app/src/main/kotlin/dev/termlink/product/AiSessionActions.kt)：文本 Diff、用量、导出与尽力通知。
- [AiAgentSession.cs](../win-agent/src/Agent/AiAgentSession.cs)：各 Agent 事件转换；[AiNativeHistory.cs](../win-agent/src/Agent/AiNativeHistory.cs)：有界历史导入。

参考实现及版本：

- HAPI `86c88df93baf5d1f738dd4b202078bdf6dec376e`：[reasoning](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/web/src/components/assistant-ui/reasoning.tsx)、[StatusBar](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/web/src/components/AssistantChat/StatusBar.tsx)、[UpdatePlanView](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/web/src/components/ToolCard/views/UpdatePlanView.tsx)、[PermissionFooter](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/web/src/components/ToolCard/PermissionFooter.tsx)、[CodexDiffView](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/web/src/components/ToolCard/views/CodexDiffView.tsx)、[useSkills](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/web/src/hooks/queries/useSkills.ts)。AGPL-3.0，仅参考行为；本轮未移植源码。
- CC Pocket `8354ffdb03d0df7786fccd703ed3fe9e1969399f`：[HTML 文档隔离](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/apps/mobile/lib/features/file_peek/html_preview_document.dart)、[HTML WebView](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/apps/mobile/lib/features/file_peek/widgets/html_file_preview.dart)、[Bridge session](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/packages/bridge/src/session.ts)。
- Happier `88d45f33a5e5faf3235f621ee168a6f0ca58762d`：[Agent 能力目录](https://github.com/happier-dev/happier/blob/88d45f33a5e5faf3235f621ee168a6f0ca58762d/packages/agents/src/manifest.ts)。支持声明按具体 Agent/runtime 解读，不当作所有后端的实测结果。
- ZCode [公开远控入口](https://zcode.z.ai/remote/v4)、[本次读取的入口脚本](https://zcode.z.ai/remote/v4/latest/assets/index-B-ilXaCQ.js)。解码为字符串后按 UTF-8 编码的 SHA256：`BA8A371A8DF195AE1014E9BC448E2DB5C8B5C61B0907D1BF2A81CFB9E318ECA4`。公开页面可能更新；本轮没有复制其产品资源、读取用户凭据或执行配对。
