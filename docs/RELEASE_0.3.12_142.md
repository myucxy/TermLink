# TermLink 0.3.12 / 142

Android 远程升级 Windows 客户端时可以最小化弹窗，继续使用其他设备；重新进入应用只恢复卡片进度。Windows 在远程任务进行期间手动更新到相同目标或更高 build 后，重连并经 Server 确认即可安全结束原任务。

## 调整

- 升级任务按电脑独立跟踪，进度、成功或失败不会重新打开已收起的弹窗；系统返回键也只收起窗口。
- 升级中的电脑暂时离线仍保留卡片，显示安装或等待重连状态；被服务端移除的设备不从缓存恢复。
- Android 保存账号与服务器作用域内的任务 ID，重新进入恢复原任务及关闭期间的完成结果，不重复创建。
- Server 增加已安装版本确认接口，核验当前 PC、可信 Windows 身份、在线连接与登记版本/build，再结束已满足目标的任务。已领取任务不再仅凭更高版本登记而成功。
- Windows 重连后恢复服务端确认和本地回执；成功回执支持实际 build 高于任务目标，回滚仍严格绑定原 build。丢失响应可幂等恢复，旧 lease 不得覆盖终态。
- Setup 与升级器按安装目录互斥；已安装更高版本时不再执行旧 Setup 或回滚覆盖，未确认连接时保留安装和备份。
- Android 完成卡片显示实际安装版本，只更新对应电脑。

## 升级顺序与限制

