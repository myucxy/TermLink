# Android → iOS 功能对齐记录

核对日期：2026-10-25。应用开发起点为 `19d8e3c`；当前改动尚未提交。iOS 独立锁固定 clean core `f519364` / Rust 1.97.1，Windows / Android 原锁和五库继续保留 `1c907c5` 的来源与字节。以下“已实现”不代表已通过真实电脑／手机联测，当前仍按 Relay Only 开发。

原生核心已正式导入 `native/terminal-core/ios/TermLinkTerminalCore.xcframework`，通过根目录独立 `terminal-core.ios.lock.json` 校验。Xcode 工程的 Frameworks 阶段正式链接，自动选取设备 arm64 或模拟器 arm64/x86_64 切片；所有 Debug／Release 构建均使用 macOS SDK 执行 Swift 校验器。[iOS 原生依赖接入](../docs/development/IOS_NATIVE_CORE.md)说明独立锁与导入方式；独立 core 的构建流程、837 项 Rust 测试和 C／Objective-C／Swift 模块及原生 smoke 记录见 [core iOS 构建说明](../../terminal-core/docs/IOS_BUILD.md)。没有将原五库锁改写为 iOS revision。

## 本轮已继续开发

- 修复首次 Xcode 构建暴露的缺失大括号、属性包装器多变量声明、缺少 Combine 导入、WebSocket ping 回调适配和密钥派生表达式类型检查问题；不改变协议或密钥派生字节。
- 修正测试 target 缺失 `PRODUCT_NAME` 导致 `.xctest` 输出冲突的问题。
- AI 配置兼容 Android 的旧主机：仅缺少新配置的 Codex/Claude 可从 catalog 的 `modelListing/configurationModes` 或会话的 `configure/modes` 回退。显式新配置优先；未知 Agent 不推断能力；授权选项默认需要确认。
- 自定义终端快捷键支持编辑模式拖动排序、持久化顺序、编辑保留位置、多条删除原位撤销；恢复存储时清理损坏/重复/超量项。修复 Shift-only 归一化后重启丢失的问题。
- 设置页提供注册、登录帮助与找回密码、账号安全和账号隐私/注销的系统浏览器入口。复用当前服务 `/register`、`/login`、`/account/identity`、`/account?section=privacy` 网页，不传应用 Token；注册完成后返回应用授权登录。找回密码仍由服务端登录页面及身份提供者提供。
- 接入固定来源的原生 XCFramework 和独立 iOS 锁；校验切片、逐文件哈希／大小、完整清单、路径与符号链接，原 Windows／Android 产物不变。core GPL、Unicode、legacy charset 与 Rust 依赖许可随包展示。
- 增加 native `history_begin`／`viewport`／`resolve_anchor`／`release` 桥接。`TerminalPresentation` 显示不可变冻结画面，实时核心仍继续应用、持久化快照再 ACK；双指拖动和上／下一屏浏览，显式返回实时尾部，290 秒释放捕获。冻结时禁输入和主机尺寸调整，返回时重新检查租约，续期失败或撤销后不恢复失效权限。
- 增加 TLSHV1 Shell 状态严格解码、打开本地 Windows 目录、命令记录和提示符定位。定位仅支持 primary 非 alternate 页，点击时重新读取并匹配命令 ID 与提示符；RIS 重置后旧 line ID 不能误定位到重用的行。
- 本轮复核修正仅包含电脑文件引用时发送被无声跳过的问题；引用仍受数量、UTF-8 长度和 NUL 校验，发送与待确认重试仅清理请求中已发送的附件／引用，保留回执等待期间新准备的内容。
- 模型设置补齐电脑默认模型、手动模型／提供方 ID（即使主机没有列表）、恢复默认推理强度。`nil` 不发送 effort 字段，空串明确恢复默认；继续检查主机能力、settings/control 版本、只读／运行／审批状态，ZCode 不允许清空模型和提供方。
- 修正文件 WebSocket 子协议为服务端要求的 `termlink.file-transfer.v2`，统一构建 WSS 请求并保留设备签名及原路径。
- 服务端补齐 iOS 配置透传、注册继续 PKCE、外部 Keycloak 默认主题注册入口及 schema 51 运行时版本；已添加签名 HTTP／WebSocket 移动角色测试，在线启用和集成边界见 [服务端 iOS 接入](../server/IOS_ACCESS.md)。
- 继续补齐文件当前目录／子目录新建 AI，预选原电脑／目录并重新检查在线与能力；当前目录也可打开终端或下载 ZIP。附件新增账号加密缓存、失败重试和重启后手动恢复，原临时文件不再是续传前提；重试不变更主机／会话／控制版本绑定，发送和恢复均需明确操作。实现与验证边界见 [附件与目录入口](ATTACHMENTS.md)。

