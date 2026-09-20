# 07 · 确认修复计划（2026-09-20，已逐项确认）

> 来源：2026-09-20 本地联调发现 + `06-project-audit.md` 遗留项。24 个决策项经逐项确认，
> 执行顺序已确认：**缺陷优先**（功能缺陷 → 安全加固 → 直连大项 → P3 扫尾），每阶段完成即验证提交。

## 一、功能缺陷（第一阶段）

| # | 问题 | 模块 | 确认方案 | 状态 |
|---|------|------|----------|------|
| 1 | 历史回看链路不通（快照 EMPTY/page_up 超时/chunk 0） | Server+Win+Android | 专项排查四段链路（SessionHost 缓冲→agent HistoryUploader→server 中继→手机拉取），5200 行标记会话作为复现用例 | **已修复**（29caac4/2ad8f8e）：根因为三层叠加——①安卓 hello 未广告 host_history_v1（relay 静默丢弃全部历史帧）；②快照权威校验单发即弃（目录尾巴心跳竞态，改为有界追赶重试）；③宿主 PrepareArtifact oldest-first 永远服务最旧快照（突发后恢复的是空屏旧快照，翻页产物集为空）。修复后实测：REMOTE_SNAPSHOT 引导、恢复 complete、scrollback 滚动可用。注：V2 TerminalCore 权威模式下 TRANSCRIPT_PAGE 产物按设计不产出（checkpoint 内嵌有界 scrollback 即回看边界，安卓渲染器容量 2000 行），翻页验收语义随架构更新 | ✔ 已修复 |
| 2 | 会话终止误导提示 + 外部 SessionHost 标注 | Android+Win | 复查结论（2026-09-20）：终止链路已实现 87 确认等待与失败 snackbar，无需改码；"外部会话"架构上不存在（所有会话均来自 SessionHost 注册，按 PID+启动时间精确终止，实测正常会话 12s 内完成）。此前"乐观 Exited"为测试行映射误判；终止已死宿主会话时 agent 正确返回失败 | ✔ 已核实 |
| 3 | 会话状态传播延迟 | Win Agent+Server | 精确计时实测（2026-09-20）：kill 宿主后 4 秒 DB 标记 INTERRUPTED、10 秒内手机 UI 免刷新翻转（生命周期帧经现有通道推送）。早期"2 分钟无反应"为 taskkill 静默失败的测试假象。**无需修改** | ✔ 已验证 |
| 4 | 会话初始标题继承启动控制台 | Win SessionHost | 启动时显式发布 managed-shell 名称作初始标题；程序后续动态改标题的链路（TerminalTitleTracker OSC 0/2）已存在且正常，不动 | 待开工 |

## 二、安全加固（第二阶段）

| # | 问题 | 模块 | 确认方案 |
|---|------|------|----------|
| 5 | 主密钥明文存库（#5） | Server | 可选 TERMLINK_MASTER_KEY_PASSPHRASE → argon2 → AES-GCM 包裹存库；未配置保持兼容+告警；首次配置自动迁移 |
| 6 | 更新信任锚自指（#6） | Win | 锚点编译进二进制嵌入资源（UpdatePublisher.cer 可选 EmbeddedResource）；安装目录 .cer 与进程自签退出信任链；无锚点部署 fail-closed |
| 7 | Authenticode 无链校验/吊销（#18） | Win | 已实现：签名策略 auto/strict/compat（TERMLINK_UPDATE_SIGNATURE_POLICY），strict 启用整链吊销检查仅接受 TrustSuccess，SHA-1 pin 保留第二因子 |
| 8 | 更新元数据无签名（#16） | Server+Win | Ed25519 签名 catalog + 客户端内置公钥强校验；REQUIRED 仅验签通过生效 |
| 9 | 管道 ACL + 握手 token argv（#37/38） | Win | 管道 ACL 加完整性级别；token 改经继承环境变量/受控临时文件 |
| 10 | Android 安全四件套（#7设置化/#22/#41/#42/#43） | Android | FLAG_SECURE 设置化（默认开，Settings→Secure screen）；WebView 禁第三方 Cookie 已合入；StrongBox 可用探测+可选生物识别与 SQLCipher 评估后续进行 |

## 三、直连传输（第三阶段，产品意图核心）

| # | 问题 | 模块 | 确认方案 |
|---|------|------|----------|
| 11 | 票据 120s TTL 硬拆（#2） | Server+Win+Android | 设计稿 v1 已完成：docs/DIRECT_TICKET_RENEWAL_DESIGN.md（续期票据双向交换、TTL 20 分钟滑动、保留紧急撤销、兼容降级矩阵、S1-S4 实施分解），待评审后进入 S1 实现 |
| 12 | 真实网络 e2e 为零（#3/24） | 测试设施 | 按 gap 清单实现真实 socket 适配器：同 LAN/跨 NAT/对称 NAT/防火墙拒 UDP/VPN/网络切换 |
| 13 | 队列满即整体退役（#25） | network-core | 有界降级：丢可丢帧+退役计数指标 |
| 14 | 直连 P3 四项（#46/47/49/50） | network-core+信令 | 探测抖动、QUIC 握手 keepalive、离线 peer 信令显式拒绝、FFI 忙轮询改事件等待 |

## 四、P3 扫尾（第四阶段）

| # | 问题 | 模块 | 确认方案 |
|---|------|------|----------|
| 15 | Server P3 四项（#32/34/35/33） | Server | 000046 down 已补（53d2ca7）；email-code 注册关闭策略检查复核已存在（sendRegistrationEmailCode 内 REGISTRATION_CLOSED 门禁）；登录退避与 AdminRoutes 默认启用待做 |
| 16 | metrics/告警（#26） | Server+运维 | 暂缓：/metrics 无认证（#36），公开端口启用前需先定认证方案；告警文案修正待做 |
| 17 | presence 统一同源（#9/31） | Server+Web+Android | presence registry 唯一在线源、会话级粒度，门户与 Android 同源 |
| 18 | 发版 CI 回归 + 恢复演练（#12/15） | 发布/运维 | 推测试 tag 验证 release.yml 与 secrets；真实恢复演练记入 VALIDATION_BASELINE |

## 五、明确不处理（本轮确认）

| 项 | 决定 |
|----|------|
| 离线 PC 历史不可见（#51） | **维持现状**（只展示在线 PC） |
| MTU 固定 1200 无 DPLPMTUD（#48） | 后置，不列入本轮 |
| NAS 升级与双实例（#28） | 用户要求暂缓，待本地"完美"后再升级（升级前需推新 Docker Hub 镜像） |

## 六、发布杂项（随阶段穿插）

- Docker Hub 推新版本镜像（含 20 天生命周期/管理页/OIDC 修复/本轮全部改动）
- `.gitignore` 补 `zcodeDemo/`
- `COMPLETE_SYSTEM_DESIGN.md` §6 重写为当前现状

## 执行备注

- 每阶段完成：本地验证（必要时浏览器/模拟器/WT 实测）→ 提交 → push。
- 直连两项（#11/#12）工作量大且相互依赖（e2e 适配器是开数据面准入的前置），排在功能缺陷与安全加固之后。
- 追加记录：本轮管理页"运行时参数"面板已上线（77676da 补保存成功提示）；本地 Docker 为最新构建。
