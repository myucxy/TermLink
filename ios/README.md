# TermLink iOS / iPadOS

原生 SwiftUI 客户端，按 Android 已有产品与 Relay 协议迁移。最低 iOS / iPadOS 17，工程使用 Xcode 16 的同步文件组。2026-10-25 已接入独立构建的 iOS terminal-core XCFramework，并通过 Xcode 27.1 的 Debug 模拟器构建与单元测试；真实服务、电脑及 iPhone / iPad 设备验收尚未完成，不能作为 Android 全功能对等的声明。逐项缺口与验证范围见 [PARITY.md](PARITY.md)。

## 已写入源码

- 系统浏览器授权、PKCE、单次刷新、设备签名、Keychain 凭据、按服务与账号隔离的加密本地状态。
- 设备列表、终端目录、创建与重开 CLI、控制租约、快捷键、中文输入组合、显式复制粘贴、结束主机进程的确认入口。
- 原生冻结历史浏览：双指拖动、上／下一屏、显式返回实时尾部；冻结画面独立于继续接收的实时输出，暂停输入和主机窗口调整，捕获在 290 秒后释放。关闭、失效和返回实时状态均重新检查租约有效性。
- Shell 集成：严格解码核心 TLSHV1 状态，打开 Shell 报告的本地 Windows 目录、显示命令记录并定位到 primary 页的非 alternate 提示符。点击定位时重新核对命令 ID 与提示符锚点，拒绝过期记录和 RIS 重置后的旧锚点。
- 连接菜单中的窗口尺寸调整（按手机窗口调整／恢复电脑窗口尺寸，复用 Android 的 `ResizePayload.restore` 字段与主机错误码文案映射）。
- 本机活动页（控制权获得／释放／回收、结束会话、历史缺口恢复、登录登出，按账号加密保留 200 条）、设置页快捷键管理（快捷输入与 Ctrl／Alt／Shift 组合键、Dock 单行／两行布局、自定义项拖动排序及持久化、多条删除原位撤销，语义与上限对齐 Android）、屏幕常亮与账号信息展示。
- 现有 AI Web 时间线与审批页面、发送／中断／恢复／分叉、模型与推理设置、授权变更确认、本机历史分页与导入、照片／相机和文件附件、电脑文件引用、待发队列、置顶／重命名／历史删除、产物下载和完整消息 Markdown 导出。
- Codex、Claude Code、OpenCode、Grok Build、ZCode 的专用图标与别名；资源由现有 Android 资源转换，许可仍随包保留。
- ZCode 与其他 Agent 统一走原生 AI 会话；已移除填 URL／官方远控网页方案，旧网页视图本地清理，旧宿主提示升级；模型可用性仍取决于电脑端。
- 电脑文件浏览、目录收藏、目录内创建终端、E2EE 下载、续传、摘要校验及服务端完成确认；下载暂停后由用户恢复。
- 当前目录／子目录可新建 AI，明确预选原电脑和目录；当前目录菜单也提供终端命令和 ZIP 下载。电脑离线或缺少 AI 能力时不自动切换到其他电脑。
- AI 附件先缓存为账号隔离的加密文件，支持失败重试和重启后的手动恢复；沿用同一附件 ID／摘要，以主机确认的偏移继续，恢复不会自动上传或发送。边界与测试见 [ATTACHMENTS.md](ATTACHMENTS.md)。
- Windows 远程升级卡片：发起后收起，成功移除，失败可查看及关闭，按设备保留任务。
- 外观、字号、设备撤销、前台 AI 状态通知、许可展示；iPad 保留侧栏与详情布局，不整合此前暂存的自由窗口设计。
- AI 新旧配置协议归一化：缺少新配置的 Codex／Claude 回退旧主机能力字段，显式新配置优先；授权方式的确认和选项校验保持默认收紧。
- 已有会话可选电脑默认模型、手动填写模型／提供方 ID，并恢复默认推理强度；保留主机能力、控制权、settings 版本和审批状态检查。仅电脑文件引用也可发送，发送回执只清理对应请求的内容，不覆盖等待期间新增的附件／引用。
- 当前服务的注册、登录帮助与找回密码、账号安全和隐私／注销网页入口，使用系统浏览器，不传应用 Token；网页完成注册后返回应用授权登录。

## 核心依赖边界

已导入 `native/terminal-core/ios/TermLinkTerminalCore.xcframework`，包含设备 arm64 与模拟器 arm64 / x86_64 切片，提供 `<TermLinkTerminalCore/termlink_terminal.h>` 模块。Xcode 工程的 Frameworks 阶段正式链接该框架并按目标自动选择切片，无需命令行覆盖 framework 搜索路径或链接参数。

应用只消费独立核心仓库构建的二进制及随产物提供的公开头文件，不将 Rust、JNI、CMake 或核心构建逻辑复制回应用仓库。iOS 使用根目录的**独立** `terminal-core.ios.lock.json`（schema 2），固定 core `f51936441dea83fa2f9459386fb6c35b90102979`、clean 源码和 Rust 1.97.1。原 `terminal-core.lock.json` 与 Windows / Android 五库保留原 revision `1c907c51b3c6652b23a7d7216ff1bb13d3410a69` 及原有字节，两个锁不能互相替代或混写来源。

