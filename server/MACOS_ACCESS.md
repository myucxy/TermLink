# macOS 桌面连接兼容性

2026-10-26：Mac portable Host 已实现账号与 Relay 控制连接，尚未实现手机远程操作 Mac 终端、文件或 AI。本页描述当前源码契约；同日已对 termlink2 完成独立测试身份的真实连接模块联测，结果见文末。本轮未部署服务端。

## 身份与协议

- Mac 沿用桌面公共 OIDC 客户端，通过 `/v1/client-config` 的 `windows_oidc_client_id` 与 `windows_redirect_uri` 发现配置。字段名沿用现有契约；回调为 `http://127.0.0.1/oauth2/callback`，实际浏览器授权使用随机 loopback 端口与 PKCE S256。
- 此公共客户端须存在于 `TERMLINK_WINDOWS_REGISTRATION_OIDC_PARTIES`；按 `PC_AGENT` 注册，不创建 `MAC_PC` 或借用移动端身份。
- 注册使用 Ed25519 持有证明，后续 REST 和 `/v1/realtime` 使用 Bearer 与独立签名请求。设备必须为 TRUSTED，账号、nonce、签名与角色校验继续生效。
- PC 元数据为 `platform=macos`、`architecture=arm64|x64`；协议为 `termlink.realtime.v1`、role `pc`、version 1，四个必选传输能力及 `liveness_probe_v1`。
- 应用层 `ping`／`pong`、连接心跳、Token 轮换与重连沿用现有服务端契约。

## 本次修复的接入阻塞

旧校验强制所有 PC 声明 `realtime_terminal_v1`。当前 Mac 尚无远程数据链路，上报 `capabilities: []`，因此 `PUT /v1/pcs/{id}` 会被拒绝为 `INVALID_PC_AGENT_METADATA`，连接无法进入稳定在线状态。此前 .NET 连接夹具接受此请求，没有执行实际 Go 元数据校验。

现在仅对 `PC_AGENT` 的 macOS／Linux 控制连接接受**显式空功能清单**，且要求：

- 不声明 session heartbeat 或远程 Agent 更新能力。
- 不上报远程 Shell 与终端命令。
- 不声明文件、snapshot 等半套产品能力。

存储仍保留空清单，不合成旧 Windows 默认能力。Windows／legacy 的原有约束继续保留，省略或 null 功能清单不能触发此兼容路径。注册、认证和账号隔离规则未放宽。

这项修复位于服务端，需要部署到仍使用旧校验的服务端才能生效；本轮没有部署。已有 Mac DEV 包的连接字段无需修改。本修复不需要新增数据库迁移，但部署必须已包含既有 `PC_AGENT` schema。

## 验证与边界

- `internal/api/portable_pc_compatibility_test.go` 验证 Mac/Linux 两种架构的元数据，以及缺省能力、错误功能声明、Windows／legacy 契约等拒绝路径；修复前可稳定复现拒绝，修复后通过。
- 同一测试用实际请求签名中间件和真实 Relay WebSocket handler 验证与 Mac 相同的 Hello／Welcome、能力协商、应用层 ping／pong。身份和在线注册表使用内存夹具，无真实 OIDC 或数据库。
- `internal/store/pc_capabilities_test.go` 验证空 Mac/Linux 能力不会继承旧 Windows 默认能力。
- Mac 客户端既有 16 个本地连接场景覆盖注册证明、重连、Token 刷新、撤销、账号隔离和完全退出；它们不等于线上端到端验收。

真实联测使用 DEV 包内原连接模块连接 `https://termlink2.kget.cc:8910`，设备注册返回 201、签名 self 核对与空能力电脑元数据上报返回 200，Relay 在线及设备列表、应用层心跳、手动重连和独立 Keychain 账号重新载入均通过；真实 PKCE 和 Token 刷新也通过。这说明该地址目前接受 Mac 控制连接，不证明部署了本机哪一份源码。测试只使用独立身份和新的 Keychain 服务，结束后已撤销测试设备并删除临时凭据，没有修改现有 DEV 登录状态。证据见 [termlink2 联测记录](../artifacts/macos-termlink2-login-20261026/REPORT.md)。

