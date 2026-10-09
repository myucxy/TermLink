# 统一发布构建

2026-10-04 当前 Docker 发布方式：用户指定本机编译后直接 push Docker Hub，不走 GitHub Actions；
历史 tag 工作流已改为手动入口。镜像版本、源码 revision、架构、digest 和签名/证明证据仍须核验，
不把 BuildKit 生成的未签名 SBOM/provenance 宣称为 Cosign 已验证。详见运行手册的当前 Docker 发布入口。

仓库根入口为 `scripts/Build-Release.ps1`。它只编排已有的组件构建器并把产物集中到
`artifacts/release/<Version>-<BuildNumber>/`；本入口不会上传产物，也不会调用外部发布技能。

实际提交、AppShip 上传、Docker Hub 发布、本机客户端升级和服务部署的顺序、故障恢复与复核清单见
[发布与部署运行手册](RELEASE_RUNBOOK.md)。该手册记录已经现场验证过的命令约束；遇到相同错误时
不要跳过门禁或重复使用已知无效的调用方式。

2026-10-09 起，自有代码采用 GPLv3（`GPL-3.0-only`）。对外分发须携带 LICENSE、NOTICE 及第三方许可，并落实[对应源码交付检查](RELEASE_RUNBOOK.md#许可证与对应源码交付2026-10-09)；制品哈希和签名校验不替代该人工检查，core 计划后续公开也不豁免源码交付。

Android 独占触发的 Windows Agent 自动升级协议、事务、取消与回滚边界见
[Windows Agent 远程自动升级](REMOTE_AGENT_UPDATE.md)。

Windows 命令配置、Server 能力同步和 Android 受控启动的兼容契约见
[远程终端命令协议](REMOTE_TERMINAL_COMMANDS.md)。包含 migration 22 的服务端必须先于自定义命令发布。

当前传输范围（2026-10-04）：CLI 与 AI 仅使用服务器转发（Relay Only）。LAN/NAT 及直连切换测试不属于当前发布门禁，记录为不适用；保留服务器转发相关回归。详见运行手册的“当前传输验收范围”。

## 版本与目标

客户端消费预编译 terminal-core DLL/SO，版本/build 规则不变。
正式构建通过 `scripts/Verify-NativeArtifacts.ps1 -RequireClean`，记录应用 revision 和锁文件中的 core revision；
核心文件大小、SHA-256 与 dirty 状态由 `terminal-core.lock.json` 固定，构建机器不需要 core checkout。参见[双仓库说明](development/REPOSITORY_LAYOUT.md)。

版本分配必须遵循 [DEV 与正式发布版本规则](RELEASE_RUNBOOK.md#dev-与正式发布版本规则)：

- 下一轮新正式版本从 `0.3.0` 开始，之后默认自动递增 patch；已有更高发布版本时从现场值继续递增。
- DEV/Debug 沿用对应平台最后成功发布的 `Version` 与 `BuildNumber`，均不加号，不能提前使用尚未发布的 `0.3.0`。
- 仅执行正式发布时自动计算下一版本/build；build 延续已用序列，不归零。同轮构建与恢复固定同一组值，发布成功后才更新对应平台的 DEV 基线。
- 本脚本不自动选号或修改发布基线，版本由发布执行者/编排核对现场后显式传入；直接调用 Gradle/.NET 也须显式传入已核验的 DEV 版本，不能依赖组件默认值。

每次调用只接收一份 `-Version` 和 `-BuildNumber`：

- `Version` 必须是三段数字，例如 `1.4.2`；
- `BuildNumber` 范围是 `1..65535`；
- Server 与 Android 使用 `Version`，Android `versionCode` 使用 `BuildNumber`；
- Windows 使用四段版本 `Version.BuildNumber`，例如 `1.4.2.107`。

目标必须通过开关显式选择：`-ServerImage`、`-WindowsSetup`、`-WindowsMsix`、
`-AndroidApk`、`-AndroidAab`。Android APK/AAB 会合并为一次 Gradle 调用。

先查看完全无副作用、可机读的计划：

```powershell
# $devVersion / $devBuild 为已核验的所选平台最后成功发布值；
# 平台基线不同则分开构建。DEV 输出目录与正式发布目录隔离。
& .\scripts\Build-Release.ps1 `
  -Version $devVersion -BuildNumber $devBuild `
  -ServerImage -WindowsSetup -WindowsMsix -AndroidApk -AndroidAab `
  -ReleaseMode Development `
  -OutputDirectory "artifacts/release/dev/$devVersion-$devBuild" `
  -Plan
```

`-DryRun` 同样不检查本机工具和签名文件、不创建目录、不执行组件脚本；它输出带
`operation: dry-run` 的确定性 JSON。这样可以在尚未注入证书的 CI 计划阶段验证编排。

## Production 签名前置条件

默认 `-ReleaseMode Production`。真实执行会在创建输出目录或启动任何构建之前一次性检查
所有已选目标的签名资料，任一缺失就整体失败：

- Server：`-ServerSigningKeyPath` 和 `cosign`。本地多架构 OCI tar 通过
  `cosign sign-blob` 签名；密钥有密码时用 `-ServerSigningKeyPassword`（SecureString）。
- Windows：`-WindowsCertificatePath`、`-WindowsCertificatePassword`（SecureString）和
  绝对 HTTPS `-WindowsTimestampUrl`。Windows SDK SignTool 使用的公共 RFC 3161 服务通常只公开
  HTTP 端点；此时必须显式传 `-AllowHttpWindowsTimestamp`。响应本身由时间戳机构签名并绑定待签摘要，
  发布门禁仍会验证时间戳证书和 RFC 3161 OID。HTTP 不提供传输保密性和可用性保护，因此不会被静默启用。
  Development MSIX 还必须提供与证书 Subject 完全一致的
  `-WindowsPublisher`。Setup 另须显式传入 `-ExpectedWindowsSignerSha1`，产物的
  Authenticode 状态、SHA-1 证书指纹、`ProductVersion`、`FileVersion` 和 RFC 3161
  时间戳必须全部通过复核。时间戳门禁直接检查 PE SignedData 的 RFC 3161
  timestamp-token OID；旧式 Authenticode countersignature 不会被当成 RFC 3161 接受。
- Android：`-AndroidKeystorePath`、`-AndroidKeystorePassword`（SecureString）和
  `-AndroidKeyAlias`，可选 `-AndroidKeyPassword`。也兼容已有的
  `TERMLINK_RELEASE_STORE_FILE`、`TERMLINK_RELEASE_STORE_PASSWORD`、
  `TERMLINK_RELEASE_KEY_ALIAS`、`TERMLINK_RELEASE_KEY_PASSWORD` 进程环境变量。
  APK/AAB 均须传入 `-ExpectedAndroidSignerSha256`；APK 还须传入 `-AndroidSdkPath`（也可通过
  `ANDROID_SDK_ROOT`/`ANDROID_HOME` 定位 SDK）。最终 APK 必须通过 v2 或更新签名方案，
  `Number of signers` 必须严格等于 1，且该唯一签名证书 SHA-256 必须与连续性指纹一致。
  AAB 会直接复核 protobuf base manifest 的 applicationId、versionName、raw/compiled versionCode，
  要求唯一 JAR `.SF`/签名块和唯一 CMS SignerInfo，校验 PKCS#7、SHA-256 manifest 绑定、每个载荷
  ZIP entry 的 SHA-256 覆盖，并要求证书指纹与 APK 使用同一条批准的连续性指纹。

Production AAB 已接入完整后置身份、版本、单签名者、连续性指纹和载荷摘要复核，可以进入
Production manifest。Production MSIX 仍会在计划或构建开始前明确失败，因为它尚未接入与
Setup 等价的完整身份、版本、连续性签名和 RFC 3161 时间戳复核；MSIX 只能以
`-ReleaseMode Development` 构建用于本地验证。

连续性指纹不是秘密，可以进入审计日志；PFX/keystore 密码仍只能使用 SecureString 或受保护的
进程环境变量。指纹门禁不根据证书 Subject、Issuer 或是否自签进行判断：Windows 允许经当前系统
验证为 `Valid` 的自签证书，但不会把自签证书描述为公开 CA 信任。更换任一指纹属于发布者轮换，
必须先完成旧客户端可接受的迁移方案，不能仅修改发布参数绕过。

秘密不会写入计划、命令行日志或 artifact manifest。传给 Android 的环境变量只在 Gradle
子进程期间设置，随后恢复原值。

正式发布流程中的构建示例（版本/build 已按上述规则固定；此命令只构建，不上传）：

```powershell
$windowsPassword = Read-Host 'Windows PFX password' -AsSecureString
$androidPassword = Read-Host 'Android keystore password' -AsSecureString
$cosignPassword = Read-Host 'Cosign key password' -AsSecureString

& .\scripts\Build-Release.ps1 `
  -Version $version -BuildNumber $build `
  -ServerImage -WindowsSetup -AndroidApk -AndroidAab `
  -ServerSigningKeyPath 'D:\ReleaseSecrets\termlink-server.key' `
  -ServerSigningKeyPassword $cosignPassword `
  -WindowsCertificatePath 'D:\ReleaseSecrets\termlink-code-signing.pfx' `
  -WindowsCertificatePassword $windowsPassword `
  -WindowsTimestampUrl 'http://timestamp.acs.microsoft.com' `
  -AllowHttpWindowsTimestamp `
  -ExpectedWindowsSignerSha1 '<40 hex SHA-1 continuity fingerprint>' `
  -AndroidSdkPath 'D:\Dev\Env\Android\Sdk' `
  -AndroidKeystorePath 'D:\ReleaseSecrets\termlink-android.jks' `
  -AndroidKeystorePassword $androidPassword `
  -AndroidKeyAlias 'termlink' `
  -ExpectedAndroidSignerSha256 '<64 hex SHA-256 continuity fingerprint>'
```

`-ReleaseMode Development` 允许 Server 和 Android 生成未签名本地产物；Windows 的既有
Setup/MSIX 构建器始终要求开发 PFX。它不改变“绝不上传”的约束。

## Server no-push 与 Publish 闸门

Server 使用 `docker buildx build --output type=oci,dest=...` 生成本地多架构 OCI tar，命令中
没有 `--push`。`-Publish` 是为未来审核后的发布实现预留的显式闸门；当前传入它会立即报错，
即使同时使用 `-Plan` 也不会调用 registry、AppShip 或其他上传入口。

构建前运行 `docker buildx ls`，选择支持多架构 OCI 导出的构建器。经典 image store 下的
`docker` driver 不支持 OCI exporter，会报 `OCI exporter is not supported for the docker driver`。
已有 `docker-container` 构建器时，用进程环境变量 `$env:BUILDX_BUILDER = '<builder-name>'`
选择它，再调用构建脚本；不要为此修改 Docker Desktop 全局 image store 或默认构建器。
本机 2026-10-05 验证使用已有的 `termlink-release-117`，其他机器须按现场清单选择。

本地验证器按镜像层的覆盖/删除顺序查找 `/termlink-server`，不能假定程序在最后一层，
因为许可证等后续 `COPY` 会生成新层。双仓库拆分后的实际入包核验见[打包验证记录](development/PACKAGING_SPLIT_VALIDATION.md)。

以下为历史 Server registry 工作流说明，当前按页首规则本机直推；`.github/workflows/release.yml` 已取消 tag 自动触发。
历史工作流曾在严格的 `vX.Y.Z` tag 上执行，
目标固定为 `docker.io/myucxy/termlink`。工作流使用受保护的 `DOCKERHUB_USERNAME` 与
`DOCKERHUB_TOKEN` secrets 登录 Docker Hub，构建 `linux/amd64`/`linux/arm64` manifest，生成
BuildKit provenance 和 SPDX/CycloneDX SBOM，并通过 GitHub OIDC/Cosign 对 immutable digest
及三类 attestation 签名和就地复核。校验通过后，将同一 digest 同步到 `latest` 标签并复核；
发布任务串行执行，版本标签保持不可变。部署仍使用明确的版本及 digest。

## AppShip Production 包装门禁

AppShip 只能通过 `scripts/Publish-AppShipProduction.ps1` 接收本仓库的 Android APK 或
Windows Setup。包装入口拒绝 `development`、`.pending`、改名或放错版本目录的 manifest，
并要求文件路径、大小和 SHA-256 与 Production manifest 完全一致。真实上传前它会重新运行
`release/Verify-LocalRelease.ps1`；manifest 同时包含 APK 与 Setup 时会复核两端，而不是只复核
本次选中的单个文件。随后再次检查所选文件的大小与哈希，才把这个精确路径交给经过审核的
`appship-publish` 客户端。
Production AAB 是供应用商店分发的正式制品，但当前不是 AppShip 包装入口支持的上传类型；
不得把 AAB 改名为 APK 或绕过 manifest/验证器交给 AppShip。

先在 PowerShell 5.1 或 7 中只读查看发布计划；`-Plan` 不需要凭据、不创建状态文件、不上传：

```powershell
& .\scripts\Publish-AppShipProduction.ps1 `
  -ManifestPath '.\artifacts\release\0.1.20-26\artifact-manifest.json' `
  -Target AndroidApk `
  -BaseURL 'https://appship.example.com' `
  -AndroidSdkPath 'D:\Dev\Env\Android\Sdk' `
  -ExpectedAndroidSignerSha256 '<64 hex SHA-256 continuity fingerprint>' `
  -ExpectedWindowsSignerSha1 '<40 hex SHA-1 continuity fingerprint>' `
  -Plan
```

真实发布必须使用 PowerShell 7.2 或更新版本，并传入受保护的 `PSCredential`、审核过的
AppShip 客户端路径、`-SignatureApproved` 和非空审核说明。密码不会进入命令参数、manifest、
计划输出或状态文件。状态文件名由包装入口固定为
`.appship/<application>-stable-<version>-build<build>-signature-approved.json`。首次调用不加
`-Resume`；只有前次结果中断或不确定时，才用完全相同的 manifest、文件、版本、说明和状态路径
加 `-Resume` 恢复。包装入口不会把开发自签证书描述为公开 CA 信任证书。

真实调用示意（签名密码与 AppShip 密码均不写入命令文本）：

```powershell
$appShipCredential = Get-Credential -Message 'AppShip release_manager credential'
& .\scripts\Publish-AppShipProduction.ps1 `
  -ManifestPath '.\artifacts\release\0.1.20-26\artifact-manifest.json' `
  -Target AndroidApk `
  -BaseURL 'https://appship.example.com' `
  -Credential $appShipCredential `
  -PublishScriptPath 'D:\ReviewedTools\appship-publish\scripts\publish.ps1' `
  -AndroidSdkPath 'D:\Dev\Env\Android\Sdk' `
  -ExpectedAndroidSignerSha256 '<64 hex SHA-256 continuity fingerprint>' `
  -ExpectedWindowsSignerSha1 '<40 hex SHA-1 continuity fingerprint>' `
  -SignatureApproved `
  -SignatureReviewNote 'Exact Production artifact independently verified'
```

## 产物清单与路径

输出路径必须严格位于仓库的 `artifacts/release/` 之下。相对的 `-OutputDirectory` 以仓库根
解析，路径逃逸会被拒绝。已有同名产物默认拒绝覆盖；只有显式 `-Force` 才会替换目标文件。

Production 构建先生成同目录的临时 manifest，完成产物哈希、版本、签名方案、签名状态、
连续性指纹和时间戳核验后才原子提升为 `artifact-manifest.json`；任何核验失败都不会留下可供
发布的正式 manifest。Development manifest 明确标为 `development`，不会冒充生产核验结果，
也会被 AppShip 包装入口拒绝。Schema 位于
`release/artifact-manifest.schema.json`。每个条目至少包含：

```json
{
  "path": "artifacts/release/1.4.2-107/TermLink-Android-1.4.2-107.apk",
  "size": 123456,
  "sha256": "<64 lowercase hex characters>",
  "platform": "android",
  "type": "apk",
  "version": "1.4.2"
}
```

执行轻量验收（不需要生产证书，也不运行组件构建；测试会临时生成并清理隔离 AAB 签名证书）：

```powershell
pwsh -NoProfile -File .\release\Test-BuildRelease.ps1
```

测试覆盖 PowerShell parser、重复 `-Plan/-DryRun` 输出一致性、no-push、Production 缺签名
fail-closed、严格 APK signer count、Android/Windows 连续性指纹、ProductVersion/FileVersion、
完整 AAB protobuf 身份/版本/JAR 单签名者/载荷摘要及篡改回归、RFC 3161/legacy countersignature、
MSIX Production 拒绝、Production/development manifest、
AppShip 精确路径与哈希门禁、`-Publish` 拒绝以及严格输出路径。
