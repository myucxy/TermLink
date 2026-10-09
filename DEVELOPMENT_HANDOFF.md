# TermLink 开发交接

更新时间：2026-08-04

> **历史交接记录。** 本文记录当时的开发状态和本机环境，不是当前构建或部署说明。
> 请从 [根 README](README.md)、[文档索引](docs/README.md) 和对应组件 README 开始；
> 当前发布规则见 [RELEASE_RUNBOOK.md](docs/RELEASE_RUNBOOK.md)。文中的机器路径、
> 阶段性方案和旧依赖只用于追溯，不能作为当前产品事实。

原始用途（仅说明当时约定）：重新打开会话后先阅读本文，再阅读
[完整产品与系统设计](docs/COMPLETE_SYSTEM_DESIGN.md)。本文只做开发导航，若细节冲突，
以完整设计文档为准。

## 1. 项目位置与当前状态

- 项目目录：`E:\WorkSpace\AICodeProject\TermLink`
- 当前阶段：阶段 0 当前环境可自动化验收已完成；10 个 Golden Stream 在 Termux 与
  xterm.js 间通过 122/122 事件后检查点，API 36 模拟器 10/10 冷启动回放通过；真机、
  厂商 IME、蓝牙键鼠和平板/折叠屏仍是外部硬件阻塞；
- 已有可运行内容：Windows ConPTY 技术验证与 Golden Stream 录制器、Android Compose
  离线/LAN/双向测试 Harness、固定上游 Termux emulator/view 真实网格、固定版本 xterm.js
  对照 Harness、Go Docker LAN 与 Phase 1 测试服务、三端可交互 UI 原型；
- `protocol`、`win-agent`、`server`、`android`、`terminal-forks` 和 `testdata` 均已有正式
  模块；生产账号、Relay、E2EE、历史、计费、产品 UI 和 Compose 接线已实现并通过自动化
  验证，生产发布与运维加固单列为开发项 7；
- 当前目录是 Git 仓库，阶段 0 开发前工作树基线为 `bbc794b`。

## 开发项 1-7 完成矩阵

| 开发项 | 范围 | 状态 | 验证与边界 |
| --- | --- | --- | --- |
| 1 | 生产账号/OIDC/设备注册与配对 | **已实现并验证** | Go `auth`、设备注册/配对 API、Android OIDC/API 客户端、Windows 配对流程；Go/Android/.NET 自动化验收通过。 |
| 2 | 生产 Protobuf 实时协议、X25519 + ChaCha20-Poly1305、Relay | **已实现并验证** | `protocol/protobuf/termlink_realtime.proto`、Go `realtimewire`/Relay、Android `core-network`、Windows `src/Protocol`；向量、协议和 Relay 测试通过。 |
| 3 | Windows ConPTY 到 Android 真实输入输出、ACK/Gap/重连、租约与本地输入优先 | **已实现并验证** | Windows `ProductionAgentRuntime`、Android 生产 Session Client、服务端租约/撤销；生产路径自动化覆盖有界重放、ACK/Gap、输入去重、租约和本地输入 epoch。 |
| 4 | Windows 主机本地持有的逐设备加密历史、Snapshot/ACK 冷恢复与关闭销毁 | **已实现并验证** | Windows 本地 WAL/checkpoint、Android `core-crypto`/`feature-history` 与 `host_history_v1` 实时请求；服务端不保存终端历史对象。 |
| 5 | 服务端 Catalog、计费验证边界、权益与配额 | **核心已实现并验证（Provider 待配置）** | Go `billing`/Catalog/entitlement API、可注入 verified-purchase 契约与 Android `feature-billing` 已测试；真实 Google/Web verifier 未配置时生产入口返回 `BILLING_UNAVAILABLE`。 |
| 6 | Android 产品页面、Windows Tray、Compose PostgreSQL/Valkey/OIDC 接线及自动化验收 | **已实现并验证** | Android product UI、Windows Account/Devices Tray、实时文件传输与当前生产运行时；Go/Android/.NET/Compose 自动化验收通过。 |
| 7 | 生产 TLS/受信发布证书真实安装更新、备份恢复演练、镜像签名、监控告警、生产 CA/发布加固 | **未开始** | 仅有开发证书/故障注入和本地 Compose 验证；真实证书链、系统级安装更新、灾备演练、镜像签名与生产运维尚未验收。 |

统一边界：`LAN Replay V1`、`TLT1`（Phase 1 Test Session V1）和 Android Compose
Harness 都是测试夹具，不是生产协议；终端历史和文件正文均不落服务端对象存储。1-6
的自动化结果不代表 arm64 真机、厂商 IME、外接键鼠、平板/折叠屏或生产发布已经验证。

真实 OIDC/Google/Web Billing Provider 仍需在部署前注入；本地默认配置只验证契约和
`BILLING_UNAVAILABLE` 失败边界，不冒充线上购买或公网部署结果。

