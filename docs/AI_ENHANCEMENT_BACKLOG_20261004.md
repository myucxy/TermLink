# AI 增强功能重新盘点（2026-10-04）

## 本轮实施更新

后续 Grok ACP 开发进展：已增加 Windows 适配及模拟协议验证，真实握手/模型查询、回复/配置/取消/恢复与隔离审批通过；
模拟器已完成 Grok 真实 Relay 基本链路。用户指定双端发布后再适配其他 Agent，不要求登录。其他 Agent 尚未接入，N2 不算全部完成。
详见 [Grok ACP 接入记录](AI_GROK_ACP_20261004.md)。

用户确认实施 O1、O2、O3 的分页部分，以及设备卡片 文件/AI 对话/+CLI 同级入口。
O1 已增加有界连续工具组和运行/失败摘要，保留逐项详情及独立审批；O2 已接入账号隔离的
Keystore 加密 UI 状态，按电脑/host/session/request 校验，支持问答、阅读锚点与展开状态恢复。
O3 已接通 50 条分页、截断/空结果提示、旧搜索响应隔离及工具命中展开；关键词高亮与自动加载
命中前后文仍是后续优化，不算本轮全部完成。验证与发布状态见 [本轮记录](DEVICE_AI_RELEASE_030.md)。

本次以当前工作树代码为准，对照此前已核查的 GitHub 项目固定 revision；不将参考项目 README 声明当作本机实测结果。本次复读缓存源码，并重新读取 HAPI PermissionFooter、Mermaid 组件和 CC Pocket rewind 设计文档；没有重新运行参考产品，也没有声称对比其最新 main。产品代码、部署、版本和发布状态未改变。

范围：CLI 与 AI 当前只用服务器转发（Relay Only）。LAN/NAT、打洞及直连切换测试不在待办或发布门禁中。Windows 仍为会话、历史和文件的权威；安卓仅列自己打开的 AI 会话，电脑离线隐藏正文；CLI 页面保持原布局。

## 统计口径

按可独立安排的交付主题，核心清单共 **19 项：8 项新增能力、10 项已有功能优化、1 项原方案架构交付**。一项包含若干关联操作，不等于 19 个缺陷，也不是立即全部开发的承诺。语音、定时任务、跨 Agent 接力等另列可选扩展，不混进核心计数。测试待补和正式发布也不计为功能缺失。

优先级：P1 为改善已有聊天完整性或日常操作；P2 为协议、性能或工作区扩展；P3 为按实际使用需求安排。具体顺序见下文。

## 已实现，不再计为缺失

- 按电脑/Agent 分组的 AI 列表、搜索、置顶、重命名及带真实标题的删除确认；待处理筛选。
- 新建时只显示电脑已支持的 Agent、自动工作目录、自选目录、原生/桌面远控策略。
- 新建及已有会话独立的模型与推理强度下拉；能力随模型变化；不同 Agent 的授权方式与作用阶段。
- 图片/拍照/手机文件附件、电脑文件引用、上传进度、移除、重试；支持时原生图片输入。
- 生成阶段动画与耗时、公开思考摘要、计划步骤、工具状态、停止及支持时的任务补充。
- Markdown/表格/代码高亮、复制、引用、编辑成草稿、导出/分享 Markdown。
- 单次允许/拒绝、问题表单、控制版本和回执校验。
- TermLink 历史分页/恢复、Codex/Claude 原生历史导入、历史授权；支持时从当前上下文分支。
- 搜索、文件列表、图片/文本/静态 HTML 预览、系统保存、按文件 Diff、上游报告的用量。
- 增量修订/游标/reset、有界长轮询、虚拟列表；草稿/附件元数据/待发队列持久化。
- 标准系统通知和会话跳转/审批入口。这不等于已经接入厂商离线推送。

## 8 项新增能力

