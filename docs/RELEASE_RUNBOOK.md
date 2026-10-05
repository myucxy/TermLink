# 发布与部署运行手册

本文用于每次 TermLink 正式版本的现场发布、故障恢复和最终复核。构建与签名门禁仍以
[统一发布构建](RELEASE.md)为准；本文补充执行顺序和已经验证过的运行环境陷阱。

## 当前 Docker 发布入口（2026-10-04）

按用户最新指定，本机编译后直接 push 到 `docker.io/myucxy/termlink`，不使用 GitHub Actions。
历史工作流保留为手动入口，Git tag 不再自动触发镜像发布。下文 GitHub keyless 工作流步骤只作历史参考。
本机发布仍固定已提交 revision 和版本/build，保留 BuildKit provenance/SBOM，逐项检查 amd64/arm64、
版本镜像 digest、源码标签和健康；版本镜像核验后再同步同 digest 的 latest。凭据使用现有 Docker credential helper。
本机没有 GitHub OIDC 身份，不能声称本地镜像通过 GitHub keyless 签名；本地签名资料未就绪时先完成
候选构建和验证，记录签名门禁未完成，不静默降级发布结论。

## 当前传输验收范围（2026-10-04）

2026-10-05 二进制边界补充：应用只消费 `native/terminal-core/` 下的 DLL/SO。客户端正式构建前执行
`scripts/Verify-NativeArtifacts.ps1 -RequireClean`，核对 `terminal-core.lock.json` 的核心 commit、
干净源码标记、大小和 SHA-256；记录应用 revision 与核心二进制来源。应用构建不检查或编译 core checkout。
更新核心时在私有 core 仓库运行 `scripts/Build-ToTermLink.ps1`，应用的五个库和锁文件一起提交。
统一构建及 Windows Setup/MSIX 已接入校验，Android Gradle 也检查入包前的 SO；Server-only 不依赖核心。
目录和旧历史边界见[双仓库说明](development/REPOSITORY_LAYOUT.md)。

用户明确指定当前仅使用服务器转发（Relay Only），CLI 与 AI 均适用。LAN/NAT 直连、打洞及直连/中转切换不再测试，不作为当前发布阻塞项；本文及历史记录中相关矩阵在本范围内记为“不适用（用户指定 Relay Only）”，不能记为通过。后续只有用户明确恢复直连范围时才执行相关测试。

发布和部署现场核对策略保持 Relay Only，不因代码或部署模板的直连默认值自动开启直连。继续验证实际服务器转发下的功能、弱网重连、网络切换后的 Relay 恢复、升级及回滚。此范围记录不代表已修改或核验在线配置。

## DEV 与正式发布版本规则

自 2026-10-03 起执行，适用于 Android、Windows 和 Docker：

1. **正式版本起点**：下一轮新正式发布为 `0.3.0`；之后默认自动递增 patch，依次为 `0.3.1`、`0.3.2`。如现场已有 `0.3.0` 或更高版本，从实际已发布的最高版本继续递增，不覆盖已用版本。major/minor 另有用户要求时按其要求执行。
2. **DEV 不加号**：DEV/Debug/开发测试沿用对应平台最后一次成功正式发布的三段版本和 build，二者都不递增。在 `0.3.0` 成功发布前，DEV 仍沿用上一正式版本。各平台发布进度不同则分别取各自成功发布值；不能把另一个平台的成功发布当作本平台基线。
3. **基线来源**：构建前只读核对对应平台的正式发布状态及发布记录。AppShip 必须确认 `published`/`ready`，Docker 必须确认版本镜像及既有签名/证明门禁通过；仅构建成功、Git tag、draft、失败上传或本机产物不算发布。离线 DEV 可使用有证据的最近成功发布记录，并注明核验时间；没有可靠基线时不得猜测版本。
4. **发布时自动分配**：只有用户明确要求执行正式发布时，发布执行者/编排才根据现场已发布版本计算下一版，并检查 AppShip、Docker Hub、远端 Git 及未完成发布是否占用了候选值。build 使用现有已用最大 build + 1，保持 `1..65535`，切换 `0.3.0` 不重置 build。候选被其他源码或发布占用时不得覆盖，应按门禁停止并处理冲突；同批所选平台固定为同一组版本/build。
5. **本轮固定一次**：版本/build 在构建前写入本轮计划并显式传给构建入口；`-Plan`/`-DryRun` 不占号、不改基线。同一发布的构建重试、断点恢复、平台补发均复用本轮值与已固定源码。若源码改变且旧候选已产生外部发布记录，按新发布处理，不冒充原轮次重试。
6. **成功后更新基线**：逐平台验证发布成功后，保存版本/build、revision、发布 ID/digest 和核验时间；后续 DEV 使用此已验证值。仅 DEV 编译、测试、修复、安装测试包均不能推进正式版本。DEV 名称、独立包标识和 revision 用于区分开发产物，不修改数字版本，不上传到 stable。