## 2. 一句话目标

让用户从 Android 安全、低延迟地查看和控制自己多台 Windows PC 上的受管终端
会话，同时提供断线恢复、可选加密历史、设备撤销、审计和可配置会员套餐。

## 3. 已冻结的核心决策

1. Windows 端只管理 TermLink 自己创建的终端会话，不承诺无侵入接管任意已经打开的
   Windows Terminal 标签页。
2. Windows Session Host 使用 ConPTY 创建 PowerShell、CMD 和 WSL，并独立拥有 Shell
   进程树及 ConPTY 生命周期。
3. Windows Terminal Profile 启动 `termlink-session-host.exe`；Windows Terminal 是外层
   本地前端，Session Host 的内层 ConPTY 是可远程管理的真实会话。
4. PC 与 Android 传输原始、有序的 VT/ANSI 字节以及 resize、模式和生命周期事件；
   不传截图，不把字节流提前转换为普通文本。
5. Android 不移植完整 Windows Terminal。默认使用 Termux
   `terminal-emulator + terminal-view` 派生 Fork，离线 xterm.js WebView 是兼容基线与
   按会话回退引擎。
6. 服务端使用 Go，MVP 将 REST API、WebSocket Relay、租约协调和后台任务放入一个
   二进制；官方支持 Docker Image 和 Docker Compose。
7. PostgreSQL 保存权威元数据，Redis/Valkey 保存在线状态、短时配额占用和输入租约；
   终端历史由 Windows 主机本地保存并通过认证实时链路按需提供。
8. Relay 只能看到路由元数据与密文大小，不能读取命令、输出、快照或历史正文。
9. 本地用户优先级始终最高；远端输入必须经过设备授权与短时输入租约。
10. 产品不集成模型推理、终端内容解释或摘要、自然语言导航和命令生成功能。

## 4. 技术栈

### Windows Agent

- C# + .NET 8；
- P/Invoke ConPTY、Named Pipe、DPAPI；
- 进程：`TermLink.Agent.exe`、`TermLink.Tray.exe`、每会话一个
  `termlink-session-host.exe`、受控更新器；
- MVP 以当前用户运行，不让高权限 Windows Service 直接托管用户 Shell；
- Session Host 已使用 `STARTF_USESTDHANDLES` 空句柄，不修改父进程全局标准句柄或
  Console code page。

### Server

- Go；
- PostgreSQL；
- Redis 或 Valkey；
- 文件传输与主机历史使用实时路由，服务端不保存内容对象；
- WebSocket + Protobuf 二进制帧；
- Docker Compose 默认只向外暴露 HTTPS 443，数据库和内部端口不公开。

### Android

- Kotlin、Jetpack Compose、Material 3；
- 单向数据流、ViewModel、Coroutines/Flow；
- OkHttp WebSocket 或 Ktor Client + Protobuf；
- Room，敏感数据密钥由 Android Keystore 包装；
- Bouncy Castle 的 Ed25519/X25519/ChaCha20-Poly1305 封装，私钥材料由 Android Keystore
  AES-GCM 包装，不自研底层密码原语；
- `terminal-engine-api` 隔离 Termux Fork 与 xterm.js 的具体类型。

## 5. 已完成的技术验证

ConPTY 原型位于 `prototypes/conpty-probe`，已经验证：

- CMD；
- Windows PowerShell 5.1；
- PowerShell 7；
- WSL；
- 输出捕获；
- 输入往返；
- resize；
- 退出码。

阶段 0 本地闭环已完成：

- Golden Stream Bundle V1 规范，使用 `events.jsonl + output.bin` 保留原始块边界；
- ConPTY 输出块、单调微秒、resize、started/ended 和退出码录制；
- 10 个固定向量：四种 Shell smoke、ANSI/Unicode、Terminal Features、WSL Vim、less、
  fzf 和 htop；覆盖颜色、CJK/emoji/combining、alternate screen、滚动区、光标、
  OSC、鼠标/focus/bracketed paste 模式及超长控制序列恢复；
- PowerShell 与 Kotlin 两套严格读取器，校验 SHA-256、序号、时间、输出范围和预期；
- Android `terminal-engine-api`、`terminal-engine-replay` 和 Compose Harness；
- Kotlin 单元测试、Android Lint、Debug APK 构建和 API 36 模拟器 10/10 冷启动回放通过；
- LAN Replay V1 二进制测试协议和只读、非 root 的 Go Docker 测试服务；
- API 36 模拟器已通过 PC 局域网地址连接 Docker，严格校验结果为
  `14 events · 702 bytes · 100×30 · exit 17`。
- Termux `terminal-emulator + terminal-view` 固定到上游提交
  `3df69d1da197dd9bd71a3bafd902dffd720576b4`，只导入 emulator/view 与测试，排除
  `TerminalSession`、JNI、本地 PTY 和 Shell；