现有 DEV 安装的 Keychain 授权、完整界面登录、应用进程重启、其他线上地址和手机端电脑展示仍待验证。远程终端、文件、AI 和设备授权操作仍待客户端数据链路开发。

Mac 应用自身的 PKG 自动更新使用独立 AppShip Mac 渠道，不等同于 TermLink 服务端的手机远程 Agent 更新能力。当前 Mac 仍上报 `remote_agent_update_capable=false`。

## ZCode 原生链路补充（2026-10-27）

Mac Host 可单独声明 `ai_agent_v1`，无需伪装支持 `realtime_terminal_v1`。元数据校验现允许 Mac/Linux `PC_AGENT` 的显式空清单或仅 AI 清单，同时继续禁止 heartbeat、Shell、终端命令与远程更新的无实现声明；其他非终端组合和 Windows 规则不变。新增拒绝混合文件／snapshot／未知功能的测试。此处仅更新源码并测试，本轮未部署。

加密 AI 帧仍使用既有 pcID 路由，Server 要求 MANAGE 和双方 AI 能力，不创建终端订阅。Mac 再从签名 `/grants` 请求检查当前接收者与 epoch；设备撤销／权限变化停止 AI Owner。没有新增公开 HTTP 代理，也没有把 ZCode 官方账号材料交给 Server。新协议链路不证明真实 ZCode 模型、手机或线上部署通过。


# 服务端接口补充（2026-11-03）

此实现补齐服务端契约；不代表当前 Mac 客户端已经实现远程终端、文件或手机触发升级，也不代表已部署线上。当前发布的 Mac DEV 仅声明 `ai_agent_v1`，保持 `remote_agent_update_capable=false`。

## 能力与 Shell

`PC_AGENT` 的 `macos` / `linux` 支持显式空能力、AI、远程升级控制能力及它们的组合，不强迫控制连接虚报终端能力。无终端时仍拒绝文件、快照、会话心跳、Shell / 终端命令以及未知数据能力。

真正实现终端的 Agent 可以显式声明 `realtime_terminal_v1`、`terminal_commands_v1` 等真实能力。Mac / Linux 检测命令允许 `bash`、`zsh`、`fish`、`pwsh`、`sh`；Windows 继续使用原来的四种 Shell 和增强 PowerShell。拒绝跨平台检测命令；手工命令仍只接受已有 `custom` 规则，不接收可执行路径或命令行参数。

文件请求继续要求 Agent 的 `file_operations_v1`，打开终端继续要求 `terminal_commands_v1` 且选择 Agent 明确上报的命令。现有迁移52及新增迁移54扩展 supported_shells 与 OPEN_TERMINAL 成功结果约束，不创建新的传输协议或放宽文件权限。

## 本机远程路由开关

新增以下设备签名接口，仅允许此 PC 自己的、同账号且受信未禁用的 PC Agent：

- `GET /v1/pcs/{pcId}/remote-routing`：读取 `policy`，含 `remote_routing_enabled` 与 `policy_version`。
- `PUT /v1/pcs/{pcId}/remote-routing`：正文 `{"enabled":true,"expected_version":1}`；使用 GET 返回的当前版本及现有 Idempotency-Key / 设备签名规则。

更新采用版本比较；版本过期返回409。策略变更、版本增加、审计及 `grant.updated` outbox 同一事务提交。移动端、其他 PC 的 Agent、跨账号及撤销 / 禁用身份不能更改此 PC 的策略。

客户端只能在用户明确启用 / 停止远程访问时调用。重连、心跳、PC upsert 不得自动开启服务器策略，避免覆盖网页上的新关闭操作。已有 Mac 包的本机连接开关尚未接入此新接口，需要后续客户端改动与联测。

AI 继续使用既有 Relay 的 PC 路由、同账号 / 受信设备 / MANAGE / 双端 `ai_agent_v1` 校验，不借用终端会话。接入 iOS 沿用独立 IOS 身份和移动 wire role，未放宽既有授权。