现有 `scripts/Build-Release.ps1` 是显式接收 `-Version/-BuildNumber` 的构建器，**不会自行查询线上或递增版本**；上述自动选号由发布执行者/编排在调用它之前完成。直接运行 Gradle/.NET 做 DEV 构建也必须显式传入已核验的发布版本/build，不能把组件默认值当作最新发布基线。DEV 与正式产物应使用独立输出目录，避免同号产物相互覆盖。

## 每次发布的执行规则（Android / Windows / Docker）

本节是发布检查单，不是固定版本的发布授权；只有用户明确要求对应平台上线时才执行提交、推送、
上传或部署。每轮从实时线上状态重做检查，不把历史发布记录或本地构建产物当成线上现状。
若某一门禁未通过，停止该平台的后续动作，记录已完成和未完成部分，不删除或改写已发布历史。

1. **确定范围和版本**：只构建/发布本次实际修改及用户指定的平台。查询 AppShip 两个 stable 更新接口、
   Docker Hub 已有 tag 和远端 Git tag；按上节自动计算并固定严格递增且未使用的三段 `Version` 和 build
   `1..65535`（下一轮从 `0.3.0` 开始，同批统一清单使用同一组）。不得以本文写下的旧版本或本地目录推断“最新”。
2. **固定源码**：`git status -sb`、`git diff --check`，复核哪些现有用户修改进入发布；完成改动范围的测试、
   必要的模拟器/真实产品链路及 Windows 安装器自动化验收。源码及预先写好的发布说明提交并推送后，确认 `HEAD`、
   `origin/main` 是同一完整 commit，且工作树干净；如源码在测试后改变，重新运行受影响测试。
   Windows ProductVersion、Docker OCI revision 均必须绑定此 commit；如创建 Git tag 也绑定此提交，tag 不触发镜像发布。制品和 `.appship/` 不提交。
3. **核验凭据/证书**：按下表核对实际证书、有效期、用途和连续性指纹，秘密仅从受保护的本机凭据库、
   `SecureString` 或受保护进程环境取得；不要在命令行参数、日志、文档、Git、截图或聊天中泄露密码/私钥。
   缺失、过期或指纹变化时停止，不要重新生成 Android key、换 Windows PFX 或跳过 signer 检查。
4. **计划、构建、复核**：用 `scripts/Build-Release.ps1 -Plan` 先看精确目标；用 `D:\Dev\Scripts\Ensure-DevEnv.ps1`
   核验清单中的现有工具；从固定源码构建 Production APK/Setup（如需商店才加 AAB），独立运行
   `release/Verify-LocalRelease.ps1` 验证 manifest、哈希、版本、证书连续性和 Windows RFC 3161 时间戳。
   Production MSIX 尚不支持；`Build-Release.ps1 -ServerImage` 只产生本地 OCI tar，不是 Docker Hub 发布命令。
5. **发布并验证**：Android/Windows 仅通过 `scripts/Publish-AppShipProduction.ps1` 顺序发布；每端必须有
   已人工审核的签名、`published` release 和 `ready` artifact。Docker 本机编译、直接 push Hub，
   不走 GitHub Actions，不创建 GitHub Release/GHCR 镜像。核对 amd64/arm64、OCI index digest、
   revision、签名及 provenance/SBOM；通过后将同一 digest 同步到 `latest`。签名未验证需明确记录和处理，
   不把本机 BuildKit attestation 当成已有 GitHub keyless 签名。失败时保留原 AppShip 状态/版本镜像 tag，恢复同一操作。
6. **部署与回归**：只有用户明确要求部署时，先做 age 加密备份，再以不可变 `tag@sha256:index-digest`
   无损升级单节点 Server；核对两个健康常驻容器、数据卷、migration、OIDC 和公网 `/readyz`。
   对客户端做原地覆盖安装与登录、历史/会话/输入全链路验证；AppShip 旧 build 应为 `optional`，
   新 build 应为 `no_update`。当前策略保持 Relay Only；历史自动直连部署可在管理页切为
   Relay-only。真实 Relay 链路回归结果单独记录；LAN/NAT 及直连切换矩阵当前不适用。
7. **留存证据**：写 `docs/RELEASE_<version>_<build>.md`，记录 commit、平台范围、文件 SHA-256、
   signer、AppShip release/artifact ID、Docker index digest 和证明、部署健康及回滚路径；
   明确区分“测试通过 / 构建成功 / 上传成功 / 部署验收成功”。

每轮凭据来源与用途（以下指纹是**历史已发布 signer**，不是对当前密钥可用性的替代验证）：

