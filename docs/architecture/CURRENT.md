# 当前架构与目录边界

本说明依据 2026-10-05 工作区的代码入口整理。它描述当前实现，不代表每个功能都在本轮做过运行验证；实际发版结果见[发布记录](../releases/README.md)。

## 组件分工

| 组件 | 当前责任 |
| --- | --- |
| Android | 设备/CLI/AI/文件 UI、Keystore 身份与会话桥、终端 JNI/View、受控 WebView、原生附件与通知 |
| pc-client | Avalonia 桌面 UI、设置与 Agent 检测入口、会话摘要、本机 Agent 控制、桌面更新检查 |
| Windows Agent | Relay/E2EE、会话目录、文件访问、AI Agent 进程与协议适配、主机归档和恢复 |
| SessionHost | 每个受管 CLI 的 ConPTY 所有者、终端输入输出、terminal-core 状态与恢复边界 |
| terminal-core | Rust 终端解析、网格、快照和 FFI；不拥有网络或 Shell 进程 |
| ai-web | AI 对话 Web UI；当前构建资产内嵌 APK |
| Server + PostgreSQL | 身份、授权、设备/会话元数据、实时 Relay、任务协调和 Web 管理 |
| network-core | 可选直连代码；当前使用及验收为 Relay Only |

Windows 的桌面 UI 和后台 Agent 是独立职责。桌面 UI 退出不应被当成对应 Shell 一定已退出；在线和进程状态需要由实际所有者确认。

## 内容与传输

TermLink 的 CLI 和结构化 AI 通过认证设备之间的 E2EE 消息交换输入、输出和操作回执。Server 可以处理身份、路由、状态和长度等元数据，不应保存完整终端正文、AI 正文或持久文件对象。

终端历史归 Windows 主机所有，Android 的显示缓存不替代主机状态。文件内容由端点传输；Server 管理任务与权限。AI 归档、附件和待审批状态由 Windows 与具体 Agent 协议协作管理。

当前不做 LAN/NAT 探测、打洞和切换验收。保留的直连库仍可能在客户端构建中打包；“打包了库”“能力开关开启”和“实际采用直连策略”不能相互替代。

## AI 的两种页面策略

| 策略 | 页面及状态来源 | 边界 |
| --- | --- | --- |
| 结构化会话 | Windows 适配器 → TermLink 协议 → Android 桥 → APK 内嵌 AI Web | 以 Agent 实际协商的模型、推理、权限、工具、附件、恢复能力驱动界面 |
| ZCode 桌面远控 | Windows 保存远控地址，Android 打开官方网页 | 网页与 ZCode 自身服务通信，不将其网页操作伪装成 TermLink 原生协议 |

Codex app-server、Claude stream-json、Grok ACP 等是适配器协议来源；安装检测、协议初始化、真实模型生成和已发布版本验收是不同结果。模型账号及请求可能访问供应商服务，不能从“TermLink Relay 为 E2EE”推导所有第三方请求都经过该通道。

Windows 动态分发完整 Web 资源、小米/FCM 真机后台推送等仍有独立待完成项。这里不把规划中的能力写成已交付功能。

## 安装与更新

- Setup 是传统 Windows 安装器，负责原地替换、安装状态及工作目录保留。
- 现有独立程序名为 `TermLink.Updater.exe`，已有远程 Setup 事务及历史 MSIX 路径。
- 桌面手动更新当前在下载和验签后直接启动 Setup；尚未完成此前讨论的桌面更新统一事务方案。
- Authenticode 发布者信任、应用自身固定签名者校验、文件哈希和 SmartScreen 信誉作用不同。
- 正式更换 Windows 签名者要先设计旧客户端迁移；Android 继续使用既有发布 keystore。

详见[远程更新协议](../REMOTE_AGENT_UPDATE.md)和[发布运行手册](../RELEASE_RUNBOOK.md)。

## 历史材料

早期总体设计和会话交接已从当前树删除，其 Termux、terminal-main、Redis/Valkey 和旧阶段矩阵不作为部署依据。当前职责以本页和组件 README 为准，历史参考的来源与版权声明继续保留。

应用代码保留原模块目录，Rust 终端核心已移到同级独立仓库，详见[双仓库说明](../development/REPOSITORY_LAYOUT.md)。当前文档入口放在 `guides/`、`development/`、`architecture/`，发布和历史材料通过[发布索引](../releases/README.md)与[归档索引](../archive/README.md)查阅。
