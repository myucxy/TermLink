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

## 发布结果（2026-10-19）

已发布 [GitHub v0.3.10](https://github.com/myucxy/TermLink/releases/tag/v0.3.10) 及 AppShip stable。

| 制品 | AppShip 下载 | SHA-256 |
| --- | --- | --- |
| Android APK | [下载 APK](https://appship.kget.cc:8910/api/v1/artifacts/d5df40ea-98c8-4ed3-b1bf-5a8265203ec0/download) | `44d7a035a2463d6e0007dd6bc63745567da5d875e1f13ab1a1482cd98f30e272` |
| Windows x64 Setup | [下载安装包](https://appship.kget.cc:8910/api/v1/artifacts/d34a6cfe-d890-4189-97c0-79db30ae3f90/download) | `16ffa54c8277e43ebf1f6b476c802cc487485fae55120b1e35c1e4845b102d6b` |
| 完整对应源码 | [下载源码](https://appship.kget.cc:8910/api/v1/artifacts/d77880c4-59ea-42ad-ac17-57fc58c925a9/download) | `d5d840a798a9ffe04bdf40b203ebb4b00fb1c29f420ba4aa335c2f50cf59ffbb` |

- 构建与 tag 源码：`7ab29a391fc4bf1d0bc82ba0097950bd466901e6`；后续文档补记不改变产物或 tag。
- GitHub 七个附件哈希一致，[独立 Windows 验签管道](https://github.com/myucxy/TermLink/actions/runs/39182834794)通过。
- AppShip 三个制品为 `published / ready`，匿名下载大小与哈希一致；双端旧 build 139 返回 `optional`，140 返回 `no_update`。
- Server 全量 PostgreSQL 集成测试及 GitHub 源码检查通过；Android 904 项单测和 lint 通过；签名连续性、Windows RFC 3161、17 个载荷签名及篡改拒绝回归通过。
- 正式 APK 在专用模拟器原地覆盖安装、冷启动与页面渲染通过，保留应用数据；未重新登录或重跑整套 Relay／MI 6 矩阵。
- Docker 双架构 OCI、本地签名与 amd64 启动／迁移／OIDC／重启测试通过；Docker Hub 本机凭据缺失，推送被拒绝，版本 tag 和 latest 均未更新。保留本地同一份归档，恢复登录后继续推送；不将其记为发布成功。

## 制品与来源

- `TermLink-Android-0.3.10-140.apk`
- `TermLink-Setup-0.3.10.140-x64.exe`
- `TermLink-0.3.10-140-source.zip` 及 `.sha256.json`，包含匹配应用/core 源码、Rust vendor 和构建资料。
- 本地待推送 Docker 镜像 `myucxy/termlink:0.3.10-build.140`，包含 amd64 / arm64、归档签名、SBOM、provenance；Docker Hub 尚未发布，arm64 不宣称真机运行验证。

终端核心沿用锁定 revision `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`。Android、Windows 和 Docker 继续使用原签名身份；Windows 为既有自签名证书，不代表公开 CA 或 SmartScreen 信誉。精确源码 revision、SHA-256、制品 ID 与 Docker digest 以随附清单和发布结果为准。