本次任务收尾能力需要配套 Server 与 Windows Agent／升级器。建议先部署本版 Server，再升级 Windows 与 Android。已运行的旧升级器不会因只更新手机而获得新保护；较早版本的首次覆盖安装与引导限制见 [0.3.10 说明](RELEASE_0.3.10_140.md#升级注意)。

Server 镜像发布后仍需单独更新现有容器，本轮不自动部署 NAS。既有 AppShip 启动网络策略保留。个别 PC 登录的设备身份 `401 UNAUTHENTICATED` 未在本版修复。Server 自身没有新增自动升级能力。

## 验证

- Server 全量单测、PostgreSQL 集成测试和 go vet 通过；覆盖相同／更高目标版本、离线和跨账号／设备／PC 拒绝、重试及旧 lease 不可修改终态。
- Windows Agent、UpdateCore、Setup、升级器的独立验证通过，包含本地回执、并行安装互斥、新版保护及实际目录回滚。
- Android 486 项应用单测、lint，以及手机／平板模拟器共 6 项仪器测试通过。
- 正式制品继续核验源码、版本、SHA-256、双端签名连续性和 Windows RFC 3161 时间戳；制品联测及最终上传状态以发布结果为准。

保持 Relay Only。Windows 沿用现有自签名身份，不代表公开 CA 信任或 SmartScreen 信誉。arm64 镜像不宣称完成真机运行；本轮不重新测试 MI 6。

## 制品

Android APK、Windows x64 Setup、带匹配 core 源码与 Rust vendor 的完整对应源码，以及 Server amd64／arm64 镜像。核心二进制继续锁定 `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`，不改写既有来源。


## 发布结果（2026-10-20）

已发布 [GitHub v0.3.12](https://github.com/myucxy/TermLink/releases/tag/v0.3.12)、AppShip stable 和 Docker Hub。构建源码及 tag 固定为 `617bde9b9ba0a50f8c65ac68b2d0d176475af3bc`；后续文档补记不改变制品和 tag。

| 制品 | 下载 | SHA-256 |
| --- | --- | --- |
| Android APK | [下载 APK](https://appship.kget.cc:8910/api/v1/artifacts/ce008c42-c086-4b2f-9385-0e6736d6c5b5/download) | `f7a83285a9932e019a03c0f52087c97b067ea293611cbd057d2ce4239429d654` |
| Windows x64 Setup | [下载安装包](https://appship.kget.cc:8910/api/v1/artifacts/97ab0455-b1e7-4d07-ad85-4d9cc4fe13f2/download) | `93ab12cd2ddda65518a6fcc474847f7fa23233c0f76949dea71f0041e884c3c7` |
| 完整对应源码 | [下载源码](https://appship.kget.cc:8910/api/v1/artifacts/7f5edde7-60d2-4981-aaca-6ea0cd1d253f/download) | `f8f9820bed3bd1a71919bdaff43d3f18052d43f0708798712bf2c57c367e518b` |

- GitHub 七个附件的大小与哈希一致，[独立 Windows 验证管道](https://github.com/myucxy/TermLink/actions/runs/39371347996)通过。
- AppShip 三个 release 均为 `published`、artifact 均为 `ready`，匿名完整下载哈希一致；客户端 build 141 返回 `optional`，142 返回 `no_update`。
- APK 单一 signer 与 v2 签名、Windows Authenticode／RFC 3161／17 个自有载荷签名及 core 哈希连续性通过。保持原有签名身份，Windows 仍为自签名。
- 正式签名 Setup 在隔离目录实际将 141 覆盖到 142；真实 Agent 经 Relay 连接本版 Server 后，目标 142 的任务变为成功，并写入成功回执和本地终态。目标 141、实际已装 142 的场景同样通过，不降级。使用独立账号、数据库和控制管道，原有 Windows 安装、账号、启动项和 Terminal Fragment 保留。
- 本轮补测覆盖真实 Setup、Agent、Server 的任务收尾；正在运行的旧版升级器并发行为未在该联测中重演，仍遵守上文旧升级器边界。独立升级器测试覆盖目录互斥、新版保护、成功回执和实际目录回滚。
- Android 手机／平板共 6 项升级界面仪器测试、486 项应用单测及 lint 通过。正式 APK 在平板模拟器原地升级、冷启动及界面检查通过，保留 appId、首次安装时间和应用数据。测试模拟器出现存储 I/O 错误后冷启动恢复，未清数据；未重跑 MI 6 或完整 Android Relay 回归。
- Server 全量单测、PostgreSQL 集成测试和 go vet 通过。固定源码的 GitHub 检查已通过；首次运行中历史帧 WebSocket 测试出现一次 2 秒超时，未修改源码，本地连续 10 次及 CI 重跑通过。
- 发布后的独立 CI 修正提交 `60b256da` 将 PostgreSQL 就绪探测改为 TCP，避开初始化临时服务，并将 DEV 基线同步为 0.3.12 / 142；[双架构构建、amd64 运行及重启管道](https://github.com/myucxy/TermLink/actions/runs/39371680188)与仓库检查均通过。该提交只修改工作流，不改变已发布制品及标签。
- 本地 OCI 检查 amd64／arm64 ELF、版本、revision、非 root 用户及 SBOM／provenance；实际 amd64 容器的迁移、OIDC、healthcheck、重启及 AppShip 下载页／版本页读取通过。arm64 未做真机运行。
- Docker index、两个平台镜像及各平台 SBOM／provenance 已使用原有本地 Cosign 密钥签名并在远端验证；不声称使用 GitHub OIDC keyless 身份。
- `0.3.12-build.142`、`0.3.12` 和 `latest` 均指向 index digest `sha256:8df93a7a02627771ed29e485a87594a150557a143bd4d79cd6c48a7395000d83`；原 latest 为 `sha256:8ce6cd78ba95aa52e7aa11a244425646e24d1a0583c9e9c0cfbb6cea9f438201`。匹配源码下载说明已附加到镜像。

NAS 生产服务未部署更新；镜像发布不会自动更换现有容器。本次任务收尾需配套 Server 与 Windows，建议先更新 Server，再更新 Windows 和 Android；只升级 Android 无法补全旧 Windows 升级器保护。个别 PC 设备身份校验 401 不属于本次修复。
