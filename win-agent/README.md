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

## 账号身份恢复

退出账号会清除本地 Token 与当前账号配置，保留按账号、OIDC issuer 和本机标识隔离的设备身份，便于再次登录时沿用原设备。

重新授权遇到旧设备状态 `401 UNAUTHENTICATED` 时，使用新授权的 Token、原设备 ID 和原密钥向现有注册接口证明身份。只有注册返回 `201 Created` 且再次请求 `/v1/devices/self` 成功，才保存新登录；PC 标识、密钥和其他账号缓存保持原有语义，不需要删除整个本地数据目录。

注册冲突不会覆盖已有设备。禁用、普通 401、错误签名、过期证明、无效 Token、网络错误及异常响应均不能直接授权登录；明确撤销仍按原有的账号内稳定 PC 恢复流程处理。旧 Server 拒绝 `PC_AGENT` 类型时仅按既有协议回退 `WINDOWS_PC`，不放宽身份校验。

这项修复只改 Windows 客户端，复用现有 Server API；纳入 [0.3.15 / 145](../docs/RELEASE_0.3.15_145.md)，渠道发布状态以该记录为准。0.3.14 / 144 及更早已发布制品不包含此修复。

验证入口为 `tests/AgentUpdateValidation/`：覆盖当前登录身份、退出后的缓存身份、原密钥证明、旧类型回退、注册拒绝及二次校验失败时的文件保留。Server 的 `TestRetainedDeviceRecoveryCannotOverwriteExistingIdentity` 使用隔离 PostgreSQL 与 Redis/Valkey 核验缺失设备重建、账号隔离、不同密钥冲突和禁用/撤销记录保持。

## 验证与维护

- 按修改范围选择 `tests/` 中的协议、SessionHost、AI、文件或更新验证，命令示例见开发说明。
- 当前生产联测覆盖 Relay Only，不重跑历史 LAN/NAT 验收。
- 真实 AI 生成需要已安装且可用的 Agent 账号；模拟协议通过不表示账号登录或额度验证通过。
- 桌面更新目前下载、验签后直接启动 Setup；尚未完成桌面更新统一迁入独立 Updater 的方案，详见[当前架构](../docs/architecture/CURRENT.md)。
- 更新启动前仍核对安装包大小、SHA-256、数字签名和固定发布者证书指纹。默认 `auto` 与 `compat` 不要求证书链受系统信任；只有显式配置 `TERMLINK_UPDATE_SIGNATURE_POLICY=strict` 才要求系统信任链及吊销检查通过。
- 老 Tray、阶段 IPC、terminal-main、机器路径和旧安装命令保存在 [HISTORY.md](HISTORY.md)，仅按记录时间阅读。

运行时凭据和发布签名材料不能放入仓库。已有许可证及署名保持不变，见[授权状态](../LICENSE-STATUS.md)。
