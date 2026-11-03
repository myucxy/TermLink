# macOS 客户端开发

2026-10-25：第一阶段已实现 portable Host 的账号与 Relay 控制链路。继续使用 `pc-client/` 的 Avalonia UI 和展示状态，macOS 适配位于 `src/MacOS/`。Windows 生产 Agent 保持现有入口；两端复用 `win-agent/src/Protocol/` 的协议、Ed25519/X25519 身份及加密实现，没有复制一套协议源码。

## 已实现的范围

- 设置服务器地址、系统浏览器授权、PKCE S256、随机 state、动态 IPv4 loopback 回调；不接受账号密码登录。
- 使用现有 `/v1/client-config` 的 `windows_oidc_client_id` 与 `windows_redirect_uri`，作为当前桌面公共客户端。回调模板为 `http://127.0.0.1/oauth2/callback`，沿用 Windows 已有的动态端口配置。通过 `PC_AGENT` 注册电脑，按 `macos`、`arm64`／`x64` 上报平台信息；不伪装成 Windows。
- 设备注册证明、每次 REST／WebSocket 请求独立的设备签名、严格 self 身份核对。注册冲突必须再次证明原设备密钥；撤销或身份不符时停止连接，不自动换钥绕过拒绝。
- `termlink.realtime.v1` Hello／Welcome、连接状态、应用层 ping/pong、电脑元数据心跳、设备列表、带上限和抖动的重连。
- Token 刷新及轮换、刷新后的 WebSocket 重新认证、账号变更拒绝、重启后恢复、手动重试和远程访问开关。登录成功默认不启用远程访问。
- Keychain 保存活动凭据及按服务器／issuer／subject 隔离的设备身份。退出登录删除活动 Token，保留无 Token 的原设备密钥和 PC ID。同账号再登录沿用原身份；没有明文文件回退。
- 配置服务器或退出登录会取消尚未完成的授权，旧回调不能写回已退出的账号。切换服务器前须退出当前账号。
- 本机 CLI 检测通过认证 IPC 接入。ZCode 额外检测现有 App 的内置 `app-server` 并使用 Electron 自带运行时；其他 Agent 目前仍只检查本机启动。安装和协议可用不等于模型授权已通过。

生产连接保留系统 TLS 验证且禁止 HTTP 重定向。HTTP 只允许 loopback 本地开发，授权端点须与 discovery issuer 同源。HTTP 响应最大 64 KiB、Relay 控制消息最大 16 KiB，未验证的服务端响应不会进入凭据存储。

2026-10-26 源码核对发现：旧服务端强制 PC 声明远程终端能力，会拒绝当前 Mac 的显式空功能清单。本轮已修复服务端校验，并用实际 Go 校验与签名 Relay handler 补测；本轮没有部署该修复。同日对 `https://termlink2.kget.cc:8910` 的真实账号联测中，打包后的原连接模块已成功注册设备、上报空能力电脑元数据并连接 Relay，说明该地址当前接受此契约，不能据此推断其部署 revision。契约见 [Mac 服务端接入](../server/MACOS_ACCESS.md)，实际结果及边界见 [联测记录](../artifacts/macos-termlink2-login-20261026/REPORT.md)。

## 托盘退出行为

2026-10-26：macOS 托盘提供“退出控制界面”和“完全退出 TermLink”。前者只关闭 UI，后台 Agent 继续运行。后者经确认后，通过认证 IPC 取消待完成的浏览器授权、断开 Relay、停止受管 PTY 会话，等待当前安装的 Agent 进程实际结束，再关闭 UI。退出过程中禁止状态轮询重新启动 Agent；停止失败会保留界面并显示错误，允许重试。

完全退出保留 Keychain 中的登录信息、设备身份和原有自启动配置。它使用正常退出码，与 launchd 模板的 `KeepAlive/SuccessfulExit=false` 配合；不会删除用户的 LaunchAgent 配置，也不会按进程名强制结束其他安装。未运行 Agent 时不会为了退出而启动它。预览模式没有真实后台，不显示完全退出入口。

