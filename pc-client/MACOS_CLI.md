# Mac 远程 CLI（2026-11-03）

2026-11-04 Codex TUI 回放修复：完整保留的 UTF-8／VT／DEC2026 输出片段不再被误判成缺失历史。声明 host_history_v1 的订阅者按连续 source event 回放，快照只选择可提交的状态；旧 iOS 的仅快照消费者仍保持原有展示门槛。传输恢复 Gap 在历史回放中还原为对应 core Advance，不伪造原始输出；真正裁剪／丢失与需要新的权威时钟状态的边界仍必须取新快照。

2026-11-04 安卓空白修复：Mac 正确声明 host_history_v1／host_session_catalog_v1／terminal_snapshot_v2，支持带累计游标的暂停订阅与实时回放、公开第一个启动事件、512KiB快照分块和 recipient-sealed 连续事件范围。同一 Relay 连接重订阅复用已发送密文；缓存已淘汰则以新的 Gap 序号恢复，禁止更改同一公开序号的字节。

首页CLI默认目录已由Mac按账户隔离创建，目录选择复用no-follow只读服务；创建回执仍绑定原caller/operation ID/host ID，未决请求只能查询原请求，不能静默换ID重做。

Mac arm64 已接入真实 POSIX PTY、预编译 terminal-core、设备签名 REST 和 Relay E2EE 终端通道。
在桌面“会话”页选择已检测到的 Shell 和绝对工作目录，点击“新建终端”，再从手机 CLI 页面打开。
也支持手机的 OPEN_TERMINAL 任务；服务端必须包含本轮 terminal-only Mac 的任务能力校验改动。
本轮没有部署服务端。文件浏览、下载和附件能力保持独立，不能为创建 CLI 伪报文件支持。2026-11-04 已接入远程文件，当前范围见 [Mac 文件](MACOS_FILES.md)。

- 注册只上报已接入的终端、快照、内置命令、PTY 尺寸与既有 AI 能力；未接入自定义命令编辑和远程更新；文件由独立适配库声明。
- 所有终端数据使用既有 X25519 / ChaCha20-Poly1305 recipient-specific Envelope；服务端仅见路由。
- 输入必须有当前 CONTROL / MANAGE 权限及匹配的服务端租约、sender、版本和有效期。相同 input ID 的重试返回同一执行结果，不重复写 PTY；冲突、旧序号、无法确认的写入保持拒绝。
- 输入结果是正式有序事件，其他查看设备同序号接收中性 Advance。iOS 同步修正输入 ACK 与回执应用顺序。
- 每个订阅者有独立累计 ACK 和事件／字节窗口；丢失或超出有界回放会分配新的 Gap 序号，再使用 recipient-sealed native snapshot 恢复。
- UTF-8 / VT 半包和 DEC2026 延迟期间保留最近可提交快照；该范围完成后使用新快照恢复，不把不可提交中间状态当作终端故障，也不提前发布／确认。
- UI 关闭、Relay 断线、Token 刷新和重连保留现有 PTY。停用连接、登出与完全退出停止进程组。
- 快照和操作 ID 以独立 Keychain 密钥加密后原子保存。Agent 重启恢复历史；原 RUNNING 标记为 INTERRUPTED，不按旧 PID 重新接管或结束其他进程。
- Mac 使用无桌面窗口的受管 PTY；“恢复尺寸”恢复初始 100×30 网格，不调整 Terminal.app 窗口。

独立 `terminal-core.macos.lock.json` 固定 core f51936441dea83fa2f9459386fb6c35b90102979、clean archive 和 Rust 1.97.1。
应用只消费 dylib 和许可证，打包前校验完整哈希，不从 core 仓库编译。Windows / Android / iOS 锁不变。
当前只有 osx-arm64 产物，Intel 架构未构建；缺少对应库的打包会失败，不能回退到另一平台二进制。

## 验证

`tests/TerminalRelayValidation` 在真实 macOS PTY / native core 上运行：加密 WSS、签名请求、原生快照导入、中文输入、租约拒绝、去重／冲突／旧序号、缩放、重连、退出、加密状态与篡改、UTF-8／ESC 分块和 DEC2026。
产出的 Envelope 摘要由 Go Relay 真实的 ACK / replay 规则再验收。
服务端另在隔离 PostgreSQL 17.5 中验证任务生命周期、混合文件队列和动态权限撤销。
以上是受控本机联测，不是线上服务与 iPhone 实机验收。

尚未接入独立于 Agent 进程的 SessionHost、崩溃后继续运行同一 Shell、图形／Broker、冻结历史页、会话清理和语义键鼠。
Codex 等公共 CLI 可在 Shell 中由用户启动；这不等于其结构化 AI 协议或模型账户授权已经接通。
