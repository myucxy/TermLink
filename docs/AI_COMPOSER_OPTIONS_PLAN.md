# AI 聊天模型、附件与授权优化方案

2026-10-03。状态：原始设计；本轮 DEV 实施与验证见 [AI_CHAT_FEATURES_20261003.md](AI_CHAT_FEATURES_20261003.md)，其中记录了附件通道等实现调整和验证限制。保留原 CLI 页面、Dock、终端输入和窗口调整。本文“授权”指 Agent 执行工具的权限；TermLink 手机控制权和模型账号登录是另外两层。

## 当前实现及缺口

| 项目 | 当前代码 | 缺口 |
| --- | --- | --- |
| 模型 | `AiCreateSheet` 已直接显示 `AiModelPicker` 模型行；支持读取电脑模型列表、默认和手工 ID | 仅创建时选择。`AiAgentSession` 保存 readonly model/provider，`ApplyAsync` 没有修改配置动作；Snapshot 不返回当前模型，手机无法可信地显示实际选择 |
| 输入 | `AiComposer` 发送 text；Windows 将其转换为各协议的文本输入 | 无附件选择、传输、消息关联或原生图片内容块 |
| 执行权限 | Codex 创建时固定 on-request/workspace-write；Claude 固定 default；ZCode native 固定 build | 无会话权限模式选择。审批仅 allow/deny，本次作用域 |
| 传输 | AI 请求最多 48 KiB；有文件 E2EE 分块下载及 ACK | `FileTransferRoute` 限制 DOWNLOAD，Android 是接收者；不能直接当作手机上传通道 |
| ZCode 桌面远控 | 独立 desktop_remote WebView | 模型和权限应由网页管理。当前 WebChromeClient 只有进度回调，没有文件选择回调 |

当前代码的模型入口已经是创建面板中的一行，旧文档“放在高级设置”的描述滞后。菜单与方案应以实际代码为准。

## 交互方案

```text
‹ 修复登录问题                  ☁ 控制中
  Claude Code · HOME-PC
─────────────────────────────────────
  聊天内容、折叠工具记录、必要的审批

  [截图.png ✓] [日志.txt 62% ×]         有附件时出现
  ┌─────────────────────────────────┐
  │ 描述问题…                       │
  │ ＋  当前模型⌄   按需确认⌄     ↑ │
  └─────────────────────────────────┘
```

- 将模型、执行模式放在输入框底部同一动作行；不额外堆叠固定工具栏。按钮外观紧凑但各自保留 48dp 触摸范围。长名称单行省略，窄屏将低频参数收进更多菜单。
- 模型入口显示 Windows 已确认的当前值；上游不报告具体默认模型时显示“电脑默认”，不推测型号。点击打开可搜索的模型选择面板，保留“电脑默认”“刷新”“手工 ID”。
- 新建面板沿用同一套模型/模式控件，均非必填；默认独立目录和创建后直接聊天保持。
- 执行模式点击打开短列表，显示当前有效范围。推理强度等选项仅在适配器及所选模型支持时放入该面板，不扩展为大表单。
- ＋ 打开附件面板：图片、拍照、手机文件；“电脑文件”在支持文件选择/引用后再显示。使用 Android 系统选择器，避免要求整盘读取权限。
- 附件状态在输入框上方横向排列，支持预览、取消、失败重试；全部就绪才可发送，支持仅附件消息。失败不丢文本或附件草稿，旋转和栏目切换保留草稿。
- 云朵菜单继续负责 TermLink 控制权、连接、关闭视图与结束会话；不把 Agent 执行权限与手机控制权混为一谈。

## 模型和配置协议

在已有 `strategy=native|desktop_remote` 与 Agent 条目上补充可选能力，不重建通道、不增加完整通用表单框架：

- 模型：是否可枚举、能否创建时设置、能否会话中修改、是否只在空闲时允许。
- 执行模式：Windows 返回受支持的 mode ID、显示名、说明与生效条件。
- 输入：图片、普通文件引用、支持 MIME、每项/总字节/数量限制。
- 审批：逐请求返回可用决定、作用域和有效期；不要由 Android 根据 Agent 名称硬编码按钮。
- Snapshot 补充 `agentId`、当前/待生效模型及模式、`settingsVersion`；缺失字段按旧版本处理，隐藏不可用修改入口。

