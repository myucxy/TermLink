# 统一发布构建

仓库根入口为 `scripts/Build-Release.ps1`。它只编排已有的组件构建器并把产物集中到
`artifacts/release/<Version>-<BuildNumber>/`；本入口不会上传产物，也不会调用外部发布技能。

## 版本与目标

每次调用只接收一份 `-Version` 和 `-BuildNumber`：

- `Version` 必须是三段数字，例如 `1.4.2`；
- `BuildNumber` 范围是 `1..65535`；
- Server 与 Android 使用 `Version`，Android `versionCode` 使用 `BuildNumber`；
- Windows 使用四段版本 `Version.BuildNumber`，例如 `1.4.2.107`。

目标必须通过开关显式选择：`-ServerImage`、`-WindowsSetup`、`-WindowsMsix`、
`-AndroidApk`、`-AndroidAab`。Android APK/AAB 会合并为一次 Gradle 调用。

先查看完全无副作用、可机读的计划：

```powershell
& .\scripts\Build-Release.ps1 `
  -Version 1.4.2 -BuildNumber 107 `
  -ServerImage -WindowsSetup -WindowsMsix -AndroidApk -AndroidAab `
  -ReleaseMode Development `
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
  APK 另须传入 `-ExpectedAndroidSignerSha256` 和 `-AndroidSdkPath`（也可通过
  `ANDROID_SDK_ROOT`/`ANDROID_HOME` 定位 SDK）；最终 APK 必须通过 v2 或更新签名方案，
  `Number of signers` 必须严格等于 1，且该唯一签名证书 SHA-256 必须与连续性指纹一致。

Production 的 MSIX 与 AAB 目前会在计划或构建开始前明确失败：二者尚未接入与 APK/Setup
等价的完整后置身份、版本、连续性签名和时间戳复核。它们只能用
`-ReleaseMode Development` 构建用于本地验证，不能生成 Production manifest，也不能进入
AppShip Production 包装入口。

连续性指纹不是秘密，可以进入审计日志；PFX/keystore 密码仍只能使用 SecureString 或受保护的
进程环境变量。指纹门禁不根据证书 Subject、Issuer 或是否自签进行判断：Windows 允许经当前系统
验证为 `Valid` 的自签证书，但不会把自签证书描述为公开 CA 信任。更换任一指纹属于发布者轮换，
必须先完成旧客户端可接受的迁移方案，不能仅修改发布参数绕过。

秘密不会写入计划、命令行日志或 artifact manifest。传给 Android 的环境变量只在 Gradle
子进程期间设置，随后恢复原值。

示例（只构建，不上传）：

```powershell
$windowsPassword = Read-Host 'Windows PFX password' -AsSecureString
$androidPassword = Read-Host 'Android keystore password' -AsSecureString
$cosignPassword = Read-Host 'Cosign key password' -AsSecureString

& .\scripts\Build-Release.ps1 `
  -Version 1.4.2 -BuildNumber 107 `
  -ServerImage -WindowsSetup -AndroidApk `
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

## AppShip Production 包装门禁

AppShip 只能通过 `scripts/Publish-AppShipProduction.ps1` 接收本仓库的 Android APK 或
Windows Setup。包装入口拒绝 `development`、`.pending`、改名或放错版本目录的 manifest，
并要求文件路径、大小和 SHA-256 与 Production manifest 完全一致。真实上传前它会重新运行
`release/Verify-LocalRelease.ps1`；manifest 同时包含 APK 与 Setup 时会复核两端，而不是只复核
本次选中的单个文件。随后再次检查所选文件的大小与哈希，才把这个精确路径交给经过审核的
`appship-publish` 客户端。

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

执行轻量验收（不需要证书，也不运行组件构建）：

```powershell
pwsh -NoProfile -File .\release\Test-BuildRelease.ps1
```

测试覆盖 PowerShell parser、重复 `-Plan/-DryRun` 输出一致性、no-push、Production 缺签名
fail-closed、严格 APK signer count、Android/Windows 连续性指纹、ProductVersion/FileVersion、
RFC 3161/legacy countersignature、MSIX/AAB Production 拒绝、Production/development manifest、
AppShip 精确路径与哈希门禁、`-Publish` 拒绝以及严格输出路径。
