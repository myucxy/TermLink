# AI 自动工作目录与聊天交互验证（2026-10-03）

本轮为工作区开发修改、Debug 构建和模拟器验证，未提交/tag、上传或发布；未替换已安装的 Windows 正式版，也没有升级 Server。此前工作区的 AI 功能修改保留。

## 行为

- Windows 原生 AI 会话默认在 `<安装目录>\AiWorkspaces\<agentId>_yyyyMMdd_HHmmss_fff_<8位随机值>` 创建独立工作目录。仅在接受创建后生成；相同 operation ID 的重复请求复用回执。结束会话不删除目录。无写权限时给出错误并允许改选目录。
- Android 新建默认自动目录，点击“修改”可手输或浏览电脑磁盘/子目录，支持“恢复默认”。自动创建的目录不会成为下一会话的自定义目录。已有目录请求继续兼容，旧电脑未声明 per_session 时保留原有目录输入行为。
- 浏览通过现有 AI 加密请求发送 `directories`，由 Windows 只读列举，使用 host/授权栅栏和分页，不借用普通文件管理状态。离线/host 变化清空旧页，未读成功的路径不能确认。
- 聊天支持代码围栏、横向滚动、复制代码和全文、标题点击查看/复制完整目录、生成中点停止。原审批、控制权和操作回执沿用；ZCode 网页策略的目录和聊天仍由 ZCode 管理。
- 安装器保留整个 AiWorkspaces：升级时移出将删除的备份，失败恢复和远程回滚时随旧版本恢复；目录冲突时停止并保留双方。卸载前移到安装目录旁的独立保留目录。
- CLI 主体（`ProductActivity.kt` 从 `private fun SessionsPage(` 到末尾）与 HEAD 一致，忽略 CRLF。

## 自动验证

| 检查 | 结果 |
| --- | --- |
| Android `:app:testDebugUnitTest :app:assembleDebug` | 最终 HTTPS 配置构建通过；336 tests，0 failures/errors/skipped |
| `AiAgentValidation` | 三种协议 fixture 的消息/审批/问题/控制权回归；默认目录格式、两会话独立目录、8 个并发重复创建只生成一个目录、自定义路径、目录过滤、授权/host/UNC 拒绝、写入失败提示通过 |
| `WindowsSetupValidation` | 在临时根目录实际调用失败升级恢复；目录跨版本移动、保留升级后的新写入、冲突拒绝、卸载保留 helper 通过 |
| `FileOperationsValidation` | 抽取共用目录列举代码后原文件操作回归通过，同时编译 Agent |
| `UpdateCoreValidation` | 版本、事务/回执与路径边界通过 |
| Windows Desktop Debug | 0 警告、0 错误 |
| `git diff --check`、CLI 主体比对 | 通过 |

新增 Android 检查覆盖 managed/custom 偏好隔离、目录响应/分页/旧 host 拒绝，以及流式未闭合代码块、短/缩进结束标记、行内反引号保留。

`UpdaterValidation` 单独无参启动因缺少 `--target-manifest` 等签名包输入退出，未执行其 MSIX 安装事务用例；不计为通过。本轮未构建签名 MSIX 测试包；改动涉及的目录转移逻辑由上述安装器临时目录检查覆盖。

## 模拟器实操

使用 `emulator-5556` 与独立 `dev.termlink.harness.aivalidation`（TermLink DEV）：

1. 真实 Windows `AiSessionManager` + `AiAgentValidation --bridge --fake-agent`，安装根定向到测试临时目录。手机直接创建默认会话、逐级浏览 E 盘选择 `E:\WorkSpace` 并创建自定义会话，再次新建默认目录；检查 Windows 三个会话实际目录存在且两个默认目录不同。未对自选目录执行工具或写入项目文件。
2. 标题弹出完整目录，复制操作可触达；返回新建默认仍为自动目录，“修改/恢复默认”正常。
3. 隔离 UI 响应服务提供代码消息：复制代码后粘贴到手机输入框，确认内容完整；发送后出现停止按钮，点击后按服务回执恢复发送。
4. 360dp 宽度（720×1280、density 320）、font_scale=1.5：代码、菜单、新建、目录选择和完整路径弹窗可用；输入法弹出后输入框和发送按钮可达。
5. 目录选择期间模拟电脑离线，旧目录清空、确认禁用；恢复在线后重新列出磁盘，恢复默认并成功创建。

