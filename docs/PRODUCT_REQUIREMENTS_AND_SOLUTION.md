# TermLink 产品需求与技术方案

文档版本：0.1
状态：立项草案（终端信息获取与服务端转发以[专项需求基线](TERMINAL_INFORMATION_FORWARDING_REQUIREMENTS.md)为准；Server Web、邀请码、兑换码与支付以[商业化控制面需求](SERVER_WEB_PRODUCT_REQUIREMENTS.md)为准）
更新日期：2026-08-27

## 1. 项目定义

TermLink 是一个多设备远程终端系统，由 Windows PC Agent、云端中继服务和 Android 客户端组成。

用户在 Windows 上启动 TermLink 受管终端后，系统持续采集该会话的 ANSI/UTF-8 输入输出流、终端尺寸和生命周期事件。Windows Agent 与 Android 登录同一 OIDC 账号后，Android 无需配对、自授信或逐 PC Grant 即可：

- 查看账号下不同 PC 的在线状态；
- 查看每台 PC 上的终端会话列表和运行状态；
- 实时显示终端内容，并在网络重连后恢复画面；
- 查看允许保留的历史会话记录；
- 在拥有控制权限时发送文字、组合键和终端尺寸变化；
- 接收命令完成、会话退出、断线和高风险操作提醒。

TermLink 不是远程桌面，也不抓取 Windows Terminal 的图像。它传输的是终端字节流，因此网络占用更低、文字更清晰，也便于重放和搜索。

## 2. 需求优化与范围边界

### 2.1 对原始想法的收敛

> 2026-08-15 架构重整：本节中“Session Host 通过 ConPTY 创建并桥接”的描述是迁移前方案，不再作为新的生产实现要求。目标生产来源是 `termForward/terminal-main` 内的 `ITerminalConnection` 异步通道；当前生产 runtime 尚未完成注册、IPC 和数据源切换，详见[终端信息获取与服务端转发需求基线](TERMINAL_INFORMATION_FORWARDING_REQUIREMENTS.md)。

原始想法中的“记录每个 Terminal 会话窗口”调整为“记录每个 TermLink 受管会话”。原因是 Windows Terminal 没有向第三方提供稳定、完整的接口，用来旁路读取并控制任意已打开标签页的输入输出。依赖窗口抓取、键盘钩子或进程注入会带来兼容性、安全性和内容准确性问题。

推荐实现方式如下：

1. PC Agent 安装后注册 TermLink PowerShell、CMD、WSL 等 Windows Terminal Profile。
2. 用户从该 Profile 打开标签页，或在 TermLink 托盘程序中点击“新建会话”。
3. `termlink-session-host` 通过 Windows ConPTY 创建真实 Shell，并在本地终端与 PC Agent 之间桥接输入输出。
4. PC Agent 将同一份会话事件加密后发送给云端 Relay；Android 解密并渲染。

这样能够可靠支持彩色输出、全屏 TUI、退格和光标移动、窗口缩放、Ctrl+C 等控制键以及 WSL 会话。

### 2.2 首版纳入范围

- Windows 10 1809 及以上，优先支持 Windows 11；
- Windows Terminal 中由 TermLink Profile 启动的 PowerShell、CMD 和 WSL 会话；
- 一个账号下多台 PC、多会话发现和切换；
- 实时输出、远程输入、断线重连和最近历史回放；
- 同账号设备自动注册与直接发现、严格账号隔离和设备撤销；
- 会话内容端到端加密；
- 本地优先的输入控制权和基础安全审计。

### 2.3 首版不纳入范围

- 无侵入接管已由普通 Profile 打开的既有 Terminal 标签页；
- 远程桌面、GUI 应用控制、文件管理和端口转发；
- macOS/Linux 主机 Agent；
- 多人同时编辑同一终端；
- 企业级组织、LDAP、审计管理员和合规留存；
- 根据命令文本做绝对可靠的危险命令判断。

## 3. 用户角色与核心场景

### 3.1 用户角色

- 账号所有者：登录、绑定设备、配置留存策略和撤销设备；
- PC 本地用户：创建会话、批准远程控制、随时夺回控制权；
- Android 远程用户：浏览同账号 PC 和受管会话，并在取得短时输入租约后控制。

底层仍保留 `VIEW`、`CONTROL`、`MANAGE` 权限和 Grant 数据，以兼容旧部署与旧客户端；首版当前产品主流程不要求用户选择或批准这些权限，同账号、未撤销的 Android 按账号级直接访问策略处理。兼容层语义如下：