- `terminal-engine-termux` 已实现远程 Session Port、原始字节 feed、resize、主题映射、
  只读输入门控和真实 `TerminalView`；本地与 Docker LAN 回放均已在 API 36 模拟器渲染；
- 上游 Termux emulator 测试与 TermLink 回放/网格/UI 目录测试合计 160 个，0 失败；
- Android 输入适配已验证 CJK/emoji code point、Ctrl+C、Alt+B、方向键、bracketed
  paste、SGR mouse、focus 和只读拒绝；
- `@xterm/xterm` 6.0.0 与 Unicode 11 addon 0.9.0 已固定并检入离线运行资产、许可证和
  SHA-256 清单；10 个原始事件流得到 122/122 checkpoint `Grid match`，对照器同时校验
  `capture_id`、事件序号、可见网格、样式段、光标和公共模式子集；
- 正式独立 Session Host 已实现内层 ConPTY、Job Object、输入输出桥、resize 和统一关闭；
  CMD/Windows PowerShell/PowerShell 7/WSL Profile、双 ConPTY、Unicode Agent pipe、控制
  键、EOF、timeout、强杀、2 MiB 输出、持续运行和 20 轮生命周期均通过。

运行方式：

```powershell
cd E:\WorkSpace\AICodeProject\TermLink\prototypes\conpty-probe
.\Run-All.ps1
```

该结果证明“可靠管理自己创建的 ConPTY 会话”和固定流的 Android 解析可行；这些固定流
本身不替代生产链路，生产链路由开发项 1-6 的独立模块和自动化覆盖。仍未证明：

- Android 真机渲染兼容；
- 生产发布环境中的真实 TLS/证书链与外部硬件兼容性；
- 规范化跨引擎 Snapshot（仍属实验能力）。

### 阶段 1 当前进度

阶段 1 的第一段本地闭环已实现并验证：

- `win-agent/src/Common/LocalIpc.cs` 定义仅供本机 Host-Agent 使用的长度前缀二进制帧，
  包含版本、类型、Session ID、64 位序号与 Payload，单帧上限 1 MiB；
- Agent 创建当前用户 SID ACL 的独立 Named Pipe，Host/Agent 通过 32 字节一次性 Token、
  Session ID 和 Windows 返回的实际对端 PID 双向认证；
- Host 按序发布 Output、Resize 和 Lifecycle，保留 4 MiB 未 ACK 有界队列，断管后重连
  重发；溢出使用覆盖精确序号范围的 `Gap`，不静默丢弃；
- Agent 持久化后发送累计 ACK，使用 DPAPI CurrentUser 保护每个事件 chunk；输入、握手和
  resize request 不进入事件 Buffer；最近 4096 个 `input_id` 单独 DPAPI 持久化去重；
- 真实双 ConPTY 集成已覆盖主动断管、重连、最终 ACK、同一输入 ID 只接受一次、111×37
  resize 和 exit 17；有界积压测试覆盖 Gap 持久化与缺口后的连续 ACK；
- 长驻 Session Registry 已在单个 Agent 进程内管理多个隔离 worker；当前用户 SID ACL 的
  控制 Pipe 支持注册、摘要、停止单会话、停止全部远程访问、重新启用和关闭；
- Registry 摘要不含终端正文、标题、Token、Pipe 名或 Buffer 路径；Host 异常退出或断管
  超时会自动移除，重复/并发停止保持幂等，所有远程停止均不终止本地 Shell；
- 停止结果结构化报告请求/完成/超时数；单会话、全局和关闭超时使用稳定错误码，超时
  worker 进入 retiring 集合，后续关闭会完整重试且控制循环不会因停止超时退出；
- 已建立固定 SDK 8.0.303 的 `TermLink.Windows.sln`，包含 AgentControl、Agent、
  SessionHost、WinForms Tray 与单文件 Updater；正式构建 0 warning/0 error；
- Tray 只通过当前用户 SID ACL 的控制 Pipe 读取脱敏摘要，支持停止单会话、停止全部远程
  访问和重新启用；退出 Tray 不结束 Agent 或本地 Shell；
- Registry 使用 DPAPI CurrentUser 和原子替换保存恢复所需注册索引，PID 与进程启动时间
  必须同时匹配；索引不含终端正文、标题、工作目录、输入或事件；
- 20 个真实 ConPTY SessionHost 已通过 Agent 强制结束和受控重启恢复，两次均 20/20 恢复
  且注册时间保持；Tray 读取 20 条脱敏摘要，远程停止后 20 个本地 Shell 均继续接受本地
  输入并正常退出；
- Windows 签名 MSIX 构建现包含 8 个已签名 TermLink 自有二进制、完整包签名、固定更新
  发布者公钥、稳定包身份、登录 StartupTask 和 Tray 当前用户单实例；开发证书验证覆盖
  解包运行与 Agent 状态，且不修改系统证书库；
