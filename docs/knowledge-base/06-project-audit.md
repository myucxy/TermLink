# 06 · TermLink 全项目深度审计（2026-09-18）

> 审计基准：工作树 HEAD `7dec0e4` + 约 92 处未提交变更；四路并行专项审计（Server 安全授权 / 直连传输 NAT / Win-Agent+Android / 部署运维发布）+ 本会话实测证据。
> 审计基准线（产品意图，用户框定）：**server 只做登录授权 + 客户间请求/连接转发；NAT 可直连则优先直连**。任何偏离、削弱或未达成该边界的内容均为发现项。
> 严重度定义：P0=阻断性/必须立即处理；P1=高（安全或核心目标受损）；P2=中（有明确风险或代价）；P3=低（改进项/一致性项）。

---

## 一、总评

**安全设计质量显著高于平均水平**：E2EE 边界严格执行（Relay 全程盲转发、终端内容永不落库）、WS 设备证明链严谨（时间窗+一次性 nonce+签名域分隔）、SQL 全参数化零注入、直连传输的三层防 MITM（证书 pin + peer proof + exporter 绑定）、TerminalCore 权威边界实现正确、Android 状态机无回退缺陷、容器加固到位（distroless/read_only/cap_drop/内部网络）。**四路审计未发现 P0 级安全漏洞。**

**核心风险集中在"体系与执行的分叉"和"产品意图未达成"两条线上**：

1. **分叉**：仓库宣称的发布/部署体系（CI 镜像、digest 锁定、禁 latest、供应链证明）与实际执行（本地构建、手工 push、推 latest、无证明）已经分叉 19 个版本，而串联两者的关键文件（`server/compose.yml`）**还没进 git** —— 这是唯一 P0。
2. **意图未达成**：`TERMLINK_DIRECT_ROUTING_PERMITTED` 默认 false（数据面关闭，纯影子观察），且即使打开，**票据 120 秒 TTL 会强制直连每约 2 分钟拆回 Relay**，真实网络验证覆盖为零 —— "可直连则优先直连"当前实际是"尚不具备优先直连的能力"。

## 二、与产品意图的对齐评估

| 产品意图 | 现状 | 判定 |
| --- | --- | --- |
| server 只做登录/用户授权 | 内置 OIDC + 设备证明 + 角色双层鉴权，实现严谨；残留 `GET /v1/billing/plans` 空目录（计费已移除的化石） | ✓ 基本吻合，有清理项 |
| server 只做请求/连接转发 | Relay E2EE 盲转发校验完备；文件传输存储无关 | ✓ 吻合 |
| NAT 可直连则优先直连 | 数据面默认关闭（shadow）；票据 TTL 120s 硬上限；对称 NAT 零命中且无预测；真实网络 E2E 为零 | ✗ **结构性未达成**（详见 §四.B） |

## 三、发现汇总（按严重度）

