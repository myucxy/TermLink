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
| 10 | Android 安全四件套（#7设置化/#22/#41/#42/#43） | Android | FLAG_SECURE 设置化与 WebView 禁第三方 Cookie 已合入（ca2e353）；StrongBox 与 SQLCipher **用户决定不处理**（收益为纵深防御，成本/体验代价高于实际需求；企业客户有合规要求时再启用） |

## 三、直连传输（第三阶段，产品意图核心）

| # | 问题 | 模块 | 确认方案 |
|---|------|------|----------|
| 11 | 票据 120s TTL 硬拆（#2） | Server+Win+Android | S1 已完成（b68bd42）：TTL 120s→20 分钟 + RenewTicket 端点（proof 验签/状态校验/限速 2/min/单节点内存态）；S2 FFI 已完成（e6ae05c）：State 增 ticket_expiry_ms/绑定/server key，data_loop 改 1s 续期轮询（软事件 EVENT_RENEWAL_DUE + 30s 宽限硬拆），新增 termlink_direct_renew_apply_v5 导出；S3 宿主接线（Win C# EVENT_RENEWAL_DUE 处理 + renew REST 调用 + renew_apply 导入；Android 同构）待下一轮；S4 指标待 S3 |
| 12 | 真实网络 e2e 为零（#3/24） | 测试设施 | 按 gap 清单实现真实 socket 适配器：同 LAN/跨 NAT/对称 NAT/防火墙拒 UDP/VPN/网络切换 |
| 13 | 队列满即整体退役（#25） | network-core | 有界降级：丢可丢帧+退役计数指标 |
| 14 | 直连 P3 四项（#46/47/49/50） | network-core+信令 | 探测抖动、QUIC 握手 keepalive、离线 peer 信令显式拒绝、FFI 忙轮询改事件等待 |

## 四、P3 扫尾（第四阶段）

| # | 问题 | 模块 | 确认方案 |
|---|------|------|----------|
| 15 | Server P3 四项（#32/34/35/33） | Server | 000046 down 已补；email-code 策略门禁复核已存在；登录锁定指数退避已实现并强制执行（5ab51a7，此前 locked_until 从未被读取——锁定形同虚设）；AdminRoutes 随机化能力已有（opt-in，默认 /admin 保持以避免锁死操作员） |
| 16 | metrics/告警（#26） | Server+运维 | 暂缓：/metrics 无认证（#36），公开端口启用前需先定认证方案；告警文案修正待做 |
| 17 | presence 统一同源（#9/31） | Server+Web+Android | 复核确认（2026-09-21）：门户和 API 均已从同一个 presence registry 读取在线状态，会话级通过 `PC presence AND state==RUNNING` 组合判定；4 秒状态传播修复后窗口已可接受 |
| 18 | 发版 CI 回归 + 恢复演练（#12/15） | 发布/运维 | **用户决定暂不处理**；推真实 tag 验证 CI 与恢复演练留待正式发版前统一执行 |

## 五、明确不处理（本轮确认）

| 项 | 决定 |
|----|------|
| 离线 PC 历史不可见（#51） | **维持现状**（只展示在线 PC） |
| MTU 固定 1200 无 DPLPMTUD（#48） | 后置，不列入本轮 |
| NAS 升级与双实例（#28） | 用户要求暂缓，待本地"完美"后再升级（升级前需推新 Docker Hub 镜像） |
| Docker Hub 新镜像推送 | 用户决定等实际要发布时再处理；本地 Docker 已是最新构建 |
| CI 发版回归 + 恢复演练 | 同上，正式发版前统一执行 |

## 六、发布杂项（随阶段穿插）

- Docker Hub 推新版本镜像（含 20 天生命周期/管理页/OIDC 修复/本轮全部改动）
- `.gitignore` 补 `zcodeDemo/`
- `COMPLETE_SYSTEM_DESIGN.md` §6 重写为当前现状

## 执行备注

- 每阶段完成：本地验证（必要时浏览器/模拟器/WT 实测）→ 提交 → push。
- 直连两项（#11/#12）工作量大且相互依赖（e2e 适配器是开数据面准入的前置），排在功能缺陷与安全加固之后。
- 追加记录：本轮管理页"运行时参数"面板已上线（77676da 补保存成功提示）；本地 Docker 为最新构建。

## 七、全链路回归结果（2026-09-21 实测）

环境：本地 Docker server（192.168.9.28:8080，schema v50）+ Windows Agent（D:\Dev\Tool\TermLink，注册模式后台运行时）+ Android 模拟器（dev.termlink.harness debug APK）。

| 项 | 结果 | 证据 |
|----|------|------|
| Android 全输入链（IME→engine→Relay→Agent→ConPTY→pwsh） | ✅ | 远程执行 Set-Content，PC 落盘 C:/Temp/tl-regress.txt（值 OK0921） |
| dock 快捷键（普通 shell） | ✅ | Esc 清行、Tab 补全 Get-Child→Get-ChildItem、Ctrl+C 多次 ^C、Enter 执行、Up 历史回溯 |
| Claude CLI `/` 崩溃修复 | ✅ | Claude Code v2.1.116 TUI 中输入 `/` 弹出命令菜单，应用存活 |
| dock 方向键/Esc 在 kitty TUI 下（本次新修复 503de70） | ✅ | dock ↓ 移动菜单选中项到 /add-dir；dock Esc 关闭菜单；修复前该路径抛 encodeInputV1 UNSUPPORTED 并杀死进程（dropbox data_app_crash 两次复现） |
| 20 天生命周期（行为面） | ✅ | 反复 force-stop/重启 APK 均保持登录态，仅切换端点后才要求重登 |
| FLAG_SECURE 默认开 | ✅ | 终端页 screencap 全黑；关闭开关后恢复可见 |
| Web 门户 | ✅ | 首页/仪表盘/PC Agent（含批量控制策略）/设备/会话记录/系统设置（Turnstile、SMTP）/系统运行/账号角色/版本 全部渲染正常 |
| 管理页"运行时参数"面板 | ✅ | /admin?section=deployment 五项参数渲染；账号注销显示"管理页覆盖"来源并即时生效；注册找回/STUN/AppShip 地址/内网白名单可编辑 |
| 直连状态 | ✅ | 会话经"服务器中转 / Relay"建立并 Controlling（本环境为同网 NAT，直连数据面仍按既定门禁关闭） |

### 回归中发现并修复

- **dock 功能键在 kitty 协议下崩溃/失效**（真缺陷）：编码路径仍走 Input V1 Key 被拒 → `NativeTerminalException(UNSUPPORTED)` 未捕获直接杀进程（键盘硬件键路径）或被 runOperation 吞掉（dock 路径，表现为无响应）。修复：`encodeKey/encodeCharacter` 迁移 Input V3 KittyKey（503de70），已随本提交推送并装回模拟器验证。

### 复核确认的已知项（非回归失败）

- 门户 PC 列表"离线"与 Android"PC online"并存 = 审计 #31 门户读 DB 心跳的陈旧显示（realtime presence 已同源，仅该列展示待切）；会话打开不受影响。
- 会话标题重复时多 tab 输入路由（此前已修）与死会话（HostLost）重复打开会得到 TL-H114/TIMEOUT——符合 fail-closed 预期，但 TL-H114 提示"产生新输出后恢复"在无新输出时不会自动重试 bootstrap，留作后续改进项。
- Claude Code 在 TermLink 管理会话内启动需要 `CLAUDE_CODE_GIT_BASH_PATH` 指向 bash.exe（宿主全局 env 或会话继承），已通过 `setx` 固化到本机用户环境。