## 尚欠缺的功能与依赖

| 优先级 | 功能 | iOS 当前状态 | 对齐要求/Android 参考 |
| --- | --- | --- | --- |
| P0 | iOS 登录与真实链路验收 | PKCE、Keychain、IOS 身份及配置／注册续接／schema 接线已补齐，签名 HTTP／WebSocket 测试通过；需显式 iOS client ID | 后续部署包含改动的新 Server 和 migration `000051`，按接入文档启用，再测真实注册／授权／刷新／设备撤销、Relay、多设备；本轮未部署。 |
| P1 | 图形、Unicode16、Broker 捕获 | 仅 legacy `terminal_state_2_21_v2`、Render V4；拒绝其他 profile | 对照 Android `NativeTerminalHandle.kt`、`RenderFrameV5Decoder.kt`；接入 core graphics API、Render V5 与对应快照，不能只改能力声明。 |
| P1 | 冻结历史的扩展与设备回归 | legacy capture／viewport／anchor 已接入；冻结显示与实时应用／ACK 分离，支持双指／分页浏览和返回尾部 | 需真实主机／设备验证长历史、窗口变化、重连／gap、切换前后台与捕获过期；尚未迁移 Android V5 冻结历史的分页窗口。 |
| P1 | 完整硬件键鼠/触摸滚轮 | 只有 Input V1、方向/Esc/Ctrl+C 等基本键盘映射 | 对照 `NativeTerminalHandle.kt` 的鼠标、Win32/Kitty 输入编码；新增桥接后迁移 UIKit 硬件键盘、指针和滚轮策略。 |
| P1 | 主机信号与平台副作用 | Shell 状态／目录／命令定位已实现；仍缺 host effects、OSC9;4 进度、铃声／音频与远端剪贴板策略 | 对照 `TerminalSignalMenu.kt`、`TerminalPlatformActions.kt`；新增桥接和去重回执，历史恢复不重放副作用。Shell 功能另需真实 Windows／设备回归。 |
| P1 | AI 附件真实生命周期验收 | 加密缓存、状态持久化、失败重试、重启手动恢复已实现；主机偏移／摘要／归属检查有回归测试 | 仍需真实 Agent／Relay、相册／相机和前后台回归；主机过期／重启或控制版本变更后不重绑定，附件本地预览尚缺。 |
| P1 | 后台提醒与通知审批 | 仅前台连接期间生成本地状态提醒，后台断开 Relay；没有 APNs 或通知审批动作 | 对照 `AiNotifications.kt`；需服务端推送集成和设备授权，通知不能带对话正文；审批前重新确认控制权与最新待办。 |
| P2 | 文件页衔接 AI 的主机验收 | 当前目录菜单、子目录新建 AI、原电脑／目录预选已实现 | 需在线主机验证创建成功后的返回、目录权限、电脑离线和取消流程；不把纯选择模型测试当作已创建远端会话。 |
| P2 | 快捷键完整编排 | 本轮已补自定义项拖动排序；仍自动均衡分行 | 对照 Android `ProductActivity.kt` 的 Dock 行选择、内置项顺序及名称编辑。 |
| P2 | 原生界面英文资源 | AI Web 已复用共享资源；原生 SwiftUI 多为硬编码中文 | 对照 Android `values/strings.xml` 与 `values-zh-rCN/strings.xml`；迁移 String Catalog 并验证中英长文、iPhone/iPad 布局。 |
| P2 | 商店资源/签名分发 | 尚无完整 App Store 图标、签名和分发验收 | 本地签名资料不可提交。先完成核心、真机和账号全流程验收，再准备分发。 |
| P2 | 快照提交与断点恢复性能 | 本地快照仅用于预览，订阅从 0 开始；每个实时事件持久化完整快照后 ACK，冻结历史不改变该链路 | 对照 `TerminalDeltaRestore.kt`；在真实链路测量后再改有界批次／delta，保留“成功应用且持久化后才 ACK”的约束。 |

