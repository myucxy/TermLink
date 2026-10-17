# TermLink 产品与功能视频

最终成片统一存放在本仓库的 [`feature-demos/`](feature-demos/README.md)，中文和英文分别按功能分类。

- [中文目录](feature-demos/中文/README.md) · [English catalogue](feature-demos/English/README.md)
- [产品视频（中文）](feature-demos/中文/06_产品视频/README.md) · [Product videos (English)](feature-demos/English/06_Product_videos/README.md)
- [网页目录](feature-demos/index.html)：下载仓库后用浏览器打开；GitHub 阅读请使用上面的 Markdown 目录。
- [原速成片存档](feature-demos/archive/README.md)

## 完整演示

| 视频 | 中文 | English |
| --- | --- | --- |
| Windows Terminal + 安卓 TUI | [约 1 分 27 秒](feature-demos/中文/00_完整演示/01_TUI双端同步.mp4) | [约 1 分 22 秒](feature-demos/English/00_Full_demos/01_TUI_on_Windows_and_Android.mp4) |
| CLI、AI、文件与外观总览 | [约 1 分 40 秒](feature-demos/中文/00_完整演示/02_功能总览.mp4) | [约 1 分 40 秒](feature-demos/English/00_Full_demos/02_Feature_overview.mp4) |

## 此前产品视频

之前的竖屏产品介绍、精简短片、原比例产品介绍、原比例精简版、原比例功能总览，以及 CLI 双向同步实录都已重剪并收进中英文目录。每种语言有 2 部完整演示、15 段功能介绍和 5 部产品视频。

操作画面为 2 倍速；旁白和字幕保持正常速度，关键结果适当停留。TUI 和 CLI 双向同步的两端共用同一加速时间轴。中文沿用此前旁白音频；英文提供新的英文标题、旁白与字幕，实录界面和真实模型回复保留原语言。

## 录制说明

- TUI 双端演示：2026-10-17，独立 Windows Terminal 窗口，PowerShell 7.6.6；Windows 客户端 0.3.5.135，安卓模拟器 DEV 0.3.4/133（包含当前已复核修复）。Codex、Claude Code 和 OpenCode 均实际输入 hi 并收到回复。
- 功能总览与产品短片：2026-10-06 的 DEV 界面。部分菜单与当前正式版本可能不同。
- CLI 双向同步：2026-10-08，Windows Terminal / PowerShell 7，同一会话在手机与电脑分别输入命令，使用服务器中转。
- 录制中使用已有 CLI 服务配置；Claude Code 使用 glm-5.3，Codex / OpenCode 使用 gpt-6.1-sol，CLI 名称不代表同名厂商模型。OpenCode 仅对本次进程切换模型，未改写全局配置。
- 中文为已有 XiaoxiaoNeural 旁白；英文为正常语速的 JennyNeural 合成旁白。音乐由本地脚本原创合成，未使用第三方歌曲或采样。

本目录只包含最终视频、封面、字幕、预览、目录和校验信息，不含中间录屏或剪映临时素材。设备名已获维护者允许公开。视频是所列真实操作的展示，不替代全部能力的回归验收。详见[中英文制作说明](feature-demos/README.md)。
