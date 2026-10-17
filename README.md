# TermLink

TermLink 让你在 Android 手机上使用 Windows 电脑上的 **CLI 终端、AI Agent 对话和文件**。任务在电脑上执行，手机负责交互；你可以部署自己的服务端连接两端。

当前主要交付 Android 客户端、Windows x64 客户端和 Docker 服务端。Linux/macOS 桌面代码仍有待验收的能力，不等同于已正式支持的完整产品。

> 自有代码采用 [GNU GPLv3，仅第 3 版](LICENSE)（`GPL-3.0-only`），允许商用。terminal-core 同样采用 GPLv3，仓库计划后续公开；第三方材料保留各自条款。详见[授权范围与源码交付说明](LICENSE-STATUS.md)。

## 产品与功能演示

| 产品介绍 | Windows Terminal 与安卓 TUI | CLI、AI、文件功能总览 |
| --- | --- | --- |
| <a href="docs/media/feature-demos/中文/06_产品视频/01_竖屏产品介绍.mp4"><img src="docs/media/feature-demos/中文/06_产品视频/01_竖屏产品介绍.png" width="180" alt="TermLink 产品介绍，点击观看视频"></a> | <a href="docs/media/feature-demos/中文/00_完整演示/01_TUI双端同步.mp4"><img src="docs/media/feature-demos/previews/tui.gif" width="285" alt="Windows Terminal 与安卓同屏动态预览，点击观看视频"></a> | <a href="docs/media/feature-demos/中文/00_完整演示/02_功能总览.mp4"><img src="docs/media/feature-demos/中文/00_完整演示/02_功能总览.png" width="285" alt="CLI、AI 对话与文件操作，点击观看视频"></a> |
| [中文产品视频](docs/media/feature-demos/中文/06_产品视频/README.md) · [English product videos](docs/media/feature-demos/English/06_Product_videos/README.md) | [中文 TUI 演示](docs/media/feature-demos/中文/00_完整演示/01_TUI双端同步.mp4) · [English TUI demo](docs/media/feature-demos/English/00_Full_demos/01_TUI_on_Windows_and_Android.mp4) | [中文功能总览](docs/media/feature-demos/中文/00_完整演示/02_功能总览.mp4) · [English overview](docs/media/feature-demos/English/00_Full_demos/02_Feature_overview.mp4) |

[中文目录](docs/media/feature-demos/中文/README.md) · [English catalogue](docs/media/feature-demos/English/README.md) · [网页目录](docs/media/feature-demos/index.html)

- TUI 双端同步：[中文（约 1 分 27 秒）](docs/media/feature-demos/中文/00_完整演示/01_TUI双端同步.mp4) · [English（约 1 分 22 秒）](docs/media/feature-demos/English/00_Full_demos/01_TUI_on_Windows_and_Android.mp4)。在 Windows Terminal 的 `pwsh 7.6.6` 中运行 Codex、Claude Code 和 OpenCode，由安卓输入 `hi` 并查看真实回复。
- 功能总览：[中文（约 1 分 40 秒）](docs/media/feature-demos/中文/00_完整演示/02_功能总览.mp4) · [English（约 1 分 40 秒）](docs/media/feature-demos/English/00_Full_demos/02_Feature_overview.mp4)。展示 CLI、AI 对话、模型与授权、文件操作和深色主题。

操作画面按 2 倍速剪辑，旁白和字幕保持正常速度；两端使用同一加速时间轴。每种语言包含 2 部完整演示、15 段功能介绍和 5 部此前产品视频，按 CLI、TUI、AI、文件、外观和产品视频分类。英文版翻译标题、旁白与字幕，实录界面及真实回复保留原语言。全部最终视频、封面和字幕均在本仓库的 `docs/media/feature-demos/`，无需桌面文件或外部视频平台。[录制与制作说明](docs/media/feature-demos/README.md)

点击封面或文字链接可打开或下载 MP4；动态预览没有声音。网页目录需下载仓库后在浏览器打开。

## 下载与开始使用