## 与 Windows 的剩余差异

**2026-10-27 已新增 Mac ZCode 原生 AI 请求链路，真实账号模型与手机跨端使用尚未验收。** 使用现有 App 内置 Agent，共享 Windows 原生会话协议和控制逻辑，Relay 只转发加密 `AiAgentRequest/Response`；电脑端再次核对 MANAGE 设备、密钥 epoch 和接收者。服务端需包含允许 Mac/Linux 单独声明 `ai_agent_v1` 的校验修复，本轮未部署。

Mac 只上报 `ai_agent_v1`，不虚报终端、snapshot、远程文件或更新能力。ZCode 的模型账户运行时授权仍明确拒绝并返回 `AGENT_DESKTOP_AUTH_REQUIRED`；独立 API Provider 需真实模型验证。其他 Agent 的远程执行暂未开放。Mac AI 目前不支持附件／文件引用／成果读取和持久化历史，不暴露共享代码中的 Windows 文件句柄与 DPAPI 路径；重启／完全退出后需要重建会话。

| 范围 | 后续工作 |
| --- | --- |
| CLI | 连接 POSIX PTY、权威终端状态、租约与输入 ACK、背压、断线恢复和独立 SessionHost 生命周期 |
| terminal-core | 从独立 core 导出 macOS arm64／x64 二进制、独立来源与哈希校验，再接入现有 .NET 绑定；不能复制 Rust 源码回应用 |
| 文件 | POSIX 路径授权、远程列目录、加密上传／下载、ZIP、取消与断点恢复 |
| AI | ZCode 原生会话及加密承载已接入；官方账户运行时授权、真实模型／手机验收、其他 Agent、归档、附件和续聊仍待完成 |
| 设备管理 | Grant／Revoke 操作尚未接入 portable Host，当前返回明确不可用错误 |
| 安装与更新 | PKG 下载／校验／系统安装／重启流程已实现；仍需 Developer ID、真实公证与两版本升级验收。无自动回滚，launchd 仍待实机验收 |
| 跨端验收 | termlink2 上的隔离真实账号控制连接已验证；现有 DEV 安装的 Keychain 授权及界面登录、Android/iOS、Intel Mac、睡眠唤醒和弱网仍待验证 |

当前范围为 Relay Only。

## 桌面功能检查（2026-10-26）

| 页面／操作 | macOS 当前行为与验证 |
| --- | --- |
| 常规与外观 | 平台和版本来自本机；系统／浅色／深色可切换。DEV 主题保存在独立的 `~/Library/Application Support/TermLink DEV/` |
| 账号 | 浏览器 PKCE 登录、退出、服务器切换及取消待完成授权已接通；重新打开同一授权网页未实现，因此不显示该按钮。除受控 OIDC 回归外，termlink2 真实 PKCE、Token 刷新及独立 Keychain 存取已通过模块联测；现有安装的界面授权仍待人工完成 |
| 服务器连接 | 登录后可从离线状态启用 Relay；停止／重试沿用连接服务。页面明确此版本只接通控制连接；短暂 IPC 失败后轮询会继续恢复 |
| AI Agent | ZCode 直接复用 `/Applications/ZCode.app` 或用户 Applications 中的内置 Agent／Electron；不填远控 URL。其他 CLI 保留本机检测与官方帮助。模型授权单独验证 |
| 命令 | 显示本机实际存在的 Shell；刷新重新检测，Unix 命令不标为管理员运行。当前仅支持内置 Shell，未实现可持久化的自定义启动，编辑／保存入口暂不提供，Host 也明确拒绝 |
| 集成 | 只显示 macOS 终端、系统登录项设置和 Finder 应用位置；Windows Terminal 配置／修复入口不显示，命令也不可执行。原生 open 调用的固定目标、路径传参及失败处理通过适配测试 |
| 登录自启动 | 系统设置入口可打开；开发包仍需手动启动，未提供自动注册开关。现有 launchd 适配不等于已完成实机启动验收 |
| 诊断 | Finder 打开 DEV 独立诊断目录，并生成 OS／架构／渠道／Agent 文件存在性摘要；不收集凭据、账号或授权回调 URL |
| 关于与更新 | DEV 包手动安装，不显示更新按钮。已配置并受信的正式安装使用独立 Mac AppShip 渠道自动检查，支持确认下载、包校验、系统授权安装与重启；当前未发布该渠道 |
| 会话、文件与设备管理 | Mac ZCode 从手机原生 AI 页面管理，本机桌面尚无 AI 任务列表。远程终端、文件及设备 Grant／Revoke 未接通；新增原生链路不等于真实模型验收 |
| 托盘与窗口 | 保留原始图标、窗口隐藏／唤回、UI-only 和完全退出；真实子进程验证 Agent／PTY 停止，原生菜单点击仍需人工验收 |