- 单文件 Updater 以包外 runner 执行 Windows 事务式部署；更新前严格验证独立 RSA 签名
  清单、MSIX Authenticode、固定发布者、Package Identity、四段版本、有效期和 SHA-256，
  只接受同一发布者的当前回滚包与更高目标版本；
- Updater 使用原子 staging 和可恢复事务日志；真实开发签名的 0.1.0.0/0.2.0.0 包已通过
  清单/包篡改拒绝，以及成功、部署失败保留旧版、健康失败回滚、回滚失败和回滚重入 5 条
  状态路径；部署后端使用故障注入，未执行真实 `Add-AppxPackage`；
- Android 新增独立 `core-session` 模块，`RemoteTerminalSession` 实现 Live、Reconnecting、
  ReadOnly、Controlling、Exited、HistoryGap、feed 后 ACK、重复丢弃、恢复点、输入租约、
  唯一输入 ID、禁止自动重试和 100～200 ms resize 防抖；
- Android 新增 `core-network` 临时测试适配，串行处理 WebSocket 事件、从最高本地 ACK 恢复、
  匹配 `InputAccepted`、测试租约、Flow Control 和明确 History Gap；
- Go 服务新增与 LAN Replay 隔离的 `/v1/test/session/`，覆盖 steady、一次断线、慢消费者、
  History Gap 和 exited；LAN Replay V1 保持只读且格式不变；
- Compose Harness 新增“双向会话”，把真实 `RemoteTerminalSession` 状态、ACK、输入确认数
  和测试租约绑定到 Termux View，并显式标注 `TEST RELAY · NO E2EE`；
- 阶段 1 Android 状态机 11 个、网络适配 5 个聚焦测试通过；全量 Android 构建、单元测试
  和 Lint 通过；
- API 36 模拟器经 Docker 验证一次断线恢复到 ACK 13、测试输入 accepted、Gap 5..6 保留
  ACK 4、exited 到 ACK 14/exit 17，0 crash/ANR。

阶段 1 证据：

- `docs/validation/phase1-local-agent.json`；
- `docs/validation/phase1-local-agent-gap.json`；
- `docs/validation/phase1-session-registry.json`；
- `docs/validation/phase1-android-session.json`；
- `docs/validation/phase1-android-network.json`；
- `docs/validation/phase1-test-relay.json`；
- `docs/validation/phase1-android-relay-emulator.json`；
- `docs/validation/phase1-windows-app.json`。
- `docs/validation/phase1-windows-installer.json`。
- `docs/validation/phase1-windows-updater.json`。

上述 Phase 1 结果仍需与生产链路区分：Agent/Tray 本机恢复、开发签名 MSIX 和故障回滚
属于本机验收；生产账号/设备、Crypto、Protobuf Relay、历史、计费和正式页面已在独立
模块中实现并有自动化验证。临时 Relay 不连接真实 Windows Shell，LAN Replay、TLT1 和
Harness 只是假数据夹具；规范化 Snapshot 仍未冻结。生产受信证书下的真实安装/卸载、
更新/降级及发布运维属于开发项 7。

## 6. Android Terminal 结论

- 不尝试把微软 Windows Terminal 直接编译成 APK；其 UI、输入、渲染和打包深度绑定
  WinUI、WinRT、DirectWrite、Direct3D 和 Windows App SDK；
- Termux Fork 只复用终端模拟器与 View，不引入 Termux 本地 Shell、PTY、包管理器；
- 需要继续扩充选择色、搜索、状态快照、复杂 grapheme cluster 和 PowerShell/ConPTY
  边缘序列；
- 同一份 Golden Stream 必须同时输入 Termux Fork 和 xterm.js，比较最终网格、样式、
  光标、模式和截图；
- Windows Terminal 配色通过 Scheme/主题字段映射实现，不依赖移植 Windows 渲染器。

当前 Termux 集成状态：

- 已完成最小 Fork、固定 Commit、可重放补丁队列和 Remote Session Port；
- 10 个固定原始流均可驱动真实网格，并在 API 36 完成 Canvas 渲染；
- 10 个用例的 `termlink.terminal-grid-draft` v0 同流对照达到 122/122 checkpoint match；
  ANSI/CJK/emoji/combining 与 TUI 已进入网格回归；OSC 8 仅覆盖恢复，focus/mouse 覆盖
  模式和输出编码，尚无安全链接点击语义或真实鼠标硬件验证；
- 尚未冻结 `normalized_grid` 或参考截图，草案网格也不是跨端 Snapshot；
- 选择色、搜索、快照、复杂 grapheme、TalkBack 和真机 IME/外设仍是明确待办，不能把
  当前最小 Fork 描述为完整生产兼容认证。

## 7. 会员与套餐规则

