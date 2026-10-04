# 设备入口与 AI 会话完善 / 0.3.0 候选

用户确认范围：设备卡片按 CLI、AI 聊天、文件 排列同级紧凑按钮；CLI 独立默认目录；Web 工具汇总、
问答/阅读状态恢复、搜索分页。CLI 页面主体布局保持原样。既有未发布 AI 功能随本轮一并集成。

## 实现与边界

- AI 聊天入口保留；旧 Windows 未上报 AI 能力时点击提示升级电脑端，不隐藏入口或进入无效创建流程。
- CLI 创建入口只列 Windows 实际广告的终端命令；默认目录为安装目录 `AiWorkspaces/cli_时间戳_UUID`，
  AI 仍为 `agent_时间戳_随机标识`。共用根沿用升级/回滚/卸载保护。可浏览选择已有目录。
- Windows 只为受信根直属的严格 CLI 命名空间创建目录，拒绝链接祖先，不创建任意远程路径；
  复用 file-operation 路径/命令校验与稳定 operation/session ID。Android 全入口防并发启动。
  旧电脑未报告 `cliWorkspaceRoot` 时提示升级或自选目录，禁止假造默认路径。
- Web 按连续工具记录分组，每组最多 20 条，避免巨型工具组破坏虚拟滚动；可展开逐项状态/结果。
  问答审批仍在独立区域，搜索命中工具会展开所属组与详情。不会删除原始消息。
- 未提交问答和阅读/展开状态存入既有账号隔离 preferences，使用 Android Keystore 加密。
  按 PC/host/session 和请求问题结构校验；退出账号/关闭视图清除；恢复不自动提交任何请求。
  阅读锚点尚未加载时提示加载更早历史，不假称自动恢复到了正确位置。
- 搜索每页 50 条，支持更多、空结果、历史截断提示；关键词变化/切会话废弃旧响应。
  未加载的命中沿用完整消息查看，关键词高亮和自动定位前后文仍列后续优化。
- CLI 与 AI 仅验收 Relay。现场发现本地策略表无记录（默认自动直连），已按用户当前策略写入
  `routing_permitted=false` 并读回确认；未做 LAN/NAT 测试。

## 已执行验证

- Android app/core-network Debug 全量单测与 DEV APK 编译通过；新增用例覆盖问答结构/旧请求过滤、
  加密、host 隔离、退出清理、默认 CLI 路径。最新定向 UI 状态回归通过。
- Windows AI 三协议 fixture、真实文件操作、UpdateCore、安装器目录保护、CLI 七天清理与故障隔离、
  AgentControl 兼容、Avalonia Headless 通过。AI 不支持功能不从 fixture 推断真实 Agent 已支持。
- Web 六项 Playwright 检查通过：万条消息虚拟化/滚动、流式回答、静态 HTML 隔离、公开思考/用量/Diff、
  页面重建恢复问答与阅读、不自动提交、75 条搜索分页/旧响应隔离/工具命中。恢复用例连续三次通过。
- Server `go test ./...` 通过，本地 server/postgres healthy，公网 readyz=ready。
- DEV `0.2.71/127` 覆盖模拟器 `emulator-5556`，保留数据；Windows DEV 原安装根
  `D:\Dev\Tool\TermLink` 安装成功，账号保留、Relay Connected、无错误。
- 真实 ProductActivity + HOME-PC：三个按钮可用；+CLI 无需填写路径直接打开 PowerShell，输入 pwd
  返回 CLI 独立目录，exit 后正常离开；文件按钮列出实际磁盘；AI 新建直接进入紧凑 Web 聊天，
  模型/强度独立下拉、附件/授权/发送入口存在，真实 Codex 回复已观察。
- 真实 Web 搜索通过 Relay 返回用户/助手两条命中；关闭 Windows 远程访问后约 8 秒隐藏正文，
  重新启用、重新确认连接后恢复，已核对真实截图与 WebView DOM。模拟器 uiautomator 在重建后的
  WebView 未列出子节点，不能仅据此断言白屏。3 秒固定等待不足以反映长轮询/目录超时，不作即时离线承诺。

日志/截图保留在 ignored `artifacts/validation/device-ai-*`、`device-cli*`，不提交用户会话数据。

按钮修正追加验证：Android app Debug 单测 365 项通过；DEV 保持 `0.2.71/127` 原地覆盖。
模拟器 HOME-PC 卡片实际顺序为 +CLI、AI 聊天、文件，点击 AI 聊天可打开绑定 HOME-PC 的创建面板。
截图与 UI 层级保留在 `artifacts/validation/device-actions-order-*`。

最终按用户要求去掉 CLI 前的 `+`，顺序为 CLI、AI 聊天、文件。旧 HT-LPC-1326 卡片的 AI 入口与
升级提示已实测；HOME-PC 创建面板和文件入口实测通过。最终发布/本地安装状态见
[0.3.0 / 128 记录](RELEASE_0.3.0_128.md)，客户端仍暂缓 AppShip，Docker 已按本轮签名例外发布。

