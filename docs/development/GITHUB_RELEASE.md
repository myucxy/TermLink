# GitHub 客户端发布管道

客户端正式发布由附注标签 `vX.Y.Z` 触发。GitHub Actions 在同一源码提交上构建和签名 Android APK、Windows x64 Setup，
校验固定签名指纹、Windows RFC3161 时间戳及载荷、Android 签名、双端原生依赖和可下载的完整对应源码。
`Build clients from tag` 通过后，独立发布任务将核验过的同一批字节上传到 GitHub Release。
Server 继续使用 [`server-vX.Y.Z` 标签流程](../RELEASE_RUNBOOK.md#server-标签发布与-docker-hub-交接)。

1. 查询实际的 GitHub Releases 及迁移前 AppShip 的最终版本，分配严格递增的 version/build。等待目标 `main` 提交的 `Repository checks` 全部通过。
2. 先在 `main` 上手动运行 `Build clients from tag` 检查两端签名及源码交付。演练使用最后已核验的正式 version/build，只有日志随运行保留。
3. 经正式发布确认后，对目标提交创建 `git tag -a vX.Y.Z -m "build=N" <commit>`，随后单独推送该标签。一次失败重试使用同一标签和构建编号。
4. 核验 Release 中的 APK、Setup、对应源码 ZIP、sidecar、`artifact-manifest.json`、`github-release.json` 和 `SHA256SUMS`。

签名材料保存在 `github-client-signing` 环境 Secrets，允许 `main` 演练及受规则集限制的 `v*` 标签。
签名步骤专用的密钥随托管 runner 作业清理，发布 job 只读取核验后的产物，并使用当次 GitHub 令牌。
迁移期固定上一版已公开对应源码 ZIP 的 SHA-256，复用其中与当前原生库锁一致的 core 源码和 Rust 依赖；core revision 改变时需先更新该输入。
发布 job 中断时选择 **Re-run failed jobs**，复用保留的签名产物；已上传的同名附件必须匹配原字节。
macOS 正式包需要 Developer ID Application、Developer ID Installer 和 Apple 公证凭据，以及 Apple Silicon/Intel 各自的签名验证。
已安装客户端的旧版更新功能仍读取历史渠道，GitHub 版本自动发现需由更新源适配接通。

独立的 `Docker server image checks` 在 Server/Docker 配置的 push/PR 及手动调度时运行：
复用现有构建和验证脚本生成 amd64/arm64 OCI，检查架构、版本、revision，再检查 amd64 镜像的
非 root 用户、入口、许可文件。隔离 PostgreSQL 上实际启动 Server，验证数据库迁移后的 `/readyz`、
容器内 healthcheck、内嵌 OIDC discovery 和容器重启恢复。arm64 只核验构建及 ELF，不宣称已运行。
只使用一次性数据库和本地镜像，不登录 Docker Hub、不推送镜像、不触碰正式签名或生产数据。

## 本机已签名批次的恢复流程

1. 按[运行手册](../RELEASE_RUNBOOK.md#dev-与正式发布版本规则)查询 AppShip、Docker Hub、GitHub
   实际版本，固定严格递增的 version/build。本轮后续重试复用同一组值。
2. 提交并推送源码和发布说明，等待该 commit 最新一次 `Repository checks` 全部通过。
3. 按本轮平台范围使用 `scripts/Build-Release.ps1 -AndroidApk`，或追加 `-WindowsSetup`；
   调用 `release/Verify-LocalRelease.ps1 -RequireSourceIdentity` 独立验证。包含 Setup 时再要求
   `-RequireWindowsTimestamp` 和 Windows signer；Android-only 批次不要求 Windows 证书。
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

命令生成 `SHA256SUMS`、`github-release.json`，Android-only 上传六个附件，双端上传七个附件，
再触发 `GitHub client release` 并等待验证完成。两种批次都包含专用对应源码 ZIP 和 sidecar。
`github-release.json` 的 SHA-256、源码 commit 和 Release ID 固定到 dispatch 输入。
Actions 仅在 `main` 上手动调度，串行校验；运行标题绑定 Release ID 和完整 metadata SHA-256。
本机最终核验成功运行的 workflow 路径、main 分支、dispatch 事件和标题，再复核同源码的 CI、tag 和远端附件 digest，才解除 draft。
运行的是 dispatch 时固定的 main 管道代码；从 Git 对象读取所选发布 commit 的原生锁和签名策略。
这样旧版本的校验或重试可以使用修复后的管道，不需要移动 tag、改写对应源码或重编译安装包。
工作流仅进行校验，使用自带 `GITHUB_TOKEN`；读取私有草稿需要 `contents:write`（GitHub 的草稿访问规则），
代码不执行发布写入，无需另存 GitHub PAT、Android keystore 或 Windows PFX。
GitHub 自带令牌不能修改 workflow，部分旧提交的 Release 发布会因此返回 403；最终写入使用本机既有授权，不把长期令牌上传为仓库 Secret。

## 重试和验证

- 上传中断后运行相同 stage 命令。同名附件必须具有相同大小与 GitHub SHA-256，不覆盖、不删除不同附件。
- CI 校验失败保留草稿；修复环境问题后用同一 `github-dispatch.json` 的三个输入重跑 workflow。
  代码修复导致源码 commit 改变时，不能移动已使用 tag 或混用不同构建。
- 本地等待进程中断后，CI 仍会完成验证。确认对应运行成功后，使用同一身份显式恢复最终发布：

  ```powershell
  python scripts/github_release.py publish --directory artifacts/release/<version>-<build> `
    --release-id <草稿ID> --revision <原制品commit> --metadata-sha256 <清单SHA256> `
    --verification-run <成功的验证运行ID>
  ```
- 已发布内容不经 stage 重传。相同 publish 重跑会重新验证并报告既有发布，不替换字节。
- 下载附件后可核对 `SHA256SUMS`；完整源码以专用 source ZIP 为准，GitHub 自动生成的源码包不包含同级 core。
- Windows 当前为既有自签名身份。托管 runner 只为本次验签导入指纹固定的**公钥证书**，
  不代表公开 CA 信任或 SmartScreen 信誉；不改变维护者电脑的信任库。
- 仓库保持原可见性。私有仓库的 Release 需要相同访问权限，不宣称已向匿名用户开放。

接口依据：[GitHub Release API](https://docs.github.com/en/rest/releases/releases)、
[workflow dispatch](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)。