| 权限 | 能力 |
| --- | --- |
| 只读 | 查看实时输出及授权范围内的历史记录 |
| 可控制 | 只读能力，以及发送输入、控制键和窗口尺寸 |
| 设备管理 | 可控制能力，以及批准、降权或撤销其他设备 |

### 3.2 核心用户故事

1. 用户在办公室 PC 的 TermLink PowerShell 中运行耗时任务，离开后在手机上继续观察日志。
2. 用户从 Android 选择家中 PC 的某个会话，取得输入权，确认提示并继续执行。
3. 手机网络切换造成短暂断线，重新连接后从最后一个已确认序号继续接收，终端画面不丢失。
4. 用户拥有多台 PC，可以按设备名称、在线状态和最近活跃时间快速定位会话。
5. 手机丢失后，用户在 PC 或账号安全页撤销该设备；其令牌、会话密钥和控制权限立即失效。

## 4. 功能需求

### 4.1 Windows PC Agent

#### 安装与运行

- 提供安装包、自动更新和卸载能力；
- 登录 Windows 后自动启动托盘程序；需要跨登录持续运行时，再启用系统服务；
- 首次登录采用浏览器授权码或设备授权流程，不在本地保存账号密码；
- 自动生成设备密钥，并使用 Windows DPAPI 保护私钥；
- 允许设置设备名称、是否允许远程输入、记录保留时长和单会话缓冲上限。

#### 会话管理

- 注册 TermLink PowerShell、CMD、WSL Profile；
- 创建、列出、重命名、终止和归档受管会话；
- 每个会话记录：会话 ID、PC ID、Shell 类型、标题、工作目录、创建时间、状态、最后活动时间和退出码；
- 转发 ConPTY 输出、输入、终端尺寸变化和进程退出事件；
- 本地网络中断时写入加密环形缓冲，恢复后按序补发；
- PC Agent 重启后，将原会话标为中断或恢复可恢复的会话元数据，不伪造仍在线状态。

#### 本地控制与隐私

- 托盘图标明确显示是否存在远程查看者和远程控制者；
- 远程首次获取输入权时，可按策略要求 PC 本地确认；
- 本地键盘输入可立即夺回输入权；
- 提供“一键停止所有远程访问”和“仅本机模式”；
- 支持会话暂停同步、停止记录和永久删除；
- 默认不把本地剪贴板自动同步到手机。

### 4.2 Android 客户端

#### 账号和设备

- 使用 OIDC 登录；Android 当前优先通过固定 HTTPS 身份端点进行原生登录，身份提供方要求交互时再使用 Authorization Code + PKCE 浏览器回退；
- 展示 PC 列表：名称、在线/离线、系统版本、Agent 版本、最近在线时间；
- 新 Android 设备登录同一 OIDC 账号后自动注册，并可直接发现和使用该账号下的 PC/Session；不得显示配对、自授信或逐 PC Grant 前置步骤；
- 同账号多台 Android 可以同时查看同一会话，但 Snapshot、Transcript、Event Chunk、签名下载 URL、密钥和本地缓存都必须按接收设备隔离；一台手机不得读取或覆盖另一台手机的对象；
- 不同 OIDC subject 的设备、PC 和 Session 必须严格隔离；
- Android 加密保存经过清理的 OIDC 会话以支持进程重启后恢复，绝不持久化登录密码；冷启动恢复和 Refresh Token 轮换必须纳入验收；
- 支持主动退出；退出只清理当前登录和本地运行态，不等同于设备撤销/解绑。显式撤销本设备入口及跨端效果仍需产品验收确认。

#### 会话浏览和显示

- 按 PC 分组展示运行中、已退出和已归档会话；
- 会话页支持 ANSI 颜色、256 色、True Color、光标、清屏、滚动区域和常见 TUI；
- 支持横竖屏、字号调整、软键盘辅助栏和保持屏幕常亮；
- 新能力启用时按 `select -> download -> prepare -> cache -> bind -> install -> subscribe(S)`
  从终端状态快照进入，随后只应用 S+1 及之后的增量事件；任一步失败都不得用不完整状态推进 ACK；
- 首次打开只显示 Snapshot S 的最后可见屏幕，不预取更早 Transcript；到达顶部后才按固定 S
  每次向前加载一页（最多 200 行）到独立历史 viewport；
