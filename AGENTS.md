# TermLink Project Guidance

## Android terminal implementation policy

- Android 产品、Harness 和自动化测试统一使用 TermLink 自有 terminal-core、JNI 与 View，不直接依赖或加载 Termux 模块。
- 不再将查询 Termux、上游 Issue/PR 或运行 Termux 差分测试作为开发、测试、发布门禁。有具体需要时可以参考源码行为，再结合本项目边界自行实现。
- `terminal-forks/termux-terminal/`、补丁、同步脚本和 `android/terminal-engine-termux/` 仅为历史来源归档，不属于 Android 构建图。保留已有许可证和署名，不把归档自动重新接入构建。
- 不要直接照搬 Termux 的本地 PTY、Shell、Bootstrap、APT、插件签名或本地进程架构。必须结合 TermLink 的 Windows ConPTY、远程传输、E2EE、控制租约、ACK、History Gap、Snapshot 和 native `terminal-core` 权威边界综合设计。
- 引入或复制代码、资源、字体、主题、测试向量前必须核对许可证、署名、签名连续性和分发要求。许可证或架构边界不清楚时，只参考行为并自行实现。
- 修复完成后以 TermLink 自有协议、输入、渲染和设备回归测试验证行为；参考外部实现时记录来源及适配边界。

## Release policy

- 每次涉及 Android、Windows 客户端或 Docker 镜像的版本构建、上传、部署或发布方案，先阅读 `docs/RELEASE_RUNBOOK.md` 和 `docs/RELEASE.md`，按运行手册的发布门禁逐项核对，并以仓库脚本及当前线上状态为准，不复用历史版本号、密钥或旧发布记录代替现场验证。
- 发布前明确本次平台范围、最终已提交的源码 revision、版本/build、签名连续性、测试及回滚证据；失败或未验证的门禁必须停止并记录，不得降级宣称发布完成。
- 回答发布咨询或编写发布计划不等于获得提交、推送 tag、上传 AppShip/Docker、升级客户端或部署服务的授权；只在用户明确要求执行相应发布操作时才改变外部状态。
