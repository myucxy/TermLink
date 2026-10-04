# AI 列表与电脑历史恢复（DEV，2026-10-03）

## 范围

- Android AI 搜索框为 40 dp，按设备 → Agent → 会话分组，可折叠；保留手机已打开列表、待处理筛选、置顶、批量关闭和后台提醒。
- 关闭视图、结束会话、删除 Windows TermLink 记录显示实际标题与 Agent 名称。电脑离线时隐藏正文及摘要，搜索/批量选择不读取旧正文作为可见标题。
- AI 云朵为单图标连接菜单。进入/重连时尝试一次取得操作权，保留控制版本校验，不因另一手机接管而循环争抢。CLI 布局与正文代码未修改。
- “电脑历史”可打开 Windows TermLink 记录，或分页查看 Codex / Claude 原生历史后继续对话。列表读取不创建会话；只导入用户点选项；使用现有 E2EE AI 消息，服务端无本轮新增代码。
- ZCode 桌面远控继续由官方页面管理历史，不套用 Codex / Claude 原生导入能力。

## 授权与边界

- 按用户要求保留历史授权，包括已确认的较高授权。主动切换授权仍走原有确认流程。
- Codex 使用原生 thread/list 与 thread/resume，恢复时不覆盖原授权；保存完整 approvalPolicy、sandbox、approvalsReviewer，下一轮保持沙箱额外目录/网络等细节。摘要采用有界 thread/turns/list；原生上下文仍由 Codex 保存。
- Claude 读取 projects 下 UUID.jsonl 的元数据与有界首尾文本，使用最近保存的 permissionMode 恢复。缺失/不支持的模式报错，不默认替换。未写入历史文件的最后一次模式切换无法推断；不承诺恢复未落盘设置。
- 配置根上层允许用户自己的目录联接（本机 .claude 已迁到 F 盘），projects 内部不跟随联接。限制页数/目录/文件/字节数；过滤旁支与工具消息，重复消息 UUID 合并，避免 Android LazyColumn 键冲突。
- 导入要求同一调用者先读取列表，缓存十分钟，受 host、身份、容量与操作回执约束；重试/同一原生 ID 不产生第二个 TermLink 活动会话。读取原生文件不持有会话管理锁。
- 不自动关闭原 Agent 窗口；界面提醒避免与原窗口同时发送。手机不持久保存历史正文。

## 验证证据

- Android：357 项单元测试，失败/错误/跳过均为零；新增搜索/身份、只读历史查询、主机切换拒绝旧响应、导入后自动打开及引用持久化测试。
- Windows：AiAgentValidation 全套通过；新增 Claude 元数据、模式、旁支/工具过滤、路径/cursor、重复 UUID、原授权存档/恢复/下一轮参数断言。原模型、附件、控制版本、归档、操作回执回归通过。
- 真 Codex 0.160.0：独立测试会话 → 原生列表 → 导入，read-only 沙箱保留，续聊能返回先前标记。日志 ai-history-native-real.log 中 Codex 通过；随后 Claude 因配置目录联接失败，已修复后单独重测。
- 真 Claude 2.1.116：修复后原生列表/导入、plan 授权和先前标记续聊均通过（ai-history-claude-real.log）。真实测试没有执行工具。
- 模拟器 5556 + 本机原生适配器隔离桥：列表设备/Agent 分组与折叠、批量关闭实际标题/Agent、电脑历史源选择、搜索独立测试原生记录、进入聊天显示历史授权/消息、云朵无只读/控制文案均实操验证。离线后断言正文标记消失。
- 自动释放控制权测试被工具自动审批拒绝，未执行，不计入通过。隔离 HTTP 桥不等同于生产 E2EE 链路。
- ProductActivity 从原 SessionsPage 开始与 HEAD 一致；git diff --check 通过（已有 CRLF 提示）。

## 构建与升级

- 本轮为本机 DEV，没有提交、推送、tag 或上传 AppShip。现场两个 stable 更新端点均为 0.2.71 / 127，DEV 沿用该版本。
- Android APK：artifacts/validation/ai-history-android-dev-0.2.71-127.apk，HTTPS termlink2.kget.cc:8910。
- SHA-256：2BB88737E30E7A2D7B806E07ACCF82EDB92EB46F3FBBFCFBC0A5688C703992A0。
- DEV signer SHA-256：b0bcabe365a44108af2101fa3b34899dfeed5efef306639a004097060871d343，与原 DEV 相同。
- Windows 暂存：artifacts/validation/ai-history-windows-dev-20261003；Desktop / Agent / SessionHost 版本均为 0.2.71.127。
- Windows 最终替换已完成（包含去重修正），原活动根 ai-features-windows-dev-20261003，最终回滚 ai-history-rollback-20261003-235540；账号保留、远程访问开启、Relay Connected，CLI 数量为零，活动 Agent 哈希与最终暂存产物一致。操作/健康记录为 ai-history-win-update.json、ai-history-win-health.json。
- 后续排查时分别提交最小范围操作，最终 HTTPS APK 覆盖安装返回 Success，ProductActivity 启动成功，18793 端口映射撤销，隔离验收桥 /shutdown 返回 204。未修改工具审批规则或清除账号数据；安装后 APK SHA-256 与上述最终产物一致。
- 模拟器真实 HTTPS / E2EE 路径验证：HOME-PC 显示 online、Agent 0.2.71.127；电脑历史返回 Codex / Claude 原生入口。选取本轮独立 Codex 测试历史导入，显示“历史授权”；发送不含标记的新请求，得到原标记 TERMLINK_NATIVE_HISTORY_7319，验证真实桥接、历史上下文与续聊。双端最终升级完成，服务端无需更新。

## 工具审批拦截排查（后续补充）

- 原失败为 exec_command 的 CreateProcess 在 PowerShell 启动前返回 Rejected / blocked by policy，没有进入 ADB 或 Android PackageManager。
- 只读核对时模拟器在线，/data 可用约 4.4 GB，最终 APK 签名验证通过；本地 default.rules 仅包含三条 allow，没有发现 ADB 禁止项。
- 合并命令无法得到逐项拒绝原因；将已授权操作按目的单独提交后，安装、启动产品入口、撤销映射、关闭隔离桥全部通过。由此确认阻塞在工具审批层，而非 APK/签名/磁盘/服务端；内部触发规则仍未公开，不推断具体关键词或认定用户授权缺失。
- 旧 crash buffer 中的 AiAcceptanceActivity Check failed 发生在此前错误启动测试入口时，与这次命令执行前的拒绝无关。最终 HTTPS 包启动的是 ProductActivity。
