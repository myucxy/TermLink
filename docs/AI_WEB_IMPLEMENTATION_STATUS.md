# AI Web 实施与验证记录

2026-10-04。本轮继续开发和本地验证，**没有正式发布、提交／tag、外部上传或生产部署，也没有替换正在运行的 Windows 客户端**。保留原目录已有修改。CLI 页面未迁移到 Web，ZCode `desktop_remote` 仍使用独立官方网页策略。

后续安装更新（2026-10-04 11:41 +08:00）：用户随后明确要求安装，已从当前工作树重新构建 Windows Development Setup，并安装、启动到 `D:\Dev\Tool\TermLink`。上段“未替换”仅指首次实现结束时的状态。本次未正式发布、上传或修改 Server。

Windows 安装证据：

- DEV 版本仍为 `0.2.71.127`；安装包 `artifacts/release/0.2.71-127-latest/TermLink-Setup-0.2.71.127-x64.exe`，SHA-256 `bc3b772a374103d9656047a1b1f58af469070fda37842a1f8474c9ffd0eb711b`。源码包含当前未提交改动，不能仅凭 ProductVersion 中的 HEAD 认定为干净提交的正式制品。
- `Verify-LocalRelease.ps1 -ExpectedReleaseMode Development` 通过，Setup 及安装后主程序／Agent／SessionHost 的 Authenticode 均 Valid，连续性 signer `91ae60bc06d866628b8eb2c62f32bde9dab56466`。该 DEV 包无 RFC 3161 时间戳，不属于正式发布资格验证。
- 安装器完成文件替换后返回 2：旧验证目录的客户端仍占据控制管道，导致启动健康检查失败。随后关闭旧客户端及其 Agent，启动安装目录内程序并单独完成健康验证；未声称第一次安装器返回 0。
- 认证控制管道返回 `Success=true, AccountLinked=true, RemoteAccessEnabled=true, RelayState=Connected, AgentVersion=0.2.71.127, LastError=""`，CLI 数量 0。记录：`artifacts/validation/windows-latest-installed-health.json`；包验证记录：`artifacts/validation/windows-latest-verify.log`。
- 旧验证目录保留，既有本地账号数据与 AI 历史未清理。此安装检查不替代后续真实双端 AI 全链路验收。

## 本轮实现

| 工作项 | 实际实现与范围 |
| --- | --- |
| Windows 消息同步 | 消息 ID 索引、增量修订、十进制游标、streamId、缺口 reset；20 秒有界长轮询，100 ms 突发合并，独立控制续期。**不是 v2 主动推送订阅** |
| 并发与响应 | Android 每电脑读操作／命令分开，AI 响应按请求 sequence 路由；Windows watch 与普通请求独立限额；Web 桥保留独立审批／停止槽位 |
| 生成体验 | 动态阶段／耗时、公开思考摘要、计划、工具状态、Markdown／表格／代码高亮、复制／引用／编辑、问答／审批表单、按文件 Diff、Agent 实际报告的用量 |
| 长历史 | 虚拟列表、翻页锚点、稳定绝对历史偏移、全文搜索、完整消息读取。Windows 和手机均有容量上限，不宣称无限历史 |
| 成果文件 | Windows 工作区有界扫描、reparse／ADS／路径边界校验，版本化 64 KiB 分块读取；手机图片／文本／静态 HTML 预览及 SAF 保存 |
| HTML 隔离 | 生成内容不进入可信 UI DOM；无脚本的独立 sandbox iframe + CSP，禁止外部资源／导航／原生桥。修复 WebView 导航拦截误拦 blob 预览 |
| Diff | 默认 16k 字符预览，按需读取 32k 字符页；版本变化拒绝拼接；最多保留 256 Ki 字符，Unicode 边界及超限提示。完整页在 Windows 测试中验证 |
| 手机可靠性 | 加密保存草稿、待发队列、文件引用、附件元数据；附件按账号缓存且恢复后必须手动重试并校验大小／SHA-256／主机／控制版本；恢复队列不会自动发送；注销清理 |
| 原生操作兼容 | 原生紧凑输入框，附件／授权靠左，模型／强度／发送靠右；保留原生历史导入、历史授权、目录选择、fork／导出 |
| 通用通知 | 系统通知、加密本地票据、会话跳转、允许／拒绝入口、最新 Windows 状态验证和真实回执；旧审批不自动抢占控制；处理完成／注销撤销旧票据 |
| Web 交付 | APK 内置 React 静态资源，SHA-256 source/file manifest 和 Android preBuild 校验；新增源码也触发资源过期检查；108 份运行时依赖许可证随包保留 |

