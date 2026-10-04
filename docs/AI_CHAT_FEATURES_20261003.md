# AI 会话操作与内容能力（DEV）

本轮实现 Android / Windows AI 功能；CLI 页面布局、Dock 和终端输入不改。没有提交、正式发布或替换正在运行的 HOME-PC Agent。DEV 基线经 AppShip stable 接口核验：Android / Windows 均为 0.2.71 / 127。

## 操作

- 输入卡底部：附件、模型、执行授权、发送 / 停止。模型和授权只在空闲且无待审批时修改。新建时也可选授权方式，默认按需确认。
- Codex：模型与 sandbox 配置用于下一轮；ask 映射 on-request + workspaceWrite，read-only 映射 on-request + readOnly。控制权和 Agent 执行授权分离。
- Claude：通过原生 set_model / set_permission_mode 等待确认；提供 default / plan / acceptEdits。计划模式不是 OS 只读沙箱；自动编辑不等于无条件授权所有工具。未提供 bypassPermissions / 完全访问入口。
- 每次 configure 携带控制版本、设置版本和操作 ID；只改一项配置，避免半成功。超时不猜测生效，Claude 配置未知时终止该进程并显示需恢复。每条消息记录当轮 model/mode。
- ZCode native 暂不提供未验证的配置切换。ZCode desktop_remote 的模型和授权由网页自行管理；WebView 提供系统文件选择回调，单选 / 多选按网页请求，关闭、离线或销毁时取消回调，不开放任意 file:// 访问。

## 附件与电脑文件

Android 系统文件选择器 / 图片 / 拍照，私有缓存内计算大小与 SHA-256。支持预览、移除、失败重试、只发附件。电脑文件通过独立文件浏览器选择；Codex 使用原生 mention，Claude / ZCode 传递明确的本地文件路径。

本轮对原方案做了简化：复用 **已有 AI E2EE 请求通道** 传输独立的 attachment.begin/chunk/commit/remove 帧，每块最多 24 KiB（二进制经 Base64 后 32 KiB），不将整份文件塞进 send。现有文件下载路由不改，服务端无需新增上传表或明文存储。仍使用现有 AI relay 通道，不宣称 AI 新增了 LAN/NAT。

Windows 验证 host、controller/version、上传 ID、原始文件名、大小、摘要、连续偏移、重复块内容，落盘后 ACK，完成后原子改名。附件不能跨上传者或跨会话引用。路径从 Windows 会话目录派生，拒绝 reparse point，不接受手机指定落盘路径。引用时重新校验摘要。

限制：5 个附件 / 次，20 MiB / 文件，50 MiB / 次；Claude 原生图片最多 5 MiB / 张；单会话本次进程内已登记附件总计最多 200 MiB。未发送附件闲置 30 分钟后在下一次附件操作清理，结束会话清理未发送文件；删除上传 ID 留取消墓碑防止迟到 begin 重建。已发送附件保留供 Agent 原生恢复引用。Windows 重启后，手机未发送附件需重新上传；不自动删除工作目录或已发送文件。

PNG/JPEG/GIF/WebP 进行文件签名核验后走 Codex localImage / Claude image source；其他文件作为路径引用。支持图片输入协议不代表当前模型支持视觉。

## 历史、恢复与交互