| 用途 | 现有来源与连续性门禁 | 使用规则 |
| --- | --- | --- |
| Android APK/AAB | `D:\Dev\Env\Android\keystores\nuanya-release.jks`，alias `nuanya-release`；Credential Manager `TermLink-Android-Keystore`；SHA-256 `f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b` | 用既有 keystore 和 SecureString；APK 至少 v2、唯一 signer；AAB 校验同一批准指纹。禁止生成新签名密钥或卸载旧 App 规避签名不兼容。 |
| Windows Setup | `win-agent/secrets/TermLink.Development.CodeSigning.pfx`（本机 Git ignored）或同一证书的 `Cert:\CurrentUser\My` 私钥；SHA-1 `91AE60BC06D866628B8EB2C62F32BDE9DAB56466` | 二选一：PFX+SecureString 或 `-WindowsCertificateThumbprint`；RFC 3161 `http://timestamp.acs.microsoft.com` 须显式 `-AllowHttpWindowsTimestamp`，并验证时间戳。当前是自签名开发证书，只能声明现有更新连续性，不能声称公开 CA 信任；公开 GA 换证须先设计旧客户端信任迁移。 |
| AppShip | 受保护的 Credential Manager Generic 项 `TermLink-AppShip-release-manager`，地址 `https://appship.kget.cc:8910` | 运行时在内存构造 `PSCredential`，以实际凭据中的用户名/角色为准；不要把凭据 blob、口令放进脚本或输出。 |
| Docker Hub/Cosign | 本机 Docker credential helper；本地镜像签名密钥及保护口令（若已配置） | 本机直接 push Hub；不读取/打印 Docker 凭据，不使用 GitHub OIDC 冒充本机身份；缺少签名资料时记录门禁未完成。历史 Actions secret 只适用于存档工作流。 |
| 公网 HTTPS | 宿主机/云端外部反向代理管理的域名证书及私钥 | 核对证书链、域名、有效期和自动续期；TLS 私钥不打进 APK、Setup、Docker 镜像或仓库，保留 Host、`X-Forwarded-Proto: https`、WebSocket Upgrade。 |
| 服务端备份 | 独立 age identity 的 public recipient `age1...` | 只把 recipient 配给 Server；私钥异机离线保管，升级前生成备份并演练恢复，不能复用发布签名或 TLS 私钥。 |

可复用的客户端构建/校验模板（从仓库根执行；仅为示例，版本、在线指纹、证书有效性须当次核实）：

```powershell
# $version / $build 已由本轮发布流程按上述规则自动计算、核对并固定。
# 以下所有构建、验证、上传和恢复操作复用同一组值。
$androidPassword = Read-Host 'Android keystore password' -AsSecureString
$windowsPassword = Read-Host 'Windows PFX password' -AsSecureString
$androidSigner = 'f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b'
$windowsSigner = '91AE60BC06D866628B8EB2C62F32BDE9DAB56466'
$env:JAVA_HOME = 'D:\Dev\Env\Jdk\jdk-21'
$env:ANDROID_HOME = 'D:\Dev\Env\Android\Sdk'
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:GRADLE_USER_HOME = 'D:\Dev\Env\gradle\user-home'

& .\scripts\Build-Release.ps1 -Version $version -BuildNumber $build `
  -WindowsSetup -AndroidApk -ReleaseMode Production -Plan
& .\scripts\Build-Release.ps1 -Version $version -BuildNumber $build `
  -WindowsSetup -AndroidApk -ReleaseMode Production `
  -DotNetPath 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe' `
  -WindowsCertificatePath '.\win-agent\secrets\TermLink.Development.CodeSigning.pfx' `
  -WindowsCertificatePassword $windowsPassword `
  -WindowsTimestampUrl 'http://timestamp.acs.microsoft.com' -AllowHttpWindowsTimestamp `
  -ExpectedWindowsSignerSha1 $windowsSigner `
  -AndroidSdkPath 'D:\Dev\Env\Android\Sdk' `
  -AndroidKeystorePath 'D:\Dev\Env\Android\keystores\nuanya-release.jks' `
  -AndroidKeystorePassword $androidPassword -AndroidKeyAlias 'nuanya-release' `
  -ExpectedAndroidSignerSha256 $androidSigner
& .\release\Verify-LocalRelease.ps1 `
  -ReleaseDirectory ".\artifacts\release\$version-$build" `
  -ManifestPath ".\artifacts\release\$version-$build\artifact-manifest.json" `
  -ExpectedReleaseMode Production -AndroidSdkPath 'D:\Dev\Env\Android\Sdk' `
  -ExpectedAndroidSignerSha256 $androidSigner `
  -ExpectedWindowsSignerSha1 $windowsSigner -RequireWindowsTimestamp
