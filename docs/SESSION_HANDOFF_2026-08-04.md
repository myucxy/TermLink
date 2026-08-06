# TermLink 开发会话交接记录

日期：2026-08-04
工作区：`E:\WorkSpace\AICodeProject\TermLink`
分支：`main`

## 1. 产品目标

TermLink 是跨 Windows 与 Android 的受管远程终端，不是远程桌面。Windows 端通过
TermLink Windows Terminal Profile 启动 Session Host，Session Host 创建内层 ConPTY 和
PowerShell/CMD/WSL。Win-Agent 将有序 VT/ANSI 字节端到端加密后经 Docker Relay 转发给
Android，Android 独立渲染并在获得控制租约后发送输入。

目标使用场景包括在受管 PowerShell 中运行 `codex`、其他 AI CLI、编译和运维命令：

```text
Windows Terminal / TermLink Profile
  -> TermLink Session Host
  -> Inner ConPTY
  -> PowerShell
  -> codex / other AI CLI
```

本地 Windows Terminal 和 Android 应看到同一份终端输出。Android 默认只读，取得短时输入
租约后才允许输入；本地键盘始终优先并可立即夺回控制。

## 2. 已确认的产品边界

- 只管理 TermLink 创建的会话，不抓取、注入或接管任意已存在的普通 Windows Terminal 标签页；
- 传输终端原始字节与生命周期事件，不传屏幕截图；
- Codex/AI CLI 只要继续运行在同一个 ConPTY 中，输出和输入都属于目标范围；
- Windows Terminal 标签栏、菜单、浏览器登录窗口和独立 GUI 不属于远程终端内容；
- 设备登录不等于获得 PC 访问权，新设备必须批准；远程输入还需要权限和控制租约；
- Relay 不持有终端正文解密密钥，生产载荷必须 E2EE；
- 本地 Shell 不依赖 Relay，Android 断线或会员到期不能终止本地 Shell；
- 简体中文和 English 是 Android、Windows、Server Console 的统一需求。

权威设计入口：

- `docs/COMPLETE_SYSTEM_DESIGN.md`
- `docs/PRODUCT_REQUIREMENTS_AND_SOLUTION.md`
- `docs/TECHNICAL_VALIDATION.md`
- `docs/UI_LAYOUT_DESIGNS.md`

## 3. 已完成内容

### 3.1 Windows 与终端基础

- Windows ConPTY 输出、输入、resize、控制键、退出和异常路径已有实机验证；
- Session Host、Agent IPC、Session Registry、DPAPI Buffer、输入 ID 去重、本地输入优先和
  Agent 重启恢复已有实现与测试证据；
- Windows Terminal TermLink Profile 与双层 PTY 是正式设计；
- 当前代码可把通过 Agent 验证的远端输入写入 ConPTY。

### 3.2 Android 与测试链路

- Termux 派生 TerminalEngine、会话状态机、只读/控制状态、输入提交、ACK、Gap、断线恢复和
  resize 基础已实现；
- API 36 模拟器完成本地回放和临时双向测试 Relay 验证；
- 临时 `TLT1` 测试链路验证了输入 accepted、测试租约、Flow Control、History Gap 和 exited；
- Android 已有生产 Realtime、Envelope、E2EE 和控制租约相关代码，但仍需真实跨进程闭环验证。

### 3.3 Server 与产品功能

- Go API、Realtime Relay、设备、配对、Grant、Lease、撤销、历史密文和 Docker/Compose 基础已实现；
- Android 账号注册登录、邮箱验证、会员兑换码、账号安全和服务端管理能力已纳入设计；
- Docker 首次启动要求创建 `admin` 超级管理员，临时密码只在首次初始化日志显示一次，首次
  登录必须改密并引导 MFA/恢复码；
- 服务器公开地址：`https://termlink.kget.cc:8910/`；
- Lucky 本机上游：`http://192.168.9.28:8080`，只用于映射测试，不作为客户端公开地址。

### 3.4 UI 原型

- `ui-prototypes` 当前覆盖 20 页：Android 7、Windows 5、Server 8；
- Android 与 Server 登录/注册页已重做，支持回车提交、必填校验、键盘 Tab、双语动态弹层；
- 账号、邮箱验证、首次管理员强制改密、MFA、登录撤销、会员兑换和兑换码批次流程均有原型；
- 320px、390px 和桌面真实浏览器检查无页面级横向溢出；
- 本地启动：

```powershell
Set-Location ui-prototypes
python -m http.server 4173
```

原型入口：`http://127.0.0.1:4173/`

### 3.5 品牌图标

正式选定 `01B Prompt Ligature`：终端框 + 命令提示符 + TL 字标 + 状态节点。

源文件位于 `assets/brand/`：

- `termlink-app-icon.svg`
- `termlink-favicon.svg`
- `termlink-mark-color.svg`
- `termlink-mark-light.svg`
- `termlink-mark-dark.svg`
- `README.md`

尚未生成或接入 Windows ICO/MSIX PNG、Android Adaptive Icon 和各网页 favicon 引用。

## 4. 当前真实能力边界

设计上支持 Android 实时观看并接管受管 PowerShell/Codex 会话，但当前项目还不能表述为生产
闭环完成。

已验证：

- 真实 Windows ConPTY 的本地捕获和输入；
- Host-Agent 本地 IPC、缓冲、ACK 和输入去重；
- Android TerminalEngine 与临时双向 Relay；
- Android 会话控制租约状态机和生产协议基础代码；
- Server Realtime、设备和租约的模块级/集成测试。

尚未完成或需要重新实测确认：