| # | 级 | 领域 | 发现 |
| --- | --- | --- | --- |
| 1 | P0 | 部署 | `server/compose.yml` 与契约测试未提交 git，fresh clone 部署故事断裂 |
| 2 | P1 | 直连 | 票据 120s TTL 硬性终止直连数据面（与"优先直连"结构性冲突） |
| 3 | P1 | 直连 | 数据面真实网络 E2E 零覆盖，开启准入条件无法满足 |
| 4 | P1 | Server | 初始管理员密码硬编码两处 + 未配置时静默回退 |
| 5 | P1 | Server | 主密钥明文存数据库，全部派生密钥单点依赖 |
| 6 | P1 | Win | 更新信任锚可被用户态目录内容替换（自指信任） |
| 7 | P1 | Android | 终端主界面无 FLAG_SECURE（截图/最近任务泄漏终端内容） |
| 8 | P1 | 会话 | 历史会话目录无界增长（服务端分页缺失，已知 4 风险之一） |
| 9 | P1 | 会话 | Presence 把"PC 在线"当"全部 RUNNING 会话在线"（WIP 引入） |
| 10 | P1 | 部署 | 部署五要素中镜像/DNS/TLS 三环文档不闭环；生产拓扑只存在于排障记录 |
| 11 | P1 | 部署 | compose 默认值三处矛盾（生产域名/latest/固定密码/0.0.0.0 绑定） |
| 12 | P1 | 部署 | 版本号三轨脱节（git tag v0.2.26 / artifacts 0.2.44 / Hub v0.2.45） |
| 13 | P1 | 部署 | 手工推送 latest 违反自订"不发布 latest"边界，无降级发布记录 |
| 14 | P1 | 部署 | 生产 TLS 网关（192.168.9.250）零 IaC/零配置备份/零故障预案，且同时承载 Agent 升级分发 |
| 15 | P1 | 部署 | 恢复演练未做 + 备份工具链双轨漂移（ops 脚本指向已淘汰的旧栈） |
| 16 | P2 | Server | Agent 更新元数据无签名校验，强制升级（REQUIRED）可被源站劫持滥用 |
| 17 | P2 | Server | Relay 两张 per-session 映射无上限（认证后内存 DoS 向量） |
| 18 | P2 | Win | Authenticode 验证接受不可信根/链、无吊销检查，仅靠 SHA-1 指纹 pin |
| 19 | P2 | Win | Tray 快速更新路径缺本地降级防护（依赖服务器带 buildNumber） |
| 20 | P2 | Win | 输出洪泛 >4MB 触发 Gap→fail-closed 杀死整个 shell（配合 21 为复合缺陷） |
| 21 | P2 | Win | durable ACK 每帧 DPAPI+fsync 序列化在关键路径（吞吐瓶颈+触发 20） |
| 22 | P2 | Android | E2EE 身份密钥未用 StrongBox/未绑用户认证 |
| 23 | P2 | 直连 | 默认公共第三方 STUN 硬编码（可用性+隐私） |
| 24 | P2 | 直连 | shadow 模式只验证控制面，数据面切流/回退行为无真实观测 |
| 25 | P2 | 直连 | 数据面队列一满即整体退役直连（大输出突发触发） |
| 26 | P2 | 部署 | metrics 默认关闭，生产无采集无告警；告警规则仍引用已移除的 Valkey |
| 27 | P2 | 部署 | compose 直用路径无备份文档、无计划化备份 |
| 28 | P2 | 部署 | 同 issuer 双 server 实例并存（本机+NAS），网关误切即数据分裂 |
| 29 | P2 | 部署 | `COMPLETE_SYSTEM_DESIGN.md` §6 整体过时且与 README 互相矛盾 |
| 30 | P2 | 部署 | 仓库卫生：.gitignore 变更未提交、zcodeDemo/42MB exe 未跟踪 |
| 31 | P2 | 会话 | Web 门户与 Android 在线状态分叉（DB 心跳 vs 内存 presence） |
| 32 | P3 | Server | `/v1/auth/register/email-code` 不检查注册关闭策略 |
| 33 | P3 | Server | 管理端混淆入口默认关闭（/admin 可枚举） |
| 34 | P3 | Server | 登录爆破限速弱（5 次锁 30s，无指数退避） |
| 35 | P3 | Server | 迁移 000046 缺 down |
| 36 | P3 | Server | /metrics 无认证（依赖运维绑对地址） |
| 37 | P3 | Win | 管道 ACL 无完整性级别；control.secret 在用户可写目录 |
| 38 | P3 | Win | SessionHost 握手 token 经 argv 传递 |
| 39 | P3 | Win | RemoteInputResult 查询对全缓冲做 DPAPI 解码（重试风暴放大） |
| 40 | P3 | Win | V5/V6 遗留 `Register` 命令永久免认证 |
| 41 | P3 | Android | Keystore 未 StrongBox/未绑生物识别（属 22 的细化） |
| 42 | P3 | Android | Room 未整库加密（依赖编码约定） |
| 43 | P3 | Android | OIDC WebView 允许第三方 Cookie |
| 44 | P3 | Android | grantControl 租约过期抛异常而非降级 ReadOnly |
| 45 | P3 | 直连 | 对称 NAT 无端口预测（结构性零命中，文档已声明） |
| 46 | P3 | 直连 | 探测固定 150ms 无抖动（与文档"指数退避有抖动"不符） |
| 47 | P3 | 直连 | QUIC keepalive 关闭，握手窗口 NAT 重映射只能靠超时回退 |
| 48 | P3 | 直连 | MTU 固定 1200 无 DPLPMTUD（保守取舍，吞吐 -20%） |
| 49 | P3 | 直连 | 信令发往离线 peer 静默丢弃，发起方白等 10s 探测预算 |
| 50 | P3 | 直连 | FFI writer 空闲 2ms 忙轮询；每次激活重建 tokio runtime |
| 51 | P3 | 会话 | 离线 PC 历史会话不可见（设备页只显示在线 PC） |
| 52 | P3 | 部署 | 根 README 开发命令为机器特定绝对路径 |
| 53 | P3 | 部署 | KB05 的 v0.2.45 digest 无第二来源可复核（发布记录单点） |