```

若已导入**同一枚**带私钥的 Windows 证书到 `Cert:\CurrentUser\My` 且指纹匹配，构建时改用
`-WindowsCertificateThumbprint $windowsSigner`，同时去掉 `-WindowsCertificatePath` 和
`-WindowsCertificatePassword`；不允许混用两种签名来源。Android 和 Windows 分开发布时仅传所选
构建目标及其签名参数；同一 manifest 同时含 APK/Setup 时 AppShip 两次调用都必须给双端指纹和 Android SDK。
具体 AppShip 调用和无损部署命令见下文第 5、8 节，详细签名/上传参数见 [统一发布构建](RELEASE.md)。

## P6-P8 PC 统一发布资格

便携 PC 制品使用 `release/unified-artifact-manifest.schema.json`，AppShip 映射固定在
`release/appship-applications.json`。使用 `scripts/New-UnifiedReleaseManifest.ps1` 生成、再用
`scripts/Test-UnifiedReleaseManifest.ps1` 复核 V2 清单。Preview 必须已有构建和签名证据；Stable 还必须
具备回滚、原生平台 E2E、健康检查、零阻塞项，并与记录的 Preview 清单保持字节完全相同。

`scripts/Publish-UnifiedPortableAppShip.ps1` 仅接受完成资格验证的 Linux/macOS 制品，并委托已审查、可恢复的
`appship-publish` 客户端上传。Windows 和 Android 继续使用 `scripts/Publish-AppShipProduction.ps1`，它会在上传
前再次验证原生签名与版本。中断后不能删除 `.appship` 状态或 draft，必须用完全相同的输入恢复。

AppShip 当前支持 beta/stable 原子频道指针和服务端回滚，但已审查的自动化客户端尚未提供按百分比灰度、
健康遥测或频道回滚操作。不能把 beta 到 stable 描述成 GA 完成，也不能用直接 HTTP 绕过。相关客户端与
服务端契约补齐、且所有平台验收文件的退出门禁通过之前，Stable 保持阻塞。

## 1. 不可破坏的边界

- Git 只提交和推送源码、测试、脚本与文档。`artifacts/`、`.appship/`、APK、EXE、截图和本地状态文件
  不进入 Git；发布前后都要运行 `git status -sb` 和 `git diff --check`。
- 不通过卸载 Android App 解决签名不一致，否则会删除登录、设备身份和 Android Keystore 数据。
- 不使用旧 `Start-ProductionAcceptance.ps1` 或它的 `-Reset` 路径做版本升级；当前
  `termlink-deploy` 不暴露 reset，禁止用 `docker compose down --volumes` 自行模拟。
- 不移动已经发布的 Git tag，不用同一版本号覆盖另一个提交。`.github/workflows/release.yml`
  先发布版本镜像，通过签名与证明校验后再把相同 digest 同步到 `latest`；构建阶段的
  `latest=false` 只用于防止提前更新别名。生产/共享部署仍引用 `TAG@sha256:DIGEST`。
- 密码、Token、PFX/keystore 密码只能从 Credential Manager、`SecureString` 或受保护的进程环境读取，
  不进入命令参数、日志、manifest 或文档。
- AppShip 失败时保留 `.appship/` 状态；Docker/AppShip draft 或状态不因失败而删除后重新创建。

## 2. 固定执行顺序

1. 用 `D:\Dev\Scripts\Ensure-DevEnv.ps1` 检查清单内的 JDK、Android SDK、.NET、Go、Git 等工具，
   使用 `D:\Dev\DEV_ENV_MANIFEST.json` 记录的探测路径，不临时安装到系统目录或用户目录。
2. 运行改动范围对应的全量测试，只提交、推送源码和文档；确认干净的 `HEAD` 与 `origin/main`
   指向计划发布的精确提交。源码如有后续变动须补测；编译产物保持 ignored。
3. 从该提交构建 Production APK/Setup 和统一 manifest；独立运行 `release/Verify-LocalRelease.ps1`，
   记录哈希、版本、Android signer、Windows Authenticode 和 RFC 3161 时间戳结果。
4. 通过 AppShip 仓库包装器发布 Android 与 Windows，并保留两个幂等状态文件。
5. 发布并验证 Docker Hub 多架构镜像；记录 OCI index digest，部署时使用 `tag@digest`。
6. 覆盖升级模拟器和 Windows 本机客户端；禁止为求省事清数据或改变安装根。
7. 仅在本次要求部署时，通过单节点部署入口无损升级，验证 `server`、`postgres`、数据库 migration、公网 `/readyz` 和
   三个持久卷；`secrets-init` 只是一次性初始化任务，不计入常驻服务。
8. 调用 AppShip 公开更新 API，验证上一 build 得到 `optional`、当前 build 得到 `no_update`。

## 3. Android 发布与模拟器升级

Production APK 必须来自统一 manifest，并由连续性证书签名。发布前至少确认：

本机验证固定使用 `D:\Dev\Env\gradle\gradle-8.13-clean\bin\gradle.bat`，并把
`GRADLE_USER_HOME` 指向 `D:\Dev\Env\gradle\user-home`。不要使用
`D:\Dev\Env\gradle\gradle-8.13`：该目录的全局 `init.d` 会注入不安全的 HTTP `Bstek` 仓库，Gradle 会在
依赖解析前拒绝构建。所需进程环境为：

```powershell
$env:JAVA_HOME = 'D:\Dev\Env\Jdk\jdk-21'
$env:ANDROID_HOME = 'D:\Dev\Env\Android\Sdk'
$env:ANDROID_SDK_ROOT = 'D:\Dev\Env\Android\Sdk'
$env:GRADLE_USER_HOME = 'D:\Dev\Env\gradle\user-home'
$gradle = 'D:\Dev\Env\gradle\gradle-8.13-clean\bin\gradle.bat'
```

```powershell
& .\release\Verify-LocalRelease.ps1 `
  -ReleaseDirectory '.\artifacts\release\<version>-<build>' `
  -ManifestPath '.\artifacts\release\<version>-<build>\artifact-manifest.json' `
  -AndroidSdkPath 'D:\Dev\Env\Android\Sdk' `
  -ExpectedAndroidSignerSha256 '<64-hex>' `
  -ExpectedWindowsSignerSha1 '<40-hex>'
```