## 发布暂缓后的安卓追加：目录新建 AI 聊天

- 文件夹、当前目录和收藏目录菜单增加「新建 AI 聊天」，进入既有 AI 创建面板，锁定所属电脑，
  使用选中路径初始化自定义工作目录；只有用户点击创建才发送创建请求。普通文件不显示该选项。
- 收藏目录使用收藏记录的 PC ID；离线或未上报 AI 能力的电脑显示原因，不回退到另一台电脑。
- 从设备卡片或 AI 列表重新新建会清除上次路径预填，保留默认独立工作目录规则。
  ZCode 桌面远控明确提示不接收路径，不能声称已替换其桌面工作目录。
- Android app Debug 单测 366 项通过。模拟器 DEV `0.2.71/127` 与 HOME-PC 联测验证三个入口的
  `D:\Dev` 回填、设备锁定、默认目录恢复和 ZCode 提示；未创建真实 AI 会话。
  证据位于 ignored `artifacts/validation/directory-ai-*`。
- 该追加仅更新模拟器 DEV，不在先前正式签名测试包或 AppShip 中；Windows/Server 无改动。

## 发布暂缓后的安卓追加：设备 AI 分组与列表定位

- 设备卡片展开后，AI 区域只显示 Agent 产品图标、名称和会话数量，不再逐条铺开会话标题。
  ZCode 桌面远控显示独立入口和「桌面远控」，不将它冒充为一条原生 AI 会话。
- 点击分组进入 AI 列表，按电脑 ID 与 Agent ID 定位，清除旧搜索/待处理过滤，展开目标分组并
  短暂高亮。折叠状态先完成重组再滚动，避免使用旧行号；同名 Agent 的不同电脑分组相互隔离。
- 从设备进入时临时合并该电脑实时目录与手机已打开引用，去重后呈现；只有点击具体会话才打开。
  顶部可退回「手机已打开」，浏览分组不会持久化整台电脑的会话，也不会获取控制或新建会话。
  离线后不展示未打开的缓存目录。原 CLI 页面布局与操作保持原样。
- 设备展开/收起箭头放在标题行最右侧，独立 48dp 点击区域；在线状态圆点移到设备名旁，
  保留标题点击展开与长按设备菜单。
- Codex、Claude Code、ZCode 使用随包本地产品图标；来源固定 revision、许可和适配说明见
  `android/app/src/main/assets/licenses/ai-agent-icons-NOTICE.md`。Codex 采用原始 PNG，避免紧凑
  SVG 弧线参数在 Android VectorDrawable 中解析失真；不引入网络图标依赖。
- Android app Debug 全量单测 368 项通过，最终图标与定位修正后相关 4 项单测和 DEV 编译通过。
  模拟器 `emulator-5556` 已原地更新 DEV `0.2.71/127`；与 HOME-PC 实测 Codex/ZCode 分组跳转、
  搜索清除、折叠后进入、临时目录退出（5 项恢复原有 4 项）、标题/箭头展开收起及长按菜单。
  Codex/ZCode 产品图标和目标行高亮经截图复核；当前电脑没有 Claude 会话，未声称真实 Claude
  会话跳转已联测。证据保留在 ignored `artifacts/validation/agent-groups-*`。
- 仅安卓 DEV 更新，Windows/Server 无改动；本轮追加未正式签名发布，AppShip 继续暂缓。

## 发布门禁与回滚

2026-10-04 现场 AppShip 双端 published=0.2.71/127、nextBuild=128；Docker Hub 最新 v0.2.61，
未使用 0.3.0。固定本轮候选版本 **0.3.0 / 128**。此记录不是已发布声明。

用户随后要求暂缓客户端 AppShip 发布，但明确接受本轮 Docker 无 Cosign 签名并发布。
按钮顺序与 AI 入口修正后，原先从 `a153d00` 构建的客户端候选不再代表最新源码；恢复客户端
发布时必须重新构建、验证并审核最终制品。Docker 从修正后的固定提交本机构建，核验后推送 Hub。

Android 与 Windows Production 将从最终已提交 revision 构建，验证既有 Android signer 和 Windows
Authenticode/RFC3161 连续性，再记录人工签名审核结论并依次上传 AppShip（Android 在前）。
Docker 按用户指定本地编译直推 Hub，完成多架构候选和 provenance/SBOM 验证；本轮用户已明确
接受无 Cosign 签名发布，记录为本轮例外，不冒称 GitHub keyless 已通过。latest 不能提前替换。

回滚参照上版 Production 0.2.71/127 原制品和 manifest、Docker v0.2.61 的已核验 digest。
Windows 工作目录及 AI DPAPI 归档必须保留，禁止清数据/卸载安卓解决签名问题；本轮无数据库迁移。
旧客户端没有 AI 界面，回滚只恢复其 CLI 功能，不能把 AI 降级后的不可访问误称数据已经删除。