## 四、分领域详细发现

### A. Server：安全与授权

**[P1-4] 初始管理员密码硬编码 + 静默回退**

- 证据：`server/internal/config/config.go:29`（`defaultInitialAdminPassword = "termLink@124.."`）、`config.go:657`（未配置时静默回退该值）、`internal/identity/accounts.go:24`（第二份硬编码）。
- 说明：installer 路径（`termlink-deploy`）强制随机密码注入，但 compose/裸跑路径会以公开于源码与二进制中的密码创建 admin。缓解：首登强制改密真实有效（bootstrap 密码=默认值时置 `password_reset_required`，登录/token/授权全链路拒绝，改密后 `auth_epoch+1` 撤销全部令牌）——所以窗口是"部署后到首登改密前"。
- 建议：删除静默回退，未提供密码文件时启动失败或随机生成+打印一次；删除 `accounts.go` 的第二份硬编码。

**[P1-5] 主密钥明文存库，单点依赖**

- 证据：`internal/store/runtime_secrets.go:30-34`（bytea 原样存）；`cmd/termlink-server/main.go:164-174`（8 类密钥全部由主密钥 HMAC 派生）。
- 说明：数据库读访问=全部密钥泄露（可伪造 cookie/令牌/admin route）。缓解：OIDC 签名私钥额外 AES-GCM 加密。威胁模型应明示："数据库访问等价于全 compromise"。
- 建议：可选口令/KMS 包裹（env 口令→argon2→AES-GCM）；提供密钥轮换命令。

**[P2-16] 更新元数据无签名**

- 证据：`internal/agentupdate/appship.go:393-400`（signerFingerprint 可空、不与信任锚比对）、`api.go` REQUIRED 强制升级直接信任源站。
- 说明：`https://appship.kget.cc:8910` 被劫持即可推送恶意 EXE 并标强制升级。TLS/SSRF/回滚防护已做好，缺的就是元数据签名。
- 建议：Ed25519 签名元数据 + 客户端内置公钥强校验；REQUIRED 仅在验签通过时生效。

**[P2-17] Relay 内存无上限**

- 证据：`internal/realtime/relay.go:238`（lastSequences）、`:240,831-841`（pcReplayRoutes）——每个新 sessionID/路由追加条目，无预算，先于授权检查。
- 建议：per-peer 条目上限 + 超限断连；只对已授权订阅记录状态。

**其余 P3**：email-code 绕过注册关闭（`api.go:278-321` 对比 `registration.go:126`）；admin route 默认 `/admin` 可枚举；5 次失败锁 30s 无退避；000046 缺 down；/metrics 无认证。均小改。