模拟器覆盖安装前先检查现装包是否为 debug：

```powershell
$adb = 'D:\Dev\Env\Android\Sdk\platform-tools\adb.exe'
& $adb -s emulator-5554 shell dumpsys package dev.termlink.harness |
  Select-String 'versionName=|versionCode=|firstInstallTime=|appId=|DEBUGGABLE'
```

若现装包是 production signer，使用已发布 APK 执行 `adb install -r`。若出现
`INSTALL_FAILED_UPDATE_INCOMPATIBLE`，先比较 signer；不要卸载。现场已确认模拟器可能安装 Android Debug
证书，而 AppShip APK 使用 production 证书。此时应从同一提交构建相同 version/build 的 debug APK，
只用于模拟器原地升级：

```powershell
$env:JAVA_HOME = 'D:\Dev\Env\Jdk\jdk-21'
$env:ANDROID_SDK_ROOT = 'D:\Dev\Env\Android\Sdk'
$gradleArguments = @(
  ':app:assembleDebug',
  '-PtermlinkVersionName=<version>',
  '-PtermlinkVersionCode=<build>'
)
Push-Location .\android
try { & .\gradlew.bat @gradleArguments }
finally { Pop-Location }

& $adb -s emulator-5554 install -r '.\android\app\build\outputs\apk\debug\app-debug.apk'
```

PowerShell 中每个 `-P...` 必须作为单独字符串传入。未引用的
`-PtermlinkVersionName=0.1.30` 曾被拆成 Gradle 任务 `.1.30`，不会开始正确构建。

升级后核对 `appId` 和 `firstInstallTime` 未变化、version/build 已更新，再启动 App 并检查 logcat。
至少现场打开一条已有 OpenCode/Claude/Codex 会话，确认最终画面恢复而不是只确认进程能启动。

## 4. Windows 本机原地升级

Windows 源码复核固定使用 `D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe` 构建
`win-agent\TermLink.Windows.sln -c Release`。多个验证项目引用相同的 `UpdateCore`、`Agent` 等项目，会共享
`obj` 输出；不得并行运行 solution build 或这些验证项目，否则会出现 `CS2012` 文件占用。构建和验证必须串行。

先从注册表读取当前安装根，校验 Setup 哈希和 Authenticode，再显式传入该目录：

```powershell
$setup = '.\artifacts\release\<version>-<build>\TermLink-Setup-<version>.<build>-x64.exe'
$installRoot = [string](Get-ItemProperty 'HKCU:\Software\TermLink').InstallRoot
$process = Start-Process -FilePath $setup `
  -ArgumentList @('--install-dir', $installRoot, '--terminate-running', '--quiet') `
  -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(120000)) { throw 'TermLink Setup timed out.' }
if ($process.ExitCode -ne 0) { throw "TermLink Setup failed: $($process.ExitCode)" }
```

两个已验证的错误调用必须规避：

- `--terminate-running` 不能单独依赖注册表自动发现，必须与 `--install-dir <现有目录>` 同时使用，否则
  安装器在修改文件前返回 1。
- 不要对会启动常驻 Tray 的 Setup 使用 `Start-Process -Wait`。PowerShell 会等待安装器的整个后代进程树，
  新 Tray 正常运行时调用反而永不返回。应使用 `-PassThru` 后调用安装器 `Process.WaitForExit(timeout)`，
  它只等待该 PID。

完成后核对 `.termlink-install.json`、所有 `TermLink*.exe` 的 FileVersion/ProductVersion，并在 Avalonia
主程序的设备/连接页面确认独立 Agent 已关联账号、远程访问已启用、Relay 已连接。旧
`TermLink.Legacy.exe --status-json` 入口已随 WinForms 客户端移除，不能再作为验收命令；自动化验收使用
`WindowsAdapterValidation` 的隔离控制管道，现场验收保留页面截图与 Agent 日志。