套餐必须完全由服务端配置，客户端不能使用固定 `FREE/PRO` 枚举决定权益。

首发预置值：

| 套餐 | 远程 PC | 同时实时 Session | 周期 |
| --- | ---: | ---: | --- |
| Free | 1 | 3 | 免费 |
| Pro Monthly | 5 | 20 | 月付、自动续费、可取消 |

上述只是初始数据。服务端需要支持配置并版本化发布：

- `plan_id`、展示名称和说明；
- 免费、月付、年付等周期；
- PC 和实时 Session 上限；
- 功能标记、排序和可见平台；
- Google Play/Web 等渠道 Product ID；
- 上下架时间、降级目标和状态；
- 存量订阅保留原版本或显式迁移，禁止静默降低已付费权益。

执行规则：

- PC 配额按启用远程路由的不同 `pc_id` 计算；
- 实时配额按账号正在订阅正文的不同 `session_id` 计算；
- 历史回放不占实时配额；
- 同一账号重复显示同一 Session 可以去重；
- 达到上限返回 `ENTITLEMENT_LIMIT_REACHED`；
- Entitlement 由服务端验证购买凭据后签发，客户端缓存只用于提前展示；
- 到期不终止本地 Shell，不删除设备、密钥或历史，只暂停超额远程能力；
- E2EE、设备撤销、本地优先、停止远程访问和数据删除不得进入付费墙。

主要 API 基线：

- `GET /v1/billing/plans`；
- `GET /v1/account/entitlements`；
- `POST /v1/billing/google/verify`；
- `POST /v1/billing/web/checkout`；
- `POST /v1/billing/restore`；
- `POST /v1/admin/billing/plans`；
- `PUT /v1/admin/billing/plans/{id}`；
- `POST /v1/admin/billing/catalog/publish`。

## 8. UI 设计状态

生产级交互原型：`ui-prototypes/index.html`

已有页面：

- Android：设备首页、实时终端、历史回放、可信设备、会员与配额；
- Windows Agent：托盘、会话管理、远程访问、设置、账户与会员；
- Server Console：运行概览、连接路由、Docker 部署、安全审计、订阅与权益；
- 15 个页面使用稳定 Hash 路由，刷新后恢复当前产品、页面、主题和本地 UI 设置；
- 支持明暗主题、320px 窄屏展示、统一命令面板、多端切换、Toast、Dialog 和详情抽屉；
- Android 已补齐控制租约、Extra Keys 门控、回放速度/键盘时间轴、端侧搜索、可信设备
  详情/撤销、设备批准、购买与恢复购买 UI；
- Windows 已补齐新建受管会话、策略持久化、查看者断开、远程紧急停止、五类设置、
  诊断、账户和 Entitlement 刷新 UI；
- Server Console 已补齐连接搜索/详情、备份、恢复演练、Compose 安全摘要、审计筛选、
  设备与授权、运维设置、套餐草稿、Catalog 发布和订阅详情 UI；
- 所有按钮均有明确行为，高风险动作二次确认；原型中的外部写操作仍需由实际客户端和
  服务端自动化结果佐证；
- UI 状态完整不等于真机硬件、生产证书、备份恢复或发布运维已经验收，不能把原型反馈
  当作这些边界的生产结果。

UI 设计语言为 Quiet Focus：内容优先、低干扰、上下文操作、清晰状态、渐进披露；
不要用大量 KPI 卡片、霓虹渐变或无功能装饰替代可用性。

本地预览：

```powershell
cd E:\WorkSpace\AICodeProject\TermLink\ui-prototypes
python -m http.server 4173 --bind 127.0.0.1
```

然后打开 `http://127.0.0.1:4173/`。

## 9. 下一步开发顺序

阶段 0 当前环境可自动化项已完成，开发项 1-6 的生产基础也已实现并通过自动化验收；
不要把仍缺少的硬件验证或 LAN 测试设施扩大解释为生产发布能力。下一里程碑是开发项 7
的证书、灾备、签名、监控和发布加固。

### 第一开发里程碑：Golden Stream 本地闭环

1. ~~创建推荐仓库骨架：`protocol`、`win-agent`、`android`、`server`、`testdata`；~~
2. ~~扩展 ConPTY Probe 并把生命周期代码迁入独立 Session Host；~~
3. ~~录制四种 Shell、Vim、less、fzf、htop、颜色、Unicode、控制模式和恶意超长序列；~~
4. ~~建立 `terminal-engine-api` 和最小 Android Harness；~~
5. ~~建立 Golden Stream → Docker WebSocket → Android 的 LAN Replay 测试闭环；~~
6. 在 Android 真机完成当前固定流与 IME/键鼠矩阵；当前只有 API 36 模拟器结果，硬件阻塞；
7. ~~接入固定上游 Commit 的 Termux `terminal-emulator + terminal-view` Fork；~~
   已完成 Remote Session Port、真实网格与阶段 0 输入适配；
