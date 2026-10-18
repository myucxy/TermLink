# TermLink Android

Android 客户端提供设备、CLI、AI、文件和设置页面。Windows Agent 持有电脑端进程、真实会话状态与历史；手机通过加密桥进行交互。

平板按窗口尺寸切换宽屏布局；折叠屏根据分隔折痕/铰链选择连续可用操作区，保留已有会话与草稿。
具体行为、模拟器验证和厂商边界见[平板与折叠屏适配](../docs/development/ANDROID_ADAPTIVE_SCREENS.md)。

## 构建与测试入口

先阅读[统一开发说明](../docs/development/BUILDING.md)。新检出也要先构建 `ai-web/` 资源，再用仓库的 Gradle Wrapper 编译 Android；不能直接沿用 Gradle 中的早期默认版本号。

DEV 构建显式传入 Android 最后一次成功正式发布的版本/build，并使用独立包后缀区分测试版。普通 Debug 编译不需要正式发布 keystore；升级已有测试包时保持包名和调试签名一致。正式签名和上传遵循[发布运行手册](../docs/RELEASE_RUNBOOK.md)。

测试入口包括 Gradle 单元测试、Lint、[AI Web 浏览器测试](../ai-web/README.md)及[跨端验证](../technical-validation/e2e/README.md)。浏览器夹具或单元测试不能代替真实 Android WebView 与 Windows 的联合测试。

## 组件

| 目录 | 责任 |
| --- | --- |
| `app/` | Compose 产品页面、Harness、AI WebView 桥、附件与通知 |
| `terminal-engine-api/` | 终端引擎接口 |
| `terminal-engine-native/` | TermLink Rust terminal-core 的 JNI、帧解码与 View |
| `terminal-engine-replay/` | 隔离的终端流回放测试 |
| `core-session/` | 远程终端会话生命周期 |
| `core-network/`、`core-protocol/`、`core-crypto/` | API、实时消息、加密与设备身份 |
| `core-model/`、`core-database/` | 模型与本地持久化 |
| `feature-history/`、`feature-billing/` | 保留的历史/权益模块；是否面向用户开放以产品流程为准 |

## 当前边界

- 产品、Harness 和测试使用自有 terminal-core、JNI 与 View。旧 Termux 模块和源码已迁出仓库，来源记录见[清理说明](../docs/archive/TERMUX_CLEANUP_20261008.md)。
- CLI 与结构化 AI 当前使用及验收范围为 Relay Only，不安排 LAN/NAT 测试。
- AI Web 资源目前内嵌 APK；ZCode 桌面远控使用其官方网页。详情见[当前架构](../docs/architecture/CURRENT.md)。
- 手机不是 Windows 进程或历史的权威来源，离线、过期登录及失效会话应按主机状态处理。
- 历史实现、旧命令和阶段测试保存在 [HISTORY.md](HISTORY.md)，不能直接作为当前安装或发布步骤。

许可证与第三方来源见[授权状态](../LICENSE-STATUS.md)；漏洞通过[私密入口](../SECURITY.md)报告。