- 支持查看最近历史、跳转最新输出以及在已解密内容中进行本地搜索；浏览历史时实时输出和 ACK
  继续前进，关闭历史层后回到最新输出；
- 明确展示“实时”“正在重连”“只读”“控制中”“已退出”等状态。
- 上述状态、History gap、历史为空、分页失败、恢复和重试操作必须提供简体中文与 English 文案，
  并在软键盘打开、横竖屏和系统大字体下保持 Terminal、底栏和 IME 不重叠。

#### 远程输入

- 支持普通文本、粘贴、Enter、Tab、Esc、方向键、Ctrl/Alt 组合键和 Ctrl+C；
- 输入以有序事件发送，包含消息 ID，服务端和 PC Agent 去重；
- 同一会话同一时刻只授予一个远程输入租约，避免多手机交叉输入；
- 获取输入租约时展示操作者及有效期，闲置或断线后自动释放；
- 粘贴多行命令前显示预览和确认，可由用户关闭；
- 高风险确认只能作为辅助保护，不承诺通过文本解析识别所有危险操作。

### 4.3 云端 Relay 与账号服务

- 管理账号归属、设备、设备公钥、撤销状态、PC 在线状态和会话索引；
- 使用 TLS WebSocket 转发加密后的实时事件；
- 支持心跳、断线检测、ACK、按序重发、背压和限流；
- 服务端只保存完成路由所需的元数据，无法解密终端正文；
- 历史内容若启用云端保存，以端到端加密的数据块写入对象存储；
- 支持令牌刷新、设备撤销、密钥轮换和所有会话强制下线；
- 记录安全审计元数据：登录、设备注册/撤销、控制租约、会话开始/结束和删除操作；兼容 pairing/grant API 被调用时仍记录对应审计；
- 审计日志默认不记录命令正文、终端正文、访问令牌或明文密钥。

### 4.4 会话记录与回放

记录模型不是简单的纯文本日志，而是按时间和序号排列的事件流：

- `output`：Shell 输出的原始字节；
- `input`：本地或远端输入，可根据隐私策略不持久化；
- `resize`：终端行列变化；
- `snapshot`：终端解析后的周期性画面快照；
- `title`：终端标题变化；
- `lifecycle`：创建、连接、中断、恢复和退出；
- `control`：输入租约变化。

每个事件至少包含 `session_id`、单调递增的 `seq`、时间戳、事件类型和加密载荷。终端正文按大小或时间切块，快照用于快速打开长会话，事件用于准确重放快照后的变化。

#### Tail-first 历史对象行为

启用新能力后，Windows 以 durable `LocalFrame`/producer WAL 为唯一历史物化前缀：从同一
1..S 前缀物化 Snapshot 与 Transcript，并在 durable ordered outbox 中按接收 Android 设备
分别密封；上传提交顺序固定为 Transcript Page 在先、Snapshot S 最后。服务端只有
在 transcript-first 提交屏障完成后，才可把 Snapshot S 返回给该接收设备。Transcript-first 是
生产顺序，不等于 Android 首开下载顺序；首开仍只下载一个 Snapshot。

旧能力 `bounded_event_replay_v1` 继续作为默认生产恢复与失败回退。新的
`terminal_snapshot_v1` capability 必须经双方能力协商和运行开关共同开启；
`TerminalTranscriptPageV1` 是受同一门禁约束的 REST 对象类型，不是独立 wire capability。
缺对象、认证失败、设备不匹配、Key Epoch 不匹配、安装失败或 S+1 缺失时 fail closed，不得把
中段 VT 字节喂给空 parser，也不得把固定 S 的 Transcript 行拼进 live parser。

默认建议：

- PC 本地加密记录保留 7 天，可改为关闭、1 天、30 天或自定义；
- 云端历史记录默认关闭，开启后使用端到端加密并设置独立配额；
- 会话输入默认只实时传输，不持久保存，以降低敏感信息泄露风险；
- 用户删除会话后立即删除索引，并异步清除加密数据块和备份到期副本。

### 4.5 会员与付费功能

服务端仍使用可配置套餐目录以兼容购买、恢复购买和功能标记，但 PC 与实时会话数量不再受套餐额度限制：

| 权益 | Free | Pro Monthly |
| --- | --- | --- |
| 可远程连接 PC | 不限数量 | 不限数量 |
| 同时远程显示的实时会话 | 不限数量 | 不限数量 |
| Windows 本地 Shell | 不限制 | 不限制 |
| E2EE、账号隔离、设备撤销和本地优先 | 完整 | 完整 |
| 周期 | 免费 | 月付、自动续费、可取消 |

