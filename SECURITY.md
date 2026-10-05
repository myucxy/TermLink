# 安全问题报告

请不要在公开 Issue 或 PR 中发布可利用的漏洞细节、凭据、终端正文或用户文件。

## 私密报告入口

安全联系邮箱：[myucxy@gmail.com](mailto:myucxy@gmail.com)。请将脱敏报告发送到该邮箱，邮件主题建议包含 `TermLink security` 和受影响的组件。

仓库 [Security 页面](https://github.com/myucxy/TermLink/security)如果提供 **Report a vulnerability**，也可以使用该私密入口。本轮未核验该 GitHub 功能是否已启用；邮箱是维护者明确指定的联系方式。

## 报告内容

- 受影响的客户端/服务端版本或 commit；
- 问题影响及触发条件；
- 使用虚构账号和样例数据的最小复现；
- 脱敏日志、建议缓解方式，以及是否已公开。

不要发送账号密码、发布私钥、Android keystore、AppShip/Docker 凭据、TLS 私钥或完整生产数据库。

## 范围与支持

可报告身份鉴权、E2EE、会话隔离、远程输入、文件访问、AI 审批、更新器和发布流程中的问题。第三方 Agent、模型供应商及反向代理自身的问题，应区分其与 TermLink 的责任边界。

项目暂未承诺安全修复时限或长期支持版本矩阵。版本影响、修复和公开披露应在私密沟通后确认；更多准备事项见[开源准备清单](docs/development/OPEN_SOURCE_READINESS.md)。
