# 独立 AI 会话（开发中，未发布）

> 历史阶段记录（2026-10-03）。“未发布”、功能缺口和 Agent 支持范围描述当时状态，不能代表当前版本。当前入口见[架构说明](architecture/CURRENT.md)，实际交付见[发布记录](releases/README.md)。以下原始记录保留。

设计基线：`faca661`，本地标签 `design/android-ai-agent-v3`。本轮代码未提交或发布到 AppShip、Docker Hub。2026-10-03 已升级本机 Docker 开发服务端，使用独立 Android 测试包与暂存 Windows 客户端完成真实加密桥联合测试，未覆盖正式客户端安装文件。部署、回滚和现场限制见 [联合测试记录](AI_AGENTS_JOINT_TEST_20261003.md)。

## 实现范围

- Windows 设置新增 AI Agent，进入时自动检测，支持重新检测，展示安装路径、版本、协议状态。检测执行有超时和输出上限，不读取模型账号凭据；用户配置的 ZCode 配对地址单独以 DPAPI 保存。Codex、Claude Code、ZCode 使用各自的结构化协议；Grok 在无已验证适配器时显示不支持。
- 安卓底部为设备 / CLI / AI / 设置；原 `SESSIONS` 路由、持久化键、SessionsPage 及其后的终端、历史、Dock、输入、鼠标、窗口尺寸实现保留。AI 使用独立草稿、选择、阅读位置和已打开列表，旋转由 ViewModel 保留。设备页列出电脑本轮的全部 AI 会话，AI 页只列手机打开的会话。
- AI 支持创建、文本发送、工具详情、一次性批准/拒绝、问题选项/文字回答、中断、获取/释放控制、关闭手机视图、确认结束进程。按钮保持至少 48dp 触控范围；输入法展开时隐藏 AI 页主导航。
- 新增界面已按 V3 紧凑设计调整：三行列表与待处理筛选、合并会话头部、分组云朵菜单、单页创建面板（仅显示可用 Agent，默认工作目录，高级参数折叠，创建后直接聊天）、组合输入栏、折叠工具/审批详情；问题单选与多选分别呈现，未提交回答在旋转与栏目切换后保留。Windows 目录补充标题、摘要和待处理数，不下发完整正文。现场验证见 [UI 复核记录](AI_UI_V3_VALIDATION_20261003.md)。
- Windows 是状态和内容权威；电脑离线时安卓隐藏正文。关闭手机视图不结束 Windows 进程。结束进程、Agent 退出/崩溃、账号失效有独立处理；Windows Job 限制避免创建的 AI 进程成为孤儿。

## 桥接与边界

新增可选能力 `ai_agent_v1`，protobuf 消息 92/93，`session_id` 使用 PC ID。复用既有设备请求的 E2EE、密钥 epoch、收发双方身份、请求序号和连接 generation 校验。服务端仅鉴权、转发密文，不解析正文或审批。第一版整个 AI 通道要求 MANAGE；旧客户端/旧服务端未协商能力时不进入该通道。AI 控制权独立于 CLI 控制租约。

AI 当前通过 Relay 传输；原 CLI 的 LAN/NAT 策略不变。不得把 AI 当前链路宣称为直连。AI 请求不占用终端入站处理队列，有 8 个并发请求槽；Android 的 AI 请求锁也独立于终端目录和清理。

每次变更有手机生成的 operation ID，Windows 同一运行实例保存对应请求和回执：重试只能返回同一操作，ID 与正文冲突拒绝。手机在发送前用现有 Android Keystore 加密保存未决请求，收到回执后清除，进程重建后只能重试原请求。新 Windows 实例拒绝旧 host ID，不把旧请求在重启后重新执行。控制权使用版本及 2 分钟续期，旧控制版本拒绝；未决审批 10 分钟到期后拒绝，未知交互也拒绝，不自动授予持久权限。

当前边界：每个 Windows 实例最多 8 个活动 AI 进程、64 个会话记录，回执最多 4096 项/约 16MiB（到达上限拒绝新操作）。聊天视图保留最多 128 项、约 48000 字符，单项 16000 字符；超过时明确显示只保留最近内容。完整记录由 Agent 自己保存，尚未实现跨 Windows 重启导入原生会话、完整 AI 历史翻页、附件、模型市场或 Windows 本地聊天页。Windows 重启后手机旧引用不能显示成 RUNNING。

## 实测协议与限制

| Agent | 现场版本 | 结果 |
| --- | --- | --- |
| Codex | 0.160.0 | app-server 初始化、thread/start、turn/start 和真实文本往返通过；模拟器成功收到指定回复 |
| Claude Code | 2.1.116 | stream-json 初始化、无正文的 set_model 成功回执、真实模型回复通过；安卓经 termlink2 加密桥收到 TERMLINK_CLAUDE_E2EE_OK。本机默认模型不可用，测试显式使用 glm-5.3-flash[1m] |
| ZCode | 0.16.9 | 独立 app-server 启动、runtime preferences 反向请求、session/create、session/subscribe 通过；本机 CLI 未选择默认模型，真实发送返回 CONFIGURATION_ERROR，未通过模型回复 |
| Grok | 未安装 | 未伪造可用能力，无结构化适配器 |

