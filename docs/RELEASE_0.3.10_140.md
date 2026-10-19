# TermLink 0.3.10 / 140

恢复 Android 长按 Windows 设备后“升级 Windows 客户端”的入口。确认后电脑下载并校验同发布者安装包，静默安装、后台启动并重新连接；活动 AI 任务会阻止升级，避免中断生成或审批。

## 修复内容

- 外部升级器停止旧 UI 与 Agent 时仅结束所属进程，避免杀死自己的父进程树；通过认证管道请求 Agent 正常退出，并核对服务进程身份。
- 服务端保留安装中任务的租约，新 Agent 完成 Relay 验证后才提交成功，避免注册版本提前触发成功、缺少本地凭据后误回滚。
- 回滚先停止目标版本的进程，失败版本隔离在安装目录同盘，支持安装盘与用户数据盘不同的情况。
- 后台启动不弹主窗口，损坏或过期的事务日志不会阻断普通启动。
- 更新登录与注册集成测试夹具，使其遵守当前每表单 CSRF cookie 和受控邀请码恢复字段约定；不放宽生产安全校验。

## 升级注意

已有 0.3.8 / 138 及本次修复前的升级器可能在停止旧进程时结束自身，第一跳不能保证通过 Android 完成。请先在 Windows 本机运行本版 Setup 完成一次覆盖安装；保留原安装路径、账户及数据。服务端也需更新到包含本次租约修复的版本，之后再使用远程升级。

仅发布 Docker 镜像不会自动升级已部署的服务器。本轮不变更生产部署或仓库可见性。

## 验证

- 模拟器连接本地 `termlink2`，从隔离的旧 138 Agent 实际下载已发布 139 Setup 并完成升级；事先替换同发布者签名的修复版升级器。手机、Server、本地 receipt 和升级器 journal 最终状态一致，后台重连且旧备份已提交删除。
- 日志在 C 盘、安装在 D 盘的实际目录回滚与凭据验证通过；该测试使用专用测试程序提供健康凭据，不冒称完成真实离线故障演练。
- Android 480 项应用单测、2 项入口仪器测试；Windows AI、更新、认证管道、Setup、UpdateCore 和桌面无窗口回归通过。正式构建重新核验版本、哈希、原生签名与 Windows RFC 3161 时间戳。
- Server 全量 PostgreSQL 集成测试包含登录、MFA、注册恢复及升级租约回归；最终结果记录在本轮发布凭据中。
- 本轮使用模拟器，未重新测试 MI 6；保持 Relay Only，不安排 LAN/NAT 验收。

## 制品与来源

- `TermLink-Android-0.3.10-140.apk`
- `TermLink-Setup-0.3.10.140-x64.exe`
- `TermLink-0.3.10-140-source.zip` 及 `.sha256.json`，包含匹配应用/core 源码、Rust vendor 和构建资料。
- Docker Hub `myucxy/termlink:0.3.10-build.140`，包含 amd64 / arm64 与签名、SBOM、provenance；arm64 不宣称真机运行验证。

终端核心沿用锁定 revision `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`。Android、Windows 和 Docker 继续使用原签名身份；Windows 为既有自签名证书，不代表公开 CA 或 SmartScreen 信誉。精确源码 revision、SHA-256、制品 ID 与 Docker digest 以随附清单和发布结果为准。