8. ~~加入离线 xterm.js 对照 Harness；~~
9. ~~同流回放并形成差异和不兼容清单；~~ 10 cases、122/122 checkpoint match；按设计
   暂不冻结 `normalized_grid`、参考截图或跨端 Snapshot；
10. 生产 Protobuf Envelope 已在 `protocol/protobuf/termlink_realtime.proto` 冻结；
    `TerminalSnapshotV1` 仍待跨 Termux/xterm.js 兼容验收，保持实验能力。

第一里程碑验收：

- Golden Stream 文件可重复生成和离线回放；
- 原始字节、resize 与事件顺序无损；
- Android API 36 模拟器已完成 PowerShell、Vim/fzf、WSL htop；真机硬件矩阵待补；
- Termux Fork 与 xterm.js 的差异可自动报告；
- Session Host 退出、断管、超时和强制终止均不泄漏句柄；
- 没有把私有终端引擎序列化格式写入跨端协议。

### 第二开发里程碑：阶段 1 本地 Agent 与 Android Session

已完成：

1. Session Host 与 Agent 的每会话 Named Pipe、双向认证、序号和累计 ACK；
2. Host 断管有界队列、重连重发、Gap 语义和独立写循环；
3. DPAPI 事件 Buffer、输入 ID 持久化去重及禁止输入帧落盘；
4. 真实双 ConPTY Host-Agent 集成和诊断阈值 Gap 测试；
5. Android `core-session` 与 11 个状态机测试；
6. 长驻 Win-Agent Session Registry、多 Host、脱敏摘要、结构化远程停止、异常/失联 Host
   回收和关闭 best-effort 清理；
7. Android `core-network` 与隔离的 Phase 1 临时双向测试协议；
8. 测试 Relay 的累计 ACK、suffix-only resume、输入 accepted、测试租约、慢消费者和 Gap；
9. Compose 双向 Harness 的真实状态绑定，以及 API 36 模拟器到 Docker 的端到端验证；
10. 正式 .NET 8 Agent/SessionHost/Tray 工程与当前用户本机控制 UI；
11. DPAPI 注册索引、Agent 强制重启恢复和 20 个真实 ConPTY Session 并发压力。
12. 单文件 Updater、独立签名更新清单、原子缓存、可恢复事务日志与故障回滚状态机；真实
    开发签名双版本和篡改拒绝已验证，系统级 AppX 更新/降级待生产受信证书。

开发项 1-6 已完成；下一段只推进开发项 7：

1. 用生产受信证书完成 Agent/Tray 真实安装/卸载、登录启动及 Updater 的真实更新/降级验收；
2. 建立并演练生产备份恢复、镜像签名、监控告警和生产 CA/发布加固；
3. 在真实 TLS/受信发布链路上复核已实现的账号、Relay、历史、计费和端到端输入输出；
4. 真机、厂商 IME、外接键鼠及其他硬件可用后补齐兼容性矩阵。

## 10. 推荐仓库结构

```text
TermLink/
├── protocol/
│   ├── protobuf/
│   └── test-vectors/
├── win-agent/
│   ├── src/Agent/
│   ├── src/Protocol/
│   ├── src/Tray/
│   ├── src/SessionHost/
│   ├── src/Updater/
│   └── tests/
├── server/
│   ├── cmd/termlink-server/
│   ├── internal/{api,auth,billing,history,kv,realtime,realtimewire,store}/
│   ├── migrations/
│   ├── Dockerfile
│   └── compose/
├── android/
│   ├── app/
│   ├── core-{crypto,database,model,network,protocol,session}/
│   ├── feature-{billing,history}/
│   └── terminal-engine-*/
├── terminal-forks/
│   └── termux-patches/
├── testdata/
│   └── terminal-golden-streams/
└── prototypes/
    └── conpty-probe/
```

## 11. 开发时不要破坏的边界

- 不把“接管普通 Windows Terminal 现有标签页”悄悄加入 MVP；
- 不把终端输出当作普通 UTF-8 日志逐行传输；
- 不允许 Relay 记录明文命令、输出、Token、密钥或完整 IP；
- 不用客户端字段 `isPro=true` 或客户端套餐名决定服务端权限；
- 不在多个 Agent 副本中使用仅进程内锁实现租约或配额；
- 不自研密码学算法；
- 不让付费墙阻止撤销设备、停止远程访问或删除数据。

## 12. 当前文件索引

- [完整产品与系统设计](docs/COMPLETE_SYSTEM_DESIGN.md)：权威开发基线；
- [多端 UI 布局设计](docs/UI_LAYOUT_DESIGNS.md)：三端布局与交互规则；
- [技术验证与 Windows Terminal 移植评估](docs/TECHNICAL_VALIDATION.md)：ConPTY 证据和
  Windows Terminal 不直接移植的依据；
