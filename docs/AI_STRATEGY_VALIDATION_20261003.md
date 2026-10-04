# AI 实现策略与紧凑创建验证（2026-10-03）

本轮在已有 AI 开发工作树上实现，未提交、打 tag、上传或部署。改动范围为 Windows Agent / Desktop 和 Android；服务端继续转发现有 E2EE 消息。本记录不替代正式发布门禁。

## 行为

- 所有 Agent 使用同一能力条目，追加可选 `strategy`：`native` 进入原生聊天，`desktop_remote` 进入 ZCode 官方 WebView；旧 JSON 缺字段默认 `native`，未知策略不列为可创建项。
- Windows 设置 → AI Agent 新增 ZCode 地址输入、保存、清除。接受 `https://zcode.z.ai/remote` 及其子路径，禁止非 HTTPS、其他主机、用户信息和非标准端口。必须使用桌面提供的完整、有效远控链接；普通官网地址不包含配对信息。
- 地址按 CurrentUser DPAPI 保存于 `%LOCALAPPDATA%\TermLink\Agent\zcode-remote-url.dpapi`，输入框掩码，保存成功后清空。能力 JSON 不携带地址；安卓点击入口时通过加密 `remote` 请求获取，校验 host、agent、策略和 URL。手机只持久化入口引用。
- 配置生效不需要重启 Agent。清除后恢复原本地检测结果；错误或损坏配置关闭该能力，避免悄悄启动另一种模式。旧手机误发原生 create 会被拒绝。
- 新建面板仅列当前电脑可用 Agent。从设备进入时固定该电脑；从 AI 新建可切换在线电脑。使用最近成功目录或 Windows 用户目录，目录/模型/Provider 放入高级设置，旧电脑没有默认目录时仍可填写。没有首条消息表单；创建成功后直接进入聊天，超时继续查询原回执。
- ZCode 网页入口不出现原生目录/模型/控制租约操作。紧凑标题、刷新、浏览器打开和关闭手机视图可用；电脑离线时销毁网页并隐藏内容。网页内容、控制状态和授权由 ZCode 提供，网页网络使用其官方服务。
- WebView 禁用文件/内容访问、混合内容和第三方 Cookie，拒绝证书错误和不受信任跳转，不注入 JS Bridge，不提取网页 token。网页渲染进程退出时显示重试提示。

仅参考 HAPI 的远端能力选择、简洁创建与共用原生聊天思路，未复制其代码；来源为本地研究目录 `artifacts/research/hapi/docs/guide/agents.md` / `how-it-works.md`。ZCode 网页直接从官方加载，没有复制、修改或重新分发其资源。

## 自动检查

| 检查 | 结果 |
| --- | --- |
| Android app | 334 项，0 失败/错误/跳过 |
| core-network | 337 项已通过；本轮无网络模块新修改，Gradle 复用同输入测试结果 |
| Windows Agent / Desktop 编译 | 0 警告、0 错误 |
| Agent 协议验证 | 三种原生适配器、控制/审批、并发去重回执通过 |
| 远控协议与存储 | DPAPI 往返、非法地址、目录不泄露链接、按需获取、授权/host 栅栏、清除恢复策略、远控拒绝原生创建通过 |
| Desktop headless | 地址保存后清空输入并刷新策略、清除恢复 native，以及既有设置/导航/托盘回归通过 |
| AgentControl | V13 与 V5–V12 兼容检查通过，无 IPC 版本变更 |
| CLI 主体保持 | ProductActivity 从 `private fun SessionsPage(` 到末尾与 HEAD 一致（忽略换行） |

日志均位于 `artifacts/validation/ai-strategy-*`。主要日志：`agent-tests.log`、`agent-build.log`、`desktop-build.log`、`desktop-tests.log`、`ipc-tests.log`、`webview-check.log`（文件名前均加 `ai-strategy-`）。

## 模拟器实操

`emulator-5556`、独立 `.aivalidation` 包、仅监听 `127.0.0.1:18793` 的隔离响应服务：

1. 新建仅显示 Codex / Claude Code / ZCode，隐藏 fixture 中缺失的 Grok；默认目录可见，首条消息和模型参数默认不出现。
2. 创建直接进入原生聊天，输入及发送取得 fixture 回复；这是 UI/状态验证，不冒充真实模型回复。
3. 选择 ZCode 时隐藏原生设置，直接打开远控；返回列表保留入口，菜单与离线隐藏有效。
4. 320dp 宽度、1.5 倍字体时 Agent 自动换行，创建按钮完整可达。
5. 官方网页以不含凭据的公开入口加载，实际展示 `Mobile Connection Invalid` 和缺失配对参数提示；已验证 WebView 渲染，未验证有效链接下的桌面控制/真实模型聊天。

实操发现并修复 WebView 初始尺寸问题：默认布局导致官方页面 html/body 根高度为 0，DOM 存在而画面空白。改为 MATCH_PARENT、启用 viewport，并在布局完成后加载；截图确认页面正常显示。未通过修改官方网页 CSS 绕过。

截图/界面树：`ai-strategy-create.png/xml`、`ai-strategy-created.png`、`ai-strategy-chat-reply.xml`、`ai-strategy-create-remote.xml`、`ai-strategy-create-narrow.png/xml`、`ai-strategy-remote-offline.xml`、`ai-strategy-remote-layout-loaded.png/xml`。

测试结束恢复模拟器原分辨率/密度、font_scale=1.0，停止 fixture，移除本轮 adb reverse/forward。重新编译并覆盖独立测试包，使默认服务恢复为 `https://termlink2.kget.cc:8910`；正式安卓包和 Windows 安装未替换。

## 尚未覆盖

本轮没有可用于配对的 ZCode 完整远控链接，所以真实桌面聊天、网页内文件选择/下载、官方全部登录跳转未验收；这些功能可使用浏览器入口继续处理。未重跑正式安装包签名升级、真实 LAN/NAT 或 Server 部署。网页方式不会自动取得 TermLink 原生 AI 的结构化消息、审批或会话列表能力。