- 真实 Windows Agent -> 公网/统一 Docker Relay -> Android 的生产 E2EE 双向闭环；
- 真实 OIDC 登录、设备注册、配对和生产凭据下的全链路；
- 使用真实 Codex/其他 AI CLI 的流式输出、全屏 TUI、Unicode、Ctrl+C、粘贴和 resize；
- PC 本地键盘抢占与在途 Android 输入竞争时的跨进程验收；
- 断网/手机休眠/Relay 重启后不重复执行输入并从 ACK 正确恢复；
- Windows Terminal 与 Android 不同网格尺寸下的 resize 所有权策略；
- 真机 IME、蓝牙键盘、TalkBack、耗电和长时间高吞吐测试；
- 生产证书链、实际登录周期和外部部署验收。

临时 `TLT1` 测试 Relay 明确不是生产 Protobuf/Relay/E2EE，而且不连接真实 Windows Agent 或
真实 Shell。继续开发时不能把它误当成生产闭环证据。

## 5. 下一阶段建议目标

优先完成一个真实、范围受控的端到端切片：

```text
Windows Terminal TermLink Profile
  -> Session Host / real PowerShell / real codex
  -> Win-Agent production realtime adapter
  -> Docker API + Relay at configured public origin
  -> Android production repository + TerminalEngine
  -> view -> request control -> input -> local preemption -> reconnect
```

推荐顺序：

1. 审计 Win-Agent、Server、Android 当前生产协议接口和未连接点，先形成准确差距表；
2. 建立可重复的本机 Docker OIDC/TLS/Relay 测试环境，统一公开 Origin 与回调；
3. 将 Win-Agent Session Registry 输出接入生产 Realtime/E2EE，不经过临时 `TLT1`；
4. 将 Android 产品页面接入真实账号、设备、Session、Realtime 和控制租约；
5. 实现并验证本地输入抢占、输入去重、ACK、背压、Gap、resize 和退出；
6. 在 Windows Terminal TermLink Profile 中运行真实 Codex，完成下面的验收矩阵；
7. 闭环通过后再接入选定品牌图标到 Windows、Android 和网页。

## 6. 真实 Codex 验收矩阵

- Android 只读观看 Codex 流式输出，颜色、光标、宽字符和滚动正确；
- Android 请求控制并输入英文、中文、Enter、Tab、方向键和 Ctrl+C；
- 本地 Windows 键盘输入立即使旧远端输入失效；
- Android 断线期间 Codex 和本地 Shell 继续运行；
- 重连只补发最后连续 ACK 之后的输出，不重复执行任何输入；
- Android 与 Windows 尺寸变化不会相互抖动或破坏 TUI；
- Codex 打开的浏览器/系统窗口明确提示只能在 PC 或对应网页处理；
- Relay、数据库、日志和诊断中不出现终端正文、提示词、Token 或 Secret；
- 稳定网络下输出和输入区域内 P95 目标均为 250ms 以内；
- 连续运行、Relay 重启、慢消费者和手机休眠恢复无 crash/ANR。

## 7. Git 状态与提交

### 2026-08-06 生产 Windows 设备重新绑定与 Relay 验证

本次现场验证解决了已安装 Win-Agent 托盘显示“Relay 未连接”的设备身份问题：

- 旧 `production-config.json` 与 `production-credentials.dpapi` 已移动（未删除）到本机
  `%LOCALAPPDATA%\TermLink\identity-backup-20260806-123119` 备份目录；文档和日志不包含其中的凭据内容。
- 使用当前已登录的 OIDC 会话完成 Windows 账户绑定，生成新的本机设备身份和 PC 配置。
- 受控重启已安装 Agent 后，控制管道 `List` 返回 `RelayState=Connected`、空错误码；
  `RefreshProductionStatus` 返回 `PRODUCTION_STATUS_REFRESHED`，同样保持 `Connected`。
- 生产状态返回 2 台设备、1 个会话；既有 `TermLink.SessionHost` PID 38000 和
  `WindowsTerminal` PID 28752 均保持运行，未关闭用户窗口。

这证明了当前账号下 Windows 设备注册、PC 上报和生产 Realtime 连接已恢复；尚不能替代
Android 真机/模拟器观看、控制租约、输入去重和断线恢复的完整跨端验收。

本交接前的关键提交：

- `6190172 feat: complete production development items 1-6`
- `4775c1e feat: complete production development item 7`
- `be9fd08 fix: unblock local relay emulator validation`
- `4fd0203 feat: refine account authentication prototypes`
- `08e7739 feat: add TermLink brand icon system`

新会话开始时先执行：

```powershell
git status --short
git log -6 --oneline
```

不得回退用户或其他代理已有修改。任何 Secret、证书密码、Token、OIDC Secret 和数据库密码
只保存在本地忽略文件或 Docker Secret 中，不写入本文档、Git、日志或测试输出。

## 8. 新会话执行原则

- 先阅读本交接记录和四份权威设计文档，再读相关代码；
- 先审计真实生产链路，不重复实现已通过的 ConPTY、TerminalEngine 或临时 Relay；
- 任何“已完成”必须有真实跨进程证据，不能只靠单元测试、UI 原型或 Mock；
- 服务端优先，随后 Windows，再接 Android，最后完成真实设备/模拟器闭环；
- 适合 `luna_worker` 的独立测试、局部实现和机械任务可以委派，但架构、安全、协议和最终
  集成由主代理负责；并行代理不得修改重叠文件；
- 每个阶段完成后运行对应测试、检查 Git 差异，并提交范围清晰的 commit。
