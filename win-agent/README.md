# TermLink Windows Agent

Windows 运行时负责受管 CLI、AI Agent 协议、电脑文件和远程加密桥。用户界面位于 [pc-client](../pc-client/README.md)，通过认证的本机控制管道与 Agent 通信。

## 构建与测试入口

先阅读[开发与构建](../docs/development/BUILDING.md)。使用固定 .NET SDK，先验证预编译 terminal-core DLL，再生成应用内 `network-core` 原生 DLL，再编译需要的 .NET 项目；DEV 版本沿用 Windows 最后一次成功正式发布的版本/build。

源码编译输出不等于完整的签名安装包。Setup、原生 DLL、Agent、SessionHost、桌面程序和 Updater 的组合、签名与验证由[发布构建规则](../docs/RELEASE.md)约束；发布执行顺序见[运行手册](../docs/RELEASE_RUNBOOK.md)。

## 组件

| 目录 | 责任 |
| --- | --- |
| `src/Agent/` | Relay、E2EE、会话目录、主机历史、文件请求、AI 检测与协议适配 |
| `src/SessionHost/` | 每个 CLI 独立的 ConPTY Owner、输入输出与终端权威状态 |
| `src/TerminalCore/` | Rust terminal-core 的 .NET 绑定与严格帧解码 |
| `src/Protocol/`、`src/AgentControl/`、`src/Common/` | 远程协议、本机控制契约与 IPC |
| `src/Setup/` | Windows 传统安装器及安装生命周期 |
| `src/UpdateCore/`、`src/Updater/` | 更新验证、远程升级事务及历史 MSIX 路径 |
| `scripts/`、`tests/` | 原生构建及协议、进程、AI、文件、安装/升级验证 |

SessionHost 管理终端进程；Agent 记录会话状态并参与恢复。桌面窗口是否打开不能代替实际进程状态。CLI 历史与 AI 归档的所有权均在主机端，Server 只承担对应元数据和桥接职责。

## 验证与维护

- 按修改范围选择 `tests/` 中的协议、SessionHost、AI、文件或更新验证，命令示例见开发说明。
- 当前生产联测覆盖 Relay Only，不重跑历史 LAN/NAT 验收。
- 真实 AI 生成需要已安装且可用的 Agent 账号；模拟协议通过不表示账号登录或额度验证通过。
- 桌面更新目前下载、验签后直接启动 Setup；尚未完成桌面更新统一迁入独立 Updater 的方案，详见[当前架构](../docs/architecture/CURRENT.md)。
- 老 Tray、阶段 IPC、terminal-main、机器路径和旧安装命令保存在 [HISTORY.md](HISTORY.md)，仅按记录时间阅读。

运行时凭据和发布签名材料不能放入仓库。已有许可证及署名保持不变，见[授权状态](../LICENSE-STATUS.md)。
