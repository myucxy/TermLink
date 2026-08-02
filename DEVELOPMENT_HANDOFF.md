# TermLink 开发交接

更新时间：2026-08-02

用途：重新打开会话后先阅读本文，再阅读
[完整产品与系统设计](docs/COMPLETE_SYSTEM_DESIGN.md)。本文只做开发导航，若细节冲突，
以完整设计文档为准。

## 1. 项目位置与当前状态

- 项目目录：`E:\WorkSpace\AICodeProject\TermLink`
- 当前阶段：需求、架构、技术选型、首轮 ConPTY 验证和多端 UI 设计已完成；
- 已有可运行内容：Windows ConPTY 技术验证原型、三端可交互 UI 原型；
- 尚未创建正式的 `protocol`、`win-agent`、`server`、`android` 工程；
- 当前目录未检测到 Git 仓库元数据，新会话开始开发时应先确认版本管理策略。

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
7. PostgreSQL 保存权威元数据，Redis/Valkey 保存在线状态、短时配额占用和输入租约，
   S3 兼容存储保存可选的端到端加密历史块。
8. Relay 只能看到路由元数据与密文大小，不能读取命令、输出、快照或历史正文。
9. 本地用户优先级始终最高；远端输入必须经过设备授权与短时输入租约。
10. MVP 不接入自动执行型 AI。将来只能提供解释、摘要和命令草稿，并在执行前确认。

## 4. 技术栈

### Windows Agent

- C# + .NET 8；
- P/Invoke ConPTY、Named Pipe、DPAPI；
- 进程：`TermLink.Agent.exe`、`TermLink.Tray.exe`、每会话一个
  `termlink-session-host.exe`、受控更新器；
- MVP 以当前用户运行，不让高权限 Windows Service 直接托管用户 Shell；
- 正式 Session Host 不得沿用验证脚本修改父进程全局标准句柄的临时做法。

### Server

- Go；
- PostgreSQL；
- Redis 或 Valkey；
- 可选 S3/MinIO；
- WebSocket + Protobuf 二进制帧；
- Docker Compose 默认只向外暴露 HTTPS 443，数据库和内部端口不公开。

### Android

- Kotlin、Jetpack Compose、Material 3；
- 单向数据流、ViewModel、Coroutines/Flow；
- OkHttp WebSocket 或 Ktor Client + Protobuf；
- Room，敏感数据密钥由 Android Keystore 包装；
- 经审计的 Tink/libsodium 封装，不自研密码算法；
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

运行方式：

```powershell
cd E:\WorkSpace\AICodeProject\TermLink\prototypes\conpty-probe
.\Run-All.ps1
```

该结果只证明“可靠管理自己创建的 ConPTY 会话”可行，尚未证明以下内容：

- 双层 PTY 对所有 TUI 和控制模式完全透明；
- Android 真机渲染兼容；
- WebSocket Relay、ACK、背压和断线恢复；
- 多设备 E2EE 和历史密钥恢复。

## 6. Android Terminal 结论

- 不尝试把微软 Windows Terminal 直接编译成 APK；其 UI、输入、渲染和打包深度绑定
  WinUI、WinRT、DirectWrite、Direct3D 和 Windows App SDK；
- Termux Fork 只复用终端模拟器与 View，不引入 Termux 本地 Shell、PTY、包管理器；
- 需要补齐或重点验证 OSC 8、焦点事件、鼠标模式、选择色、搜索、状态快照、复杂
  grapheme cluster、PowerShell/ConPTY 边缘序列；
- 同一份 Golden Stream 必须同时输入 Termux Fork 和 xterm.js，比较最终网格、样式、
  光标、模式和截图；
- Windows Terminal 配色通过 Scheme/主题字段映射实现，不依赖移植 Windows 渲染器。

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

可交互原型：`ui-prototypes/index.html`

已有页面：

- Android：设备首页、实时终端、历史回放、可信设备、会员与配额；
- Windows Agent：托盘、会话管理、远程访问、设置、账户与会员；
- Server Console：运行概览、连接路由、Docker 部署、安全审计、订阅与权益；
- 支持明暗主题、320px 窄屏展示和多端切换；
- Server Console 套餐页已经体现 Catalog 版本、新建套餐、管理套餐和渠道状态。

