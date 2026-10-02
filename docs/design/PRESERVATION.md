# V3 原有功能保留约束

用户明确要求：新增 AI 栏目的同时，保证原有功能不丢弃。本次仅修改设计文档、图片和浏览器原型，没有修改 Android、Windows 或服务端产品代码。

后续补充约束：**CLI 页面的布局和内容完全不改；紧凑化仅用于新增按钮和 AI 功能。** 原终端页签高度、标题、终端区域、Dock、鼠标滚轮、输入行为及原菜单不能按 AI 设计稿重排。V3 使用已有 CLI 截图，取代早期重绘示意。

## 导航与数据

- “会话”仅更改用户可见名称为“CLI”。优先保留现有内部路由标识、旧版持久化键、打开页签、选择状态与偏好读取，避免仅因重命名触发迁移或丢失已有会话。
- AI 是新增目的地，持有独立的打开列表、选择状态、草稿、阅读位置和未决请求。不能与 CLI 共用会话类型不明确的选择 ID、控制状态或输入队列。
- 新增显式会话类型。旧版已知终端记录仍按终端处理；无法识别的新类型显示不兼容提示，不能解析成终端输入。
- 原设备目录继续包含该电脑的全部会话，新增类型标识并选择对应入口。不能为了简化 AI 列表而删掉设备页的完整目录。
- 页面关闭、停止订阅、结束进程、删除失效记录继续是不同操作。切换栏目不能隐式关闭进程、释放仍应持有的资源或重放输入。

## 保留对照

| 现有能力 | V3 归属及约束 | 代码参照 |
|---|---|---|
| 已打开终端页签、选择、关闭、长按操作、窄屏和宽屏布局 | CLI 复用原实现，旧页签和持久化状态保留 | `ProductActivity.kt`：`MobileSessionContent` / `MobileSessionTab` / `saveProductSessionTabs` |
| 终端渲染、光标、历史回滚、加载/清理历史、重试打开 | CLI 保留自有 terminal-core/JNI/View 与原历史链路，AI 事件不进入终端解析 | `ProductActivity.kt`：`SessionDetail` / `TerminalSurface`；`android/core-session` |
| 输入法唤起/收回、粘贴确认、组合键、输入顺序与 ACK | 原输入入口和确认流程继续使用，不能经 AI 消息接口转发 | `ProductActivity.kt`：`TerminalActionBar` / `PasteConfirmationDialog` |
| Dock 单行/双行、横向滚动、自定义文字/特殊键/组合键、排序 | 完整保留布局与已保存配置，不因新版原型按钮较少而删减 | `ProductActivity.kt`：`TerminalActionBar` / `TerminalShortcutSettings` / 编辑器 |
| 触摸作为鼠标、鼠标模式判定、单指历史滑动、滚轮及左右选择 | CLI 继续遵守原触摸策略，非鼠标模式不能被 AI 手势影响 | `SessionInputMenu.kt` / `TerminalSurface` |
| 按手机窗口调整、恢复电脑尺寸、字体/Dock/收起输入法后的行数 | CLI 保留原计算与窗口控制，AI 页不调用终端 resize | `SessionWindowSizeMenu.kt` |
| 获取/释放控制权、只读状态、云朵状态 | CLI 原控制租约与菜单保留；AI 使用自己的会话上下文 | `ProductActivity.kt`：`SessionControlMenu` 相关调用、`SessionDetail` |
| LAN/NAT、自动直连/仅代理、强制检测结果与失败原因 | 原策略、回退和按策略显隐保留，新导航不改变传输决策 | `ProductActivity.kt`：网络菜单；`android/core-network` |
| 设备刷新、目录展开、在线/真实会话状态、长按设备 | 设备页保留原处理，增加 AI 入口；超时不伪造 Windows 在线状态 | `DevicesPage` / `PcRow` / `ProductionSessionCatalog.kt` |
| 单会话终止、失效记录删除、原生批量清理 | 原按钮、确认、Windows 校验与逐项结果继续保留；新增 AI 类型需单独支持才可操作 | `InactiveSessionCleanup.kt` / `ProductActivity.kt` 对应确认框 |
| 文件浏览、搜索、传输、下载中心、从目录启动终端 | 原设备/文件/下载入口保留；新增 AI 工作目录选择复用可用能力 | `RemoteFileBrowserPage.kt` / `RemoteFileDownloader.kt` / `ProductionFileTransferClient.kt` |
| 账号、登录失效、服务地址、身份校验、退出登录 | 原认证行为保留，并清理新增 AI 的敏感会话视图 | `SettingsPage` / `OidcSessionStore.kt` / `ProductionProductRepository.kt` |
| 主题、屏幕常亮、终端字体导入/字号/配色、快捷键、关于与更新 | 所有设置及保存值保留。新增 AI 偏好不能覆盖终端偏好 | `SettingsPage` / `TerminalDisplaySettings` / `TerminalShortcutSettings` / `SettingsAboutCard` |
| E2EE、鉴权、会话边界、重连恢复和后台/前台行为 | 原传输与安全边界保持，新消息需能力协商；不能以 UI 改版为由绕过 | `ProductionRealtimeClient` / `ProductionRealtimeMultiplexer` / `RemoteTerminalSession` |

上述 Android 产品文件位于 `android/app/src/main/kotlin/dev/termlink/product/`，其他模块路径按表内注明。该表是本轮设计涉及边界的保留清单，不意味着未列出的原功能可以删除。

## 后续实现验收（尚未执行产品测试）

1. 升级前准备 CLI 页签、自定义字体、Dock、鼠标/滚轮偏好和登录状态；升级后验证全部保留。
2. 已有终端打开 Codex / Claude，确认仍归 CLI，显示和输入不变。
3. 连续切换设备、CLI、AI、设置，验证 CLI 不重启、不重放输入，AI 不重复创建；分别保留阅读与输入状态。
4. 回归输入法、单/双行 Dock、粘贴、组合键、鼠标开关、滚轮两侧、历史和尺寸调整。
5. 回归获取/释放控制权、只读、自动直连/仅代理、LAN/NAT 检测及回退。
6. 回归设备刷新、文件/下载、单个结束/删除与批量清理，确认不会操作错误类型或仍活跃的会话。
7. 模拟器与 Windows 联合检查电脑重启、断网、认证失效和后台恢复；不把设计原型检查视为产品联调通过。
8. 验证 AI 关闭视图不停止电脑任务，AI 结束任务不影响 CLI，过期审批不能执行。
9. 同设备、同主题、同字体、同 Dock 配置、同屏幕与输入法状态下，对比升级前后的 CLI 内容区域，验证布局与内容呈现保持一致；底部四栏导航单独验收。
10. 新 AI 页面验证内容区占比、字号放大、长标签、窄屏、横屏及触控范围，不能为追求紧凑隐藏必要信息或缩小既有 CLI 控件。

浏览器原型仅演示部分交互。菜单中的网络、删除和窗口操作均不会对真实设备执行。
