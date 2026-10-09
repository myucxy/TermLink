# TermLink Project Guidance

## Repository layout

- 应用仓库与独立 `../terminal-core` 仓库同级，工作区父目录不属于任何 Git 仓库。core 计划后续公开，仍不得将核心源码复制回应用仓库。
- Windows/Android 只消费 `native/terminal-core/` 下的预编译 DLL/SO，不得读取同级 core 源码或在本仓库编译核心。核心 Rust、JNI C++、头文件、CMake 和编译脚本均放在 core 仓库；本仓库只保留应用调用绑定、UI 和二进制依赖校验。
- `terminal-core.lock.json` 记录核心 revision、构建时脏状态和各库 SHA-256；二进制与锁文件一起提交。发布执行 `scripts/Verify-NativeArtifacts.ps1 -RequireClean`，拒绝缺失、哈希不符或 dirty core 产物，不要求构建机器持有 core 仓库。
- 本轮按用户要求保留旧 Git 历史，旧历史仍包含核心源码；不重写旧提交/标签或主动推送远端。
- 2026-10-09 起，应用与 core 自有代码统一采用 `GPL-3.0-only`，允许商用；保留第三方许可和署名，不附加非商业限制。根 LICENSE、NOTICE、组件元数据与分发声明须一致。core 后续公开不等于本轮获准改变可见性或推送。
- 分发 GPLv3 二进制前提供匹配应用/core revision 的完整对应源码及构建资料，按 `docs/RELEASE_RUNBOOK.md` 核验接收者可取得；私有仓库地址或“后续开源”不代替源码交付。

## Android terminal implementation policy

- Android 产品、Harness 和自动化测试统一使用 TermLink 自有 terminal-core、JNI 与 View，不直接依赖或加载 Termux 模块。
- 不再将查询 Termux、上游 Issue/PR 或运行 Termux 差分测试作为开发、测试、发布门禁。有具体需要时可以参考源码行为，再结合本项目边界自行实现。
- 已停用的 Termux 模块、上游源码、补丁与同步工具已迁出应用仓库，保存在工作区私有备份中；不得自动恢复或重新接入构建。来源与许可记录见 `docs/archive/TERMUX_CLEANUP_20261008.md`，保留现有代码仍需的署名及旧协议兼容边界。
- 不要直接照搬 Termux 的本地 PTY、Shell、Bootstrap、APT、插件签名或本地进程架构。必须结合 TermLink 的 Windows ConPTY、远程传输、E2EE、控制租约、ACK、History Gap、Snapshot 和 native `terminal-core` 权威边界综合设计。
- 引入或复制代码、资源、字体、主题、测试向量前必须核对许可证、署名、签名连续性和分发要求。许可证或架构边界不清楚时，只参考行为并自行实现。
- 修复完成后以 TermLink 自有协议、输入、渲染和设备回归测试验证行为；参考外部实现时记录来源及适配边界。

## Current transport scope

- 自 2026-10-04 起，按用户要求，当前仅使用服务器转发（Relay Only），适用于 CLI 和 AI 会话；后续开发、联测及发布验收以此策略为准。
- 不再安排 LAN/NAT 直连、打洞或直连与中转切换测试，不将它们列为当前待办、发布阻塞项或要求用户准备的测试环境。历史直连验收要求在当前范围内标记为不适用，不记为测试通过。
- 保留服务器转发链路的登录、会话、输入、附件、审批、断线重连、弱网、多设备与升级回滚等受影响回归；网络切换只验证 Relay 的连接恢复。
- 未经用户后续明确要求，不恢复直连测试或主动开启直连。既有直连代码及历史记录保留；本规则记录使用和验收范围，不代表已修改或核验任何在线服务配置。

## Release policy

- Docker 发布方式（2026-10-04 用户指定）：本机从已提交源码编译后直接 push 到 Docker Hub，不使用 GitHub Actions 构建/发布镜像，不上传 GHCR 或 GitHub Release 镜像。版本、digest、架构、签名/证明状态仍需逐项记录；缺少签名不能冒称通过。`latest` 只在版本镜像核验后同步。

- 版本规则（2026-10-03 起）：下一轮新正式发布从 `0.3.0` 开始，后续未另行指定时自动递增 patch（`0.3.1`、`0.3.2`……）。不得回退或覆盖已发布版本；现场若已有 `0.3.0` 或更高版本，应从实际已发布版本继续递增。
- DEV/Debug/本地开发测试构建必须沿用对应平台最后一次成功正式发布的 `Version` 和 `BuildNumber`，两者均不递增；`0.3.0` 正式发布成功前 DEV 仍使用上一正式版本。用 DEV 名称、独立包标识及构建 revision 区分开发产物，不靠提高版本号区分。
- 仅在用户要求执行正式发布时，由发布执行者/编排根据 AppShip、Docker Hub 和远端 Git 的现场状态自动计算并固定本轮新版本/build，再显式传入构建脚本。build 在现有已用最大值上递增，不因切换到 `0.3.0` 而归零；同批发布平台使用同一组版本/build。
- 同一轮发布的重复构建、失败恢复和上传重试必须复用已固定的版本/build；不得每编译一次就加号，且不得用相同发布版本替换不同源码。构建完成或未发布的 tag/draft 不算成功发布，不可更新 DEV 的最后发布基线。详细步骤见 `docs/RELEASE_RUNBOOK.md` 的“DEV 与正式发布版本规则”。
- 每次涉及 Android、Windows 客户端或 Docker 镜像的版本构建、上传、部署或发布方案，先阅读 `docs/RELEASE_RUNBOOK.md` 和 `docs/RELEASE.md`，按运行手册的发布门禁逐项核对，并以仓库脚本及当前线上状态为准，不复用历史版本号、密钥或旧发布记录代替现场验证。
- 发布前明确本次平台范围、最终已提交的源码 revision、版本/build、签名连续性、测试及回滚证据；失败或未验证的门禁必须停止并记录，不得降级宣称发布完成。
- 回答发布咨询或编写发布计划不等于获得提交、推送 tag、上传 AppShip/Docker、升级客户端或部署服务的授权；只在用户明确要求执行相应发布操作时才改变外部状态。