所有 Debug / Release 构建都通过 macOS SDK 执行 `scripts/verify_native.swift`：拒绝缺失或脏来源、错误 revision 格式、非 iOS 条目、重复／不规范路径、符号链接、文件遗漏或篡改，并核对切片平台、架构、iOS 17.0 和头文件／模块结构。不会回退原五库锁。随应用打包 core GPL、Unicode、legacy charset 和 Rust 依赖许可；许可展示不替代分发时匹配 revision 的完整对应源码交付。

独立锁、产物导入及构建说明见 [iOS 原生依赖接入](../docs/development/IOS_NATIVE_CORE.md)。

当前应用协商的终端 profile 仍是 legacy `terminal_state_2_21_v2` / Render V4。历史 capture／viewport／anchor 和 Shell 状态桥接已实现；图形／Unicode16 profile、Render V5 UI、Broker 捕获、host effects／OSC9;4 进度、触摸鼠标／滚轮侧设置和完整硬件键鼠语义仍待迁移。核心支持某项能力不代表应用已完成对应协商、解码或 UI，不能只改能力声明或把 VT 内容当普通文本来绕过。

`TerminalPresentation` 将冻结历史画面与实时状态分离；历史画面不提供 ACK。实时事件仍先应用核心、持久化带 ack／输入仲裁 epoch 的加密快照，再推进 ACK。打开会话时本地快照仅用于预览，订阅仍从 0 开始，再由主机权威快照确认替换；有界批次与 delta 恢复需在真实链路测量后继续设计。

## 服务端源码兼容

此次增加独立 `IOS` 设备类型及迁移 `000051`，保持原有 Android / Windows 身份隔离。默认不启用 iOS 登录。

未来部署启用时，设置 `TERMLINK_IOS_OIDC_CLIENT_ID=termlink-ios`，回调固定为 `dev.termlink.ios:/oauth2redirect`；自定义 allowed parties 必须包括该客户端。内置身份提供者为它提供公共客户端授权码与刷新流程，不启用密码 grant。外部 OIDC 提供者需要单独注册同样的公共 PKCE 客户端。此轮只修改源码，未迁移数据库、修改部署或开启线上配置。

服务端本轮已补齐 Compose／安装器配置透传、授权页注册后的继续 PKCE、运行时 schema 51 和移动身份权限测试。受管旧部署须使用新版安装器／镜像升级，不能只向旧容器添加变量。具体启用步骤与验证边界见 [Server iOS 接入](../server/IOS_ACCESS.md)。客户端的文件通道子协议已修正为 `termlink.file-transfer.v2`，与服务端契约一致。

V1 设备列表与电脑加密接收者接口将 iOS 投影成既有移动端 `ANDROID` wire role，以兼容已经发布的客户端；数据库实际身份、登录 authorized party 与授权检查仍保留 `IOS`。Relay 与文件链路复用现有 `android` 移动协议角色，不开启直连。

## 后续代码与验收缺口

- 补齐 graphics／Unicode16／Broker、完整键鼠、host effects／信号／进度及平台通知音频和剪贴板策略；冻结历史与 Shell 集成仍需真实 Relay、电脑和设备回归。
- 附件重试／跨进程恢复和文件目录内新建 AI 已接入，仍需真实主机／照片／相机／设备生命周期联测；完整英文资源、附件本地预览、快捷键内置项标签改名和手动分配 Dock 行仍待迁移。其他已写入源码的功能不能据此计为真实端到端验收通过。
- 账号网页入口已接入；授权页内的注册续接已补齐服务端支持，但独立注册入口完成后仍需返回应用授权登录。推送通知服务端集成、App Store 图标与签名分发配置尚未完成。
- 原生核心已接入，不能据此声称真实电脑 CLI、iPhone / iPad 兼容或 Android 全功能对等已验收。

2026-10-25 最新代码在 iPhone 18 Pro 和 iPad Pro 13-inch (M5) / iOS 27.0 / arm64 模拟器中**各 91 项测试通过（88 项逻辑／原生测试 + 3 项 UI 测试），0 失败、0 跳过**。覆盖实际 native ABI、中文／输入／快照、冻结 viewport／Shell 锚点、Keychain、E2EE、模型配置、发送回执、网络契约，并新增加密附件缓存／恢复／丢失回执重试、归属限制和目录预选回归。独立产物校验的 **16 项防篡改测试此前全部通过**，本轮核心产物未变；设备 arm64 无签名 Release 编译通过。具体验证范围见 [PARITY.md](PARITY.md)。

`TermLinkUITests/` 实际点击四入口导航、核心状态／许可正文、快捷键新增／重启恢复／删除撤销。使用专用无账号模拟器及当前进程的 `-server https://ui-smoke.invalid`，不会发起授权或接入真实主机。快捷键“保存”位于编辑工具栏，避免 iPad 键盘收起时表单移动造成误触，快捷输入禁用自动大写／纠正。真实 Relay、电脑和真机测试仍未完成，服务端数据库身份集成仍需独立环境。

AI 页面资源与第三方许可证以 Xcode folder reference 直接打包现有 `android/app/src/main/assets/ai-web` 和 `licenses`；重新生成图标可使用 `scripts/sync_agent_assets.py`，不需要网络或新依赖。不要将 Xcode 用户目录、签名身份、账号 Token 或 `Local.xcconfig` 提交。