- Windows 以 server/account/pc 派生隔离目录，用 DPAPI CurrentUser 加密，临时文件 + 原子替换存储。保存原生 thread/session ID 和 transcript，不保存待审批授权。
- 重启目录显示 suspended，用户明确“恢复会话”后使用 Codex thread/resume 或 Claude --resume。旧进程中断后恢复也递增控制版本，避免旧请求作用于新进程。安装路径不从历史恢复，按本机当前检测结果重新查找 Agent。
- 时间线最近窗口 + 向前分页；长消息保留全文，列表只给预览，完整内容通过 message 分段请求打开。当前上限 10,000 条 / 12 Mi 字符、单条 1 Mi 字符，超限明确标记保留边界。不是无限历史。
- Codex 通过 thread/fork 从当前上下文创建分支；分支共享原工作目录，不承诺文件隔离。编辑重发把内容放回草稿，由用户再发，不篡改原消息或自动重放旧操作。
- 发送状态：已接收 / 已完成 / 失败 / 结果待确认；失联重试保持相同操作 ID。手机队列在空闲、控制版本与 host 均一致时发出；失败暂停该项，需取回处理。队列目前存在内存，进程重建不自动执行旧队列。未知回执仍沿用既有 Keystore 持久请求记录。
- Codex 支持当前 turn 的 steer；Claude 仅显示其 initialize 报告的命令补全；不伪造所有 Agent 通用的 `/` 命令。
- 模型报告的 token / 上下文窗口 / Claude cost 才显示，不按价格表推测费用。整理上下文事件按原生事件显示。
- Diff 使用 Codex reported diff；其他 Agent 可查看其文件变更工具记录。引用回复或工具内容到草稿、选择文本复制、重命名、搜索、置顶、批量关闭手机视图、完整 Markdown 导出与系统分享。
- 已停止的会话可经确认删除 TermLink 电脑记录，释放 64 条目录容量；工作目录、附件与 Agent 原生历史保留，不允许删除运行中的会话记录。
- 后台提醒由 Activity 生命周期启动独立轮询，通知不含提示词、回复或路径。用户选择开启并授予通知权限。它是进程存活期间的 best-effort 提醒，**不是系统杀进程后的可靠推送**；没有引入常驻服务或服务端读取聊天内容。

## 验证记录

- Windows AiAgentValidation：原适配器 / workspace / models / dedup 回归，以及配置 CAS、旧控制权、授权模式、上传重放 / 错序 / 摘要 / 大小、只附件消息、DPAPI、分页 / 长消息、恢复与分支验证通过。
- Android app 单元测试（349 项）及 DEV APK 编译通过；新增设置未知回执重复查询、历史不被新轮询覆盖、待发队列不跨控制版本、旧端能力回退测试，以及失效的其他会话不能删除当前选中会话状态的回归。
- 原 ProductActivity 从 SessionsPage 起的 CLI 布局及输入代码与 HEAD 比较一致。
- 真 Codex：只读配置，图片颜色识别，原生进程重启后上下文恢复通过。
- 真 Claude：plan、默认 / 指定模型切换，文字任务和原生进程重启后上下文恢复通过。当前 glm-5.3 对红色测试图回答为其他颜色，**识图未通过**，不能作为图像效果验收。
- emulator-5556 + 独立 Windows 协议桥：实际模型下拉、授权确认、系统文件选择、上传就绪、只附件发送、Markdown 导出、生成中排队和后续仅一次发送通过。隔离桥不替代公网 E2EE 联调。
- 模拟器切后台后：观察到 running → idle，并实际生成本应用系统通知；随后模拟电脑离线，聊天正文、输入栏与摘要隐藏。日志 `TermLinkAiAttention` 只在 DEV 输出状态，不记录提示词、路径或会话 ID。
- ZCode 桌面网站实际附件上传、相机硬件、应用被系统杀死后的通知均未宣称已实测通过。

DEV 输出：`artifacts/validation/ai-features-android-dev-0.2.71-127.apk`；Windows 完整暂存目录 `artifacts/validation/ai-features-windows-dev-20261003/`。当前运行的 `ai-joint-windows` Agent 承载用户会话，未强制重启。正式发布前应升级新 Windows DEV 后完成真实公网端到端联调。

最终 APK SHA-256：`2438B2B51DBE6E8FF3E393888FE51CAAB96A12B2302C9DF781857678B21E0754`；DEV signer SHA-256：`b0bcabe365a44108af2101fa3b34899dfeed5efef306639a004097060871d343`。已覆盖安装 emulator-5556，保留原登录，打开 ProductActivity，测试 loopback 端口反向映射已撤销。Windows Desktop / Agent / SessionHost 文件版本均为 `0.2.71.127`。构建源码为当前未提交工作区，不是正式 release revision；不上传 AppShip。

行为参考沿用 AI_COMPOSER_OPTIONS_PLAN.md / AI_MOBILE_CLIENT_RESEARCH_20261003.md 中记录的固定来源。未复制 HAPI AGPL 源码，未新增第三方依赖。