Free/Pro Monthly 只是兼容性的首发配置，不是代码枚举。管理员可以配置套餐名称、计费周期、功能标记、渠道 Product ID、排序和上下架时间；修改后通过版本化发布生效。历史 `max_remote_pcs` 与 `max_live_sessions` 字段仅为兼容保留，接口以 `-1` 表示不限，服务端不再根据这两个字段拒绝注册或订阅。

实际价格、货币和税费由购买渠道在最终确认前展示，不硬编码在客户端。Android 使用 Google Play Billing；服务端验证 Purchase Token/Webhook 并签发账号级 Entitlement，客户端不能自行声明会员状态。

会员到期不会终止 Windows 本地 Shell，也不会删除 PC、设备、历史或已建立的实时订阅；由于数量不限，不再执行超额暂停、配额选择或降级清理。

## 5. 非功能需求

### 5.1 性能目标

- 稳定网络下，PC 输出到 Android 显示的区域内 P95 延迟不高于 250 ms；
- Android 输入到 PC Agent 写入 ConPTY 的区域内 P95 延迟不高于 250 ms；
- 单会话持续输出 2 MB/s 时 UI 不阻塞，通过背压和批量帧保持可用；
- 首版支持同账号不限数量 PC 与并发受管会话；服务端仅保留连接背压和资源安全上限。
- 重连后仅补发最后 ACK 序号之后的数据，不重复执行输入事件；
- 10 万行级历史会话通过快照和分块加载，在普通手机上可渐进打开。

以上指标应在开发原型完成后通过压测校准，不作为未经验证的市场承诺。

### 5.2 可用性和兼容性

- Shell 内容内部统一按字节传输，不在 Relay 中转换编码；
- 首版以 UTF-8 为主，遇到 Windows 旧代码页时由 Session Host 显式标注和转换；
- 网络状态变化、App 切后台和手机休眠后可以恢复；
- Relay 临时不可用不影响 PC 本地终端继续操作；
- Agent 与服务端协议至少兼容前一个正式版本。

### 5.3 安全要求

- 所有外部通信使用 TLS 1.3；
- 终端载荷在 PC 与同账号、未撤销的 Android 设备之间端到端加密，推荐 X25519 密钥协商与 ChaCha20-Poly1305/AES-GCM；
- OIDC subject 决定账号访问边界，每台设备仍拥有独立密钥并必须证明私钥持有；同账号直连不允许共享设备私钥；
- Access Token 短时有效，Refresh Token 可轮换且与设备绑定；
- PC 私钥由 DPAPI 保护，Android 私钥存入 Android Keystore；
- 每帧包含会话、方向、序号和随机数关联信息，防止重放和跨会话替换；
- 设备撤销后拒绝新连接并轮换受影响会话密钥；
- 不在日志、崩溃报告和监控指标中写入终端正文或令牌；
- 服务端对登录、设备注册/撤销、输入和历史读取实施频率限制和异常告警；兼容 pairing/grant 端点继续受限流保护；
- PC 端安装、升级和二进制文件必须签名。

## 6. 总体技术方案

### 6.1 系统组件

```text
Windows Terminal Profile
        │ 本地 stdin/stdout、resize
        ▼
TermLink Session Host ── ConPTY ── PowerShell / CMD / WSL
        │ 有序会话事件
        ▼
TermLink PC Agent
        │ E2EE payload over TLS WebSocket
        ▼
Cloud API + Realtime Relay ── PostgreSQL / Redis / Object Storage
        │ E2EE payload over TLS WebSocket
        ▼
Android App + Terminal Renderer
```

组件职责：

| 组件 | 主要职责 |
| --- | --- |
| Session Host | 创建 ConPTY、桥接本地终端、采集 I/O、处理 resize 和 Shell 生命周期 |
| PC Agent | 登录和设备密钥、会话目录、加密、缓冲、重连、权限策略和本地通知 |
| API 服务 | 账号归属、设备、撤销状态、会话元数据、历史索引和审计接口；兼容旧授权 API |
| Realtime Relay | PC/Android 在线连接、路由、ACK、背压和输入租约协调 |
| Android App | 设备与会话浏览、解密、ANSI 渲染、历史回放和远程输入 |