- [Android Terminal 引擎调研](docs/ANDROID_TERMINAL_ENGINE_RESEARCH.md)：Termux Fork、
  xterm.js 等候选对比；
- [产品需求与技术方案](docs/PRODUCT_REQUIREMENTS_AND_SOLUTION.md)：早期需求背景；
- [ConPTY 原型说明](prototypes/conpty-probe/README.md)；
- [.NET 8 Windows Agent、SessionHost 与 Tray](win-agent/README.md)；
- [Golden Stream V1](protocol/test-vectors/GOLDEN_STREAM_V1.md)；
- [LAN Replay V1](protocol/test-vectors/LAN_REPLAY_V1.md)；
- [Phase 1 临时双向测试协议](protocol/test-vectors/PHASE1_TEST_SESSION_V1.md)；
- [Android Harness 与真机测试](android/README.md)；
- [固定 Termux 来源与归档记录](docs/archive/TERMUX_CLEANUP_20261008.md)（原 Fork 与补丁已迁出工作树）；
- [LAN Docker 测试服务](server/README.md)；
- [可交互 UI 原型](ui-prototypes/index.html)。

## 13. 当前质量检查

以下检查在交接前已通过：

```powershell
cd E:\WorkSpace\AICodeProject\TermLink
node --check ui-prototypes/app.js
npx --yes html-validate ui-prototypes/index.html
npx --yes markdownlint-cli2 "**/*.md" "!**/node_modules/**"
```

UI 已实际验证 Android、Windows Agent、Server Console、明暗主题持久化、320px 窄屏、
命令面板、会员、控制租约、设备撤销、远程停止、设置、备份和 Catalog 工作流。15/15
页面路由通过，按钮显式绑定覆盖 100%，文档与活动页面横向溢出为 0；无重复 ID、空名称
按钮、无标签表单或无标题 Dialog，浏览器控制台无错误。

阶段 0 新增检查：

```powershell
.\prototypes\conpty-probe\Record-Phase0GoldenStreams.ps1 `
  -CaptureRoot "$env:TEMP\termlink-phase0-recording"

.\win-agent\tests\Test-SessionHost.ps1
.\win-agent\tests\Test-LocalAgentLoop.ps1
.\win-agent\tests\Test-LocalAgentGap.ps1
.\win-agent\tests\Test-SessionRegistry.ps1
.\win-agent\Build-Windows.ps1
.\win-agent\tests\Validate-Phase1WindowsApp.ps1
.\win-agent\tests\Validate-ProductionProtocol.ps1
.\win-agent\tests\Validate-ProductionRuntime.ps1

docker compose -f server\compose\lan-test.compose.yml up --build -d

cd android
.\gradlew.bat testDebugUnitTest assembleDebug lintDebug
.\Validate-Phase1Session.ps1
.\Validate-Phase1Network.ps1
.\Validate-Phase1RelayEmulator.ps1
.\Validate-Phase0Emulator.ps1 -DeviceSerial emulator-5554

cd ..
cd terminal-reference\xterm-harness
npm ci
npm test
cd ..\..

# 网络恢复后补跑；本次两次 git fetch connection reset，不计入已通过项
.\terminal-forks\Sync-TermuxTerminal.ps1 `
  -Destination "$env:TEMP\termlink-termux-sync-check"

cd server
D:\Dev\Env\Go\bin\go.exe test ./...
D:\Dev\Env\Go\bin\go.exe vet ./...
.\Validate-Phase1TestRelay.ps1

# 生产 Compose 配置/集成（需要 Docker 和临时 secrets）
docker compose --env-file .env -f compose\core.compose.yml config
docker compose -f compose\integration.compose.yml up --build --abort-on-container-exit --exit-code-from server
docker compose -f compose\integration.compose.yml down -v

cd ..
Invoke-RestMethod http://<PC-LAN-IP>:8080/healthz
```

Android Harness 已在 API 36 Google APIs 模拟器安装并冷启动。2026-08-03 再次在
`NuanYa_API36_Review` 验证真实 Termux `TerminalView`：10 个固定 case 全部独立冷启动并
显示 `Verified`，event/bytes/size/exit 与 Manifest 一致，0 crash/ANR。最终截图非空，
ANSI、True Color、CJK/emoji/combining、Vim、less、fzf 和 htop 均可见且没有布局重叠。
切换到“LAN 固定回放”后，`ws://192.168.9.28:8080` 回放
`smoke-powershell-v1`，得到 `Verified · 14 events · 702 bytes · 100×30 · exit 17`。
加入 Phase 1 网络层后，Android/Termux 单元测试总计 176 个、0 失败。阶段 0 模拟器证据在
`docs/validation/phase0-android-emulator.json`；该 IP 是验证时地址，网络变化后必须更新。