建议使用现有操作通道增加 `configure`，携带 hostId、sessionId、operationId、controlVersion、expectedSettingsVersion 和修改字段。沿用回执去重；Windows 在串行操作锁内检查状态与能力，再调用实际 Agent 协议。

模型修改第一阶段只在 idle 且无待审批请求时执行。生成中显示“本轮完成后可修改”，不强行重启 Agent。成功回执后才更新选择；明确拒绝保持旧值；超时显示待确认，通过原 operation ID 查询结果，不能猜测生效或重新发起不同操作。每轮消息记录使用的模型/模式，配置变更不改写旧消息。

Claude 已在初始化时使用 set_model，可复用控制请求通道扩展会话切换，但须补真实回执与失败测试。Codex 应按本机导出协议验证下一轮模型覆盖及有效配置；ZCode native 需另行验证。不能从“能列模型”推导“运行中能切换”。不支持原会话切换的 Agent 明确提示需要新会话，不静默重启或宣称保留上下文。

## 执行权限

推荐界面语义，实际展示以 Agent 返回能力为准：

| 模式 | 意图 | 约束 |
| --- | --- | --- |
| 按需确认（默认） | 使用 Agent 的正常审批流程 | 不是每个工具都必然弹窗；保留原生可信规则与沙箱 |
| 计划/只读 | 分析方案，限制修改 | Plan 和 OS 只读沙箱不一定等价，说明实际约束，不能仅发送一句提示词冒充限制 |
| 自动编辑 | 当前工作区内允许编辑 | 不等于自动允许命令、外部路径和网络；各 Agent 独立映射 |
| 完全访问（高级） | 原生支持时减少审批并允许更广操作 | 单独显式确认实际范围，不默认启用，不自动延续到其他会话；审批策略和沙箱分别处理 |

普通审批卡继续显示“允许本次／拒绝”，更多菜单按请求能力显示“本会话允许此工具/规则”等作用域。不把单次允许扩大为永久授权，不通过“手机自动点击同意”模拟模式。提升模式不能追溯批准已经挂起的请求，已有审批单独解决；控制权变化、到期、另一设备已处理时拒绝旧审批。

模型账号仍由 Windows 现有 Agent 管理。手机显示需要在电脑登录的状态，不搬运 API key、Cookie 或桌面 token。ZCode 网页继续使用其自己的登录、模型及授权流程。

## 附件传输与生命周期

推荐复用文件 E2EE 通道，增加明确的 UPLOAD 方向与用途 ai_attachment：

1. Android 通过系统选择器获得用户所选文件，在私有临时缓存准备，计算实际 MIME、大小和摘要；图片提供预览及“原图/压缩”选择。代码截图默认避免有损压缩。
2. Windows 为指定账号/设备/host/session 创建上传接收记录和 attachmentId；控制版本验证后才允许写入。服务端只记录必要的路由状态并转发密文，不保留可读正文和文件内容。
3. 反向复用分块、连续偏移 ACK、断点续传、哈希校验及取消。内容写入隔离临时文件，完成验证后原子转为可引用状态；上传失败不能被消息引用。
4. `send` 携带文本、attachmentIds 和已确认配置版本，数据本体不放入现有 48 KiB AI 请求。Windows 校验所有附件已完成且属于当前会话，再按适配器转换为原生图片输入或受支持的文件引用。
5. 普通文件“传到电脑”与“模型能理解”分别处理。PDF/文档/二进制依赖 Agent 的文件工具或原生输入能力，不能只把路径当成文本就宣称支持视觉或文档解析。

落盘建议为会话工作区内 `.termlink/attachments/<attachmentId>/...`，由 Windows 生成路径，不接受手机任意绝对写入目标，拦截路径穿越及目录链接逃逸。同名文件不覆盖。仅用户确认发送后的内容才交给 Agent；附件上传完成不代表消息已经发送。

建议首版上限：每条 5 个、单文件 20 MiB、总计 50 MiB，最终取产品限制、协议限制和 Agent 限制的较小值，由 Windows 下发，不在两端各自硬编码。大文件先不扩大 JSON；缩略图也要独立预算。