### 6.2 推荐技术选型

| 层 | 首选方案 | 原因 |
| --- | --- | --- |
| Windows Agent/Session Host | C# + .NET 8，P/Invoke ConPTY | Windows 集成、安装服务、DPAPI 和原生 API 支持成熟 |
| Android | Kotlin + Jetpack Compose | 原生生命周期、Keystore、键盘和后台连接控制更可靠 |
| 终端渲染 | Termux `terminal-emulator` + `terminal-view` 派生 Fork；xterm.js 作为兼容基线和回退 | 在许可证不构成约束、完整度优先的前提下，复用经过大量 Android 用户验证的原生 VT、IME、触控和鼠标实现，并持续补齐 ConPTY 差异 |
| API/Relay | Go | WebSocket 并发、低资源占用和单文件部署较合适 |
| 数据库 | PostgreSQL | 账号、设备、权限、会话索引和审计元数据 |
| 在线状态/租约 | Redis | TTL、心跳状态、分布式输入租约和临时路由 |
| 历史内容 | S3 兼容对象存储 | 适合存放不可变的加密事件块和快照 |
| 协议 | WebSocket + Protobuf 二进制帧 | 双向、低延迟、字段演进和流量控制较直接 |

首个验证版本可将 API 与 Relay 合并为一个 Go 服务，并暂不引入消息队列。只有在多实例路由或事件量确实需要时再引入 NATS/Kafka，避免过早复杂化。

### 6.3 核心数据流

#### 建立实时查看

1. PC Agent 登录并通过 WebSocket 注册 `pc_id` 和当前会话摘要。
2. Android 获取有权访问的 PC 与会话列表。
3. Android 请求订阅 `session_id`，Relay 校验账号、设备状态和只读权限。
4. PC Agent 与 Android 使用已验证设备公钥派生会话密钥。
5. 新能力协商开启时，Android 经 REST 选择只属于本设备的 Snapshot S，下载并完成认证、
   detached prepare、持久缓存、checkpoint/ACK S 原子绑定和安装；Snapshot 不通过 Realtime 发送。
6. Android 以 S 发起 Realtime 订阅，只接受 S+1 及之后的连续事件；旧能力或任何安全回退场景
   使用 `bounded_event_replay_v1`。
7. Android 解密、校验序号、渲染，并只在事件应用与必要持久化完成后推进累计 ACK。

#### 发送远程输入

1. Android 请求会话输入租约。
2. Relay 检查控制权限，PC Agent 根据策略自动同意或请求本地确认。
3. Android 将包含唯一 `input_id` 的输入事件端到端加密后发送。
4. PC Agent 校验租约、解密并去重，再写入 ConPTY。
5. Shell 输出按正常输出流返回；Android 不应仅凭本地输入预测最终终端状态。

#### 断线恢复

1. Android 保存每个会话最后连续 ACK 的输出序号。
2. 重连后提交 `last_acked_seq`。
3. PC Agent 若仍有安全连续增量缓冲，直接续传；双方已开启新能力时，也可以选择、认证并原子
   安装该接收设备的最新合格 Snapshot S，再从 S+1 续传。
4. 若所需数据已过期且没有合格 Snapshot，Android 保留安全 ACK 并明确提示 History gap；不得
   静默清空后从中段事件继续。

### 6.4 推荐的核心实体

| 实体 | 关键字段 |
| --- | --- |
| User | `user_id`, `status`, `created_at` |
| Device | `device_id`, `user_id`, `type`, `name`, `public_key`, `trust_state`, `revoked_at` |
| PcPresence | `pc_id`, `agent_version`, `online`, `last_seen_at` |
| Session | `session_id`, `pc_id`, `shell_type`, `title`, `state`, `created_at`, `ended_at` |
| DeviceGrant（兼容层） | `device_id`, `pc_id`, `permission`, `approved_by`, `expires_at`；账号级直连主流程不依赖该记录 |
| ControlLease | `session_id`, `holder_device_id`, `lease_id`, `expires_at` |
| EventChunk | `session_id`, `start_seq`, `end_seq`, `cipher_ref`, `created_at`, `expires_at` |
| AuditEvent | `actor_device_id`, `action`, `target_id`, `result`, `created_at`, `ip_hash` |

## 7. 协议设计要点

所有实时消息使用统一信封，业务载荷加密：

```text
Envelope {
  protocol_version
  message_type
  session_id
  sender_device_id
  seq
  ack
  sent_at
  key_id
  nonce
  ciphertext
}
```