| 编号 | 未实现部分 | 参考与证据范围 | TermLink 的具体补齐方向 | 优先级 |
| --- | --- | --- | --- | --- |
| N1 | 可靠后台推送及无界面审批 | HAPI 原生应用说明；Happier inbox/push 文档声明 | 接小米/FCM、Token 生命周期、Server 投递、冷启动事件校验、后台动作和真机投递。当前无推送项目；已有通知按钮不能代替通道。胶囊另按厂商准入处理 | P1，依赖外部项目 |
| N2 | ACP 和更多原生 Agent | Happier 的 manifest/ACP backend 源码；HAPI 支持矩阵；Agmente 配置模型 | Grok ACP 已完成开发态接入、模型查询、模拟协议和真实基本聊天/审批验证；模拟器 Relay 已验收。OpenCode/Kimi/Qwen 等尚未接入，不能只增加名字 | P2 |
| N3 | 更细的单次请求授权 | HAPI PermissionFooter 源码明确区分 approved_for_session、工具规则和拒绝原因 | 在协议支持时提供本会话允许、指定工具/命令规则及拒绝原因。不能把当前会话的“完全访问”模式当成这一项已完成 | P1 |
| N4 | 从指定消息重试、分支、回退 | CC Pocket rewind 设计及对应 UI/测试路径；需按具体协议验证 | 保存原生 turn/message 标识，提供目标消息选择和能力校验。当前“编辑”为草稿、“fork”为当前上下文，均不是回到指定消息 | P2 |
| N5 | Skills / MCP 管理 | HAPI useSkills 参考；Happier/AionUi 的管理能力声明 | 显示电脑实际配置和可用状态；按 Agent 支持启用/停用、查看失败原因。当前 slash 命令建议不等于管理页 | P2 |
| N6 | Git / worktree / 文件检查点 | CC Pocket、Happier README 功能声明 | 先 Git 状态与分支，再暂存/提交、独立 worktree、文件撤销。独立时间戳目录不是 worktree；会话回退与文件回退分别处理 | P2 |
| N7 | 子 Agent 父子任务树 | HAPI subagent 类型/组件路径；Happier 子任务能力声明 | 将已上报子任务关联到父会话，展示状态、错误和结果；计划列表已实现，不重复开发。自动跨 Agent 编排另列可选扩展 | P2 |
| N8 | 更完整的成果渲染 | HAPI Mermaid 源码；CC Pocket 媒体预览声明。动态开发服务器是本项目此前规划 | 静态 HTML 之外按需增加隔离 JS/本地开发服务预览、Mermaid/公式、音视频/PDF。当前 SVG 文件以文本查看；不能直接给现有 HTML iframe 开放原生桥或脚本权限 | P2/P3 |

## 10 项已有功能优化