Android WebKit 为 1.14.0，前端依赖固定在 `ai-web/package-lock.json`。没有复制 HAPI AGPL 前端或 ZCode 资源，没有重新接入 Termux。旧 WebView 不支持受限消息桥时保留原生聊天展示。

## 验证证据

- Windows 完整 AI 验证通过：Codex／Claude／ZCode 适配、设置 CAS、审批、控制变更、附件重放／hash、归档／恢复／fork、原生历史、timeline、文件范围和 reparse 排除。`artifacts/validation/ai-web-windows-tests.log`。
- timeline 测试覆盖 watch 唤醒／取消、Agent 退出返回终态、10,000 条消息及保留上限后的稳定历史、Diff 分页／版本冲突／Unicode 截断。最终定向复测：`ai-web-timeline-final.log`。
- Windows Agent 完整编译通过，0 warning／0 error。隔离产物：`artifacts/validation/ai-web-agent-build/`，不等于已安装到本机客户端。
- 真实 Codex 0.160.0 和 Claude 2.1.116 返回 `TERMLINK_AI_SMOKE_OK`，state=idle、error 为空。日志：`ai-web-codex-real.log`、`ai-web-claude-real.log`。ZCode CLI 因未指定模型出现 `AGENT_MODEL_REQUIRED`，不计为通过；Grok 未安装。
- Android app 363 项、core-network 338 项测试通过，0 failure／error。包含草稿／队列／附件恢复、审批有效性和拒绝回执、桥 action/path 边界、并发乱序响应。日志：`ai-web-android-tests.log`、`ai-web-android-final.log`。
- Edge/Playwright 4 项通过：10,000 条消息少于 45 个渲染节点、翻历史不被尾部更新拉回、审批输入不随增量丢失、HTML 隔离、阶段／计划／用量／Diff。`ai-web/tests/conversation.spec.ts`。
- `emulator-5556` + Windows 隔离测试 Agent：原生输入发送、流式最终回复不重复、思考、文件列表、PNG 解码、HTML 预览、搜索、用量和 Diff 已通过。`ai-web/tests/emulator-smoke.mjs`；截图：`ai-web/test-results/emulator-static-preview.png`、`emulator-search.png`。
- 模拟器通过系统文件选择器保存 `report.html`，245 字节，SHA-256 `334c3b1aa18b46b41d223fdb5ee46b85aa4d3b60e7608a9e45d757f091c912a6`。`artifacts/validation/ai-web-saved-report.html`。
- 模拟器确认模型／授权菜单可打开，电脑离线即隐藏正文，恢复在线可重新加载。Web 引用进入原生输入框；结束隔离 DEV 进程后重新进入，引用草稿仍保留。`ai-web/tests/emulator-lifecycle.mjs`。
- 标准通知通过 debug-only 投递入口验证：点击定位原会话，“拒绝”后 Windows 最终 `state=idle,pendingCount=0`。此入口模拟通知到达，不代表厂商推送。自动审核拒绝后续创建测试审批／触发提醒命令，仅报告 `blocked by policy`；“允许本次”的模拟器端到端测试未完成，策略和协议验证不能替代此项。

联合测试使用 `AiAcceptanceActivity`、ADB loopback 和隔离 fixture，**不替代产品 E2EE Relay、真实远端 Windows 或小米推送真机验收**。生产协议路由的并发与隔离通过现有自动化测试验证。后续真实 ProductActivity 与 HOME-PC 的模型/强度验证见 `AI_MODEL_SELECTION.md`。

