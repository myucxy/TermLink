# Android AI V3 界面复核（2026-10-03）

本页记录 2026-10-03 AI V3 的验证现场，源码为基线 `faca661` 上当时尚未提交的开发修改。作为依据的 V3 静态设计稿已于 2026-10-18 从当前树删除；当前交互见[功能演示](media/README.md)。下方结果仅适用于原验证批次，不作为当前完成度清单。

## 实现与边界

- AI 列表采用标题/状态、Agent/电脑/目录、摘要三行，支持全部/待处理筛选、长按关闭手机视图与设备目录入口。
- 详情合并标题和电脑信息；云朵菜单按控制权限、连接、当前会话分组。AI 当前仅 Relay，菜单如实显示服务端中转。
- 消息输入单行起步，最多五行；键盘展开时隐藏 AI 底栏。工具详情折叠，审批显示操作、目录、一次性范围；问题出现时隐藏普通输入框，单选使用 RadioButton。
- 创建改为底部两步面板：电脑/Agent → 目录/模型/可选首条草稿。ZCode 的 Provider 与模型在宽屏并排，窄屏/大字号纵排。首条消息创建后保留为草稿，用户点击发送后才执行。
- 新按钮视觉紧凑、触控区至少 48dp；没有缩小原 CLI 控件。深色创建面板使用对应系统栏图标颜色。
- Windows 加入 title/preview/pendingCount 目录字段，旧字段解析保持兼容；正文仍从 Windows 会话读取。元数据刷新不会清空已加载正文，失效引用明确显示已失效。
- 未提交问题回答存在会话 workspace 中，旋转/栏目切换保留；请求消失、关闭视图或退出账号时清除。未提交草稿不跨进程持久化；已发送但未确认请求仍走原加密回执恢复机制。

设计中的附件、模型目录、权限模式选择等尚无完整协议能力，本轮未添加无效按钮。工作目录仍为手动输入，没有把文件选择器画成已实现功能。

## 真实联合测试

使用 `emulator-5556` 的独立 `dev.termlink.harness.aivalidation` 包、普通 ProductActivity、本机暂存 Windows Agent 和 `https://termlink2.kget.cc:8910`，连接真实 Docker 服务与 E2EE Relay。

- 新建 Claude，目录 `D:/Temp`，显式模型 `glm-5.3-flash[1m]`。
- 可选首条草稿在创建后保留；手动发送后取得真实 `TERMLINK_UI_V3_OK` 回复。
- AI 列表显示 Windows 提供的首条消息标题和回复摘要。
- 恢复最终 HTTPS 测试包后重新打开同一会话，获取控制权并确认结束，Windows 返回已结束；`ai-ui-final-real-reopened.xml`、`ai-ui-final-real-ended.xml/png`。
- 没有改写全局 Claude 模型配置或正式客户端安装目录。

证据位于 `artifacts/validation/`：`ai-ui-real-reply.xml`、`ai-ui-real-chat.png`、`ai-ui-real-list.xml/png`、`ai-ui-create-filled.xml`。

## 模拟器隔离界面检查

使用仅绑定 `127.0.0.1:18793` 的临时合成响应服务与已有 Debug AiAcceptanceActivity；不执行模型命令，不代表真实模型触发过所有审批/问题状态。

| 检查 | 结果与证据文件（`artifacts/validation/`） |
| --- | --- |
| 审批详情展开、允许后卡片消失 | 通过；`ai-ui-approval.png`、`ai-ui-approval-accepted.xml` |
| 单选替换、旋转保留、提交后卡片消失 | 通过；`ai-ui-question-before-rotate.xml`、`ai-ui-question-after-rotate.xml`、`ai-ui-question-submitted.xml` |
| 只读与获取控制权 | 按回执变化；`ai-ui-readonly.xml`、`ai-ui-menu.png` |
| 待处理筛选 | 仅保留未决会话；`ai-ui-attention.png` |
| 320dp 宽、系统字号 1.5 倍 | 纵向表单和回答卡片可滚动，操作可达；`ai-ui-create-narrow.png`、`ai-ui-question-narrow-actions.png` |
| 横屏 | 表单字段按可用宽度并排，底部创建操作可达；`ai-ui-create-landscape.png` |
| 键盘与草稿 | 输入栏可见、底栏隐藏，恢复屏幕配置后草稿保留；`ai-ui-keyboard-narrow.png`、`ai-ui-draft-restored.xml` |
| 新消息滚动 | 底部跟随，手动浏览历史不跳动，回到最新可用；`ai-ui-scroll-follow.xml`、`ai-ui-scroll-retained.xml`、`ai-ui-scroll-latest.xml` |

已目视检查主要截图。测试结束恢复分辨率/密度、font_scale=1.0、accelerometer_rotation=1、user_rotation=1，关闭临时响应服务、移除 adb reverse，重新安装 HTTPS termlink2 的独立测试包。测试包保留深色主题，正式包不变。

## 代码与自动检查

- Android app 332 项通过，无失败/错误/跳过；`ai-ui-v3-final-build.log`、`ai-ui-v3-final-checks.log`。core-network 337 项在此前联合测试通过，本轮 UI 修改未改网络模块。
- Windows 适配器/管理器检查通过，补充目录标题、摘要、待处理数及不携带完整正文的断言；`ai-ui-v3-agent.log`。
- Windows Agent 编译零警告/零错误；`ai-ui-v3-agent-build.log`。
- `ProductActivity.kt` 从 `private fun SessionsPage(` 到末尾与设计基线逐字一致（忽略换行）；`git diff --check` 通过。

本轮未重新覆盖实体手机、真实多网卡 LAN/跨 NAT、各 Agent 全部真实审批方式、正式签名升级与回滚。ZCode 桌面远控仅完成只读架构与入口可达性分析，未扫码控制。