AI CLI 探测逐进程限时 4 秒、双流合计最大 64 KiB；超时、取消和输出超限会停止本次创建的探测进程。测试覆盖含空格路径、stderr 版本、缺失运行时、非零退出、超时、取消、输出洪泛和显式 PATH 隔离。

## 本机开发与构建

使用固定 .NET SDK `8.0.303`。Grpc.Tools `2.72.0` 随附的 macOS protoc 为 Intel 程序；Apple Silicon 可通过 `-p:Protobuf_ProtocFullPath=<arm64 protoc>` 指定经校验的 protoc `31.1`，无需安装 Rosetta 或改动 NuGet 缓存。工具安装在开发环境目录，不能放进仓库或改系统 PATH。

从本机磁盘检出构建；网络盘工作区可先同步到本机缓存目录。以下变量由调用者指向已安装的工具，不包含 Token 或密码：

```bash
cd pc-client
"$dotnet_path" run --project tests/RelayConnectionValidation -c Debug \
  -p:Version=0.3.16 -p:AssemblyVersion=0.3.16.146 -p:FileVersion=0.3.16.146 \
  -p:TermLinkDevelopmentBuild=true -p:RestoreLockedMode=true \
  "-p:Protobuf_ProtocFullPath=$protoc_path"
"$dotnet_path" run --project tests/Desktop.HeadlessTests -c Debug \
  -p:EnableWindowsTargeting=true -p:TermLinkDevelopmentBuild=true -p:RestoreLockedMode=true \
  -p:Version=0.3.16 -p:AssemblyVersion=0.3.16.146 -p:FileVersion=0.3.16.146
```

保留锁文件中的全部框架和 RID，不通过覆盖 `TargetFrameworks` 将桌面锁文件改成单平台。

从仓库根目录生成当前机器架构的自包含 DEV `.app` 和 ZIP：

```bash
TERMLINK_SOURCE_REVISION="$source_revision" bash pc-client/package/macos/build-dev-app.sh \
  0.3.16 146 "$dotnet_path" "$protoc_path" "$new_output_directory"
```

`source_revision` 为对应检出的 Git commit；工作区有未提交变更时还须记录源码清单与哈希。版本参数沿用现有开发基线，不表示发布新的 macOS 正式版本。脚本要求新输出目录，使用本机临时目录构建包，附本地 ad-hoc 签名，无在线发布或正式公证。

DEV 构建使用独立 bundle `dev.termlink.desktop.dev`、Keychain service `dev.termlink.agent.dev`、`~/Library/Application Support/TermLink DEV`、`agent-dev-v1.sock`、launchd label 和桌面实例作用域。正式版保留原有路径。Agent apphost、DLL、依赖和 PTY 库共同保存在 `Contents/MacOS/agent/`，不将 apphost 单独移出依赖目录。

DEV 和 PKG 使用原始 `assets/brand/termlink-app-icon.svg` 生成多尺寸 `TermLink.icns`，打包至 `Contents/Resources/` 并声明 `CFBundleIconFile`，保持 Finder、Dock 与现有窗口／托盘的品牌图标一致。