**核查通过项（无需动作）**：设备证明（±60s 窗口、SetNX nonce、域分隔、先验签后消费 nonce）；OIDC 校验完整（含 typ/azp/jti/auth_epoch 实时核对）；全部资源查询绑 user_id + actor 设备参与授权，无越权；SQL 全参数化；尺寸限制齐备；ACK 回退拒绝、慢消费者双预算断连、吊销 Pub/Sub 立即生效；会话 cookie `__Host-`+CSRF 三重来源校验+CSP `default-src 'none'`；密文列覆盖（标题/TOTP/邀请码摘要/subject 哈希）。

### B. 直连传输：与"优先直连"意图的差距（重点）

**[P1-2] 票据 120s TTL 硬性拆直连**

- 证据：`server/internal/transport/tickets.go:29`（`ticketTTL=120s`）；`termlink-direct-ffi/src/v5.rs` 数据循环把 `ticket_expires_at_ms` 作为终止分支；重试门槛：同对 30s 间隔、连败 3 次冷却 5 分钟（`ProductionDirectTransport.cs:61-62`）。
- 说明：一切正常时直连也每约 2 分钟强制拆回 Relay，再走完整 gather→probe→QUIC→proof→cutover。"优先直连"实际是"每 2 分钟内优先直连"。文档未披露此用户可感行为。
- 建议：已认证连接上做票据续期（双向 proof 续期 expiry），或大幅延长 TTL 保留服务端紧急撤销。**这是达成"优先直连"意图的第一优先改动。**

**[P1-3] 数据面开启的证据链为零**

- 证据：compose 默认 `ROUTING_PERMITTED=false`；Windows 数据面需 `TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE` 编译符号；Android release 硬编码 false（`build.gradle.kts:313,342`）；direct-e2e 16 个案例全是内存确定性模型（README:53-73 自认 "zero real-network E2E coverage"）。
- 说明：开启需要 server+Win+Android 三端开关同时满足 + 阶段 4 真实网络矩阵通过——目前不具备。shadow 模式只验证控制面（候选/STUN/探测/QUIC/proof），切流 barrier、重复宽限、Receipt、回退重发等数据面核心行为无真实观测。
- 建议：按 direct-e2e README 的 gap 清单实现真实 socket 产品适配器，至少覆盖同 LAN/跨 NAT/对称 NAT/防火墙拒 UDP/VPN/网络切换。

**[P2-23/24/25] 环境与韧性**：默认公共 STUN（compose:18，隐私+企业网封 UDP）；对称 NAT 结构性零命中（无端口预测，文档已声明边界——建议指标区分回退原因以量化）；队列满（256 帧/8MiB）即整体退役直连，1MiB 级突发可频繁触发。

**核查通过项**：STUN RFC 8489 合规；单 socket 全程持有（保证 NAT mapping 一致）设计正确；三层防 MITM（E2EE 信令交换证书指纹 pin + 握手后证书校验 + Ed25519 peer-proof 绑定 ticket/epoch/exporter）；票据 Ed25519 签名、TTL+时钟容差+connection_id 单次有效+epoch 服务端原子分配；任何失败同步回 RelayOnly 且不产生 Relay 断连；数据面双载波 ingress hold/去重/同 seq 异字节退休；FFI 全导出 catch_unwind、Drop 等待 worker 退出、两端宿主串行化落实。

### C. Win-Agent 与 Android

**[P1-6] 更新信任锚自指**

- 证据：`win-agent/src/UpdateCore/WindowsUpdateSecurity.cs:85-116`（信任锚优先取安装目录内 .cer，兜底"当前进程自身签名"）；安装根为用户可写目录（`RemoteSetupTransaction.cs:146-147`）。
- 说明：同用户恶意软件替换 exe+cer 后可让更新链永久信任攻击者签发的包——远程终端产品上这等于持久化后门通道。
- 建议：信任锚固化进带 manifest 的二进制资源或 HKLM；或安装根迁 Program Files。

**[P1-7] 终端界面无 FLAG_SECURE**

