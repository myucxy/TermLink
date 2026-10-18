# TermLink 桌面客户端

本目录是 Avalonia 桌面 UI，Windows 安装后的入口为 `TermLink.exe`。Windows 包同时包含 [win-agent](../win-agent/README.md) 生成的 Agent、SessionHost、Setup 和 Updater。

桌面 UI 提供设备/会话摘要、设置、AI Agent 检测入口与更新交互。ConPTY、主机历史、远程桥和 AI 进程由 Agent 管理；手机端完整 AI 对话使用 [ai-web](../ai-web/README.md)。

## 开发入口

工具、DEV 版本参数、原生依赖和测试命令见[开发与构建](../docs/development/BUILDING.md)。源码预览与完整安装包是不同产物，签名安装遵循[发布运行手册](../docs/RELEASE_RUNBOOK.md)。

依赖方向：

```text
Contracts <- Platform.Abstractions <- Presentation <- Desktop
```

平台实现引用 Contracts 和 Platform.Abstractions；Presentation 不应引用 Avalonia、WinForms、WPF、具体平台实现或生产 Agent 程序集。

| 目录 | 责任 |
| --- | --- |
| `src/Desktop/` | Avalonia 视图、绑定与入口 |
| `src/Presentation/` | 平台无关的展示状态和交互 |
| `src/Contracts/`、`src/Platform.Abstractions/` | 跨层契约和平台接口 |
| `src/Windows/` | Windows 本机 Agent 适配 |
| `src/Agent.Local/`、`src/Agent.Host/`、`src/Unix/`、`src/Linux/`、`src/MacOS/` | 可移植 Host 与 Unix 平台适配 |
| `tests/` | 架构、Headless、平台适配与性能验证 |
| `package/` | 平台打包脚本 |

## 运行及平台边界

Windows 正常启动会附着或启动本机 Agent；不要把正在使用的安装目录当测试输出目录。`--preview-fixture` 使用隔离样例，仅适合 UI/Headless 检查，不连接真实业务。

主窗口默认以最小允许尺寸 820×600（逻辑像素）启动，仍可手动缩放和最大化。

AI Agent 检测优先使用可用 CLI，也检查 Codex / ChatGPT 与 Claude 的当前用户商店注册、
卸载注册、运行进程和常见安装位置。桌面内置的编码引擎通过帮助与版本探测后才标为可用；
仅安装图形程序或系统阻止启动时分别显示明确状态。检测不启动桌面窗口、不安装 CLI 或修改 WindowsApps 权限。

以下桌面历史续聊与 OpenCode 接入自 0.3.9 提供。
Codex 原生历史与续聊使用桌面版同一 Windows 用户／CODEX_HOME。商店包阻止直接启动时，
TermLink 校验已安装引擎的受信任 OpenAI 签名，将引擎及所需已签名助手按内容哈希缓存至
`%LOCALAPPDATA%/TermLink/Agent/CodexDesktopRuntime/`，再运行原生协议，无需另装 CLI。
缓存目录只允许当前用户、SYSTEM 和管理员访问；文件哈希不符、签名失败或目录重定向时拒绝启动。
桌面升级使用新的哈希目录，保留正在使用的旧版本；不会复制账号凭据或历史数据、修改 WindowsApps 权限或下载引擎。
选择“Codex 原生历史”后打开会话，保留原生会话 ID、上下文及授权并继续发送；应先停止原桌面窗口的并发生成。
桌面历史恢复可等待 90 秒，Android 导入／恢复请求可等待 120 秒；普通输入和查询保留原超时。
Claude Code 历史同时覆盖 `CLAUDE_CONFIG_DIR/projects`／`~/.claude/projects` 和桌面数据中的
`claude-code-sessions/<account>/<organization>/[local_<session>/].claude/projects/`。
检测包含 `Claude-3p`、`Claude-Data`、常规 Roaming 目录及商店虚拟目录，支持版本／哈希双层引擎路径。
继续时通过原生 `--resume <transcript.jsonl>` 加载已验证路径，保留会话 ID、上下文与历史权限；
沿用当前编码引擎可用的认证配置，不复制桌面凭据。Transcript 路径随 TermLink 的 DPAPI 归档保留。
普通 Claude Chat、Cowork、SSH／WSL 和云端独立会话不在本轮范围。

OpenCode 使用本机 `opencode acp`，支持创建、文本流、审批、中断、原生历史列表及同会话续聊。
原生历史使用不透明的 `ses_…` ID；加载时回放历史正文，TermLink 归档恢复时避免重复正文。
沿用 OpenCode 的模型、登录及原生权限规则，不注入 Grok 自动批准参数、不改变用户全局设置。
当前只接本机 CLI；未验证 OpenCode 桌面版、接管仍运行的 TUI 或外部 HTTP Server。

2026-10-18 已使用 Android 模拟器和已安装的 Windows 开发版完成 Relay 联测：Codex、Claude Code
桌面引擎和 OpenCode 均能加载原生历史并按原会话上下文继续对话。Agent 重启后可恢复加密归档；
Codex 在仅排除测试进程中的独立 CLI 后，仍能使用已验证签名的桌面引擎续聊。
ACP 模型选项以会话加载响应为准，避免在初始化阶段误报模型能力不可用。

Linux/macOS 有独立 Host 与认证 Unix-domain IPC；当前真实远程 Relay 仍存在 `PORTABLE_RELAY_NOT_CONFIGURED` 限制，不能按 Windows 的功能和发布状态宣传。AppImage/PKG 需要目标平台、签名材料及专项验收，交叉编译成功不等于可发布。

当前生产验收为 Relay Only。历史直连测试和机器专属 WSL 命令不作为新开发者的默认入口。

## 编译兼容说明

Avalonia 12.1.1 的可选 Roslyn 分析器面向较新编译器，固定 .NET 8.0.303 SDK 的编译器版本不同。相关项目在编译前移除该包的分析器，并显式加载 XAML；仍保留 XAML 编译、warnings-as-errors 和 Headless 验证。未来升级 SDK 时应一起复核该兼容处理。

第三方图标、字体和依赖授权见[许可证状态](../LICENSE-STATUS.md)。