### 4.1 Avalonia Preview 发布

P4 Preview 与正式版是两个独立更新域。构建时向统一发布脚本传
`-WindowsUpdateChannel preview`，发布时向仓库包装器传 `-Channel preview`。必须核对安装后的
`TermLink.Platform.Windows.dll` 程序集元数据：

```json
{
  "application_slug": "termlink-win64-preview",
  "channel": "beta"
}
```

Preview 只能发布到 `termlink-win64-preview/beta`，不得把同一产物上传至 `termlink-win64`。仓库脚本的
产品轨道参数仍是 `preview`，它会显式映射到 AppShip 唯一支持的非稳定渠道 `beta`；稳定版继续使用
`termlink-win64/stable`。Avalonia 客户端直接执行 AppShip 检查、固定哈希/签名验证和 Setup 启动，
不再依赖已移除的 WinForms 回滚面。
从 0.1.32 等 P3 以前稳定版原地升级时，旧目录没有 `TermLink.Agent.exe` 属于正常布局；P4 安装器通过
`.termlink-install.json`、`TermLink.exe` 和 `TermLink.SessionHost.exe` 三个归属锚点兼容识别。不能通过手工
补一个空的 Agent 文件绕过旧安装器，也不能放宽到只看目录名。
首次发布时包装器会通过审核过的 AppShip 客户端创建私有 Preview 应用并启用签名审核；已有应用只做
slug 解析，不重复创建。若首次尝试因缺少应用而在上传前失败，保留原状态文件，将其归档到 `.appship/failed/`
后再以修正后的完整输入开始新状态；不能把失败状态删除或改发 stable。

PC 项目的锁文件预先包含 `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`
六个 RID。任何 `dotnet publish -r ...` 都必须使用 `-p:RestoreLockedMode=true`。发布前后比较
`git status --short`；若 publish 改写了 `packages.lock.json`，立即停止发布并补齐锁图，不能把构建污染
作为源码提交。

## 5. AppShip 正式发布与恢复

只使用 `scripts/Publish-AppShipProduction.ps1` 和审核过的 `appship-publish/scripts/publish.ps1`，不要自行实现
AppShip HTTP 上传。manifest 同时含 APK 与 Setup 时，每次发布任一端都会复核两端签名参数。

- 首次调用没有状态文件时不加 `-Resume`。
- 中断、超时或 exit 6 后，使用完全相同的 manifest、title、release notes、签名审核说明和状态路径加
  `-Resume`；不要创建新 release 或新状态文件。
- 凭据从 Windows Credential Manager 的 `TermLink-AppShip-release-manager` 读取并在内存中构造
  `PSCredential`。不得输出 `CredRead` 返回的 blob。
- 状态文件固定保存在 `.appship/<slug>-<channel>-<version>-build<build>-signature-approved.json`；Preview
  与 stable 不能共享状态文件。
- 成功后必须同时记录 application ID、release ID、artifact ID，并确认 release=`published`、
  artifact=`ready`。

公开更新接口的最终门禁：

```text
GET /api/v1/apps/termlink-android/updates?platform=android&arch=arm64-v8a&channel=stable&build=<build>
GET /api/v1/apps/termlink-win64/updates?platform=windows&arch=x64&channel=stable&build=<build>
```

上一 build 应返回 `status=optional` 和当前 version/build；当前 build 应返回 `status=no_update`。

## 6. Docker Hub 历史 GitHub 路径（当前不使用）

以下为历史流程；当前使用页首规定的本机编译直推。`.github/workflows/release.yml` 登录 `docker.io/myucxy/termlink`，发布 amd64/arm64，生成
BuildKit provenance、SPDX/CycloneDX SBOM，并通过 GitHub OIDC/Cosign 签名 digest 与三类 attestation。
校验通过后自动更新 Docker Hub `latest`，并复核其 digest 与版本镜像一致；发布任务串行执行。
`latest` 是可更新别名，版本标签保持不可变。重跑旧版本任务也会更新 `latest`，因此不要通过
重跑旧 tag 回滚；回滚部署应指定历史版本的不可变 digest。

仅同步现有已发布版本到 `latest` 时，无需重新编译或修改版本标签。先检查源 manifest 的
digest、架构与 OCI revision，再使用 `docker buildx imagetools create --prefer-index=false`
把精确 `docker.io/myucxy/termlink@sha256:...` 复制到 `docker.io/myucxy/termlink:latest`，
最后复核两标签摘要相同。此操作保留源镜像原有架构与签名状态，不会补齐缺失的架构或证明；
必须在发布记录中注明现有制品的限制。不要用单架构本地 `docker tag/push` 覆盖多架构索引。

推 tag 前先确认仓库 Actions secrets 中 `DOCKERHUB_USERNAME` 与 `DOCKERHUB_TOKEN` 可用。若 workflow 在
`docker/login-action` 失败，后续 build/sign/attest 都没有发生；不要移动 tag、不要重复创建同版本 tag，
先修复 secrets。源码仓库无法通过代码变更恢复一个缺失或过期的 GitHub secret。

