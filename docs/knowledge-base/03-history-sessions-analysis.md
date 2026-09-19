# 03 · "历史会话"功能分析

> 来源：2026-09-16 Codex 会话第一轮（用户请求"分析历史会话"），结论已对照 2026-09-17 工作树核实。

## 当前数据流

1. **服务端只保存会话元数据**，通过 `include_ended=true` 返回已结束会话；不保存终端正文。（`android/app/src/main/kotlin/dev/termlink/product/ProductionProductRepository.kt:3698`）
2. **Windows 主机保存正文**：本地 WAL、Snapshot、Transcript，DPAPI（CurrentUser）加密落盘。
3. **Android 打开已结束会话**：建立临时零窗口 E2EE 通道，从**在线** Windows 主机恢复内容，然后发布只读渲染器。（`ProductionProductRepository.kt:2409`）
4. **上滑历史**：固定 Snapshot 锚点 + 独立 Transcript viewport，不污染实时 parser/ACK。
5. **删除顺序正确**：Windows 先持久化删除标记并销毁本地历史，之后才删服务端元数据和 Android 缓存。（`win-agent/src/Agent/ProductionAgentRuntime.cs:1993`）

主体结论：架构合理，安全边界清晰；但存在以下 4 个风险。

## 风险 1 · 历史会话目录无界增长

- 服务端明确禁止自动清理：`server/internal/store/postgres.go:1884` 的 `CleanupSessions` 是保留后台契约的永久空操作（并有 `session_cleanup_retired_test.go` 锁定该语义）。
- Android 每次刷新为每台 PC 拉取**全部**已结束会话，并用普通 `Column/forEach` 一次性渲染。
- 后果：自然退出的会话持续累积后，数据库查询、刷新响应、Room 写入、Compose 布局线性膨胀。
- **建议**：保留"不自动删除"语义，给目录增加服务端分页 + Android 懒加载（LazyColumn）。

## 风险 2 · Presence 改动混淆"PC 在线"与"会话在线"（当前未提交 WIP）

- `server/internal/api/api.go:1327` 附近：只要 PC 有 WebSocket（`a.Presence.Online(userID, pcDeviceID)`），就把该 PC 下所有 `State == "RUNNING"` 的元数据行标记为在线。
- 这违反了模型中"精确到会话 UUID 的可达性"定义（`android/core-model/src/main/kotlin/dev/termlink/model/ProductModels.kt:85`）。
- 后果：陈旧或 Agent 已不持有的 RUNNING 会话在 Android 上显示为可打开，只能在订阅阶段失败。
- **建议**：Presence 至少需要会话级注册信息，或继续使用会话心跳判断。

## 风险 3 · Web 门户与 Android 在线状态可能不一致

- Relay 启用内存 Presence 后不再更新数据库心跳；而 Web 门户仍用 PostgreSQL 的 `last_seen_at`/心跳判断（`server/internal/store/user_portal.go:123` 附近的 `userPortalSessionsSQL`）。
- 后果：Android 显示 PC 在线，Web 门户约 45 秒后显示离线（或反之）。
- **建议**：门户读同一 presence 来源，或 presence 刷新时回写心跳。

## 风险 4 · 离线历史不可发现

- Android 设备页只展示在线 PC（`android/app/src/main/kotlin/dev/termlink/product/ProductActivity.kt:3370` 附近的 `visibleOnlineWindowsPcs`）。
- 后果：冷启动时无法查看离线 PC 的历史会话元数据。正文必须由在线主机提供是合理限制，但元数据列表可以仍展示（禁用态）。
- **建议**：设备页展示离线 PC 的历史记录入口（禁用态），标注"需主机在线"。

## 测试覆盖现状与缺口

已验证通过（2026-09-16 会话内实际执行）：

- Go：`go test ./internal/presence ./internal/api ./internal/store ./internal/realtime` 全部通过。
- Android：`ProductUiLogicTest`、`ProductSessionDeletionTest`、`SessionCatalogReconciliationTest` 全部通过。

缺口（无覆盖）：

- "Agent 重启后冷开已结束会话"的端到端测试。
- "PC 在线但单个 RUNNING 会话已失效"的边界测试。
- "Web 门户与 API 在线状态一致性"测试（各测各的，没有交叉断言）。

## Termux 参考结论（按 AGENTS.md 政策记录）

- 参考：`termux/termux-app` 固定 commit `3df69d1da197dd9bd71a3bafd902dffd720576b4`（`terminal-forks/termux-terminal/UPSTREAM.md`）。
- 判定：Termux 的 TerminalBuffer/滚动/换行/选择行为可继续作为差分参考；Termux 没有跨设备"历史会话"协议，其本地 PTY 与环形 scrollback 不能直接复用；当前独立 `TermuxTranscriptViewport` 方案是正确适配。
