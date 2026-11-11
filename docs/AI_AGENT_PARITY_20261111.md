**AI 对话能力与主机平台适配 — 2026-11-11**

本次在独立 worktree `/Users/myucxy/Developer/TermLink-worktrees/ai-parity-20261111`、分支 `feature/ai-agent-parity-20261111` 开发。原工作区有其他任务，未切换其分支、暂存、清理或覆盖文件。

基线 `2e3c72baf30ac8f732186771c361e98316821670` 是从原工作树复制的本地快照，包含此前未提交工作；不是本次 AI 功能提交。`aaa7c7a` 单独更新既有回执测试：允许待确认期间的只读目录刷新，同时保持原发送回执和重试身份。`c3ea0b7` 单独修正快照中两项原有测试编译问题：Android JUnit 参数顺序、iOS 终端测试缺少已有 API 的 reset 参数。本次功能差异应与该基线比较，不将其他任务代码算作本次实现。没有发布、推送、合并 main 或安装到现有客户端。

**手机按实际主机处理**

主机在会话 capabilities 中报告 `hostPlatform` 和 `pathStyle`。Android/iOS 的 AI 文件、成果、目录及下载均使用当前会话的 `pathStyle` 和能力，不用手机操作系统、Agent 名称或固定 Windows/Mac/Linux 判断远端规则。

- `windows`：验证盘符/UNC 根、Windows 分隔符和保留名称，拒绝 ADS 与设备路径。
- `posix`：保留大小写、反斜杠和合法冒号字符；Mac/Linux 使用同一协议规则。
- 非空未知样式：明确拒绝文件操作；不默认到另一平台。
- 旧主机未报告样式：仅根据该会话实际目录语法兼容，不假设主机操作系统。
- 下载恢复：重新确认原 PC、host、session、目录和文件版本；不会随当前选中电脑重新绑定。

主机自己决定文件能力。Windows 保留原实现；Mac 只有打包的原生文件桥具备所需导出时才声明可用。未接入的 Linux 主机文件后端不会因手机识别 POSIX 就被宣称可用。这次没有新增 Linux Relay 主机运行时或进行 Linux 实机验收。

**实现范围**

| 范围 | 实现 |
| --- | --- |
| 五个现有 Agent | Codex、Claude Code、ZCode、OpenCode、Grok；后两者共享 ACP 适配 |
| 工具呈现 | 版本化 `termlink.tool.v1`；命令、目录、输出、退出码、文件 Diff、MCP、搜索、图像引用、子任务卡片；旧格式和未知格式可查看详情 |
| 流式工具 | Codex 输出/进度和新 item；Claude 参数流、结果与父子流隔离；ACP 局部更新保留省略/null 字段；ZCode 按实际事件更新 |
| 状态与错误 | 不把工具参数流结束当成工具完成；迟到进度不复活终态；错误以固定本地说明呈现，不转发上游敏感错误详情 |
| 历史 | Codex 有界分页完整 item，兼容旧协议；Claude 文本/思考摘要/工具调用/结果还原；保留 OpenCode 原生历史路径；明确容量边界 |
| 能力页 | 功能、Skills、MCP/工具与命令的公开元数据；分别标记可用、未报告、不支持；不把无管理接口等同于不能使用已配置工具 |
| 审批 | 保留各协议原有审批；增加 Codex 权限子集、仅本轮授权及 MCP 基本类型表单；可选字段、数值/长度/枚举约束、未知字段校验 |
| 历史操作 | 更换历史代次并拒收旧分页，结果不确定时中止本地所有者、恢复后读取权威历史；Codex 原生 review、指定已完成轮次 fork、revert；只允许当前线程刚查询的轮次，用户确认后执行；旧协议失败明确返回 |
| Mac AI 文件 | 成果读取、附件上传/续传/校验/移除、文件引用；描述符相对操作，拒绝符号链接和不安全 ACL；丢失文件清理幂等 |
| Mac 发现目录 | 增加 Claude/OpenCode/Grok CLI 探测及账号独立配置目录，保留 Codex/ZCode；不复制用户凭据，不把可启动当成已认证 |

原生高级操作仅在协议提供时开放。没有为其他 Agent 伪造 Codex API。分叉使用原工作目录，不等于 worktree 隔离；revert 只改变对话记录，不恢复文件。MCP URL 模式及复杂/未知 schema 保留不可批准的明确状态；本轮未提供通用 MCP OAuth/配置编辑器或插件安装器。

