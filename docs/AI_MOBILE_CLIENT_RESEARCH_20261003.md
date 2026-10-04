# 多 Agent 手机聊天客户端调研（2026-10-03）

本次通过 GitHub 仓库搜索、README、许可证及部分关键源码核查；没有安装或运行这些项目，也没有进行各 Agent 的兼容性实测。仓库宣称的支持范围不等于本机 Windows 已验证可用。调研不改变 TermLink 产品代码或发布状态。

## 结论

存在接近需求的开源实现，但不能据此承诺“所有 AI Agent 全功能统一支持”。最适合 TermLink 的组合是：参考 Happier 的 Agent 能力目录与 ACP 接入，参考 CC Pocket 的移动聊天和增量同步，参考 HAPI 的原生 Compose 交互行为。保留现有 Windows 执行权威、E2EE 桥接和独立 CLI 页面。

“支持很多模型的聊天客户端”与“控制电脑上现成的 Agent 会话”不同。后者还需要处理工具执行、审批、问题回答、取消、原生会话 ID、历史和进程状态。本次优先筛选后者。

## 项目比较

| 项目 | 手机形态与接入范围 | 值得借鉴 | 许可证与边界 |
| --- | --- | --- | --- |
| [Happier](https://github.com/happier-dev/happier) | Expo / React Native Android、iOS、Web；目录包含 Claude、Codex、OpenCode、Kimi、Qwen、Copilot、Cursor、Grok、Pi 等，以及 custom ACP | 协议适配、分能力支持状态、模型/权限/恢复能力、跨设备审批 | 根目录 LICENCE 为 MIT；功能较多，不适合整套移植到 Kotlin/.NET |
| [HAPI](https://github.com/tiann/hapi) | 当前已有 Kotlin Compose Android、Swift iOS、Web/PWA；Claude Agent SDK、Codex app-server、多个 ACP Agent、Pi RPC 等 | Compose 聊天分块、工具分组、审批、阅读位置、历史翻页、配置描述 | 当前根许可证 AGPL-3.0；先参考行为。若复制/改编源码，需单独评估合并、分发、联网服务对应的源码义务及具体文件许可 |
| [CC Pocket](https://github.com/K9i-0/ccpocket) | Flutter Android/iOS；电脑 Bridge 管理 Codex 和 Claude | 主机权威、增量历史、输入回执、断线恢复、简洁聊天投影 | MIT；Agent 范围较窄，Flutter 组件不能直接放进 Compose |
| [Happy](https://github.com/slopus/happy) | Expo 手机/Web；主要围绕 Claude Code、Codex | 消息归并、localId/serverId 对应、审批与工具结果关联、加密同步 | MIT；其服务器保存加密会话的方式不同于 TermLink 当前边界 |
| [Agmente](https://github.com/rebornix/Agmente) | iOS Swift；ACP 与 Codex app-server 两类客户端 | 不同协议共同呈现会话、动态配置选项、协议测试与会话隔离 | MIT；不是 Android 客户端，可参考协议层设计 |
| [AionUi](https://github.com/iOfficeAI/AionUi) | 桌面 Cowork 平台，README 提供 WebUI 手机远程入口；支持多种 CLI/ACP Agent | 安装探测、多 Agent 统一入口、模型与权限能力配置 | 仓库标示 Apache-2.0；平台和内置 Agent 范围更大，本次未验证其 mobile 目录的原生客户端完成度 |

其他搜索结果：

- [Lody](https://github.com/LodyAI/Lody)：仓库标示 Apache-2.0，提供手机/桌面入口和 ACP，重点在共享工作区、团队协作及 CRDT 同步；可作能力/UI 参考，当前不需要引入其协作数据系统。
- [CloudCLI / Claude Code UI](https://github.com/siteboon/claudecodeui)：响应式 WebUI，支持多种编码 Agent，当前根许可证 AGPL-3.0；可以参考项目/会话/文件切换体验。
- [AgentAPI](https://github.com/coder/agentapi)：README 明确标记 deprecated。它用终端模拟与输出差分拼出聊天消息，不推荐作为 TermLink 新 AI 聊天层基础。

MIT/Apache 项目同样需要保留适用版权、许可证和 NOTICE，单独核查引入文件、依赖及资源的许可。项目名称、图标及商标不因为源码许可宽松就能直接使用。本次没有复制供应商产品源码到 TermLink 构建目录。

## 已核查的关键源码

1. **Happier 能力目录和 ACP backend**
   - [Agent 类型与支持级别](https://github.com/happier-dev/happier/blob/88d45f33a5e5faf3235f621ee168a6f0ca58762d/packages/agents/src/types.ts)：分别表达 supported / unsupported / experimental，而非只判断进程能否启动。
   - [Agent manifest](https://github.com/happier-dev/happier/blob/88d45f33a5e5faf3235f621ee168a6f0ca58762d/packages/agents/src/manifest.ts)：原生恢复 ID、会话能力和不同 runtime 的能力覆盖。
   - [ACP 配置](https://github.com/happier-dev/happier/blob/88d45f33a5e5faf3235f621ee168a6f0ca58762d/packages/agents/src/acp.ts)、[共享 ACP backend 创建](https://github.com/happier-dev/happier/blob/88d45f33a5e5faf3235f621ee168a6f0ca58762d/apps/cli/src/agent/acp/catalog/createCatalogDefinedAcpBackend.ts)：复用通信与会话流程，保留个别 Agent 的差异。
2. **CC Pocket 主机历史与移动简洁视图**
   - [session.ts](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/packages/bridge/src/session.ts)：实际包含 historyRevision、seq、增量获取和历史低水位后的 snapshot 回退。
   - [弱网同步设计](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/docs/design/network-resilience-sync.md)：区分客户端输入 ID、回执、历史游标、兼容回退。设计文档包含阶段规划，不能单凭文档认定每一项已实现。
   - [lite_mode_projection.dart](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/apps/mobile/lib/features/chat_session/lite_mode_projection.dart)：简洁模式只改变显示投影，保留完整记录；必要问题/审批不随工具细节一起消失。
3. **Happy 消息归并**
   - [typesMessage.ts](https://github.com/slopus/happy/blob/9f9c4de3c80e689cb002e9047501e6d3e0ab686a/packages/happy-app/sources/sync/typesMessage.ts)、[reducer.ts](https://github.com/slopus/happy/blob/9f9c4de3c80e689cb002e9047501e6d3e0ab686a/packages/happy-app/sources/sync/reducer/reducer.ts)：按消息和工具 ID 归并，并处理发送回执先于本地临时消息到达的情况。应吸收一致性规则，不照搬大型 reducer。
4. **HAPI Compose 与 Agent 配置**
   - [支持矩阵](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/docs/guide/agents.md)：多种协议并存；每个 Agent 的权限、恢复与本地/远程能力不同。
   - [agentConfig.ts](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/shared/src/agentConfig.ts)：配置字段类别、来源与创建/会话阶段适用范围。
   - [ChatTranscript.kt](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/android/app/src/main/kotlin/app/hapi/companion/feature/chat/ChatTranscript.kt)、[安卓说明](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/android/README.md)：历史读取、返回最新、长内容与阅读状态。这些是行为参考，不是已批准复制的实现。
5. **ACP 协议本身**
   - [Agent Client Protocol](https://github.com/agentclientprotocol/agent-client-protocol)：当前 README 声明稳定协议版本为 1，以 initialize 协商的 protocolVersion 与能力为准，不能根据 SDK 包版本判断兼容性。
   - ACP 统一的是客户端与 Agent 的交互，不会让没有 ACP/适配器的程序自动获得工具审批、模型列表或恢复能力，也不替代 TermLink 跨设备鉴权和加密传输。

## 对 TermLink 的具体建议

### 1. 优先补齐能力描述和 ACP

沿用现有 `strategy=native|desktop_remote`，它决定原生聊天或 WebView；将协议作为另一维度，保留目前 Codex、Claude、ZCode 协议并增加 `acp`。

在现有 Agent 条目增加可选能力：模型枚举、运行中切换模型、模式选择、审批、问题回答、历史读取、恢复、图片输入。应标记未知/不可用/实验/已支持，只有现场协议与当前会话实际支持才展示对应按钮。现有全局 `modelListing` 只表示 Windows 支持该查询，不能代表所有 Agent 都提供列表。

Windows 复用已有进程生命周期、JSON 请求关联、超时与 Job 清理，增加共享 ACP 适配器。第一轮选一到两个本机实际安装、能验证的 ACP Agent；通过初始化、创建、消息、审批/拒绝、取消、退出及不支持能力测试后再扩展。无需先替换已验证的 Codex app-server 或 Claude stream-json 接入。

主要落点：`win-agent/src/Common/AiAgentDiscovery.cs`、`win-agent/src/Agent/AiAgentSession.cs`、`AiSessionManager.cs`、Android `AiWorkspace.kt` / `AiModelPicker.kt`。

### 2. 统一消息与工具结构，压缩手机显示

目前 Windows `Message` 只有 Id/Role/Text，部分工具直接把 JSON 转成 Text。建议按需补充 turnId、toolCallId、kind、status、关联请求 ID 与可读摘要。Windows 负责协议转换，安卓只渲染统一类型。

默认展示用户输入、回答、简短执行摘要；连续工具活动折叠成一组，展开才查看命令、文件和差异。审批和问题单独保持可见。缩小显示内容不删除完整工具记录。继续采用单行模型入口、紧凑底部输入栏，不扩张固定工具栏。

主要落点：`AiAgentSession.cs` 的 Receive 方法和 Snapshot，`AiWorkspace.kt` 的消息解析，`AiPage.kt` / `AiReplyContent.kt` 的显示。

### 3. 增量同步先于扩大轮询内容

当前 AI 页面活动会话约每秒 poll，回复为受大小限制的完整快照。建议在同一 AI 请求通道添加可选 `afterSeq`，Windows 返回顺序事件和最新状态；旧客户端继续完整快照。事件超出保留窗口时明确要求 snapshot，不能静默缺消息。

复用既有 operation ID 回执去重，不另造一套发送 ID。不把“已发送到网络”当作“Windows 已接收”，也不把“已接收”当作“Agent 已执行”。审批回复继续校验控制版本、请求 ID 和有效期。Windows hostId 改变后使旧游标及未决操作失效，不能向新进程自动重放旧操作。

历史内容和可恢复原生会话索引仍由 Windows 保管。跨重启恢复是新的一次受控操作：验证原生 Agent 的会话 ID/目录/恢复能力后执行，不能因为磁盘存在记录就显示 RUNNING。仍遵循手机只列自己打开的会话、电脑离线隐藏正文、服务端不承载可读聊天历史的现有规则。

### 4. 保留 ZCode 的独立远控策略

上述已核查的支持清单不能证明已解决 ZCode 桌面账号和远控接管。不要混淆 Jcode、Qoder 与 ZCode。ZCode 继续使用已实现的 `desktop_remote` WebView，原生模型仍按本机协议能力处理；只有其公开协议实际可验证时才扩展原生聊天。

## 建议顺序与验收

1. 先补能力描述和消息结构，再接入一个真实 ACP Agent，证明同一安卓聊天页面可复用。
2. 优化工具/审批卡片和长消息阅读，再做增量同步与 Windows 历史分页。
3. 图片附件、通知、跨 Agent 转交等另列后续需求，不在本轮调研中承诺实现。

验证矩阵应包括：Codex/Claude 原功能回归；新 ACP 的拒绝审批与取消；模型/设备切换；Agent 退出与 Windows 重启；弱网漏事件、重复回执、错序；历史分页保持阅读位置；新旧客户端能力回退；原 CLI 页面布局和交互不变。当前调研没有执行这些产品测试。

本次读取的上游 revision：Happier `88d45f33a5e5`，HAPI `86c88df93baf`，CC Pocket `8354ffdb03d0`，Happy `9f9c4de3c80e`，Agmente `87f224e7d588`，AionUi `6744099b279b`。只读检索材料保存在 Git ignored 的 `artifacts/research/agent-mobile-20261003/`。