UI 设计语言为 Quiet Intelligence：内容优先、低干扰、上下文操作、清晰状态、渐进
披露；不要用大量 KPI 卡片、霓虹渐变或装饰性 AI 元素替代可用性。

本地预览：

```powershell
cd E:\WorkSpace\AICodeProject\TermLink\ui-prototypes
python -m http.server 4173 --bind 127.0.0.1
```

然后打开 `http://127.0.0.1:4173/`。

## 9. 下一步开发顺序

不要先开发完整服务端或计费。当前最高风险是终端兼容，因此从阶段 0 开始。

### 第一开发里程碑：Golden Stream 本地闭环

1. 创建推荐仓库骨架：`protocol`、`win-agent`、`android`、`server`、`testdata`；
2. 扩展 ConPTY Probe/Session Host，按原始字节保存输出块、时间、resize、Shell 生命周期
   和期望最终状态；
3. 录制 PowerShell、CMD、WSL、Vim、fzf、WSL htop、ANSI 色板、True Color、CJK、
   emoji、组合字符、控制键、鼠标和 alternate screen 用例；
4. 建立 `terminal-engine-api` 和最小 Android Harness；
5. 接入固定上游 Commit 的 Termux `terminal-emulator + terminal-view` Fork；
6. 加入离线 xterm.js 对照 Harness；
7. 同流回放并形成 Golden Grid、截图和不兼容清单；
8. 通过验证后再冻结 Protobuf Envelope 与 `TerminalSnapshotV1`。

第一里程碑验收：

- Golden Stream 文件可重复生成和离线回放；
- 原始字节、resize 与事件顺序无损；
- Android 真机完成 PowerShell、Vim/fzf、WSL htop 基本画面对照；
- Termux Fork 与 xterm.js 的差异可自动报告；
- Session Host 退出、断管、超时和强制终止均不泄漏句柄；
- 没有把私有终端引擎序列化格式写入跨端协议。

后续阶段依次为：本地 Agent/Android/测试 Relay 闭环 → Docker 服务端、账号、设备与
E2EE → 历史、计费、完整 UI、安装更新和发布流水线。

## 10. 推荐仓库结构

```text
TermLink/
├── protocol/
│   ├── protobuf/
│   └── test-vectors/
├── win-agent/
│   ├── src/Agent/
│   ├── src/Tray/
│   ├── src/SessionHost/
│   ├── src/Updater/
│   └── tests/
├── server/
│   ├── cmd/termlink-server/
│   ├── internal/
│   ├── migrations/
│   ├── Dockerfile
│   └── compose/
├── android/
│   ├── app/
│   ├── core-*/
│   ├── feature-*/
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
- 不在终端正文上直接运行自动 AI 命令；
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
- [可交互 UI 原型](ui-prototypes/index.html)。

## 13. 当前质量检查

以下检查在交接前已通过：

```powershell
cd E:\WorkSpace\AICodeProject\TermLink
node --check ui-prototypes/app.js
npx --yes html-validate ui-prototypes/index.html
npx --yes markdownlint-cli2 "README.md" "docs/**/*.md" "DEVELOPMENT_HANDOFF.md"
```

UI 已实际验证 Android、Windows Agent、Server Console、明暗主题、320px 窄屏和会员
入口，浏览器控制台无错误。

## 14. 新会话建议首条指令

```text
请先完整阅读 E:\WorkSpace\AICodeProject\TermLink\DEVELOPMENT_HANDOFF.md 和
docs\COMPLETE_SYSTEM_DESIGN.md，以完整设计为权威基线。开始阶段 0：创建正式仓库骨架，
扩展 ConPTY Golden Stream 录制格式，并建立最小 Android TerminalEngine 回放 Harness。
先检查现有工作区和 ConPTY 原型，制定短计划后直接实现与验证；不要提前开发完整计费，
不要改变受管会话、原始 VT 字节、Termux Fork 默认引擎、E2EE 和可配置套餐等冻结决策。
```