未发送临时上传到期清理；已发送附件跟随 AI 工作目录保留策略，不受 CLI 七天历史规则影响。关闭手机视图不删除工作文件。Windows 重启使旧 host 上传令牌失效，恢复时重新确认，不能重放旧发送。

ZCode desktop_remote 单独补受控 `onShowFileChooser`：只将用户选中的 URI 交给当前允许的页面，取消、离线、页面替换及时结束回调；不开放任意本地文件读取。上传目标和能力由 ZCode 网页决定，不纳入 TermLink 原生附件回执。

## 改动范围和顺序

1. Android + Windows：每 Agent/会话能力、当前配置快照、模型切换、权限模式和审批作用域。已有 AI E2EE JSON 通道可承载，通常不需要改 Server。
2. Android + Windows + Server：手机附件上传路由、发送/接收、附件引用与模型输入映射。当前服务端仅批准 DOWNLOAD，需要实际扩展并测试，不能声称仅升级手机即可。
3. Android：紧凑组合输入框、附件预览状态、系统选择器、ZCode WebView 文件选择。实现随上述能力分步开启。

主要落点：`AiPage.kt`、`AiModelPicker.kt`、`AiWorkspace.kt`、`AiRemotePage.kt`；Windows `AiSessionManager.cs`、`AiAgentSession.cs`、Agent 能力目录；现有文件传输客户端/发送器与 Server 文件操作授权。没有请求发布，本方案不提交、不上传或部署。

## 验收

- Codex/Claude：空闲切换后真实下一轮采用目标模型；非法模型、超时、重复回执、旧配置版本和旧控制权不会假成功。会话上下文及目录保持，生成中修改不会偷偷结束进程。
- 模式/审批：只展示真实支持项；临时允许和会话范围分别生效；拒绝、过期、撤销、双手机并发批准、Agent 自己拒绝均有确定结果。
- 附件：图片与文件、仅附件、重复名字、URI 授权失效、大小越界、摘要错误、断线恢复、取消、重复发送、跨会话引用、Windows 重启、无图片能力模型、磁盘写入失败。
- 模拟器 + Windows：从系统选择器选图、预览、上传、发送、模型真实识别；模式修改后触发一次真实审批；ZCode WebView 文件选择另测。
- 原功能：CLI 布局和输入不变；AI 关闭视图不结束进程；手机离线隐藏正文；升级保留工作文件、账号隔离、新旧版本能力回退。

## 上游行为参考

使用此前调研固定 revision，不把分支新版本或注释当作本机能力验证。部分 raw URL 超时后改用 GitHub Contents API 与已存源码核查。本轮没有运行这些项目或复制其实现。

- HAPI [agentConfig.ts](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/shared/src/agentConfig.ts)：模型/权限配置描述、数据来源、创建/会话阶段适用性。
- HAPI [ChatComposer.kt](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/android/app/src/main/kotlin/app/hapi/companion/feature/chat/composer/ChatComposer.kt)：紧凑输入卡、附件状态、全部就绪才可发送及仅附件发送。
- HAPI [PermissionActionViews.kt](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/android/app/src/main/kotlin/app/hapi/companion/feature/chat/blocks/PermissionActionViews.kt)：逐次操作和更多菜单中的会话级决定，保留协议差异。
- HAPI [AttachmentPolicy.kt](https://github.com/tiann/hapi/blob/86c88df93baf5d1f738dd4b202078bdf6dec376e/android/app/src/main/kotlin/app/hapi/companion/feature/chat/attachments/AttachmentPolicy.kt)：上传限制、图片采样/缩略图。其 REST + Base64 上传与 TermLink 的加密桥不同；不照搬上传架构和统一 JPEG 压缩策略。
- CC Pocket [chat_input_bar.dart](https://github.com/K9i-0/ccpocket/blob/8354ffdb03d0df7786fccd703ed3fe9e1969399f/apps/mobile/lib/widgets/chat_input_bar.dart)：图片预览、移除、粘贴与组合输入；采用交互思路，不引入 Flutter。
- [此前调研](AI_MOBILE_CLIENT_RESEARCH_20261003.md)：Happier 能力目录及支持等级可用于能力建模，不需要整套导入。

HAPI 当前根许可证 AGPL-3.0，本方案参考行为并自行实现；若复制具体代码需重新评估许可义务。MIT 项目复制代码同样需保留适用声明与来源。本轮未新增依赖。