## 7. Docker Hub DNS 受限网络降级路径

2026-08-25 现场确认经典 DNS 把 `auth.docker.io` 解析到 `31.13.*`、`173.252.*`、`199.59.*` 等无关网段，
导致 buildx 在 token 请求处等待约 42 秒后失败。DoH 对照返回 Cloudflare CDN 地址。判断方法：

```powershell
Resolve-DnsName auth.docker.io -Type A
Invoke-RestMethod `
  -Uri 'https://cloudflare-dns.com/dns-query?name=auth.docker.io&type=A' `
  -Headers @{ Accept = 'application/dns-json' }
```

不要连续重跑普通 buildx，也不要只关闭 SBOM：认证 token 和最终 push 同样会失败。不要未经授权修改
Windows hosts、系统 DNS 或 Docker Desktop 全局代理。

已验证的无系统改动降级方案是一次性官方 BuildKit 容器：

1. 固定本机已有 `moby/buildkit:buildx-stable-1` 的精确 digest。
2. 容器使用唯一名称和 `dev.termlink.release-temp=true` label；仓库以只读 bind mount 提供。
3. 只在容器内用 `--add-host auth.docker.io:<DoH 已验证 IP>`，并让该域名进入 `NO_PROXY`；其余流量仍走
   Docker Desktop 代理。
4. 通过 `docker-credential-desktop.exe get` 在内存读取 Docker Hub 凭据，将最小 `config.json` 通过 stdin
   写入容器的 `/root/.docker` tmpfs。不得打印 helper 输出、不得把凭据放进 `docker exec` 参数。
5. 在同一容器内运行 `buildctl build`，指定 `platform=linux/amd64,linux/arm64`、四个 OCI build args、
   `attest:provenance=mode=max`、`attest:sbom=` 和 `push=true`。本机固定的 BuildKit v0.32.2 `buildctl`
   没有顶层 `--attest` 参数；两项证明必须作为 Dockerfile frontend 参数传入：
   `--opt 'attest:provenance=mode=max' --opt 'attest:sbom='`。误用 `--attest` 会在构建和推送开始前直接失败。
6. 从 `--metadata-file` 读取 index digest；在 `finally` 中先检查 ownership label，再删除精确容器。容器删除后
   tmpfs 凭据随之消失。

该路径保留 BuildKit SLSA provenance 与 SPDX SBOM，但没有 GitHub OIDC/Cosign keyless signature，也不自动
生成独立 CycloneDX attestation。最终报告必须明确为“降级发布”，不能写成与首选 workflow 等价。

远端验证不能只看标签存在。用 registry API 或 `docker buildx imagetools inspect` 确认：

- OCI index 含 `linux/amd64` 与 `linux/arm64`；
- 两个 image config 的 `org.opencontainers.image.version/revision/source/created` 精确匹配；
- 每个平台的 attestation manifest 至少包含 `https://spdx.dev/Document` 与
  `https://slsa.dev/provenance/v1`；
- 记录 index 的 `sha256`，后续部署只使用该摘要。

若主机上的 `docker buildx imagetools inspect` 因 `registry-1.docker.io` 被经典 DNS 解析到无关地址而失败，
不要修改系统 hosts/DNS，也不要据此判定已经成功的 push 失败。可用一次性本地 Alpine 容器取得公开 pull token：
仅对 `auth.docker.io` 注入 DoH 验证地址并加入 `NO_PROXY`，Registry 请求继续走 Docker Desktop 代理，然后通过
Registry API 读取 index、manifest、config blob 和 attestation descriptor。该校验不需要向容器注入 Docker Hub
写凭据，容器退出后不保留状态。

## 8. 单节点无损升级（Server + PostgreSQL）

当前生产入口是 `server/deploy/single-node-production/termlink-deploy`。它生成并管理单节点 Compose：
常驻服务只有一个 TermLink `server` 和一个 PostgreSQL；身份服务、migration、短期协调状态以及
Direct/NAT signalling 均由 `server` 内嵌处理。`secrets-init` 只在启动前复制受保护的 secret，成功退出是
预期状态。不要再使用旧 acceptance 栈，也不要把 Keycloak、Valkey、MinIO 或 Lucky 当成当前生产依赖。

镜像参数必须同时包含可读 tag 和不可变 OCI index digest；仅 tag、仅 digest 或 `latest` 都会被入口拒绝。
从源码目录首次安装时在 Linux 主机运行：

```bash
sudo ./server/deploy/single-node-production/termlink-deploy install \
  --domain termlink.example.com \
  --image docker.io/myucxy/termlink:<version>@sha256:<64-hex-index-digest> \
  --data-dir /opt/termlink
```

