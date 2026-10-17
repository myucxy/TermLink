# GitHub 客户端发布管道

GitHub Release 分发同一批正式签名 APK、Windows x64 Setup 和带 core Rust vendor 的完整对应源码。
构建和签名在维护者本机完成，私钥与口令不上传 Actions；GitHub 托管 Windows runner 下载草稿附件，
核验哈希、源码、双端原生签名、版本、Windows 载荷及 RFC3161 时间戳，然后发布草稿。
Docker 正式发布仍由本机直接推送 Docker Hub；本管道不发布 Docker、AppShip 或升级现有安装。

独立的 `Docker server image checks` 在 Server/Docker 配置的 push/PR 及手动调度时运行：
复用现有构建和验证脚本生成 amd64/arm64 OCI，检查架构、版本、revision，再检查 amd64 镜像的
非 root 用户、入口、许可文件。隔离 PostgreSQL 上实际启动 Server，验证数据库迁移后的 `/readyz`、
容器内 healthcheck、内嵌 OIDC discovery 和容器重启恢复。arm64 只核验构建及 ELF，不宣称已运行。
只使用一次性数据库和本地镜像，不登录 Docker Hub、不推送镜像、不触碰正式签名或生产数据。

## 每轮执行

1. 按[运行手册](../RELEASE_RUNBOOK.md#dev-与正式发布版本规则)查询 AppShip、Docker Hub、GitHub
   实际版本，固定严格递增的 version/build。本轮后续重试复用同一组值。
2. 提交并推送源码和发布说明，等待该 commit 最新一次 `Repository checks` 全部通过。
3. 使用 `scripts/Build-Release.ps1 -WindowsSetup -AndroidApk -ReleaseMode Production` 构建；
   调用 `release/Verify-LocalRelease.ps1 -RequireSourceIdentity -RequireWindowsTimestamp` 独立验证。
   显式传入 `release/github-release-policy.json` 中的连续性指纹和本机工具路径。
4. 用 `scripts/New-CorrespondingSourceArchive.ps1 -IncludeCoreVendor` 导出选定 commit 的对应源码，
   文件名固定为 `TermLink-<version>-<build>-source.zip`，与 sidecar 一起放在同批 `artifacts/release/<version>-<build>/`。
5. 创建并推送只指向该 commit 的 `v<version>` tag；不移动既有 tag。
6. 将 GitHub 凭据仅绑定到当前进程的 `GH_TOKEN`，运行下方命令。可使用现有 Git Credential Manager；
   不把令牌写进文件、命令参数或日志。提交草稿需要仓库 Contents:write 和 Actions:write 权限。

```powershell
python scripts/github_release.py stage `
  --directory artifacts/release/<version>-<build> `
  --notes docs/RELEASE_<version>_<build>.md
```

命令生成 `SHA256SUMS`、`github-release.json`，上传七个附件，再触发 `GitHub client release`。
`github-release.json` 的 SHA-256、源码 commit 和 Release ID 固定到 dispatch 输入。
Actions 仅在 `main` 上手动调度，串行发布；再查同提交的最新 CI 和 tag，全部校验通过才解除 draft。
工作流自带 `GITHUB_TOKEN`，无需另存 GitHub PAT、Android keystore 或 Windows PFX。

## 重试和验证

- 上传中断后运行相同 stage 命令。同名附件必须具有相同大小与 GitHub SHA-256，不覆盖、不删除不同附件。
- CI 校验失败保留草稿；修复环境问题后用同一 `github-dispatch.json` 的三个输入重跑 workflow。
  代码修复导致源码 commit 改变时，不能移动已使用 tag 或混用不同构建。
- 已发布内容不经 stage 重传。相同 workflow 重跑会重新验证并报告既有发布，不替换字节。
- 下载附件后可核对 `SHA256SUMS`；完整源码以专用 source ZIP 为准，GitHub 自动生成的源码包不包含同级 core。
- Windows 当前为既有自签名身份。托管 runner 只为本次验签导入指纹固定的**公钥证书**，
  不代表公开 CA 信任或 SmartScreen 信誉；不改变维护者电脑的信任库。
- 仓库保持原可见性。私有仓库的 Release 需要相同访问权限，不宣称已向匿名用户开放。

接口依据：[GitHub Release API](https://docs.github.com/en/rest/releases/releases)、
[workflow dispatch](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)。
