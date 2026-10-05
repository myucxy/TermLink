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

Linux/macOS 有独立 Host 与认证 Unix-domain IPC；当前真实远程 Relay 仍存在 `PORTABLE_RELAY_NOT_CONFIGURED` 限制，不能按 Windows 的功能和发布状态宣传。AppImage/PKG 需要目标平台、签名材料及专项验收，交叉编译成功不等于可发布。

当前生产验收为 Relay Only。历史直连测试和机器专属 WSL 命令不作为新开发者的默认入口。

## 编译兼容说明

Avalonia 12.1.1 的可选 Roslyn 分析器面向较新编译器，固定 .NET 8.0.303 SDK 的编译器版本不同。相关项目在编译前移除该包的分析器，并显式加载 XAML；仍保留 XAML 编译、warnings-as-errors 和 Headless 验证。未来升级 SDK 时应一起复核该兼容处理。

第三方图标、字体和依赖授权见[许可证状态](../LICENSE-STATUS.md)。
