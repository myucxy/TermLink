# AI 会话内容呈现与阅读优化（2026-10-03）

本轮实现调研后的会话页优化，不扩展 ACP、历史分页或服务端存储。基线为 `faca6618685cbc7924f8b176115d2601d0eb6dd5` 上的未提交开发修改；不代表正式发布。

## 实现

- Windows 为工具消息补充可选的 `kind/title/summary/status`；原 `text` 保留受大小限制的详情。Codex 的开始/完成事件使用同一 ID，Claude 的 `tool_use/tool_result` 按调用 ID 关联，ZCode 原生工具事件使用现有适配器。
- 成功、失败、拒绝、取消、执行中分别呈现；缺少结果或结束时仍未确认的工具显示“状态未报告”，不根据整轮结束推断工具成功。非零退出码覆盖成功标签。
- Android 连续工具记录折叠为一组，展开显示操作、摘要与状态，点击单项打开可滚动、可复制的详情。审批和问题独立显示，不折叠进工具组。
- 回复支持 Markdown 标题、列表、引用、行内样式、代码块、表格、显式 HTTP(S) 链接。代码可复制和横向滚动，表格可横向滚动；HTML 作为文字显示，不执行脚本，不加载远程图片。
- 保留已有紧凑输入框、停止、发送、控制租约与请求回执。审批默认显示摘要，展开保留请求详情；超过现有安全显示上限的请求仍禁止在手机批准。
- 在底部阅读时跟随新输出；拖动浏览历史后暂停跟随，点击“回到最新”恢复。展开工具或审批详情暂停跟随；“待处理”定位第一张待处理卡片。键盘展开/收起保持阅读锚点。

CLI 页面从 `private fun SessionsPage(` 到文件末尾与 HEAD 一致（忽略换行）。本轮不改变 ZCode `desktop_remote` 的 WebView 策略，服务端仍只桥接现有 E2EE 请求。

## 来源与许可

行为参考 [调研记录](AI_MOBILE_CLIENT_RESEARCH_20261003.md) 中的 HAPI ChatTranscript、Happy 工具呈现、CC Pocket 简洁聊天投影；没有复制这些项目的产品源码、图标或资源。尤其没有将 HAPI AGPL 源码移入产品。

新增依赖为 `org.commonmark:commonmark:0.24.0` 与 `org.commonmark:commonmark-ext-gfm-tables:0.24.0`，用于标准 Markdown 解析，界面仍由 TermLink Compose 绘制。已核对 Maven JAR 内 BSD-2-Clause 许可证，原文随 APK 保存在 `assets/licenses/commonmark-LICENSE.txt`，保留 Atlassian 版权声明。

## 自动验证

| 检查 | 结果 |
| --- | --- |
| Android app 单元测试 | 340 项，失败/错误/跳过均为 0；包含消息分组、稳定 key、新旧字段兼容、Markdown 结构、代码围栏与链接限制 |
| Windows AI 验证 | 通过；Codex/Claude/ZCode fixture 各验证完成、失败、未确认工具的摘要/详情；原模型查询、审批拒绝、控制版本、工作目录与回执测试通过 |
| Android DEV 编译 | 通过；最终 HTTPS 配置 `0.2.71 / 127` |
| Windows DEV 编译 | Desktop、Agent、SessionHost 均成功，版本 `0.2.71.127` |
| CLI 保留与 diff | CLI 正文比对通过；`git diff --check` 无错误 |

日志位于 Git ignored 的 `artifacts/validation/`：`ai-chat-win-test.log`、`ai-chat-android-preview-final-build.log`、`ai-chat-android-final-https-build.log`、`ai-workspace-win-dev-*-build.log`。

## 模拟器验证

使用 `emulator-5556` 的独立 DEV 包与 Windows AI 适配器 fixture，临时桥接只监听本机 `127.0.0.1:18793`；这些测试没有执行模型命令，不等于每家真实模型都触发过相同工具事件。

| 场景 | 结果与证据（`artifacts/validation/`） |
| --- | --- |
| 工具分组、展开、失败详情、复制 | 通过；`ai-chat-tools-expanded.png`、`ai-chat-tool-detail.png` |
| Markdown 标题、列表、表格、代码、链接样式 | 通过并目视复核；`ai-chat-markdown.png` |
| 25 段流式输出期间上滑阅读 | 前后首个可见段落及坐标相同；`ai-chat-stream-reading-before.xml` / `after.xml` |
| 返回最新 | 定位流式第 25 段；`ai-chat-stream-latest.png` |
| 键盘显示/收起 | 阅读段落与坐标保持；输入栏可见、导航收起/恢复；`ai-chat-keyboard-open.png` / `closed.png` |
| 审批摘要、展开、允许本次 | 通过，收到回执后卡片消失；`ai-chat-approval.png`、`ai-chat-approval-expanded.png`、`ai-chat-approval-accepted.xml` |
| 问题选项、提交、恢复普通输入框 | 通过；`ai-chat-question.png`、`ai-chat-question-submitted.xml` |
| 横竖屏往返的精确段落锚点 | **未通过**：会话与暂停跟随状态保留，但整条长回复重排后可能改变可见段落。当前 LazyListState 保存消息级索引及像素偏移，不保证跨宽度的段落定位；`ai-chat-rotation-before.xml` / `after.xml` |
| 最终正常服务器链路 | 恢复 HTTPS 包后，通过现有 E2EE/Relay 读取 HOME-PC 既有 Claude 对话，无发送/控制权变更；`ai-chat-final-real-claude.xml/png` |

UIAutomator 不包含键盘窗口节点，键盘可见性以 `dumpsys input_method` 的 `mInputShown`、截图以及应用布局共同核验。

## 最终安装状态与限制

- 本轮只读核对 AppShip Android/Windows stable 均为已发布且 ready 的 `0.2.71 / 127`，DEV 沿用该版本；未递增版本、提交、打 tag 或上传。
- 模拟器已覆盖安装最终 HTTPS DEV，包名 `dev.termlink.harness.aivalidation`、名称 TermLink DEV、地址 `https://termlink2.kget.cc:8910`；原登录数据保留。DEV signer SHA-256 为 `b0bcabe365a44108af2101fa3b34899dfeed5efef306639a004097060871d343`，连续性核验通过。
- 临时 fixture 桥接进程已按 PID 与可执行路径核验后关闭，其子进程及 18793 监听均已退出；adb reverse 已移除。恢复 `show_ime_with_hard_keyboard=0`、`accelerometer_rotation=1`、`user_rotation=0`。
- 新 Windows DEV 位于 `artifacts/validation/ai-workspace-windows-next-20261003/`，**尚未替换运行中的 Windows DEV**。现有 HOME-PC Claude 对话仍存活，为保留会话未重启 Agent。正常链路复核使用原 Windows 构建，验证 Android 对旧字段的兼容；新增工具摘要的联合 UI 验证使用新版 Windows fixture。
- 工具详情及历史仍受现有快照边界限制，未提供无限历史：最多 128 条消息、文本合计约 48,000 字符，单条普通消息约 16,000、工具详情约 8,000；原生 Agent 是否保存完整历史取决于各自实现。
- 未覆盖实体手机、各家真实工具审批、LAN/跨 NAT、正式签名升级与回滚。横竖屏精确段落恢复为已知待完善项，不计为通过。