| 编号 | 当前实现与具体缺口 | 参考方式 | 建议改动 | 优先级 |
| --- | --- | --- | --- | --- |
| O1 | Web 工具记录逐条 details；原生备用页已有连续工具分组 | CC Pocket lite projection、HAPI 工具分组 | Web 默认显示整轮工具数量、进行/失败摘要，展开查看逐项；审批始终独立可见 | P1 |
| O2 | Web 阅读位置、展开状态、问答表单答案只存组件内；页面重建无恢复通道 | HAPI TranscriptReadingState 的可保存状态；本项目原生页已有状态复用 | 按账号/电脑/host/session/request 保存位置与未提交回答；审批失效即清理。普通输入草稿已持久化，不能混为“所有草稿都会丢” | P1 |
| O3 | 搜索接口一次最多 50 条并返回 nextOffset；Web 与桥未继续传分页 offset；未加载命中只打开全文 | CC Pocket/HAPI 的历史定位体验；具体缺口由本项目代码确认 | 接通下一页、总数/截断提示、关键词高亮、加载命中前后文并定位消息 | P1 |
| O4 | 附件已有文件 chip、进度和外部打开；缺少聊天内缩略图、图片粘贴/IME 富内容、缩放画廊 | HAPI AttachmentPolicy、CC Pocket 输入栏/图片查看器 | 复用现有安全上传，增加有界缩略图、来源接入和图片查看；不统一强制压缩所有文件 | P1 |
| O5 | 成果为有界扁平扫描列表；文件引用缺行号定位、目录层级和精准成果入口 | CC Pocket 文件查看；Happier 文件编辑声明 | 在工作区边界内按目录浏览；解析 file:line/#L 引用并定位；把本轮生成文件关联到消息。当前不是完整文件编辑器 | P1/P2 |
| O6 | Diff 已有按文件折叠、增删行和分页；缺精确行号/选择行反馈、图片差异、完整 Git 对比来源 | HAPI Diff、CC Pocket 图像 diff、Happier 行级 review 声明 | 添加行号和选择行引用/评论；区分 Agent diff 与 Git diff；保持超限提示，不把 UI 截断当完整变更 | P1/P2 |
| O7 | TermLink 历史可翻页，但内存/归档有界；原生 Codex 导入是近期 turns summary；冷读仍解密整份档案 | CC Pocket 增量历史、Happier 恢复能力目录 | 分离索引/正文、懒加载；按原生协议继续读取更早内容，明确摘要/完整记录和保留边界，提供迁移回退 | P2 |
| O8 | 已有增量修订和 20 秒长轮询；变化消息仍包含累计文本，Put 仍拼接旧字符串；未实现 v2 主动订阅 | CC Pocket seq/historyRevision 及弱网设计（设计不等于全量实现）；Happy ID 归并 | 先测 Relay 传输和长回复分配，再做追加缓冲/批量发布与主动订阅；保留 cursor/reset/operationId，不改成仅降低轮询间隔 | P2 |
| O9 | 上传有断点 offset 和 hash，但仍 24 KiB 一块等回执，Windows 每块 Flush(true)；成果预览先收齐后显示 | 此项是 TermLink 自有桥接性能债，不声称参考项目已有相同优化 | 二进制有界窗口与累计确认，区分收到/持久化；读取可取消、渐进预览。以固定 RTT 和文件大小测量，不先承诺速度倍数 | P2 |
| O10 | 已有待发队列、取回、待处理筛选；缺队列排序/直接编辑、持久未读标识及聚合事项详情 | Happier pending queue / inbox 声明 | 允许编辑和重排尚未发送项；统一展示已打开会话中的审批/问题/失败/完成未读；保持恢复后不自动发旧消息的现有规则 | P2 |

O2 为静态代码确认的生命周期缺口，具体旋转、栏目切换和 WebView 重建场景仍需修复时定向复现；不把未实测路径说成已发生的用户故障。O3 分页缺口可直接从 Windows 返回值与 Android/Web 调用参数对照确认。

## 1 项原方案架构交付

| 编号 | 现状 | 剩余交付 |
| --- | --- | --- |
| A1 | React 正文页面已使用 APK 内置资源；原生输入栏/模型/附件保留 | Windows 携带同一 Web 包，通过认证通道交付；发布签名、公钥轮换、协议版本协商、内容哈希缓存、原子更新与回退。属于此前已讨论的 TermLink 架构后续，不能用某个项目有 WebUI 证明已解决这些环节 |

不必为了“全 Web”立即重写刚完成的原生输入栏；是否迁移应以功能等价、软键盘、附件与性能验证决定。

## 额外可选扩展，不计入核心 19 项

- 语音输入/语音交互：HAPI、Happier、CC Pocket 有相关声明。先系统听写，不把输入法自带语音算 TermLink 已实现；语音批准不默认开启。
- 同一会话切换 Agent、跨电脑迁移和跨 Agent 接力：Happier 有声明，需独立设计上下文/文件/凭据及恢复边界；当前切模型不是切 Agent。
- 定时任务、专业助手/模板和团队任务板：AionUi 有声明，TermLink 尚无对应调度或编排。会增加 Windows 执行生命周期与授权范围，不作为基本聊天前置。
- 共享会话和公开链接：Happier 有声明，会改变当前权限与内容可见范围，只有明确产品需求时再做。
- Windows 本地 AI 聊天页/独立浏览器客户端：可复用已有 Web 展示；目前 Windows 主要承担管理与执行，新增入口仍需认证和协议协商。
- 更复杂的上下文用量分类、额度耗尽后的排队重试：当前用量/成本只显示 Agent 实际报告值；不猜费用、不自动跨账号发送。

## Agent 适配与验证缺口，单独记录

