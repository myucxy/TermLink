# AI 授权状态、Mac 能力检测与 iOS 恢复功能

本次基于 `a892d15` 合入后的源码继续开发，使用 DEV 0.3.16 / build146，不递增正式版本。保留工作区未提交修改，未提交、推送或发布。原 `AI_AGENT_PARITY_20261111.md` 及旧联测报告保留为其对应版本的历史证据。

## 本轮实现

### Mac 能力与配置

`PortableAiCatalog` 为桌面检测和账号Host提供同一套五品牌发现/启动条件。实际可启动与模型已认证分开，诊断runtime不读取账号密钥。Mac AI文件卡检测必需的原生导出；旧文件库或缺失库不能开启附件能力。原生历史与跨进程归档分开说明，Mac未实现的归档继续明确关闭。

ZCode在探测和实际启动前同时设置账号独立的配置、会话、SQLite目录；不复制原用户provider配置。私有目录通过no-follow句柄、owner/mode和ACL校验。已有ZCode配置可能需要在新的runtime重新设置；本次没有迁移或读取原授权。

### 独立模型授权

AIcatalog新增 `runtimeAuthentication` 元数据(version/status/isolated/method/canCheck/canStart/configurationDirectory)。协议available只代表可以启动协议，不能据此判断模型权限。

Codex支持固定的 `authentication`、`authentication.start`、`authentication.cancel`。它们仅初始化选定账号的独立app-server，调用 `account/read(refreshToken:false)`，或由用户明确发起设备码授权/取消；不创建对话线程、不运行模型、不转发密码/APIkey/Token/email。其他Agent目前显示电脑独立配置的setup提示，不伪造通用OAuth。

- MANAGE和原host/caller边界沿现Relay/E2EE保留；跨设备不能读取他人挑战。
- start/cancel保留原operation/body回执去重；回执不含设备码/授权URL，只有当前owner只读查询返回临时挑战。
- URL只允许固定 `https://auth.openai.com/codex/device`；code ASCII有界、过期隐藏。未完成授权不自动发送消息。
- start/cancel失败会退役本次独立进程，取消不调用account/logout。只读查询无挑战时及时释放，不占用其他手机的授权所有权。
- 十分钟到期退出ownedruntime；提前完成通知按loginId关联；账号/host/可用性变化清除手机临时状态，旧查询/错回执不能覆盖新状态或释放pending。

在本机实际未登录Codex上，`account/rateLimits/read`返回 `-32600` 且message中有固定Authentication required分类。现在精确分类为认证需要，仅该可选用量读取显示requires_auth；Skills/MCP继续展示。其他无此认证分类的invalidJSONRPC、超时与异常不被吞为成功。上游错误正文不透传。

### iOS AI成果下载

新增独立持久任务列表，从AI页面常驻下载入口进入。任务固定原PC、host、session、目录、pathStyle、artifactID、version。重试先重新确认原catalog与会话，不跟当前选择的电脑重新绑定；恢复不自动联网。

网络分页有界，落盘为16KiB AES-GCM帧；账号独立密钥、加密元数据、fsync及原子提交。暂停/取消/重试/移除/显式分享均有状态。崩溃未提交尾部截断、孤立文件清理、metadata/帧篡改与任务交换拒绝。当前主机协议只提供version/size，UI明确不把它说成主机完整hash保证；测试使用独立夹具SHA256另行验收。

### iOS CLI创建

首页和目录内打开Shell共用持久化创建协调器。支持主机可用位置、目录选择/筛选/分页、最近成功目录及完整路径显示。近期目录绑定同账号/服务器/设备/PC/keyepoch，没有记录时不猜Mac用户名或home。

先加密保存原幂等编号、路径和命令，再发POST；已知operationID只GET原任务。首次响应丢失时，用户显式恢复同编号同body的POST，不能换目录或新编号。现服务端无按幂等编号GET接口，24小时保留期下客户端只允许23小时保守窗口，时钟回退或身份/权限变化拒绝自动重放。未确认任务不能清除后重新创建。

## 验证与实际边界

- 新主仓库全量iOS：166通过/0失败/2个opt-in跳过；授权回执加固后追加3项定点测试通过。包含目录选择/丢回执实际UI和加密下载缓存测试。
- Android认证/品牌/工作区/桥接/CLI相关：43JVM通过/0失败/0跳过；排除原DirectJNI三项任务，不冒充APK或设备测试。
- 便携AI/能力目录/文件/授权fixture通过，WindowsAI项目交叉编译通过。
- 实际termLink3→iOS模拟器→Mac：CLI/普通文件兼容链通过；五品牌协议目录和isolated状态、Codex只读required、空线程创建、附件upload/remove、成果持久下载及12MiB真实部分暂停/同任务恢复/导出独立SHA256通过。
- 最后严格复测Skills/MCP available和rateLimits requires_auth通过。
- 独立私有Codexruntime实际设备码发起→pending→取消→required验证通过；没有完成模型账号登录，没有模型消息或工具请求，设备码/Token未写日志。此项不是完整账号授权完成或模型推理验收。

## 尚欠缺

Mac跨进程AI归档、独立SessionHost/原Shell跨Agent崩溃存活、会话清理和自定义命令、完整键鼠/图形/Broker/主机冻结历史、Mac授权管理/路由政策同步/远程升级worker、iOS APNs及部分视觉/高级终端能力仍需后续开发。五品牌协议目录通过不等于五个模型账户已授权，真实推理/审批工具/历史高级操作仍需单独验收。

当前测试环境活动显示器数为0时，Avalonia12.1.1的CoreVideo native时钟初始化会返回-6661，Mac控制窗口不能创建；后台Agent独立保持在线。本轮独立最小Avalonia程序已复现并确认公共API无可注入的时钟fallback，未通过反射/升级库/改变系统显示器绕过。显示环境恢复后才能继续桌面窗口实测，不能将后台在线说成此环境的UI已通过。