DEV 首次安装可用 [手动 PKG](package/macos/DEV-PKG.md)。正式更新的信任固定、下载与安装流程、AppShip 字段和签名／公证前提见 [macOS 自动更新](package/macos/AUTO-UPDATE.md)。当前只有本地开发签名，DEV 不能在线自动升级；尚未执行正式安装、发布或公证。

只预览界面、不连接真实账号时：

```bash
open "$new_output_directory/TermLink DEV.app" --args --preview-fixture
```

## 验证边界

2026-10-26 另行使用 DEV 包内原版 `BrowserAuthorization` 和 `RelayConnectionService` 连接 termlink2：真实 PKCE 登录、Token 刷新、签名设备注册／self 核对、空能力元数据上报、Relay 握手／心跳、设备列表、手动重试、从独立 Keychain 服务重新载入账号及退出登录已通过。仅替换打开浏览器的动作来驱动真实登录表单，未替换 HTTP、Token、设备签名或 WebSocket。独立 Keychain 服务与现有 DEV 凭据隔离；测试设备已撤销，临时凭据已删除。重新载入发生于同一测试进程中重新创建连接服务，不等于应用进程重启验收。现有 DEV 安装的钥匙串授权未完成，完整界面登录、设备重启和手机端操作未验收。详见 [联测记录](../artifacts/macos-termlink2-login-20261026/REPORT.md)。

同次真实 IPC 检查发现 .NET 8 无法反序列化 `IReadOnlySet<string>`，会导致 UI 读取 Agent 状态失败。`LocalAgentProtocol` 现为能力集合提供精确类型转换，保留字符串数组格式、Ordinal 比较和只读集合；真实 Unix socket 回归覆盖空／非空集合、非法值拒绝及错误后恢复。

连接验证使用真实本机 HTTP/WebSocket listener 和独立 Ed25519 验签，OAuth Token、设备目录和服务端状态使用受控夹具。16 个场景覆盖 PKCE 回调、签名注册与冲突、Relay 重连、账号隔离、退出取消、Token 轮换及换账号拒绝、撤销设备、异常 discovery／Welcome、消息长度及 Host 命令接入，以及完全退出后的登录保留、PTY 清理、授权取消、清理失败重试、主动取消授权和只读命令刷新。夹具 Token 不等于真实 OIDC 验签或线上接入验证。

macOS 上还验证本机 PTY 的 Unicode/ANSI、窗口大小和进程组退出，及文件根目录隔离、Unix socket 认证、共用协议向量和桌面 Headless。修复了 macOS `EAGAIN=35` 与 Linux `11` 的差异、桌面激活管道长度限制，以及 Headless 无运行中 dispatcher 导致的 await 等待。Linux AppImage 验证在 macOS 明确标为不适用。

完全退出测试覆盖伪造或拒绝的 Shutdown 不关闭服务、取消／重复点击／失败重试、仅退出 UI 不停止后台，以及退出期间的启动和轮询屏障。隔离的真实子进程测试验证认证回执、Agent 正常退出码 0、PTY 子进程结束及 socket／PID 文件清理，并拒绝监视其他可执行文件的 PID。PTY 停止时排空待读输出，避免 macOS 子进程卡在退出阶段。Headless 验证菜单命令和退出顺序，不代替原生托盘点击及真实 launchd 验收。

本地编译和 ad-hoc 包不代表正式签名、公证、真实账号或远程终端验收通过。

## ZCode 原生迁移（2026-10-27）

电脑端移除保存／清除／打开远控 URL 的界面和存储读取；旧地址文件不再解密或传给手机，也不因其存在覆盖 native 策略。Android／iOS 同时移除官方远控网页路由，逐条清理旧网页引用并保留原生会话和草稿；旧宿主仅声明 `desktop_remote` 时提示升级而不调用原生请求。ZCode 图标与别名保留。

独立会话数据库放在 TermLink 账户目录，不打开现有 ZCode 的桌面或 CLI 会话 DB。电脑端正常关闭、禁用远程访问、账号退出、授权变化或连接错误会停止对应 AI Owner。Unix 退出清理使用受管进程树，不等同于 Windows Job 的异常崩溃清理保证；真实异常终止仍待平台专项验证。
