# TermLink iOS / iPadOS

原生 SwiftUI 客户端源码，按 Android 已有产品与 Relay 协议迁移。最低 iOS / iPadOS 17，工程使用 Xcode 16 的同步文件组。此目录尚未编译、运行测试或进行设备验收，不能作为已完成 Android 全功能复刻的声明。

## 已写入源码

- 系统浏览器授权、PKCE、单次刷新、设备签名、Keychain 凭据、按服务与账号隔离的加密本地状态。
- 设备列表、终端目录、创建与重开 CLI、控制租约、快捷键、中文输入组合、显式复制粘贴、结束主机进程的确认入口。
- 连接菜单中的窗口尺寸调整（按手机窗口调整／恢复电脑窗口尺寸，复用 Android 的 `ResizePayload.restore` 字段与主机错误码文案映射）。
- 本机活动页（控制权获得／释放／回收、结束会话、历史缺口恢复、登录登出，按账号加密保留 200 条）、设置页快捷键管理（快捷输入与 Ctrl／Alt／Shift 组合键、Dock 单行／两行布局，语义与上限对齐 Android）、屏幕常亮与账号信息展示。
- 现有 AI Web 时间线与审批页面、发送／中断／恢复／分叉、模型与推理设置、授权变更确认、本机历史分页与导入、照片／相机和文件附件、电脑文件引用、待发队列、置顶／重命名／历史删除、产物下载和完整消息 Markdown 导出。
- Codex、Claude Code、OpenCode、Grok Build、ZCode 的专用图标与别名；资源由现有 Android 资源转换，许可仍随包保留。
- ZCode 官方桌面远控网页，限定 HTTPS 官方地址，不注入应用原生桥或账号 Token。
- 电脑文件浏览、目录收藏、目录内创建终端、E2EE 下载、续传、摘要校验及服务端完成确认；下载暂停后由用户恢复。
- Windows 远程升级卡片：发起后收起，成功移除，失败可查看及关闭，按设备保留任务。
- 外观、字号、设备撤销、前台 AI 状态通知、许可展示；iPad 保留侧栏与详情布局，不整合此前暂存的自由窗口设计。

## 核心依赖边界

本仓库目前的 `terminal-core.lock.json` 只有 Windows / Android 产物，**没有 iOS XCFramework**。Debug 在缺少核心时显示明确不可用原因；不能执行真正的终端解析或输入。Release 在缺少核心时拒绝编译。

应用只消费独立核心仓库发布的 `TermLinkTerminalCore.xcframework`，不得复制 Rust、头文件源码或 CMake 回本仓库。将来接入时，需要将框架中所有文件纳入现有锁文件的 `platform: "ios"` 记录，并验证其 revision、clean 状态、大小和 SHA-256；`scripts/verify_native.swift` 为该校验的源码，当前锁文件会被拒绝。框架应提供设备 arm64 与模拟器 arm64/x86_64 切片及 `<TermLinkTerminalCore/termlink_terminal.h>` 模块，然后在 Xcode 的 Frameworks 中链接，并配置对应搜索路径。

当前终端绑定仅实现 legacy `terminal_state_2_21_v2` / render-frame V4。图形／Unicode16、Broker 捕获、冻结历史与浏览锚点、Shell 集成（打开目录／命令定位）、信号与 OSC9;4 进度、触摸鼠标／滚轮侧设置、完整硬件键鼠语义、远程通知音频／剪贴板策略尚未达到 Android 对等能力；这些能力的 Android 实现依赖 native 核心暴露的 hostSignals、shellState、鼠标编码与历史页渲染 API，legacy 桥接没有对应入口，不能通过伪造能力声明或把 VT 内容当普通文本来绕过。逐事件持久化快照已升级为带 ack／输入仲裁 epoch 的加密信封，打开会话时先导入本地快照即时显示，再由主机权威快照确认替换；恢复入口仅为显示预览，订阅恢复点仍从 0 开始，避免主机序列回退时死锁，ACK 正确性优先，后续需要在真实 iOS 核心接入后测量并改为有界批次提交。

## 服务端源码兼容

此次增加独立 `IOS` 设备类型及迁移 `000051`，保持原有 Android / Windows 身份隔离。默认不启用 iOS 登录。

未来部署启用时，设置 `TERMLINK_IOS_OIDC_CLIENT_ID=termlink-ios`，回调固定为 `dev.termlink.ios:/oauth2redirect`；自定义 allowed parties 必须包括该客户端。内置身份提供者为它提供公共客户端授权码与刷新流程，不启用密码 grant。外部 OIDC 提供者需要单独注册同样的公共 PKCE 客户端。此轮只修改源码，未迁移数据库、修改部署或开启线上配置。

V1 设备列表与电脑加密接收者接口将 iOS 投影成既有移动端 `ANDROID` wire role，以兼容已经发布的客户端；数据库实际身份、登录 authorized party 与授权检查仍保留 `IOS`。Relay 与文件链路复用现有 `android` 移动协议角色，不开启直连。

## 后续代码与验收缺口

- 补齐上述原生核心能力；终端本地快照恢复入口已完成显示预览级（本地信封导入 + 主机权威快照确认），ack 级断点续传待真实核心接入后评估。
- Agent 配置的旧协议兼容分支、附件跨进程恢复、完整英文资源仍需继续迁移；电脑文件引用、历史删除、照片／相机、待发队列、重命名／置顶、新建会话配置、窗口尺寸调整、活动页与快捷键管理已写入源码，但均未经设备验证。快捷键暂不支持 Android 的拖拽排序与内置项标签改名，自定义项按自动均衡分配 Dock 行。
- 账号注册／找回／删除等已有网页流程的系统授权入口、推送通知服务端集成、App Store 图标与签名分发配置尚未完成。
- 真正接入原生核心前，不能声称 CLI 已可用或 iPhone / iPad 已通过兼容测试。

`TermLinkTests/` 和服务端新增测试覆盖共享 E2EE 向量、协议拒绝条件、Agent 图标及打包资源、远控地址、终端帧索引与 CRC、队列归属／恢复保护、文件摘要边界、窗口尺寸错误码映射、快照信封、快捷键归一化与容量上限及 iOS 身份隔离。本轮按要求只编写测试源码，未执行。

AI 页面资源与第三方许可证以 Xcode folder reference 直接打包现有 `android/app/src/main/assets/ai-web` 和 `licenses`；重新生成图标可使用 `scripts/sync_agent_assets.py`，不需要网络或新依赖。不要将 Xcode 用户目录、签名身份、账号 Token 或 `Local.xcconfig` 提交。
