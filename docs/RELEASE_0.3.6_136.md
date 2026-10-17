# TermLink 0.3.6 / 136

## 2026-10-18 发布记录

Android、Windows 与完整对应源码已发布到 [GitHub v0.3.6](https://github.com/myucxy/TermLink/releases/tag/v0.3.6) 和 AppShip stable；AppShip 三个制品均已匿名下载并核对 SHA-256，GitHub 七个附件 digest 与本地逐一一致。
两个客户端的 build 135 返回 `optional`，build 136 返回 `no_update`。

| 制品 | 下载 | SHA-256 |
| --- | --- | --- |
| Android APK | [AppShip APK](https://appship.kget.cc:8910/api/v1/artifacts/ac7eef98-6151-4e1b-b633-7c2ef8ac621d/download) | `c0165b43c9de00451266d08ebf16186e6e83ba667ff30f5c76eaecd5511ddb0c` |
| Windows x64 Setup | [AppShip Setup](https://appship.kget.cc:8910/api/v1/artifacts/e1860848-5713-4b0f-8975-b0b94805960e/download) | `bdca4ffd7355fc4ed93b20f9123dac05c0edcd6271256fe53722d05e54e70ad8` |
| 完整对应源码 | [匿名源码下载](https://appship.kget.cc:8910/api/v1/artifacts/17695a71-5155-4b90-89e9-0b6534e7d743/download) | `68132b88ea4b3f1f871cf61ffac6b45c1e999071b152d1694e917487d9e49b74` |

- 应用与 Git tag `v0.3.6`：`dfa727c3731e9f14bddc0c4f69ed98ddb44efecd`；core：`1c907c51b3c6652b23a7d7216ff1bb13d3410a69`。
- Android release：`2aea4156-d43f-41c6-9ef5-ff75b66eb2f3`；Windows release：`c14c0a35-4011-4b5c-b793-454abdf1eb70`；源码 release：`918b218f-f57c-4a1f-a0c7-1ec616b45b3a`。
- Windows 保持原签名者，RFC3161 和 17 个自有载荷签名通过；APK 版本 0.3.6 / 136、唯一 signer 及 v2 签名通过。
- GitHub Release ID `415995809`，`draft=false`、`prerelease=false`，七个附件完整；[最终独立验签管道](https://github.com/myucxy/TermLink/actions/runs/39065172252)全部通过后，本机既有授权凭据完成发布。
- 本轮修复托管 Windows runner 导入自签公钥时的交互等待、新 Build Tools `V2 Signer` 输出兼容，以及 GitHub 自带令牌无法发布含 workflow 差异提交的 403 限制。只在一次性 runner 导入已核对指纹的公钥；发布最终写入留在本机，不新增远端长期令牌 Secret。
- Docker CI 位于后续 main 提交 `46c8f8006b6573a4c5e6f3a75a1e75d43eb0f7b5`，[双架构构建与 amd64 运行检查通过](https://github.com/myucxy/TermLink/actions/runs/39059735168)。它不改变本批客户端来源，也不发布 Docker 镜像。

后续管道修复与发布结果提交未进入本批已编译客户端；制品、tag 和对应源码始终保持 `dfa727c`，未重编译替换或移动 tag。本批没有执行设备覆盖安装、全量真机回归或服务部署；上一版 AppShip 制品和 Docker 0.3.5 保留。

下方为随客户端源码提交的初始发布说明；实际平台范围补充为 AppShip 与 GitHub，签名与来源保持同一批。

## 初始发布说明

本轮发布范围为 GitHub Release：Android APK、Windows x64 安装包和完整对应源码。
版本依据已核验的 Android、Windows、Docker 0.3.5 / 135 继续递增，固定为 0.3.6 / 136。
此文件是随源码提交的发布说明；实际发布成功以 GitHub Release 的 published 状态和管道结果为准。

## 更新内容

- 提供 GitHub 客户端发布入口和可重试的发布管道，本地沿用既有签名，Actions 独立验证后发布。
- 补齐中英文项目说明、参考项目、实际第三方依赖及 Go 原始许可包。
- 清理被当前指南取代的旧总体设计、会话交接和设计图，维护文档入口和构建指南。
- 固定 npm 官方下载来源，保留原版本及 integrity；更新嵌入式 Web 资源来源清单。
- 对应源码按实际应用/core revision 导出，包含锁定的 core Rust 依赖及离线构建配置。
- 新增文档、精确测试夹具秘密扫描、Server/Web、源码打包和发布安全边界检查。

## 文件与来源

- `TermLink-Android-0.3.6-136.apk`
- `TermLink-Setup-0.3.6.136-x64.exe`
- `TermLink-0.3.6-136-source.zip` 及 `.sha256.json`
- `artifact-manifest.json`、`github-release.json`、`SHA256SUMS`

应用 commit、core commit、文件大小和 SHA-256 记录在附件清单中；Git tag 固定到同一应用 commit。
终端核心继续消费锁定 revision `1c907c51b3c6652b23a7d7216ff1bb13d3410a69` 的五个原生库，不用另一 RC 代替来源。

Android signer SHA-256：`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`。
Windows signer SHA-1：`91AE60BC06D866628B8EB2C62F32BDE9DAB56466`，保持更新连续性；既有自签名身份不等于公开 CA 信任。

## 验证范围

前一轮独立检出的 Android、Windows、Web 构建及双仓库 CI 已通过，core 对应源码离线测试 837 项通过。
本批重新进行正式签名构建、原生库/清单/源码哈希验证，并由 GitHub runner 独立验证 APK、Setup 载荷及时间戳。
这是发布与源码整理版本，不宣称新增终端运行时功能或重新完成真机全量回归。

当前 CLI 与 AI 继续使用 Relay Only。AppShip 更新通道和 Docker Hub 不由本 GitHub 管道变更；未自动部署服务或升级客户端。
仓库仍为私有时，Release 及附件遵循相同访问权限。