这些是 UI 与真实 Windows 目录管理器联测，Agent 模型进程使用 fixture；不是本轮真实云模型/E2EE Relay/跨 NAT 或正式安装包升级验收。

测试结束停止 loopback 服务和测试进程，移除本轮 adb reverse，恢复 1080×2400、density 420、font_scale=1.0。最终以 `https://termlink2.kget.cc:8910` 重编 DEV 包并覆盖安装，保留其登录和正式 Android 包。

日志和截图位于不提交的 `artifacts/validation/ai-workspace-*`：`final-android.log`、`agent-test.log`、`setup-test.log`、`files-test.log`、`updatecore-test.log`、`desktop-build.log`、`real-dirs.json`、`default.xml`、`custom-selected.xml`、`code-reply.png`、`copy-pasted.xml`、`generating.xml`、`stopped.png`、`compact*.png`、`browser-offline.png`、`browser-reconnected.xml`。

## 后续 Windows DEV 更新与真实链路复核（2026-10-03 13:32）

用户随后授权更新 Windows。现场查询 AppShip，Windows 最后正式版本仍为 `0.2.71` / build `127`；本次将 Desktop、Agent、SessionHost 及其公共组件统一编译为 `0.2.71.127`，InformationalVersion 标记 `+dev.<revision>`，不推进正式发布版本。

- 替换的是当前实际运行的 `artifacts/validation/ai-joint-windows/` 暂存 DEV 程序；正式安装目录 `D:\Dev\Tool\TermLink` 未替换。三个项目使用 Release 配置编译，以保留原生 Direct 功能；这些本地 DEV 制品未作 Production 签名或上传。
- AI 协议/工作目录回归、Desktop headless 均通过。切换前确认活动 CLI 会话为零，向旧 Agent 发送正常 RestartAgent 请求；此前该实例下的 AI 子进程随其退出。
- 旧程序保留在 `artifacts/validation/ai-workspace-windows-rollback-20261003-133233/`；账号配置另做 CurrentUser DPAPI 加密备份，备份路径见 `ai-workspace-win-update.json`。未清除账号/设备身份或用户工作目录。
- 新 Desktop/Agent 启动后，IPC 报告 `0.2.71.127`、AccountLinked=true、原 PC ID、`https://termlink2.kget.cc:8910`、RemoteAccess=true、Relay=Connected、无错误码。
- 模拟器使用普通 ProductActivity、已有账号及真实 E2EE Relay，无 acceptance fixture 或 adb reverse。点击刷新后，创建表单从“指定目录”变为“自动创建独立目录”；“修改 → 浏览选择”取得电脑的实际磁盘列表。
- 不填写工作目录直接创建真实 Codex 会话成功进入聊天，Windows 生成 `AiWorkspaces\codex_20261003_133340_436_a49c1c28`。本轮仅验证初始化与目录创建，没有发送云模型消息。
- 测试后经安卓菜单确认结束该测试会话，收到已结束回执，再关闭手机视图；工作目录仍保留。模拟器最终停在新建表单，默认仍显示自动创建独立目录。

本次日志/状态为 `ai-workspace-win-dev-{agent,sessionhost,desktop}-build.log`、`ai-workspace-win-update-{agent,desktop}-tests.log`、`ai-workspace-win-{before,after,update}.json`；界面证据为 `ai-workspace-real-new-default.xml`、`ai-workspace-real-browse.png`、`ai-workspace-real-created.png`。该追加记录补充上文的测试边界，不代表正式安装包升级、发布或 Server 部署。