- 证据：`ProductActivity.kt:750-858` 全文件无 FLAG_SECURE；对比登录页 `EmbeddedOidcActivity.kt:55-58` 反而设了。
- 说明：远端 shell 内容（密钥/token/源码）可被系统截图、任务缩略图、录屏捕获。一行级修复。
- 建议：终端可见界面统一加 FLAG_SECURE（可做设置项，默认开）。

**[P2-18/19/20/21] Windows 更新与吞吐**：Authenticode 接受 UntrustedRoot/Chaining 且无吊销检查（`WindowsUpdateSecurity.cs:50-51,148-155`）；Tray 路径缺本地降级强制（`AppShipUpdateClient.cs:280-284` 对比 MSIX 的 `ValidateUpgrade`）；输出洪泛 4MB 触发 Gap→fail-closed 杀 shell（`SessionHost.cs:2319-2341` + `TerminalCoreAuthority.cs:64-88`）；每帧 DPAPI+fsync 的 journal（`SessionAgent.cs:1460-1467,1339-1353`）既造成输入分发锁竞争又是 20 的触发条件。修复顺序：21 合批化 → 20 改"可丢弃+快照追赶" → 19/18 补校验。

**核查通过项**：ConPTY Job 对象杀树无孤儿；DPAPI 损坏 fail-closed（远控默认关）；MSIX 更新链（签名/有效期窗/三重绑定/双重降级拦截）；Android 更新链比 Windows 更严（SHA+HEAD 一致性+签名摘要比对+防降级）；重连退避+jitter 无风暴；ACK 先落 Room 后上 wire；租约过期/续约失败保守降级；日志无敏感内容。

### D. 部署/运维/发布

**[P0-1] `server/compose.yml` 未提交 git**

- 证据：README/README-runtime 的部署命令都指向它，但文件未跟踪；契约测试 `compose_auto_adapt_contract_test.go` 同样未提交。fresh clone 第一步即失败。
- 建议：立即提交（本会话已精简到 8 行 environment 且带契约测试，是干净的可提交状态）。

**[P1-11/12/13] 发布体系分叉**：compose 默认 `latest`+生产域名+固定密码，撞 runbook 的"仅 digest/禁 latest/TAG@sha256"三条边界；git tag 止于 v0.2.26 而 Hub 已手工推 v0.2.45；CI（digest 签名/SBOM/provenance）自 v0.2.26 未运行，secrets 存活状态未知。建议：恢复"发版=推 tag"单一入口；手工发布必须按 runbook §7 降级路径执行并留档（tag+digest 记录入库）。

**[P1-14/15] 边缘与数据安全**：TLS 网关（192.168.9.250，Lucky，LE 通配符证书）零文档零 IaC，同时承载实时终端与 Agent 升级分发两个关键功能，证书续期/超时/重建步骤均无记录；备份工具链双轨（`termlink-deploy` age 加密备份质量高但从未做过真实恢复演练；`server/ops/Backup-TermLink.ps1` 指向已淘汰的旧多容器栈必误用）。

**[P2-26/27/28/29/30]**：metrics 默认关闭无告警（现成 observability 栈未启用且规则文案含过时 Valkey）；同 issuer 双实例并存（网关误切=数据分裂事故）；`COMPLETE_SYSTEM_DESIGN.md` §6 与现状全面矛盾（仍在教 Redis/Caddy/ghcr.io）；`.gitignore` 扩大规则未提交、42MB `server/termlink-server.exe` 与 `zcodeDemo/` 无 ignore 规则。

**核查通过项**：Dockerfile（digest 锁定基镜像、distroless、SOURCE_DATE_EPOCH 可复现）；compose 加固（read_only/cap_drop ALL/内部网络/日志轮转/双 healthcheck）；`termlink-deploy`（tag@digest 强制、age 加密备份、restore 破坏性门禁）；runbook 对踩坑的诚实记录。

### E. 历史会话已知风险（上轮分析，仍有效）