## 已有源码但尚未通过真实服务/设备验收

设备列表与目录、CLI 创建／重开／控制租约／结束确认、窗口尺寸调整、冻结历史手势与提示符定位、AI 对话／审批／模型／推理／授权切换、AI 原生历史分页／导入／删除、照片／相机／电脑文件引用、待发队列、置顶／重命名、Markdown／产物导出、ZCode 官方远控、E2EE 文件下载／续传／摘要校验、电脑远程升级卡片、活动页、iPad 侧栏布局。

这些功能需要可配置的测试服务和主机完成端到端验证。源码覆盖、单元测试与真机验收分别记录，不能用模拟器单测代替真实 Relay/E2EE/控制权测试。

## 本轮验证

从 NAS 复制 iOS 源码、共享 AI Web／许可资源、独立锁与原生产物到 Mac 本地临时目录构建，没有在网络共享上编译，未复制正式凭据或签名。

- 工具：Xcode 27.1 (27A9275)，Swift 6.4，Debug。首次无签名编译用于排错；最终测试使用模拟器 ad-hoc 签名（`CODE_SIGN_IDENTITY=-`），不使用正式签名资料。无签名应用会因钥匙串不可访问而在启动时提示错误，因此不能用无签名运行替代签名后的运行验收。
- 正式工程通用 iOS Simulator 构建通过；iPhone 18 Pro 和 iPad Pro 13-inch (M5) / iOS 27.0 / arm64 **各 48 项测试通过，均为 0 失败、0 跳过**。包括 Keychain 真正读写／更新／删除、共享 E2EE、协议拒绝、帧 CRC、队列归属、摘要、窗口尺寸／快照、快捷键、AI 配置、账号网页目的地址，以及实际 native ABI／解析／输入／快照、不可变历史 viewport／锚点、Shell 状态／目录解析和 RIS 重置后旧命令锚点拒绝。iPad 结果保存在本机 `ImportTests-iPad.xcresult`。
- 独立 iOS 产物校验器的 **16 项防篡改测试全部通过**：缺锁／旧锁不回退、错误来源／dirty、非 iOS 条目、路径、重复／遗漏／多余文件、哈希／大小、各层符号链接、错误切片及模块契约。
- 带 ad-hoc 签名的应用已在 iPhone 18 Pro 和 iPad Pro 13-inch (M5) / iOS 27.0 模拟器安装、启动并查看截图：手机标签栏与 iPad 侧栏/详情正常显示，没有钥匙串错误。仅验证未登录启动页，未进行真实账号授权、电脑连接或终端验收。
- core 在干净 `f519364` 上通过 **837 项 Rust 测试**和三个 Apple target 的 Release 构建；C 头／Objective-C／Swift 模块链接与 arm64 模拟器 native smoke 均通过，覆盖 legacy 和 graphics／Unicode16 核心路径。graphics smoke 通过不代表 iOS 应用已实现 V5 UI 或协议协商。
- 正式工程在加入原生依赖和历史／Shell 功能后，设备 arm64 **无签名 Release 构建通过**，未使用命令行 framework／header 路径或链接参数覆盖；没有安装到设备或执行发布。原生依赖是所有 Debug／Release 构建的必需输入，已移除缺核心时继续构建的分支。设备目标编译成功不代表真机运行验收通过。
- 后续模拟器实际交互测试增加独立 `TermLinkUITests` target：iPhone／iPad **各 51 项通过（原 48 项 + 3 项 UI），0 失败、0 跳过**。UI 覆盖手机标签栏／平板侧栏导航、CLI／AI 空态、设置核心状态和许可正文、快捷键新增／重启保留／删除撤销；使用专用无账号模拟器与进程级隔离服务地址，不执行真实登录。快捷键保存按钮移入编辑工具栏，解决 iPad 键盘关闭时表单位移造成的保存点击问题；快捷输入禁用自动大写和自动纠正。报告、可安装模拟器 ZIP 与截图保存于本地忽略目录 `artifacts/ios-simulator-20261025/`。
- 最新服务端接入与 AI 复核后，iPhone／iPad **各 73 项通过（70 项逻辑／原生 + 3 项 UI），0 失败、0 跳过**。新增电脑引用独立发送、回执期间新附件保护、默认／手动模型及 effort 省略／重置、WebSocket 子协议与签名请求保留测试。服务端 **1102 项通过、0 失败、57 项跳过**（含子测试），相关包 race 与 Go vet 通过，Linux amd64／arm64 编译成功；跳过数据库或外部环境用例不计为通过。源码与接口支持已补齐，尚未部署在线环境或验证真实 OIDC／PostgreSQL／电脑端到端，记录见 [服务端 iOS 接入](../server/IOS_ACCESS.md)。
- 随后附件与目录功能继续迁移，最终 iPhone／iPad **各 91 项通过（88 项逻辑／原生 + 3 项 UI），0 失败、0 跳过**，设备 arm64 无签名 Release 编译通过。新增 18 项缓存、恢复、故障回执和电脑／目录资格用例；UI 同时验证无电脑时新建不可执行。模拟协议 fixture 与选择模型测试不替代真实上传、照片权限或目录创建联测。记录和更新模拟器包在 `artifacts/ios-feature-recovery-20261025/`。
- 固定 Rust 1.97.1 工具链按官方来源与哈希校验安装在 Mac 独立目录，仅调整进程环境；详情见 core 构建说明。本轮未提交／推送 Git、迁移数据库、启用线上 iOS 配置或正式发布。

在 Mac 本地应用仓库副本中可复现测试（替换为本机可用模拟器；不要从 NAS 直接编译）：

```sh
xcodebuild -project ios/TermLink.xcodeproj -scheme TermLink \
  -configuration Debug \
  -destination 'platform=iOS Simulator,name=iPhone 18 Pro,OS=27.0' \
  -parallel-testing-enabled NO -derivedDataPath /tmp/termlink-ios-derived \
  CODE_SIGNING_ALLOWED=YES CODE_SIGN_IDENTITY=- test
```

## 下一步实施顺序

1. 用测试服务、真实主机和 iPhone／iPad 验证 legacy CLI 的附着／输入、快照／ACK／gap、控制租约、冻结历史及 Shell 定位；保留单测与端到端证据区分。
2. 继续迁移 graphics／Unicode16 profile、Render V5 UI 与 Broker 捕获，再扩展图形历史分页；不能以核心 API 存在替代应用协议与渲染实现。
3. 补齐 semantic input、host effects／signals／progress 与平台策略，并迁移附件跨进程恢复、文件页 AI 入口、APNs／通知审批及英文资源。
4. 测量真实 Relay 重连、弱网、多设备、前后台切换及大历史内存／持久化开销后，再推进有界批次与 delta 恢复。