- Android / Windows：[AppShip](https://appship.kget.cc:8910)，安装包版本及签名说明见[发布记录索引](docs/releases/README.md)。
- 服务端：[Docker Hub](https://hub.docker.com/r/myucxy/termlink)，部署方式见 [Server README](server/README.md)。
- 首次使用：[快速开始](docs/guides/QUICK_START.md)。
- 修改代码：[开发与构建](docs/development/BUILDING.md)及[贡献指南](CONTRIBUTING.md)。

下载包有独立的签名要求。当前 Windows 发布记录仍使用自签名开发证书，不能把“GitHub 构建”或“签名校验通过”理解为已经获得公开 CA 的信任。

## 可以做什么

- **CLI**：打开和操作电脑上的受管终端，支持输入、快捷键、鼠标报告、窗口尺寸调整和历史浏览。
- **AI**：从电脑实际上报的 Agent 能力创建或恢复对话；按协议能力提供模型、推理强度、附件、工具详情、审批和中断。
- **文件**：浏览和传输电脑文件，从目录创建 AI 会话，以及从 AI 文件结果跳到对应目录。
- **多设备与恢复**：同账号设备发现、加密 Relay 转发、断线恢复；Windows 提供会话真实状态与主机历史。
- **自部署**：服务端内置身份服务、设备管理和 Web 管理页面，使用 PostgreSQL 持久化元数据。

各 Agent 支持的模型和操作不同。程序已安装不等于已登录或具备可用额度；ZCode 桌面远控采用独立网页入口，具体边界见[当前架构](docs/architecture/CURRENT.md)。

## 系统组成

```mermaid
flowchart LR
    A[Android：CLI / AI / 文件] <-->|HTTPS / WebSocket| P[外部 HTTPS 反向代理]
    P <--> S[TermLink Server：认证 / 元数据 / Relay]
    W[Windows：桌面 UI / Agent / 本地任务] <-->|HTTPS / WebSocket| P
    S --> D[(PostgreSQL)]
```

正常 CLI 和 TermLink 结构化 AI 的内容通过客户端间的 E2EE 通道传输。Server 管理身份、设备、会话元数据和任务协调，不执行电脑命令，不保存完整终端历史或持久文件对象。第三方 Agent 自身的模型服务访问及 ZCode 官方网页远控有各自的网络边界。

**当前使用与发布验收范围为 Relay Only。** 仓库保留直连代码与历史实验，但不把 LAN/NAT 记为本轮已验证能力；配置与构建能力开关不等于线上策略已经核验。

## 仓库导航

| 目录 | 用途 |
| --- | --- |
| [android/](android/README.md) | Android 产品 UI、网络、加密、预编译 JNI 调用与 View |
| [pc-client/](pc-client/README.md) | Avalonia 桌面 UI、本机 Agent 控制接口及平台适配 |
| [win-agent/](win-agent/README.md) | Windows Agent、AI 适配器、ConPTY SessionHost、Setup、Updater |
| [ai-web/](ai-web/README.md) | APK 内嵌的 AI 对话 Web 界面 |
| [server/](server/README.md) | Go 服务端、数据库迁移、Web 管理和部署 |
| [protocol/](protocol/README.md) | 实时消息、加密信封与协议契约 |
| native/terminal-core/ | 预编译 DLL/SO；实现与编译在独立 core 仓库，采用 GPLv3、计划后续公开，见[双仓库说明](docs/development/REPOSITORY_LAYOUT.md) |
| [network-core/](network-core/README.md) | 可选直连核心；当前运行及验收使用 Relay |
| scripts/、release/ | 构建、制品验证和发布脚本 |
| testdata/、technical-validation/ | 测试向量、协议和跨端验收工具 |
| [docs/](docs/README.md) | 使用、开发、架构、发布及历史文档导航 |
| terminal-reference/、prototypes/、ui-prototypes/ | 独立参考工具和原型；已退役的 Termux 源码迁出应用仓库，详见[归档索引](docs/archive/README.md) |

## 开发、贡献与发布

请先阅读[开发与构建](docs/development/BUILDING.md)。它说明固定工具链、AI Web 资源生成、Android 原生依赖及 DEV 版本参数，不要求贡献者复用维护者电脑的盘符路径。

正式构建、签名及上传遵循[发布运行手册](docs/RELEASE_RUNBOOK.md)和[发布构建说明](docs/RELEASE.md)。客户端分发使用 AppShip；Docker 按当前规则由本机构建并推送 Docker Hub。Windows/Android 的 GitHub Actions 构建尚未接入。

- 改动与问题反馈：[CONTRIBUTING.md](CONTRIBUTING.md)
- 漏洞报告：[SECURITY.md](SECURITY.md)
- 项目及第三方授权：[LICENSE-STATUS.md](LICENSE-STATUS.md)
- 开源准备进度与未完成项：[整理清单](docs/development/OPEN_SOURCE_READINESS.md)
