# 0.3.0 / 128 发布记录（Docker 已发布，客户端暂缓）

2026-10-04，用户明确暂缓 Android/Windows AppShip 发布，并单独批准 Docker 本轮无 Cosign 签名发布。
本记录不代表客户端已上线，也不代表本地/NAS Server 已升级。

## Docker Hub

- 本机构建源码：`634b5ce7b8e5522321be758b21c3c039058d62fa`，构建时工作树干净、已推送 `origin/main`。
- 版本/build：`0.3.0+128`；仓库 `docker.io/myucxy/termlink`。
- 已发布标签：`0.3.0`、`latest`，两者指向同一 OCI index：
  `sha256:dd39557370f80d62ca560aaa9f5f73eaaf9bc6729668377ae812eec7a027a2c1`。
- amd64 image manifest：`sha256:039e277880837d62b1d36f588bcbd8c2e541cec0a9ffd03fd1bdabcf8122b8d8`。
- arm64 image manifest：`sha256:934ae678db91fdfd8b27b619b8364224a04ee6871359f159af5c1e0a4bf370f8`。
- 两架构均验证 OCI version/revision、非 root 用户、SPDX SBOM、SLSA provenance v1 及 subject 绑定。
- **无 Cosign 签名**：依据用户本轮明确接受的例外发布；BuildKit 证明不是签名，不声称 GitHub OIDC 已验证。
- 未使用 GitHub Actions、GHCR 或 GitHub Release；版本镜像验证后才同步 latest。

本地 OCI tar SHA-256：`526cfaa9ba837db83f7177305239ecc6f7c3f84920b6e13d85b15f56403fb4a5`。
本地 OCI index 为 `sha256:21189f57901636e70348cfdf4b32388bfe8dd8dcae3e8101b68c77c73e5a4f66`；
registry exporter 生成了不同的 attestation/index。两个实际平台 image manifest 与本地候选完全一致，
已独立读取远端 index、manifest、config、attestation 并核验哈希，不能混用本地 index 与远端 digest。

Docker Desktop pull 遇到 registry 网络超时；启动检查使用已核验 OCI 中的 amd64 config/layers，转换为
经典 Docker archive 后加载。14 层解压后的 SHA-256 与 config `rootfs.diff_ids` 一一匹配，加载后的
image ID 精确为 `sha256:9085335ae334aacd479be1857a468e0c6ad1b6ad4b54c3d11ef2e6770bde6ecc`。
在独立临时网络、临时 PostgreSQL 数据库下通过启动迁移、`/readyz`、内置 healthcheck、client-config
和 OIDC discovery；关闭直连。临时容器/网络已清理，未使用生产数据。arm64 完成构建与制品验证，
本轮未在 arm64 主机运行容器。既有 Server/PostgreSQL 仍健康，未部署该新镜像。

验证文件（ignored）：`artifacts/validation/release-128-registry-latest-verification.json`、
`release-128-container-smoke.json`、`release-128-oci-634b5ce-verification.json`。

部署应使用不可变引用：
`docker.io/myucxy/termlink:0.3.0@sha256:dd39557370f80d62ca560aaa9f5f73eaaf9bc6729668377ae812eec7a027a2c1`。
回滚镜像：`docker.io/myucxy/termlink:v0.2.61@sha256:68611748731e2039ce6f1411fd123983deed2577b07e28b657ed9abcd3e90be2`。
不通过移动版本标签回滚；部署前仍须备份及核对配置、数据卷与 Relay Only 策略。

## 客户端本地验证，未上传 AppShip

按钮顺序最终为 **CLI → AI 聊天 → 文件**。未报告 AI 能力的旧 Windows 仍显示 AI 聊天，点击解释升级要求；
支持 AI 的 HOME-PC 打开锁定设备的创建面板。CLI 仍为新建会话入口，只移除文字前的加号。

- 最后一次正式 AppShip 基线仍为 Android、Windows `0.2.71 / 127`。
- 最终 Android UI 源码：`46aadae9392edaa466d3e56cf3e5a4e17ec702da`；相对 Docker 源码仅修改安卓按钮文案和说明，
  没有修改服务端。不得用它覆盖已发布 Docker `0.3.0`。
- Android DEV 保持 `0.2.71 / 127`，模拟器 `emulator-5556` 原地安装。
- 同模拟器正式包已原地安装本地签名候选 `0.3.0 / 128`，保留账号与首次安装时间。
  APK 位于 `artifacts/release/device-actions-final/0.3.0-128/TermLink-Android-0.3.0-128.apk`，
  SHA-256 `8f1058f9b6c0e425f3c9e811adcd86ae31f0e0dfb3d03d4e031534b3f105dc4a`。
- APK v2、单 signer、包名与版本独立验证通过，signer SHA-256：
  `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`。
- Android app 单测 365 项通过；按钮及旧电脑升级提示在真实产品页面验证。其余本轮功能测试见
  [设备与 AI 验证记录](DEVICE_AI_RELEASE_030.md)。最后仅移除 `+` 的文案变更重新编译并验证模拟器点击。
- 本机 Windows 仍是之前已安装的 `0.3.0.128` / `a153d00` 候选，本次按钮修改无需重装 Windows。

原 `artifacts/release/0.3.0-128` 双端候选已落后于安卓 UI 修正，不可直接上传。
恢复客户端发布时重新核对线上占号和平台源码、准备最终正式 manifest、复核签名并记录人工审核；
不能以已安装候选或已发布 Docker 替代客户端发布结论。客户端回滚基线仍为原 `0.2.71/127` 制品。