## Mac PKG 更新任务

现有移动端创建 / 查询 / 取消、Agent 领取 / 续租 / 进度 / 完成 / 对账接口保持路径不变，增加按 PC 平台及架构选择产物：

- Windows：原稳定 x64 EXE 目录、原 SHA1 签名指纹、原 build47引导要求。
- macOS：`termlink-macos-arm64` 或 `termlink-macos-x64` 稳定目录，严格要求匹配平台 / 架构的 ready PKG、同源 HTTPS 下载、大小、SHA256 和生产 Developer ID Installer Team ID。Mac 不使用 Windows 的引导版本门槛。

服务端启动配置新增 `TERMLINK_APPSHIP_MACOS_TEAM_ID`，格式为10位大写字母 / 数字。空值只关闭 Mac 升级 offer；非法非空值阻止错误配置启动。与 Windows 的 `TERMLINK_APPSHIP_SIGNER_FINGERPRINT` 完全独立。不要填示例 Team ID 或开发证书：必须由正式发布方提供实际生产 Developer ID 的 Team ID。

根 Compose、core Compose、验收 Compose及受管安装器均透传该变量。受管安装器保留已配置值，显式空值可关闭；旧运行时缺少映射时拒绝静默启用，需先升级安装器 / 镜像。此文档不执行部署。

任务固化 `artifact_platform`、`artifact_architecture`、`artifact_type` 和独立 `artifact_apple_team_id`；Agent claim 视图提供这些信息，移动查询不返回安装包 URL、摘要或签名身份。创建、领取及租约操作复核平台 / 架构，不能把 Windows EXE 或 x64 PKG 交给 arm64 Mac。

仅具有 `remote_agent_update_v1` 的在线、远程路由已开启 PC 才能创建任务。请求方必须有 MANAGE（或已启用现有同账号自动信任策略）；安装前领取和租约推进还会复核请求方信任、禁用状态、授权有效期及路由开关。安装已开始后允许原 PC Agent 继续完成 / 回滚，避免撤销请求设备使本机停留在半安装状态。

服务端只验证目录元数据。Mac 安装端仍须对实际下载字节验证大小 / SHA256、Developer ID Installer 签名、Team ID 和公证，再经本机必要的系统授权执行安装。DEV / ad-hoc 包不能作为自动升级产物。当前 Mac 客户端尚未实现此远程任务消费者，禁止提前声明升级能力。

## 数据库与验收

现有迁移52保留原字节；新增迁移53/54：

- 54 `portable_pwsh_metadata`：在既有 POSIX Shell 支持上增加 `pwsh`，保留结果身份校验及已有 Shell；有对应记录时拒绝破坏性降级。
- 53 `platform_agent_updates`：旧任务保留 windows/x64/exe 默认身份，新增 Mac 包身份约束；手动安装较新版本的对账保留真实 observed build，并允许它高于目标 build。有 Mac 任务或高于目标的成功记录时拒绝降回旧约束。

运行时 `LatestVersion=54`。已有迁移文件保持原字节；正式仓库若已占用53/54，合入前必须重新编号并同步 schema/test，不能覆盖线上已应用迁移。

验证包括签名 HTTP、能力 / 平台拒绝矩阵、隔离 PostgreSQL 中实际迁移和任务生命周期、MANAGE / 撤权、CAS 并发、门户关闭与延迟启用竞争、审计失败事务回滚、较高版本对账，以及 Windows 回归。生产 PKG、真机手机和实际桌面安装联测仍需单独完成。

APNs 后台通知不属于此次 Mac 接入改动；尚未新增推送 token 注册或 APNs 发送。iOS 前后台、图形终端及客户端入口的差距也需要客户端后续开发。

## 2026-11-03 后台通知源码补充

服务端已补状态事件及APNs投递队列，默认关闭，迁移55。Mac/Windows AI Owner仍需接入最小状态上报；不能因为服务端代码存在就声明真机后台提醒已经可用。详见 [后台通知契约](PUSH_NOTIFICATIONS.md)。