1. 目录无界增长（P1-8）：`CleanupSessions` 永久空操作 + Android 全量拉取一次性渲染 → 需服务端分页 + 懒加载。
2. Presence 混淆（P1-9）：`api.go` 把"PC 有连接"当"全部 RUNNING 会话在线"，违反会话级可达性语义。
3. 门户分叉（P2-31）：门户读 DB 心跳 vs Android 读内存 presence。
4. 离线历史不可见（P3-51）：设备页只显示在线 PC。

## 五、修复路线图

> **进度（2026-09-19）**：第一周 6 项中 5 项完成——#1/#30 部署资产已提交（含 PUBLIC_URL
> 占位符与管理员密码文件接线）、#4 管理员密码随机化（config/identity/测试/README）、
> #7 FLAG_SECURE（编译通过）、#13 runbook latest 边界修订、#14 EDGE_GATEWAY.md、
> #15/27 备份弃用横幅与 README 最小备份方案；第 5 项黑盒拨测待接入监控。

### 第一周（P0/P1 快赢）

1. 提交 `server/compose.yml` + 契约测试 + .gitignore（#1/#30，半小时）
2. 移除初始管理员密码静默回退与双份硬编码（#4）
3. Android 终端界面加 FLAG_SECURE（#7，一行级）
4. 决策 latest 处置：compose 默认镜像改占位符 or 停推 latest（#13）
5. 撰写 `docs/EDGE_GATEWAY.md`：网关拓扑/证书/upstream/故障预案（#14）
6. 删除或改写指向旧栈的 `server/ops/Backup-*.ps1`；compose 路径补最小备份命令（#15/27）

### 第一个月（P1/P2 主体）

1. 直连票据续期机制设计 + 实现（#2）——达成"优先直连"的钥匙
2. 真实网络 direct-e2e 产品适配器（#3/24），按 gap 清单逐条复现 16 案例 → 全绿后开数据面准入
3. Relay 内存上限 + agentupdate 元数据签名（#17/16）
4. Windows journal 合批化（#21）→ 输出洪泛策略改"丢弃+快照追赶"（#20）→ Tray 降级防护（#19）→ 信任锚出目录（#6）
5. 会话目录服务端分页 + Android 懒加载；presence 会话级语义；门户读同一 presence 源（#8/9/31）
6. 发布回归 CI：推一个测试 tag 验证 release.yml 与 secrets 存活（#12）
7. 真实恢复演练一次并记入 VALIDATION_BASELINE（#15）

### 季度内（P2 尾部/P3）

1. Keystore StrongBox/用户认证、Room 整库加密、第三方 Cookie、FLAG_SECURE 设置化（#22/42/43）
2. STUN 自托管或默认留空、票据信令对离线 peer 显式拒绝、QUIC 握手期 keepalive、MTU 协商能力（#23/49/47/48）
3. 登录限速退避、email-code 策略检查、admin route 随机化、000046 down、/metrics 认证（#32-36）
4. `COMPLETE_SYSTEM_DESIGN.md` §6 重写或加过时横幅；KB05 digest 增加第二来源（#29/53）
5. 崩溃收集（沿用"仅类型+栈帧+行号"白名单）与直连命中率指标（区分对称 NAT 回退原因）

## 六、值得保持的实践（审计正面确认汇总）

- Relay 全程 E2EE 盲转发 + 内容永不落库的边界纪律
- 设备证明的密码学细节（域分隔、先验签后消费 nonce、body 摘要绑定）
- 直连"同 socket NAT→QUIC 交接"与三层防 MITM
- 任何直连失败同步回 RelayOnly、事件面无 Relay 断连的不变量
- TerminalCore durable ACK 先落盘后 ACK、同序号常量时间比对
- Android 更新链的防降级/防重签名强度
- Dockerfile/compose/termlink-deploy 的加固与可复现构建
- runbook 对已知缺陷的诚实记录文化（这是本次审计能快速定位的根基）