生成 HTML 继续使用原有隔离静态预览；未放宽主聊天页面 CSP，也未将交互网站放入具有原生权限的页面。Mac 持久归档仍沿用既有主机实现范围，本次丰富的是消息结构和原生历史读取，不宣称补齐所有桌面产品功能。

**实现入口**

- Web：`ai-web/src/ToolCard.tsx`、`toolPresentation.ts`、`AgentFeatures.tsx`、`HistoryActions.tsx`；共享 Android/iOS 打包资源已重新生成并验证哈希及 108 份依赖许可。
- 共享主机：`AiToolPresentation.cs`、`AiAgentCapabilities.cs`、`AiStructuredInteractions.cs`、`AiHistoryActions.cs`；现有 Codex/Claude/ZCode/ACP 分支及历史读取接入这些公共模型。
- 文件：`AiWorkspaceFiles.cs`、`AiSessionArtifacts.cs`、`AiSessionFeatures.cs`、`pc-client/native/macos/files.c`。
- 手机：两端 `AIWebConversation`、Android `AiWorkspace` 和下载模块；保留原生来源校验、原 PC/会话选择与 Relay/E2EE。
- 便携主机：`PortableAdditionalAiDiscovery.cs` 与 `PortableAiHost.cs`。

**验证与重现**

本机工具为 .NET SDK 8.0.303、protoc 31.1、JDK 17、仓库锁定前端依赖及 Xcode。证据在工作树忽略目录 `artifacts/ai-agent-parity-20261111/`。未读取模型账户凭据或启动真实付费模型任务。

| 验证 | 结果 |
| --- | --- |
| Web TypeScript/Vite、资源 manifest | 通过 |
| Chrome Playwright | 49 项通过；含五代理新旧格式、虚拟化、窄屏、原型属性名表单、权限确认和主机路径策略 |
| Portable AI 完整验证程序 | 通过：既有会话/控制/回执、五代理协议回放、目录/附件、丰富历史、能力目录、审批表单、历史操作与独立发现 |
| macOS 原生文件桥 | `-Wall -Wextra -Werror` 编译通过；实际 ACL、符号链接、越界和 private 文件测试通过 |
| Windows AI 验证项目交叉构建 | 通过，0 warnings/0 errors；Mac 未运行 Windows 专属测试 |
| iOS App 与测试 bundle | 无签名模拟器编译通过，核心锁校验保留 |
| iOS AI 测试 | 专用新建模拟器 59 项通过，0 失败；未接入真实账号或主机 |
| Android 专项独立测试 | 19 项路径/桥接/下载测试通过（实际实现，repository/resource 测试替身） |
| Android 完整工程 JVM 测试 | 44 项通过，0 失败；实际工程编译和 JVM 回归，排除不可用的 Direct JNI 构建，不属于 APK 验收 |
| Agent 品牌检查 | 自检及五品牌覆盖通过 |

Android 原构建包含 Windows 固定路径的 Direct JNI 任务，本机也缺相应 NDK。JVM 回归显式排除 `:core-network:buildTermlinkDirectNative`、`verifyTermlinkDirectNativeAlignment`、`verifyTermlinkDirectNativeExports`，没有修改或跳过正式 APK 发布门禁。用户随后授权使用代理下载依赖，本轮只给构建进程设置本机现有代理，不修改系统或仓库永久代理配置。

前端：在 `ai-web/` 执行 `npm run build && npm run verify`；Playwright 的 `tests/playwright.tools.config.ts` 接受 `AI_WEB_ASSETS_DIR` 和独立 `AI_WEB_TEST_PORT`。测试目录与打包目录应分别生成，避免使用旧 bundle。

后端：在 `pc-client/` 使用固定 SDK 执行 `dotnet build tests/PortableAiValidation -p:RestoreLockedMode=true -p:Protobuf_ProtocFullPath=<31.1>`；先用 `native/macos/build-files.sh` 编译文件桥并复制到验证程序输出目录，再通过 apphost 运行。`test_macos_files.py <dylib>` 独立校验真实 ACL/no-follow。

现有运行中的 TermLink、其他任务使用的模拟器及原工作区均保持原状。本次专用模拟器可在验证后关闭。真实 Windows/Mac/Linux 主机 + Android/iOS + Relay 的端到端、模型授权及升级兼容仍需另行验收；构建和协议夹具不作为这类验收的替代。
