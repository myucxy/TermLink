# CLI 会话自动清理

2026-10-03 起的产品规则：手机不保留已确认失效的 CLI 页签；Windows 只保留失效后 7 天内的 CLI 历史。此规则不适用于 AI 对话、ZCode 桌面远控、会话工作目录或用户项目文件。

## Android

- Windows 的受信任目录确认会话为 EXITED/INTERRUPTED 且进程已停止后，自动关闭手机页签，更新选中页签、持久化导航记录与输入偏好，释放本地渲染器和订阅。
- 失效 CLI 不再从设备页重新打开为历史页签；设备页隐藏受信任 Windows 目录已确认失效的条目。Windows 的七天历史保留不受手机显示过滤影响，长按设备的批量清理继续使用完整目录中的失效记录。
- 手机断网、电脑离线、目录超时、授权变化或目录尚未取得时，不据此删除页签。原页面仍按已有规则隐藏无法确认的会话正文。
- 旧版页签只保存会话 UUID，没有所属电脑信息；缺失的 UUID 必须等所有电脑目录完整确认后才能删除。已知电脑确认失效的会话不受另一台离线电脑影响。
- CLI 页面布局、Dock、云朵菜单和输入方式不变。自动关闭不调用远程终止或历史删除接口。

## Windows

- Windows Agent 初始化账户运行时后立即检查，此后每分钟检查一次；本地检查不依赖 Relay 或 HTTP 在线。
- 按首次确认进程失效的 UTC 时间计时，**超过 7×24 小时**后删除；恰好 7 天仍保留。首次检查可能比实际结束晚一个检查周期。
- 时间写入各会话的 `inactive-since.dpapi`，使用现有 CurrentUser DPAPI、设备/会话作用域和原子写入机制，重启不重新计时。
- 旧历史没有可靠的持久化结束时间，首次确认失效时开始完整的 7 天宽限期；不以创建时间、最后输出时间或文件修改时间推断失效时间。重新发现同一进程仍存活时清除失效计时。
- 删除前在注册锁内复查 Host PID 和进程启动时间。进程存活、状态无法读取、时间记录损坏或删除标记无法落盘时，不授权自动删除。
- 复用现有持久化删除标记，清理 Windows 历史 WAL、快照、转录、发送缓存，以及该 UUID 的遗留原始输出缓存。被占用文件后续检查继续重试；删除标记保留，防止迟到注册或重启恢复旧内容。
- 服务端的数量/目录快照继续走已有删除同步与重试，不需要升级 Server。本轮不接触用户目录内容。

## 2026-10-03 开发验证

源码为基线 `faca6618685cbc7924f8b176115d2601d0eb6dd5` 上的未提交工作树，不是正式发布。

| 检查 | 结果 |
| --- | --- |
| Android 单元测试 | 344 项通过，失败/错误/跳过均为 0；包括失效与缺失页签、离线/超时/未初始化、信任和 key epoch 变化、持久化清理 |
| Windows 自动清理 | 7 天边界、跨重启、活跃进程、时钟回拨、损坏记录、旧记录宽限、原始缓存清理、服务端同步重试通过 |
| Windows 批量清理回归 | 245 条记录 / 4 次同步；活跃进程保护、重复请求、迟到注册及失败重试通过 |
| Windows 历史回归 | WAL、producer、outbox、uploader 验证通过 |
| 模拟器 + HOME-PC | 正常 HTTPS / E2EE / Relay 打开临时 CLI；Windows Shell 正常退出后手机自动移除页签，保存的导航不含其 UUID |
| UI 保留 | `ProductActivity.kt` 从 `private fun SessionsPage(` 到末尾与 HEAD 一致（忽略换行） |

日志及截图位于 Git ignored 的 `artifacts/validation/`：

- `cli-retention-android-build.log`
- `cli-retention-windows-tests.log`
- `cli-retention-history-regression.log`
- `cli-retention-live.xml/png`、`cli-retention-after-exit.xml/png`
- `cli-retention-win-*-build.log`

已现场核对 AppShip 两端 stable 的最新公开版本为 `0.2.71 / 127`，DEV 沿用该号。模拟器 `emulator-5556` 已覆盖安装 HTTPS DEV，保留登录信息；Windows 完整 DEV 位于 `artifacts/validation/cli-retention-windows-dev-20261003/`，尚未覆盖正在运行的 Windows DEV，以保留用户当前 Claude/Codex 会话。Windows 七天策略需安装并启动该构建后生效；七天行为使用隔离测试时钟验证，没有修改系统时间或删除真实用户历史。

未提交、打 tag、发布 AppShip、构建/部署服务端镜像。本机运行中的 Windows 仍是此前的 DEV，因此本轮模拟器退出联测验证的是新 Android 与现有 Windows 协议的兼容；新版 Windows 清理验证在隔离历史目录中完成。

## 遗留索引删除修复（2026-10-03）

现场复现：HOME-PC 有 11 条只有注册索引、已没有历史目录的失效 CLI。设备目录枚举注册索引，但清理接口只查询托管会话对象与历史文件，返回 `NOT_FOUND`；Android 将其转成通用连接错误。用运行中的旧 Windows 二进制在隔离目录验证了 `ListedInCatalog=true / ContainsSession=false / CleanupStatus=NOT_FOUND`。

- Windows 清理改用包含持久注册索引的存在性判断，保留路由与历史恢复所用 `ContainsSession` 的原语义；仍在注册锁内验证 PID/启动时间，落盘删除标记后清理历史和遗留缓存。真正不存在的 UUID 仍返回 `NOT_FOUND`。
- Android 仅在收到经过身份及请求关联校验的 `DELETED` 或 `NOT_FOUND` 后完成本地清理，单个和批量入口一致；活跃、失败、超时及未确认结果不会清掉本地状态。已确认删除的 UUID 在当前 repository 生命周期内阻止旧目录刷新重新加入。
- Windows 明确返回清理失败时，单个删除显示“Windows 未能删除此会话记录”，不再提示检查连接。设备页和 CLI 页签使用相同的目录信任条件过滤已失效会话，保留 AI、CLI 正文、Dock 与输入布局。

验证：Android 345 项测试通过（无失败、错误、跳过）；Windows 245 条/4 批回归、无历史的活跃进程保护、仅注册索引清理、原始缓存删除、重复请求、重启后拒绝迟到注册、七天保留边界及同步重试均通过。新增用例在修复前失败、修复后通过。

模拟器已覆盖安装同签名 `0.2.71 / 127` DEV。正常 HTTPS 桥接旧 Windows，批量处理 11 条返回不存在的旧记录，界面结果为 11 完成、0 失败、0 跳过；刷新后清理计数为 0、失效 CLI 不显示，两个 AI 对话仍在。这个联测验证 Android 兼容处理，不等于旧 Windows 已持久删除注册索引；旧 Windows 的完整索引修复仍需启动新构建，Android 当前进程的删除屏障也不替代 Windows 的持久标记。

最新开发产物：

- Android：`artifacts/validation/cli-orphan-android-dev-0.2.71-127.apk`（模拟器已安装）。
- Windows：`artifacts/validation/cli-orphan-windows-dev-20261003/`（完整 Desktop/Agent/SessionHost DEV，未替换运行中的 AI 会话）。
- 证据：`cli-orphan-android-build.log`、`cli-orphan-windows-before.log`、`cli-orphan-windows-tests.log`、`cli-orphan-batch-result.xml`、`cli-orphan-menu-after-cleanup.xml`，均位于 ignored `artifacts/validation/`。

Server 无需改动；本轮没有提交、正式发布或升级运行中的 Windows。