正式发布的自包含安装器会内嵌不可变镜像，正常安装可省略 `--image`；源码 checkout 仍必须显式提供。
安装结果默认位于 `/opt/termlink`，后续统一使用该目录内被管理的入口：

```bash
sudo /opt/termlink/termlink-deploy upgrade \
  --image docker.io/myucxy/termlink:<version>@sha256:<64-hex-index-digest>
sudo /opt/termlink/termlink-deploy doctor
```

`upgrade` 会重写受管理的 Compose 资产并执行 `docker compose up --remove-orphans`，不会删除数据卷；配置了
age recipient 时会先创建加密备份。禁止为了升级执行 `docker compose down --volumes`，也不要顺带清理
`postgres-data`、`postgres-secrets` 或 `server-secrets`。初始管理员密码只显示一次；非交互安装后使用
`sudo /opt/termlink/termlink-deploy credentials` 领取。

以下 Direct 配置与测试流程保留作历史参考，当前 Relay Only 范围不执行；仅在用户明确恢复直连范围后适用。

Direct probing/signalling 与 Direct 数据路由是两个门禁。部署文件启用前者；后者的权威值位于 PostgreSQL
`direct_routing_policy`，旧 `TERMLINK_DIRECT_ROUTING_PERMITTED` 环境字段不能作为生效证据。没有保存过
策略的新部署默认使用“自动选择（直连优先，Relay 热备）”；既有部署中管理员显式选择的 Relay-only
继续生效。仅发布范围包含直连时运行确定性故障矩阵：

```powershell
pwsh -NoProfile -File .\technical-validation\direct-e2e\Test-DirectFaultMatrix.ps1
```

该模型测试不能代替真实网络。仅在直连验收范围内，发布记录必须如实标明真实网络覆盖；应在安装后的 Android、Windows、Server
上完成真实故障矩阵，确认
Relay→LAN→Relay、Relay→NAT→Relay、Direct socket/protocol/queue 故障、Relay 暂断、网络切换、锁屏恢复、
长输出与 resize burst、两台 Android 并行均无重复、无 History Gap、无 Relay generation 非预期变化。
若真实环境暂未覆盖，不能把模型测试写成实机通过；出现异常时由 `SUPER_ADMIN` 立即切为 Relay-only。
从仍广告旧 Direct capability 的旧 Server 切换时，停止旧实例后至少等待 60 秒，使旧 presence 过期。

验收构建打开 routing gate 后，Server config、Realtime Welcome、Transport Signalling Welcome 以及
Android/Windows 两端必须同时包含 `direct_transport_data_v1` 和 `direct_session_cutover_v1`。只出现
前者属于 legacy data-only 协商，必须验证仍停留在 Relay，不能计为 Direct 验收通过。

在管理中心“连接与路由 → 网络传输策略”核对当前选择。策略权威存储在 PostgreSQL，不再由部署环境变量
或 `termlink-deploy config --enable-direct-routing` 修改。若任一真实场景失败，立即在同一页面切换为
“仅服务器中转（Relay Only）”；这只关闭 Direct 数据路由，不应关闭 Relay、重启 Agent/SessionHost
或终止现有会话。修改后再执行 `sudo /opt/termlink/termlink-deploy doctor` 核对服务健康。

以下为通用部署健康检查，当前 Relay Only 范围仍必须执行：

- `server` 和 `postgres` 各恰好一个实例处于 `running`，两者 healthcheck 均为 `healthy`；
- `secrets-init` 成功退出，且渲染后的 Compose 不包含 secret 明文；
- server 的 `Config.Image` 是 `<tag>@<digest>`，OCI revision 是计划发布提交；
- `SELECT MAX(version) FROM schema_migrations` 等于当前最高 migration；
- 升级前后的 `postgres-data`、`postgres-secrets`、`server-secrets` 卷保持不变；
- 本机 loopback `/healthz`、`/readyz` 及 OIDC discovery 通过，外部 HTTPS 代理保持 Host、
  `X-Forwarded-Proto` 和 WebSocket upgrade，配置的公网 URL `/readyz` 返回 `{"status":"ready"}`；
- Android 与 Windows 客户端仍能通过 Relay 登录、读取目录、打开会话并恢复历史；Direct 未通过上述门禁时
  仍显示 Relay，不得因 shadow signalling 已启用而显示 LAN/NAT Direct。

## 9. 最终交付记录

每次发布结论至少包含：源码 commit、Android/Windows 文件哈希与 signer、AppShip 双端 release/artifact ID、
Docker index digest 与架构、供应链证明是否完整或降级、Compose 服务健康、schema 版本、公网 readyz、
模拟器 version/build 与数据保留证据、Windows marker/status，以及 AppShip 新旧 build 的更新决策。

任何未完成项都按事实写明，不用“构建成功”替代“已上传”，不用“标签存在”替代“digest/架构/attestation
已验证”，也不用“应用能启动”替代“真实历史会话画面已恢复”。