ZCode 桌面打包的脚本需要安装包自带 provider 配置路径；适配器使用现有安装路径，未复制其凭据或源码。独立 CLI API 模型可通过创建表单指定电脑已配置的 Provider ID / 模型 ID；模型有效性以 Agent 回执为准。需要 `interaction/requestProviderRuntimeHeaders` 的 ZCode 桌面账号模型尚不支持，明确返回失败；不能谎称 headersApplied，也不能提取桌面 token 作为临时绕过。

ZCode 桌面远控采用独立策略：用户在 Windows 设置中粘贴完整远控地址，安卓从 AI 入口内嵌官方网页。无需通过手机扫码导入。网页与官方 Relay 通信，访问地址由 TermLink E2EE 传递；不把网页会话伪装成本地 app-server 会话，也不提取桌面 token。协议与测试边界见 [策略实现记录](AI_STRATEGY_VALIDATION_20261003.md)。

接口依据为本机 CLI 帮助、Codex 导出的 JSON Schema、ZCode 已安装 0.16.9 的协议行为及其本机源码参考；未复制供应商代码或资源。官方 Codex 文档访问返回 403，因此未把它记为已完成的官方在线文档核验。

## 验证（2026-10-03）

- Windows Agent / Desktop Debug 编译通过；AgentControl V13 往返及 V5–V12 兼容检查通过；Avalonia headless 验证新增检测/重扫及原设置、终端命令、导航、托盘生命周期通过。
- `win-agent/tests/AiAgentValidation`：三个适配器的消息、拒绝审批、旧控制版本、过期请求、问题回答与结束子进程；管理器并发重复创建、冲突 operation ID、旧 host ID 通过。最终复核补充畸形回答拒绝、ZCode 选项显示文本到协议值的映射检查。这里的交互错误矩阵使用协议 fixture，不代表 Claude/ZCode 的真实模型验证。
- Android app：332 tests；core-network：337 tests，均无失败或跳过。包括 AI 未知回执重试、进程重建恢复请求、关闭视图不结束任务、延迟目录响应不能覆盖新操作回执、请求超时后继续轮询及原请求重试、目录摘要/待处理数、创建后首条消息保留为草稿、回答草稿随请求失效清除、CLI 内部导航键保持、E2EE 错设备/错 epoch/重放/能力缺失及旧目录/清理回归。
- Go `internal/realtime` / `internal/realtimewire` 通过，新增 AI 的 MANAGE、撤销、跨账号、方向、能力、ACK、过期连接检查，确认不创建终端订阅或 ACK 状态。
- 模拟器 `emulator-5556` 安装独立 `dev.termlink.harness.aivalidation` Debug 包：四栏、创建真实 Codex、输入法/底栏、真实回复、控制权过期后重新获取、确认结束并收到 Windows 回执。第二轮收到 `TERMLINK_AI_FINAL_OK`，验证离线隐藏正文及恢复、设备页会话状态、关闭视图不结束任务、重开同一任务恢复正文、横竖屏旋转保留未发送草稿。最终 Windows 回执为 exited，临时桥和 adb reverse 已清理。测试不覆盖/注销正式包。
- CLI 保留校验：从 `private fun SessionsPage(` 至文件末尾与设计基线逐字一致（忽略换行），未改终端页面布局和功能。

主要现场证据位于 `artifacts/validation/`：`ai-validation.log`、`ai-agent-build.log`、`ai-dotnet-build.log`、`ai-ipc-test.log`、`ai-desktop-test.log`、`ai-go-test.log`、`ai-android-tests.log`、`ai-acceptance-build.log`、`ai-real-smoke.log`、`ai-real-zcode.log`，以及 `ai-reply.png`、`ai-ime.xml`、`ai-control.xml`、`ai-ended.xml`。制品和日志不提交。

最终复核使用 `ai-final-validation.log`、`ai-final-agent-build.log`、`ai-final-desktop-build.log`、`ai-final-ipc-test.log`、`ai-final-desktop-test.log`、`ai-final-android.log`、`ai-final-regression-summary.log`；界面证据为 `ai-final.png`、`ai-offline.xml`、`ai-online.xml`、`ai-device-after-close.xml`、`ai-view-restored.xml`、`ai-rotated.xml`、`ai-rotation-restored.xml`、`ai-final-ended.xml`。双端编译为零错误，Windows 编译同时为零警告。

首轮模拟器测试使用 `AiAcceptanceActivity` + Windows `AiAgentValidation --bridge` 的显式 loopback 测试桥；这部分证据仅覆盖界面与适配器。随后通过普通 `ProductActivity`、真实 Windows Agent、`https://termlink2.kget.cc:8910` 与本机 Docker 完成 Claude 真实 E2EE Relay 联测，无 adb reverse 或本地 acceptance bridge。真实 LAN/跨 NAT、新签名发布制品验收及 ZCode 模型成功回复仍未完成；不能把开发联测宣称为正式发布验收通过。