关键约束：

- 输出 `seq` 由 PC Agent 针对每个会话单调递增；
- 输入使用独立方向序号和全局唯一 `input_id`；
- Relay 可以读取路由与限流所需信封字段，但不能读取 `ciphertext`；
- ACK 表示已连续接收，不表示终端命令执行成功；
- 协议版本和能力协商分开，旧客户端遇到未知可选消息时可以安全忽略；
- 大输出按帧上限切分，压缩应先于加密，并设置解压大小限制防止资源耗尽。

## 8. 关键产品决策

### 8.1 同账号直接访问，但输入仍需短时租约

OIDC subject 是首版访问边界：Windows Agent 与 Android 登录同一账号后，未撤销设备自动注册并直接发现、订阅该账号的 PC 与受管 Session，不需要用户执行配对、自授信或逐 PC Grant。不同账号必须在列表、REST、Realtime 路由、历史和密钥接收方选择上全部隔离。

直接访问不等于永久占有输入权。每个远程输入仍需短时会话租约，PC 本地策略可以要求确认，本地键盘可以抢占，设备撤销会断开连接并停止获得新密钥。逐设备密钥与签名证明继续存在；旧 `trust_state`、pairing 和 `device_grants` 表/API 仅作为兼容层，不得重新出现在当前产品主流程。

### 8.2 本地用户优先

PC 本地用户始终可以查看远端连接状态、撤销输入租约或停止远程访问。本地操作不应因为 Relay 故障而被阻塞。

### 8.3 只保证受管会话

产品界面必须明确区分受管会话和普通 Windows Terminal 会话。后续若研究“附加已有会话”，应作为实验能力单独评估，不能降低主路径稳定性或使用进程注入作为默认方案。

### 8.4 终端输出是真实状态来源

远端输入只是发送按键/字节；命令是否被 Shell 接受、执行到哪里、是否成功，应由返回的终端输出和进程状态决定。首版不构建基于命令字符串的业务语义层。

## 9. MVP 验收标准

满足以下条件才视为首版闭环：

1. 一台 Windows 11 PC 安装 Agent 后能通过 TermLink Profile 打开 PowerShell、CMD 和至少一个 WSL 会话。
2. Android 使用与 Windows Agent 相同的 OIDC 账号登录后，无配对、自授信或 Grant 即可看到该账号的 PC 及其受管会话列表；其他账号不可见且不可路由。
3. Android 能正确显示常见 ANSI 输出、中文、颜色、光标移动和一个典型 TUI 程序。
4. Android 获得输入租约后，可以发送文本、Enter、方向键和 Ctrl+C；只读设备的输入被可靠拒绝。
5. Wi-Fi/蜂窝网络切换并断开 30 秒后，会话可自动重连，已确认输出不重复，缺失输出可补齐或明确提示缺口。
6. Relay 数据库、对象存储、应用日志和运维监控中均不能查到终端明文。
7. 撤销 Android 设备后，该设备的现有连接及时断开，旧令牌无法重新连接，新的会话密钥不再发给该设备。
8. PC 断网或 Relay 停机时，本地 Terminal 仍可正常输入和运行命令。
9. PC 用户可以一键切换只读/禁用远程访问，并能明确看到当前远程查看和控制状态。
10. 删除历史会话后，列表不可见且对应加密内容进入可验证的删除流程。

### 9.1 Tail-first 七项原始需求验收表

此表是产品上线条件，不是当前 E2E 通过声明。执行结果和证据必须由主代理在最终代码上更新；
当前统一为“待验证”。

| ID | 原始需求 | 产品验收条件 | 当前结果 |
| --- | --- | --- | --- |
| TF-01 | Windows durable 生产链 | 真实受管会话的全部 marker 先持久化到 producer WAL；逐接收设备 Transcript 先提交，Snapshot S 最后提交，崩溃后幂等续作。 | 待主代理最终验证 |
| TF-02 | Android S/S+1 进入实时 | 真实产品完成选择、下载、准备、缓存、绑定、安装和订阅；订阅从 S 开始且第一个新事件为 S+1。 | 待主代理最终验证 |
| TF-03 | 首次只有最后一屏 | 初开只有一个 Snapshot，无 Transcript/Event Chunk 预取；最后 marker 可见、最早 marker 不可见。 | 待主代理最终验证 |
| TF-04 | 固定 S 上滑分页 | 每次上滑只取一页且不超过 200 行；独立 viewport、固定 S，分页不改变 live ACK。 | 待主代理最终验证 |
| TF-05 | 历史与实时并行 | 历史层打开时新输出仍推进 ACK；关闭后回到实时尾部并看见新输出。 | 待主代理最终验证 |
| TF-06 | 同账号多 Android 隔离 | 两台以上 Android 可订阅同一会话，但对象、URL、密钥与缓存严格按 recipient 隔离，交叉访问失败。 | 待主代理最终验证 |
| TF-07 | 失败、回退、双语与 IME | 所有失败 fail closed 或回到连续事件回放；中英文状态完整，Terminal/底栏/IME 无越界或重叠。 | 待主代理最终验证 |