范围更新（2026-10-04）：用户指定当前仅使用服务器转发（Relay Only）。CLI/AI 的 LAN/NAT 及直连切换测试不再安排、不作为未完成任务或发布阻塞项；历史未覆盖记录保持为历史事实，当前验收记为不适用。服务器转发的真实链路与断线恢复仍须按改动范围验证。

## DEV 构建与复现

沿用前次已核验成功发布基线 `0.2.71 / 127`，applicationId 为 `dev.termlink.harness.aivalidation`、名称含 DEV。本次未重新查询 AppShip/Docker 在线基线，不能作为下次正式版本决策。模拟器覆盖安装保留 `firstInstallTime=2026-10-02 17:08:17`，未改正式登录数据。

最终 APK：`android/app/build/outputs/apk/debug/app-debug.apk`，SHA-256 `82ab3c1b9177debf3da4b44c923b0082a878966cb6f411dfe7c9e37bc4693561`。已覆盖隔离 DEV 并打开可信 WebView；最终整屏截图 `artifacts/validation/ai-web-final.png`。本轮当前测试截图和文件含测试内容，不是用户正式会话。

使用 `D:\Dev\Scripts\Ensure-DevEnv.ps1 -Tool dotnet,node,jdk,android-sdk` 检查工具。在 `ai-web` 执行 `npm run build`、`npm run verify`、`npm test`。Android 使用仓库 wrapper（缓存 Gradle 8.13），不使用带全局 HTTP 仓库注入的 Gradle 安装：

```powershell
$env:JAVA_HOME = 'D:\Dev\Env\Jdk\jdk-21'
$env:ANDROID_HOME = 'D:\Dev\Env\Android\Sdk'
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:GRADLE_USER_HOME = 'D:\Dev\Env\gradle\user-home'
# cwd: android
.\gradlew.bat :app:testDebugUnitTest :core-network:testDebugUnitTest :app:assembleDebug `
  '-PtermlinkVersionName=0.2.71' '-PtermlinkVersionCode=127' `
  '-PtermlinkDebugApplicationIdSuffix=.aivalidation' `
  '-PtermlinkServerUrl=http://127.0.0.1:18793' --console=plain
```

HTTP 仅用于隔离 debug acceptance gate，生产不使用该地址。Windows 验证项目使用 `D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe` 编译到 `artifacts/validation/ai-web-validation/` 后执行；`--bridge --fake-agent` 启动独立 fixture，结束时 POST 其 loopback `/shutdown`，不终止用户实际 Windows Agent。

## 尚未完成的后续阶段

模型/强度独立菜单完成后，按当前代码与此前 GitHub 参考版本重新统计的功能清单见 [AI 增强功能重新盘点](AI_ENHANCEMENT_BACKLOG_20261004.md)。该清单区分新增能力、已有功能优化、架构交付和验证缺口，并排除 LAN/NAT。

1. 小米／FCM SDK、Token 注册／轮换、Server 推送网关、冷启动加密信封、无界面 Worker、真机锁屏／进程回收后投递；用户已确认暂无项目。见 [AI_PUSH_INTEGRATION.md](AI_PUSH_INTEGRATION.md)。Android 16 模拟器实测 cached 进程被冻结，后台轮询会暂停，不能承诺常驻推送。
2. v2 主动事件订阅及 Server 转发／回放协议。目前是兼容长轮询与加密 request/reply，未改生产 Server。当前仅验收 Relay，LAN/NAT 不在范围内。
3. Windows 交付且签名的 Web 资源更新、发布公钥／回滚协商；当前只有 APK 内置资源。
4. Windows 历史懒加载索引／分页迁移、delta 字符串缓冲、附件二进制窗口传输；仍保留整份 DPAPI 归档和 24 KiB 逐块持久回执上传。
5. 可执行 JS／开发服务器预览、完整 Git/worktree／Skills/MCP 管理、子 Agent 树、厂商胶囊。当前只显示实际上报的公开摘要／工具信息，不等同于 ZCode 全部能力。

不能将本轮可用 Web 聊天和通用通知理解为整份架构方案全部交付。正式发布前仍需现场版本／签名／已提交 revision／真实链路／回滚等门禁。
