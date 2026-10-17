# Grok ACP 接入（2026-10-04，开发态）

本轮先完成代码和模拟协议测试；用户随后确认 Grok 已登录，并要求先测试发布双端，再适配其他 Agent。
开发编译沿用已核验的正式基线 0.3.1/129（发布证据见 RELEASE_0.3.1_129.md），不递增版本。

## 范围

- Windows 检测 Grok 的 PATH 入口及 `~/.grok/bin/grok.exe`，避免安装后旧进程 PATH 漏检。
  通过 `grok agent --help` 验证 stdio 入口，不自动安装、不修改系统 PATH。
- 使用 ACP v1、JSON-RPC 2.0、数字请求 ID；独立 `agent --no-leader stdio` 子进程，
  复用现有 Job、E2EE Relay、操作回执、控制版本和历史存储。
- 模型查询只执行 initialize，解析 Grok `_meta.modelState` 和每个模型的 reasoningEfforts；
  不为读取模型列表创建会话。会话配置使用实际返回的 configOptions 和 set_config_option 回执。
- 接入创建、流式文本/思考、计划、工具增量、单次审批、取消、TermLink 已保存会话恢复。
  prompt 等待整轮完成时不持有操作锁，审批和停止可继续；cancel 10 秒未完成则结束连接，
  明确报告结果不确定。取消后旧审批不能继续允许。
- ACP 文本按增量原样追加，不按相同正文去重；用户回显不重复写入。消息使用独立 turn key，
  不以每次进程重新从零计数的 RPC ID 作为历史消息身份。load 重放不重复覆盖保存的消息。
- 审批只映射原生 allow_once/reject_once；无单次允许选项时禁用允许，不选择 allow_always。
  错 session、探测过程、取消过程和非运行状态的审批自动拒绝；未知反向请求返回协议错误。
- 新建授权提供默认审批和需二次确认的完全访问，分别显式传入 Grok 原生启动权限，
  并传入 session/new/load 的 yoloMode。已有会话暂不提供授权切换；ACP mode 不视为授权模式。
- Android 复用现有 AI 页面与能力解析，增加 Grok 名称/气泡配色和可操作的登录错误提示。
  没有复制官方产品图标，暂用既有通用 AI 图标。CLI 和 Server 无改动。
- Grok 1.0.46 实际 set_config_option 接收 scalar string，与随附示例的嵌套对象不同，
  已按真实回执修正。新建时推理强度在 session/new 返回配置后校验，避免 initialize
  的不完整目录误拒绝有效选项；失败后的进程退出不覆盖原始错误。
- 原生 retry_state 显示限流/网络重试，恢复正文/工具后清除提示，停止仍可用；
  不透传上游原始错误 URL，不把间歇性 HTTP 429 误报成正常生成或登录失效。

## 已验证

- 受测 Grok Build `1.0.46 (2765805b9442)`；用户安装位置为 `$env:USERPROFILE\.grok\bin\grok.exe`，也可由 PATH 探测。
- 实际只读握手及模型查询成功。目录是动态返回，不能硬编码模型名称或统一推理档位。
- 登录后真实创建、模型切换、独立推理强度切换、精确单条回复、DPAPI 归档、进程重启
  与上下文续聊、取消均通过。上游曾间歇返回 429，失败记录保留；后续有限复测成功。
  未读取/复制 auth.json、API key 等凭据。
- 隔离目录真实审批验证：reject_once 后文件不存在；allow_once 前文件不存在、回答后
  文件创建成功。没有把 allow_once 转为 allow_always，未扩大用户授权。
- emulator-5556 覆盖安装 0.3.1/129 DEV，实际新建页 Grok 能力气泡、原生模型目录、
  独立强度与默认目录验证通过。通过现有账户的真实 E2EE Relay 到临时新版 HOME-PC Agent，
  创建 Grok 对话、收到回复、思考、工具与工作区文件；隔离 loopback 结果单独记录，不冒充 Relay。
- Windows AI 全量验证通过，包含既有 Codex/Claude/ZCode 测试；新增 ACP 模拟子进程验证
  模型去重/能力、配置确认与拒绝、工具合并、精确增量、计划、一次性授权、错误会话隔离、
  不支持的反向请求、取消、取消后旧审批、恢复不覆盖原历史、子进程退出及登录错误。
- Windows Agent 工程编译零错误、零警告。Android 编译及四个测试类共 19 项通过：
  AiConfigurationTest、AiModelTest、AiStrategyTest、AiWorkspaceTest。另有 Android 展示/Web 桥
  11 项、Web Playwright 12 项及 Windows 桌面 headless、安装/升级/卸载通过。

日志位于 Git ignored 的 `artifacts/validation/ai-grok-*.log`。

重跑（先按开发环境清单检查工具）：

```powershell
& 'D:/Dev/Env/dotnet-sdk-8.0.303/dotnet.exe' run --project win-agent/tests/AiAgentValidation/TermLink.AiAgent.Validation.csproj -p:Version=0.3.1 -p:AssemblyVersion=0.3.1.129 -p:FileVersion=0.3.1.129 -- --acp-only
# 只读本机检测与模型能力，不创建会话：
& 'D:/Dev/Env/dotnet-sdk-8.0.303/dotnet.exe' run --project win-agent/tests/AiAgentValidation/TermLink.AiAgent.Validation.csproj --no-build -- --grok-probe
```

## 明确未完成的范围

- Grok 真机后台、长时间运行及全部授权/网络组合未全覆盖，不从本次基本链路验收推断。
- 外部直接创建的 Grok 原生会话目录导入；当前仅恢复 TermLink 保存的 Grok 引用和历史。
- 会话中途授权切换、自动审批、问答扩展、usage 扩展等未验证协议，不暴露为已支持能力。
- 本机握手未声明原生图片输入，按电脑文件引用处理附件，不宣称识图能力。
- Gemini、OpenCode、Qwen、Kimi、Copilot、Cursor、Pi 等未新增适配或伪造可用状态。
  本轮的 JSON-RPC/审批/流式基础可复用，但发现、启动参数、配置和扩展需逐项确认。
- 已安装模拟器 DEV；Windows 安装文件未覆盖，联测使用临时新版 Agent；正式发布状态
  以本轮发布记录为准。Server 无需因本轮代码改动升级。
  LAN/NAT 测试不在本轮范围（用户指定 Relay Only）。

## 参考边界

依据本机 Grok 随附 user-guide/15-agent-mode.md、14-headless-mode.md、
22-permissions-and-safety.md 和实际 help/initialize 响应自行实现。
参考 HAPI 缓存的 Grok/ACP 行为说明：精确 delta、不要把 mode 当权限、认证错误提示。
HAPI 为 AGPL-3.0，本轮未复制/改编其源码、资源或依赖，亦未引入 Grok 二进制到发行包。