- Codex/Claude 基础真实回复及最新模型/强度真实 Relay 联合验证已有证据；不是所有配置、审批和弱网组合都已全覆盖。
- ZCode desktop_remote 页面策略保留；原生成功模型回复、桌面授权适配和有效配对后的完整远控验收尚未完成。不能从“安装了桌面版”推出独立 app-server 已获得同一授权。
- 通知“允许本次”的模拟器端到端验证此前被工具自动审批拒绝，未完成；拒绝路径及协议测试已有记录。
- 真机后台/锁屏、低内存、长历史性能和大附件吞吐仍需验收；仅服务器转发模式。正式提交/签名制品/升级回滚/发布为交付门禁，不计作新增功能。

## 推荐实施顺序

1. 先完成 O2、O3、O1：问答与阅读状态恢复、搜索分页、工具汇总。修复已有体验的完整性，避免先扩更多按钮。
2. 接着 O4、O5、O6：图片输入/查看、成果导航、行级 Diff，保持紧凑入口。
3. N3 与 O10：更细授权、队列/待处理中心；依赖 Agent 能力时明确隐藏或解释。
4. 根据测量执行 O7、O8、O9；N1 在小米/FCM 项目到位后完成真机闭环。所有传输优化只验 Relay。
5. 然后安排 A1、N2 及其余工作区扩展；可选项逐项确认需求后实施。

## 证据入口

本项目当前源码：

- `ai-web/src/main.tsx`：工具/思考/问答、搜索、文件、Diff、虚拟列表、预览与用量；`CodeBlock.tsx`：高亮和 HTML 入口。
- `android/app/src/main/kotlin/dev/termlink/product/AiWebConversation.kt`：Web 生命周期、允许的桥动作、搜索参数、工作区路径边界和 APK 资源。
- 同目录 `AiModelPicker.kt`、`AiComposerOptions.kt`、`AiWorkspace.kt`、`AiPage.kt`、`AiNotifications.kt`：独立下拉、原生输入/附件、持久化队列、原生工具分组、通知。
- `win-agent/src/Agent/AiSessionTimeline.cs`：长轮询/搜索分页；`AiAgentSession.cs`：消息追加与保留；`AiSessionFeatures.cs`：上传/归档；`AiNativeHistory.cs`：原生 summary；`AiSessionArtifacts.cs`：文件扫描；`src/Common/AiAgentDiscovery.cs`：当前 Agent/策略探测。

固定参考版本（源码证据和 README/设计声明按正文分别标注）：

- HAPI `86c88df93baf5d1f738dd4b202078bdf6dec376e`：[审批](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/web/src/components/ToolCard/PermissionFooter.tsx)、[Mermaid](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/web/src/components/assistant-ui/mermaid-diagram.tsx)、[阅读状态](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/android/app/src/main/kotlin/app/hapi/companion/feature/chat/TranscriptReadingState.kt)、[Agent 矩阵](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/docs/guide/agents.md)。AGPL-3.0，仅参考行为；未复制到产品。
- CC Pocket `8354ffdb03d0df7786fccd703ed3fe9e1969399f`：[功能声明](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/README.md)、[历史同步](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/packages/bridge/src/session.ts)、[简洁视图](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/apps/mobile/lib/features/chat_session/lite_mode_projection.dart)、[回退设计](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/docs/rewind-fork-rename.md)。MIT。
- Happier `88d45f33a5e5faf3235f621ee168a6f0ca58762d`：[能力目录](https://github.com/happier-dev/happier/blob/88d45f33a5e5faf3235f621ee168a6f0ca58762d/packages/agents/src/manifest.ts)、[功能声明](https://github.com/happier-dev/happier/blob/88d45f33a5e5faf3235f621ee168a6f0ca58762d/README.md)。MIT。
- AionUi `6744099b279b991c17e31c243f0920477bd31cb6`：[功能声明](https://github.com/iOfficeAI/AionUi/blob/6744099b279b991c17e31c243f0920477bd31cb6/readme.md)。Apache-2.0；未实测其移动页面。
- Happy/Agmente 的消息归并与能力配置仍参考 [此前调研](AI_MOBILE_CLIENT_RESEARCH_20261003.md)，不单独重复计数。

许可证只说明可参考/使用的条件；实际复制任何源文件、资源或引入依赖前仍须单独核对。
