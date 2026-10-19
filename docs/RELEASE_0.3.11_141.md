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
