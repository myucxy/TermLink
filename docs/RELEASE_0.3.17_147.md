# TermLink 0.3.17 / 147

本次发布 Android APK 与匹配的完整对应源码。Windows、macOS、iOS 安装包及 Docker 镜像不在本轮发布范围，既有服务部署保持原状。

## Android

- 统一按电脑端能力显示 AI、CLI、文件和远程升级入口；AI-only 主机不会被误报为需要升级 Windows。
- 补齐 Bash、Zsh、sh、Fish、pwsh 命令元数据兼容，保留严格校验和旧客户端响应协商。
- 默认 CLI 目录由支持该能力的电脑端创建；丢失回执时复用原请求，不重复创建。旧电脑端仍可选择已有目录。
- POSIX 收藏路径保留大小写，避免不同目录被合并。
- 远程升级提示改为通用电脑端文案，保留自动最小化、成功清理、失败查看/关闭和多设备隔离。
- 读取服务端连接阶段诊断，在保留认证/权限提示的同时给出安全的阶段说明。
- ZCode 使用原生会话链路；不恢复已停用的旧桌面网页接入。

## 兼容范围

旧电脑端仍可使用其上报的原生 Agent。ZCode 旧网页模式已停用，需要电脑端提供原生接入；手机会给出升级提示。

Mac 当前通过 Relay 提供的远程 AI 能力仍以实际主机声明为准。不能把本次 Android 适配视为 Mac 已实现远程 CLI、整机文件或手机触发的远程升级。

共享目录的新 Server/portable Host 源码一并整理；Server schema 53～55、macOS 更新目录和 APNs 接口属于服务端代码准备，未在本轮部署。Android 不调用仅供 iOS 的 APNs 注册接口。传输验收范围保持 Relay Only。

## 验证

- Android App 全量单测 504 项、网络层 425 项通过。
- 模拟器真实 JNI 核心 46 项、升级 UI 6 项通过，均无失败或跳过。
- Server 全量单测 1296 项通过、89 项环境相关测试跳过；独立 PostgreSQL 的迁移/存储/推送回归 222 项通过、1 项跳过。
- Portable AI 协议模拟通过，GitHub 发布脚本 8 项测试通过；图标、依赖清单和公开文档检查通过。

制品哈希与实际发布状态记录在最终发布结果中。本轮使用现有 Android 签名身份，不更换签名密钥、不清理用户数据。Mac/iOS 真机、真实模型账号和 APNs 投递未在 Windows 环境验收。

## 发布结果（2026-11-03）

已发布 GitHub `v0.3.17` 与 AppShip stable，平台范围为 Android APK＋完整对应源码。制品固定应用提交 `9d0a207d3cd21e0c74a02e3225bab0573d913095`、Android/Windows core `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`。GitHub 上传器修复提交 `e2bdab4c6f538c696ace5fbcadd320e760930577` 仅修正 Android-only 附件清单，复用原制品、源码包、草稿和标签，没有重新编译或移动标签；发布工具回归共9项通过。

| 制品 | 大小 | SHA-256 |
| --- | ---: | --- |
| TermLink-Android-0.3.17-147.apk | 43,785,130 | `67823e5e9cc305112f61fe18e6a8fe52c0e2e12f40c42f14f6337b3f02327de7` |
| TermLink-0.3.17-147-source.zip | 88,343,402 | `069925d3c89447f6c4e893ea11d49b9e663342ba3906fb3e0577426dc2a80ee7` |

- [GitHub Release](https://github.com/myucxy/TermLink/releases/tag/v0.3.17)：6个附件，含APK、源码、sidecar、manifest、SHA256SUMS和发布元数据。
- [GitHub 独立验证](https://github.com/myucxy/TermLink/actions/runs/41628722117)：源码、附件哈希、版本、Android签名全部通过。源码提交对应的 Repository checks `41626430545` 与 Docker DEV checks `41626430898` 通过；后者没有发布镜像。
- [AppShip APK](https://appship.kget.cc:8910/api/v1/artifacts/ef77e5d9-75cd-4678-9163-97d9963c7366/download)：release `4d7fa8c9-8286-4214-962b-f3ee6c84abaa`，`published`；artifact `ready`、签名审核 `manual_approved`。
- [AppShip 完整对应源码](https://appship.kget.cc:8910/api/v1/artifacts/a94896be-c6f6-4cec-b3dd-848633acc58d/download)：release `369622bc-2179-42cf-a981-d047eb62feb6`，`published`；artifact `ready`。
- APK唯一签名者指纹延续 `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`，apksigner v2验证通过。公开更新API：146返回`optional`、147返回`no_update`，两份匿名完整下载哈希匹配。
- 正式构建首次因JVM本机内存分配失败而中止；关闭本轮测试模拟器、降低并发后用相同版本/build与同一提交成功重试。没有改变签名或绕过验证。
- Windows/macOS/iOS安装包、Docker镜像与现有Server均未升级；本地Server仍健康。Mac/iOS设备、真实模型与APNs验收仍按上文边界记录。
