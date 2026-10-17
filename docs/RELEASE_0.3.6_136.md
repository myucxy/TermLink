# TermLink 0.3.6 / 136

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
