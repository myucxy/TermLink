# TermLink

TermLink 是一个面向个人和小型团队的多 PC 远程终端系统。Windows 端 Agent 负责创建、采集和控制受管终端会话；Android 客户端使用同一账号发现不同 PC，并实时查看终端输出、恢复历史记录及在授权后输入命令；云端 Relay 负责认证、设备发现和加密数据转发。

项目当前处于需求与方案设计阶段。

## 商业模式

- 首发预置 Free：每个账号最多启用 1 台 PC 的远程访问，同时远程显示 3 个实时会话；
- 首发预置 Pro Monthly：按月自动续费、可取消，解锁更多 PC 和实时会话；
- 套餐名称、周期、额度、排序、上下架状态及渠道 Product ID 均由服务端配置，客户端不硬编码套餐；
- Android 使用 Google Play Billing 展示本地化价格，Windows/Web 使用服务端购买渠道；
- 会员到期只暂停超出免费额度的远程能力，不终止本地 Shell，不删除设备或历史；
- E2EE、设备撤销、本地用户优先和紧急停止等安全能力不进入付费墙。

## 文档

- [重新打开会话时的开发交接](DEVELOPMENT_HANDOFF.md)
- [完整产品与系统设计（开发基线）](docs/COMPLETE_SYSTEM_DESIGN.md)
- [Android / Windows Agent / Server Console UI 布局设计稿](docs/UI_LAYOUT_DESIGNS.md)
- [可交互 UI 原型](ui-prototypes/index.html)
- [产品需求与技术方案](docs/PRODUCT_REQUIREMENTS_AND_SOLUTION.md)
- [第一轮技术验证与 Windows Terminal Android 移植评估](docs/TECHNICAL_VALIDATION.md)
- [Android 全功能 Terminal 引擎调研与技术方案](docs/ANDROID_TERMINAL_ENGINE_RESEARCH.md)
- [ConPTY 可运行原型](prototypes/conpty-probe/README.md)

## 一句话目标

让用户可以从 Android 安全地查看和控制自己多台 Windows PC 上正在运行的终端会话，同时保留可恢复、可审计、端到端加密的会话记录。