固定上游的初次重建已从精确提交生成 49 个源码/构建说明文件并与检入 Fork 一致。新增
第二项兼容补丁后，两次完整 `git fetch` 均因 GitHub connection reset 未能完成；两项
检入 patch 已通过 `git apply --reverse --check`。网络恢复后仍需重跑完整同步。检入 Fork
不包含 Termux JNI 或本地 PTY。

离线 xterm.js Harness 已批量复核全部 10 个固定流：10/10 case、122/122 事件后检查点、
100% match。它严格校验 Bundle、capture ID、序号、网格、样式、光标与公共模式。Harness
无 CDN、Kotlin Bridge、终端输入、Clipboard 或外部导航；它是开发 Oracle，不是 Android
WebView 回退引擎或生产 Relay。报告在 `docs/validation/phase0-grid-report.json`。

Session Host 证据在 `docs/validation/phase0-session-host.json`：四个 Profile、双 ConPTY、
Unicode Agent pipe、Ctrl+C/Alt+B/UpArrow、EOF、timeout、强杀、快速 resize、2 MiB 无
截断、10 秒持续运行及 20 轮生命周期全部通过。Windows 10 19045
`ClosePseudoConsole` 已知 handle 问题、CP936 外层 Unicode 和 Windows EOF 语义见
`docs/TECHNICAL_VALIDATION.md`。

阶段 0 仍缺少 arm64 真机、厂商 IME、蓝牙键鼠、平板/折叠屏与 TalkBack。现有 AVD 只有
API 24 和 API 36，而 app `minSdk` 为 26。这些是外部硬件阻塞，不能用模拟器结果冒充。

阶段 1 临时双向链路已在同一 API 36 模拟器连接 Docker `ws://10.0.2.2:8080`：一次断线
从 ACK 6 恢复并最终到达 ACK 13，测试租约与输入 accepted 可用；History Gap 保留 ACK 4
并明确报告缺失 5..6；exited 到达 ACK 14、exit 17；0 crash/ANR。APK SHA-256 为
`1025fa916abb464df36cec864b571259a83671dd8c8187fec28f2e39e9195d4e`。证据见：

- `docs/validation/phase1-android-session.json`：11 个状态机测试；
- `docs/validation/phase1-android-network.json`：5 个测试传输适配测试；
- `docs/validation/phase1-test-relay.json`：Go Relay 与 LAN Replay 回归；
- `docs/validation/phase1-android-relay-emulator.json`：真实模拟器双向场景。

该链路是明文固定夹具测试设施，不连接真实 Shell；它不替代生产 Relay、Protobuf、设备授权、
E2EE 或可移植 Snapshot。生产实现及其自动化证据位于 `protocol/protobuf`、Go `internal`、
Android `core-*`/`feature-*` 和 Windows `src/Protocol`/`ProductionAgentRuntime`；
`LAN Replay V1` 仍保持只读且未修改。

Windows 正式工程使用 SDK 8.0.303 构建 AgentControl、Agent、SessionHost、Tray 和 Updater，0 warning、
0 error。`Validate-Phase1WindowsApp.ps1` 启动 20 个真实 ConPTY SessionHost，强制结束和
受控重启 Agent 后均从 DPAPI CurrentUser 注册索引恢复 20/20；Tray 读取 20 条脱敏摘要。停止全部
远程访问会清除索引，但 20 个本地 Shell 均继续接收本地输入并正常退出。证据在
`docs/validation/phase1-windows-app.json`。Updater 已通过开发签名双版本、篡改拒绝和 5 条
故障回滚状态验证；生产协议与 runtime 另由 `Validate-ProductionProtocol.ps1` 和
`Validate-ProductionRuntime.ps1` 覆盖。生产受信证书下真实 AppX 安装/更新、备份、镜像签名、
监控告警和 CA/发布加固仍属于开发项 7。

## 14. 新会话建议首条指令

```text
请先完整阅读 E:\WorkSpace\AICodeProject\TermLink\DEVELOPMENT_HANDOFF.md、
docs\COMPLETE_SYSTEM_DESIGN.md 和 docs\UI_LAYOUT_DESIGNS.md，以设计为权威基线并保留
全部未提交成果。阶段 0 当前环境可自动化验收已完成，开发项 1-6 的生产账号/OIDC、设备
配对、Protobuf/E2EE Relay、ConPTY 输入输出、加密历史、计费、产品 UI 与 Compose 接线也
已实现并通过自动化验收；矩阵和边界以本文第 1 节为准。下一步只做开发项 7：生产受信
证书下的真实安装/更新、备份恢复、镜像签名、监控告警和发布加固，以及硬件矩阵。
临时协议不得标记为生产 Protobuf；不要把 LAN Replay 或 Phase 1 Test Session 冒充生产
Relay，不要提前冻结 Snapshot，也不要改变 E2EE、设备授权、本地用户优先和远程输入租约边界。
```
