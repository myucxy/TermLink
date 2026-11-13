# 客户端能力同步复核 — 2026-11-12

依据当前主仓 a892d154f25f718d4bca8c10406f00f237d1dddf 及保留的未提交源码、11月12日本机验证记录。本文是源码与既有证据审查，没有重新运行全端验收或改变服务配置。旧 ios/PARITY.md 的10月25日条目不作为当前缺失清单。

## 已实现的基础链路

- Mac账号、Relay、远程CLI、终端状态快照/有界事件恢复、普通文件、五品牌AI目录与协议启动、AI附件/文件引用/成果后端均已有代码。逐项可用性仍受安装、协议、权限与实际Agent版本约束。
- iOS目录选择/最近目录/未决CLI回执恢复、持久AI成果下载与暂停续传、共享AI工具/功能/Skills/MCP/历史操作呈现均已实现。不要重复列为待开发。
- ZCode自动探测已改外置Node；Mac完全退出身份误判已修复并安装。真实PKCE/Relay与部分CLI/文件链、12MiB成果暂停续传已有本机证据。见各artifacts报告，不把这些结果扩展为完整模型推理或全部平台验收。

## 确认仍需同步

| 优先级 | 范围 | 当前差距与影响 | 当前源码依据 |
|---|---|---|---|
| P0 | Mac运行稳定性验收 | 界面在部分桌面/显示环境中有RenderTimer -6661；睡眠唤醒、网络恢复、原生菜单退出与升级生命周期尚需完成实际验收。前次身份误判修复不能代表这些全部通过。 | artifacts/macos-exit-fix-20261112/REPORT.md；artifacts/feature-parity-implementation-20261112/REPORT.md |
| P1 | 独立终端宿主 | Mac PTY仍由Agent进程内平台宿主管理；缺Windows式独立SessionHost，不能承诺Agent异常退出/升级后原Shell继续运行。正常“完全退出”仍应停止受管Shell。 | pc-client/src/Agent.Host/PortableAgentRuntime.cs:83；pc-client/src/Agent.Terminal/PortableTerminalHost.cs:47 |
| P1 | AI持久归档 | Mac构造AiSessionManager没有archiveRoot；手机缓存/Agent原生DB不是TermLink跨后台进程归档恢复。需账号隔离、加密存储及重启恢复。 | pc-client/src/Agent.AI/PortableAiHost.cs:35；win-agent/src/Agent/ProductionAgentRuntime.cs:622 |
| 本轮已实现 | 自定义CLI启动项 | 已补本机持久配置、JSON参数/启停、GUI动态创建列表和手机目录同步；工作目录继续每次显式选择。 | pc-client/src/Agent.Host/PortableAgentRuntime.cs:150；pc-client/src/Windows/WindowsAgentClient.cs:309 |
| 本轮已实现 | 已结束会话清理 | Mac已补持久删除标记、权限检查、服务器对账及能力声明；iOS已补确认范围、分批/进度/回执与重试入口。运行中Shell跳过；验收结果以本轮报告为准。 | pc-client/src/Agent.Connection/RelayTerminalTransport.cs:15；ios/TermLink/App/ProductStore.swift:169；ios/TermLink/Features/Devices/DevicesView.swift:24；android/app/src/main/kotlin/dev/termlink/product/InactiveSessionCleanup.kt |
| P1 | Mac授权与路由策略 | Grant/Revoke明确未接；本机远程开关只保存本机状态，未同步服务端版本化remote-routing接口。 | pc-client/src/Agent.Host/PortableAgentRuntime.cs:180；pc-client/src/Agent.Connection/RelayConnectionService.cs:205；server/MACOS_ACCESS.md:68 |
| P2 | Mac升级闭环 | 本地受信PKG更新框架已有；Mac远程升级任务消费者尚缺，未声明remote_agent_update_v1。DEV/ad-hoc不会启用正式更新渠道。iOS升级标题/无障碍文字仍硬编码Windows，也需按目标平台改。 | server/MACOS_ACCESS.md:87；pc-client/src/MacOS/MacUpdateOptions.cs:15；ios/TermLink/Features/Devices/DevicesView.swift:51 |
| P2 | 高级终端对齐 | 基础文本/快捷键/快照及本地冻结浏览已有；iOS未接Android图形/Unicode16高级状态与完整硬件键鼠语义，收到Broker输出会明确拒绝。主机冻结历史分页、OSC进度/响铃/远端剪贴板策略也未对齐；Mac缺完整图形/Broker/主机冻结历史链路，当前有界回放不等同于全部历史能力。 | ios/TermLink/Terminal/TerminalController.swift:215；ios/TermLink/Terminal/TerminalSurface.swift:78；pc-client/src/Agent.Connection/RelayTerminalTransport.cs:185；pc-client/src/Contracts/CapabilityReportFactory.cs:34 |
| P2 | 通知与后台事件 | iOS只有收到在线会话状态后生成本地通知；APNs注册/回调/授权配置链尚未接入，退后台会断Relay。Mac后台AI提醒也未接。服务端已有APNs发送器仍需客户端及配置联调。 | ios/TermLink/Features/Settings/StatusNotifications.swift:19；ios/TermLink/App/ProductStore.swift:132；pc-client/src/MacOS/MacDesktopOperations.cs:37；server/internal/pushnotify/apns.go |

移动端细节还包括快捷键所在行/位置编排（iOS已有编辑、排序、撤销，未对齐Android细编排）和平台化升级文字。iOS后台当前暂停下载、前台恢复；这不等于后台持续下载。Android已有通知功能也不能据此推断具备可靠远程推送。

## 已有实现但验收未完成

- 五个Agent的发现/协议可用，不等于五个模型账户均已授权。Codex独立运行时已验证设备码发起/待完成/取消，尚无完成授权后的真实模型回复验收。
- 真实工具调用、审批、原生历史续聊/review/fork/revert需要按各Agent支持范围验收。已有共享呈现与协议实现不记为缺失，也不当作真实模型成功。
- x64 Mac、正式签名/公证更新、异常进程退出、睡眠唤醒和长期弱网属于平台/运行验收边界；当前模拟器与arm64本机证据不能替代。

## 后续顺序

先收尾Mac启动和生命周期稳定性；然后做自定义CLI命令与跨端会话清理；再做AI归档及独立SessionHost；随后接设备授权/路由与升级任务；最后集中推进通知和高级终端。SessionHost/加密归档属于架构工作，应单独设计和验证。所有链路沿Relay Only，不恢复LAN/NAT验收。

服务端已具备Mac/iOS身份、路由策略、Mac包目录和升级任务等接口，后续以具体能力缺口补齐客户端接线与端到端验证，不把“接口存在”称为“客户端能力已可用”。
