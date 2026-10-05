# 文档导航

按阅读目的选择入口。当前说明与历史证据分开索引；原有协议、脚本和发布记录路径保持稳定。

## 使用与部署

- [快速开始](guides/QUICK_START.md)：安装、登录、CLI、AI 和文件操作。
- [Server 部署](../server/README.md)、[运行时](../server/README-runtime.md)、[安装升级与备份](../server/deploy/single-node-production/README.md)。
- [漏洞报告](../SECURITY.md)、[许可证状态](../LICENSE-STATUS.md)。

## 开发

- [双仓库目录与 Git 边界](development/REPOSITORY_LAYOUT.md)：应用与私有核心同级开发、历史保留和迁移。

- [构建与测试](development/BUILDING.md)：工具版本、原生依赖、AI Web 资源及 DEV 参数。
- 组件入口：[Android](../android/README.md)、[桌面 UI](../pc-client/README.md)、[Windows Agent](../win-agent/README.md)、[AI Web](../ai-web/README.md)。
- [贡献指南](../CONTRIBUTING.md)、[开源准备进度](development/OPEN_SOURCE_READINESS.md)。

## 当前架构与专项契约

- [当前架构](architecture/CURRENT.md)：模块、数据归属、Relay、AI 策略和升级边界。
- [终端核心](TERMLINK_TERMINAL_CORE_V2.md)、[协议总览](../protocol/README.md)、[生产实时消息](../protocol/protobuf/PRODUCTION_REALTIME_V1.md)。
- [远程终端命令](REMOTE_TERMINAL_COMMANDS.md)、[主机历史](HOST_OWNED_TERMINAL_HISTORY.md)、[主机会话目录](HOST_SESSION_CATALOG.md)。
- [AI 文件目录跳转](AI_FILE_DIRECTORY_NAVIGATION.md)、[模型选择](AI_MODEL_SELECTION.md)、[Grok ACP](AI_GROK_ACP_20261004.md)。
- [AI 推送接入](AI_PUSH_INTEGRATION.md)：包含待申请的通道配置，不能视为厂商推送已验收。

专项文件可能包含阶段性记录；有状态或发布日期的内容按其时间阅读。代码事实、当前配置与发布记录分别核对。

## 发布与验收

- [发布运行手册](RELEASE_RUNBOOK.md)、[构建及签名规则](RELEASE.md)。
- [发布记录索引](releases/README.md)、[远程 Agent 升级](REMOTE_AGENT_UPDATE.md)。
- [跨端验证工具](../technical-validation/e2e/README.md)、[终端测试向量](../testdata/terminal-golden-streams/README.md)。

当前 CLI 与 AI 均按 Relay Only 验收。LAN/NAT 历史用例不属于当前门禁，也不记为通过。

## 历史和参考材料

[归档索引](archive/README.md)汇总旧总体设计、阶段交接、原型及来源归档。日期文件不一律表示“已作废”：发布记录是当时的证据，专项设计需看状态标记。旧总体设计不能覆盖当前组件入口和发布运行手册。