## 重跑

先运行 `D:\Dev\Scripts\Ensure-DevEnv.ps1 -Tool dotnet,jdk,android-sdk,node,python,go`，使用清单中的工具。

```powershell
& 'D:/Dev/Env/dotnet-sdk-8.0.303/dotnet.exe' run --project win-agent/tests/AiAgentValidation/TermLink.AiAgent.Validation.csproj
& 'D:/Dev/Env/dotnet-sdk-8.0.303/dotnet.exe' run --project win-agent/tests/AgentControlValidation/TermLink.AgentControl.Validation.csproj
& 'D:/Dev/Env/dotnet-sdk-8.0.303/dotnet.exe' run --project pc-client/tests/Desktop.HeadlessTests/TermLink.Desktop.HeadlessTests.csproj -p:BuildInParallel=false
# server 目录：go test ./internal/realtime ./internal/realtimewire
# android 目录，配置 JAVA_HOME / ANDROID_HOME / GRADLE_USER_HOME 后：
./gradlew.bat :app:testDebugUnitTest :core-network:testDebugUnitTest
```

`--real-smoke --send` 会创建隔离临时目录并使用现有 Agent 账号做真实模型请求，结束后停止测试进程，不是纯离线测试。未取得真实回复时不能把退出状态或仅成功初始化当成通过。

模拟器测试构建：`./gradlew.bat :app:assembleDebug '-PtermlinkDebugApplicationIdSuffix=.aivalidation' '-PtermlinkServerUrl=http://127.0.0.1:18793'`。测试桥仅接受 loopback；启动 `AiAgentValidation --bridge` 后用 `adb reverse tcp:18793 tcp:18793`，打开 `dev.termlink.product.debug.AiAcceptanceActivity`。该 Activity 只存在于 Debug，且要求独立包名和显式本地测试配置。完成后 POST `/shutdown` 停止桥并删除 adb reverse 映射。

## 实现策略与兼容

统一能力条目保留 `id/name/path/runtimePath/version/protocol/status`，追加 `strategy`：

- `native`：现有结构化协议，TermLink 原生聊天；旧条目缺少策略字段时按此处理。
- `desktop_remote`：当前用于 ZCode；已配置合法地址时 `status=available`，安卓打开 WebView。未知策略不展示为可创建能力。
- 目录只包含策略，不包含配对链接。`remote` 只读请求携带 `hostId/agentId`，通过现有 `AI_AGENT_REQUEST/RESPONSE` E2EE 路由返回临时链接；不写入操作回执或手机已打开列表。
- Windows 清除地址即恢复原本地探测结果。保存/清除在下次能力刷新生效，不必重启 Agent；旧手机误发 native create 时返回 `AGENT_STRATEGY_CHANGED`，不启动错误进程。
- catalog 提供 `defaultDirectory`、`directoryMode=per_session`、`directoryBrowsing=true`。安卓每次新建默认自动目录；Windows 在接受创建时生成 `<安装目录>\AiWorkspaces\<agentId>_yyyyMMdd_HHmmss_fff_<8位随机值>`，回执返回实际路径。相同 operation ID 的重试不会重复建目录；关闭/结束会话不删除工作文件。
- 点击“修改”可输入本地绝对目录或“浏览选择”电脑磁盘、子目录；“恢复默认”切回自动目录。只记忆成功选择的自定义目录，不将上次自动生成的目录复用给新会话。旧电脑缺少 per_session 标记时仍使用已有目录逻辑；浏览能力缺失时允许手工填写。
- 只读 `directories` 请求复用 AI E2EE 通道，携带 hostId/path/offset（路径字段名为 `directory`），Windows 返回仅目录的分页列表；电脑重连或离线后不能继续选取旧结果。手机普通文件管理状态不受影响。
- 新建不再收集首条消息；用户进入聊天后发送。模型、Provider 放入高级设置，ZCode 网页策略不出现原生工作目录或模型参数，其工作空间由 ZCode 自身管理。
- 原生聊天支持代码围栏、代码横向滚动、复制代码/完整回复；生成时发送按钮变为停止按钮，仍通过 Windows 控制权及回执校验。点击会话标题查看、复制完整目录。
- Windows 安装器在替换整个安装目录时转移 AiWorkspaces，升级失败与远程回滚时移回；冲突时保留双方并停止，不覆盖用户文件。卸载将其移到安装目录旁的独立保留目录。目录无写权限时明确提示，不悄悄改用另一位置。
- 网页入口只持久化设备与 Agent 引用，关闭仅移除手机视图。网页中的控制、审批、会话列表均以 ZCode 自身为准，不显示 TermLink 的原生控制权按钮。

本次未修改 Server 的转发协议或发布线上版本。Windows IPC 继续使用 V13，仅 JSON 添加可选字段。