## 10. 里程碑建议

### 阶段 0：技术验证（1～2 周）

- ConPTY Session Host 与 Windows Terminal Profile 桥接；
- 输出、输入、resize、Ctrl+C、PowerShell 和 WSL 验证；
- Android 终端渲染器对中文、ANSI 和 TUI 的兼容性验证；
- 形成“不支持的终端行为”清单。

### 阶段 1：本地闭环（2～3 周）

- PC Agent 会话管理和本地事件缓冲；
- Android 与 PC 在局域网或测试 Relay 上完成实时显示和输入；
- 实现序号、ACK、重连、快照和输入去重。

### 阶段 2：账号与安全（3～4 周）

- OIDC 登录、同账号设备自动注册和账号隔离；
- 输入租约、设备撤销和审计；兼容 pairing/grant API 只做回归保护；
- 端到端加密、密钥保存与轮换；
- 完成最小化威胁建模和安全测试。

### 阶段 3：可用 MVP（2～4 周）

- 多 PC/多会话交互、托盘状态和 Android 移动端体验；
- 加密历史回放、配额、删除和异常恢复；
- 安装、签名、升级、崩溃恢复、性能和兼容性测试。

## 11. 主要风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| 无法可靠捕获普通 Terminal 既有标签页 | 原始“全部会话”预期落空 | 明确受管 Profile；把附加既有会话放到独立研究项 |
| Android 软键盘与 TUI 兼容性差 | 控制体验不可用 | 技术验证阶段优先测试组合键、IME、横屏和硬件键盘 |
| 高频输出导致手机卡顿 | 延迟和内存上升 | 批量帧、背压、渲染节流、滚动缓冲上限和快照 |
| 网络重试造成命令重复 | 可能执行危险操作两次 | 输入 `input_id` 去重；租约校验；不自动重发未确认的整段粘贴 |
| 终端内容含密钥或隐私 | 高严重度数据泄露 | E2EE、短保留、输入默认不落盘、日志脱敏、设备撤销 |
| 服务端 E2EE 与多设备密钥管理复杂 | 延误或产生安全漏洞 | 每个设备独立密钥和接收方密文；严格按账号/撤销状态筛选；使用成熟密码库并对协议做第三方审查 |
| Agent 高权限运行扩大攻击面 | PC 被远程利用 | 会话进程尽量以当前用户权限运行；服务与会话宿主隔离；签名升级 |

## 12. 后续演进方向

- iOS、macOS 和 Linux Agent；
- 通过 WebRTC DataChannel 建立点对点通道，Relay 作为回退；
- 命令完成和失败通知，但不替代 Shell 真实输出；
- 端到端加密的会话分享和限时只读链接；
- 文件上传下载、端口转发等能力需单独威胁建模后立项；
- 企业组织、细粒度策略、SSO 和合规审计；
- 实验性附加既有控制台会话，仅在 Windows 提供稳定受支持接口时进入正式范围。

## 13. 立项前待确认事项

以下问题不会阻止技术验证，但应在 MVP 开发前确定：

1. 产品首先服务个人用户还是小型团队；本文默认个人账号、多设备自用。
2. 是否必须支持公网自建部署；本文建议先设计可容器化 Relay，但首版只维护一种官方部署方式。
3. 云端历史记录是否作为默认能力；本文建议默认关闭、用户显式开启。
4. 同账号访问已经确定无需配对或 Grant；仍待确认的是每次申请短时输入租约是否需要 PC 本地确认。本文建议提供 PC 策略，默认首次控制确认。
5. 历史配额和保留时长；PC 与实时 Session 数量已经确定为不限，不应让商业状态影响协议正确性和本地可用性。
