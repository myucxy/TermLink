# TermLink 0.3.11 / 141

本版统一 Server 的 AppShip 下载与 Windows 升级分发配置，解决两条读取链路使用不同配置导致下载页不可用的问题。Android 与 Windows 使用同批版本发布，客户端功能沿用 0.3.10。

## Server 调整

- 固定使用官方 AppShip 分发地址；移除管理员页面中的分发地址和相关网络配置项。
- 下载页、版本页与 Windows 升级目录共用启动时确定的网络策略，旧数据库中的 AppShip 配置不再生效。
- 保留 HTTPS、DNS 地址校验、重定向限制及签名连续性检查；不新增数据库迁移。
- 发布新镜像与升级 NAS 部署是两个步骤；已运行的旧容器需另行更新。

## 验证与已知限制

Server 已完成全量单测、PostgreSQL 集成测试、管理员配置移除及实际服务启动检查，部署包与 Compose 配置检查通过。正式制品在发布前继续核验源码、版本、哈希、签名连续性与 Windows RFC 3161 时间戳。

个别 Windows PC 登录出现 `401 UNAUTHENTICATED / ACCOUNT_DEVICE_STATUS_UNAVAILABLE` 的问题仍在排查，本版未修改设备身份恢复流程，不将其列为已修复。

保持 Relay Only；不安排 LAN/NAT 验收。arm64 镜像只验证构建和制品结构，不声称完成真机运行。Windows 继续使用原有自签名发布身份，不代表公开 CA 信任或 SmartScreen 信誉。

## 制品

- Android APK、Windows x64 Setup，以及带匹配 core 源码与 Rust vendor 的完整对应源码通过 GitHub Release 和 AppShip 提供。
- Server 提供 `myucxy/termlink:0.3.11-build.141` 双架构镜像；实际上传、签名和别名同步结果以发布记录为准。
- 终端核心二进制保持锁定 revision `1c907c51b3c6652b23a7d7216ff1bb13d3410a69`，不改写其真实构建来源。


## 发布结果（2026-10-19）

已发布 [GitHub v0.3.11](https://github.com/myucxy/TermLink/releases/tag/v0.3.11)、AppShip stable 和 Docker Hub。构建源码及 tag 固定为 `7736d6960ba91e0a7105584694dbe32f13b8be51`；后续文档补记不改变制品和 tag。

| 制品 | 下载 | SHA-256 |
| --- | --- | --- |
| Android APK | [下载 APK](https://appship.kget.cc:8910/api/v1/artifacts/b2e5a27a-74dc-4552-b9dd-98a2585350b2/download) | `cb39fe9d080d43329f69c42e4b8df2e282360cb7f1f1824bfeb8493c45722a6a` |
| Windows x64 Setup | [下载安装包](https://appship.kget.cc:8910/api/v1/artifacts/246ae84e-bd19-48c8-b53d-c3b4a2cf14a0/download) | `6a8dc8ab6436097d65d4de83152afcc6d7961b587cbce8099d7f263fe13ca58d` |
| 完整对应源码 | [下载源码](https://appship.kget.cc:8910/api/v1/artifacts/a4245454-257e-4106-b23c-537e0861b620/download) | `9eea76cbc8af1392a0ddca9db54c1aec0492463ebddb3448e535444a94094a9f` |

- GitHub 七个附件的大小与哈希一致，[独立 Windows 验证管道](https://github.com/myucxy/TermLink/actions/runs/39249040527)通过。
- AppShip 三个 release 均为 `published`、artifact 均为 `ready`，匿名下载哈希一致；客户端 build 140 返回 `optional`，141 返回 `no_update`。
- APK 单一 signer 与 v2 签名、Windows Authenticode／RFC 3161／载荷签名连续性通过。专用模拟器原地升级及冷启动 UI 检查通过，保留 appId、首次安装时间和应用数据；未重跑完整 Relay 或 MI 6 测试。
- Server 全量单测、PostgreSQL 集成测试、go vet、部署包及公开文档检查通过；固定源码的 GitHub 源码检查与 Docker CI 通过。
- 本地 OCI 检查 amd64/arm64 ELF、版本、revision、非 root 用户、许可与两个平台的 SBOM／provenance。实际 amd64 容器的迁移、OIDC、healthcheck、重启及 AppShip 下载页／版本页读取通过。
- Docker index、两个平台镜像及各平台 SBOM／provenance 已使用原有本地 Cosign 密钥签名并在远端独立验证；不声称使用 GitHub OIDC keyless 身份。
- `0.3.11-build.141`、`0.3.11` 和 `latest` 已核对为同一 index digest：`sha256:8ce6cd78ba95aa52e7aa11a244425646e24d1a0583c9e9c0cfbb6cea9f438201`。旧 latest 为 `sha256:3660cd6252c21acdd93286ebf4f6e9176d3b0b798705a4613aaec503f0044104`。对应源码下载说明已附加到镜像。

NAS 生产服务未部署更新；镜像发布不会自动更换现有容器。旧页面保存的 AppShip 配置在新 Server 中不再生效，部署时应核对本机启动配置。

0.3.8 / 138、0.3.9 / 139 等修复前的 Windows 升级器，首次升级建议在 Windows 本机运行新版 Setup 覆盖安装；详情见 [0.3.10 升级说明](RELEASE_0.3.10_140.md#升级注意)。本版未修复个别 PC 的设备身份校验 401。
