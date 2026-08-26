# 发布与部署运行手册

本文用于每次 TermLink 正式版本的现场发布、故障恢复和最终复核。构建与签名门禁仍以
[统一发布构建](RELEASE.md)为准；本文补充执行顺序和已经验证过的运行环境陷阱。

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
- 不使用 `Start-ProductionAcceptance.ps1 -Reset` 做版本升级；`-Reset` 会删除 volumes，只能用于明确授权的
  一次性重建。
- 不移动已经发布的 Git tag，不用同一版本号覆盖另一个提交，也不发布 `latest` Docker 标签。
- 密码、Token、PFX/keystore 密码只能从 Credential Manager、`SecureString` 或受保护的进程环境读取，
  不进入命令参数、日志、manifest 或文档。
- AppShip 失败时保留 `.appship/` 状态；Docker/AppShip draft 或状态不因失败而删除后重新创建。

## 2. 固定执行顺序

1. 用 `D:\Dev\Scripts\Ensure-DevEnv.ps1` 检查清单内的 JDK、Android SDK、.NET、Go、Git 等工具，
   使用 `D:\Dev\DEV_ENV_MANIFEST.json` 记录的探测路径，不临时安装到系统目录或用户目录。
2. 运行改动范围对应的全量测试，再构建 Production APK/Setup 和统一 manifest。
3. 独立运行 `release/Verify-LocalRelease.ps1`，记录哈希、版本、Android signer、Windows Authenticode 和
   RFC 3161 时间戳结果。
4. 只提交、推送源码和文档。确认 `origin/main` 指向计划发布的精确提交；编译产物保持 ignored。
5. 通过 AppShip 仓库包装器发布 Android 与 Windows，并保留两个幂等状态文件。
6. 发布并验证 Docker Hub 多架构镜像；记录 OCI index digest，部署时使用 `tag@digest`。
7. 覆盖升级模拟器和 Windows 本机客户端；禁止为求省事清数据或改变安装根。
8. 无损升级 Compose，验证 7 个常驻服务、数据库 migration、公网 `/readyz` 和 named volumes。
9. 调用 AppShip 公开更新 API，验证上一 build 得到 `optional`、当前 build 得到 `no_update`。

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

完成后核对 `.termlink-install.json`、所有 `TermLink*.exe` 的 FileVersion/ProductVersion，并执行：

```powershell
& (Join-Path $installRoot 'TermLink.exe') --status-json '<ignored diagnostic path>'
```

要求 `Success=true`、`RemoteAccessEnabled=true`、账号仍关联、Relay 已连接，且版本中的 revision 是计划发布提交。

### 4.1 Avalonia Preview 发布

P4 Preview 与正式版是两个独立更新域。构建时向统一发布脚本传
`-WindowsUpdateChannel preview`，发布时向仓库包装器传 `-Channel preview`。必须核对安装后的
`TermLink.Legacy.exe --update-config-json <path>` 返回：

```json
{
  "application_slug": "termlink-win64-preview",
  "channel": "beta"
}
```

Preview 只能发布到 `termlink-win64-preview/beta`，不得把同一产物上传至 `termlink-win64`。仓库脚本的
产品轨道参数仍是 `preview`，它会显式映射到 AppShip 唯一支持的非稳定渠道 `beta`；稳定版继续使用
`termlink-win64/stable`。P4 的一个 Preview 周期内，Avalonia 是 `TermLink.exe`，旧 WinForms 仅作为
`TermLink.Legacy.exe` 回滚面保留；下一个阶段移除前必须先验证 Preview 更新与回滚统计。
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

## 6. Docker Hub 首选路径

首选 `.github/workflows/release.yml`：它登录 `docker.io/myucxy/termlink`，发布 amd64/arm64，生成
BuildKit provenance、SPDX/CycloneDX SBOM，并通过 GitHub OIDC/Cosign 签名 digest 与三类 attestation。

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

## 8. Compose 无损升级

部署参数必须同时带可读 tag 和不可变 index digest；仅 tag 或仅 digest 都会被脚本拒绝：

```powershell
& .\server\ops\Start-ProductionAcceptance.ps1 `
  -ServerImage 'docker.io/myucxy/termlink:<version>@sha256:<64-hex-index-digest>'
```

不要传 `-Reset`，不要执行 `docker compose down --volumes`。脚本会拉取固定镜像、运行 migration、重建
server，并复用 PostgreSQL、Valkey、MinIO、Keycloak 和 secret volumes。

升级后必须独立确认：

- `server`、`lucky-upstream`、`keycloak`、`edge`、`valkey`、`minio`、`postgres` 共 7 个服务均为
  `running/healthy`；
- server 的 `Config.Image` 是 `<tag>@<digest>`，OCI revision 是计划发布提交；
- `SELECT MAX(version) FROM schema_migrations` 等于当前最高 migration；
- 升级前后的 named volume 名称和数量一致；
- `https://termlink.kget.cc:8910/readyz` 返回 `{"status":"ready"}`；
- 脚本内置的 Production OIDC/TLS/API/Realtime probe 通过。

## 9. 最终交付记录

每次发布结论至少包含：源码 commit、Android/Windows 文件哈希与 signer、AppShip 双端 release/artifact ID、
Docker index digest 与架构、供应链证明是否完整或降级、Compose 7 服务健康、schema 版本、公网 readyz、
模拟器 version/build 与数据保留证据、Windows marker/status，以及 AppShip 新旧 build 的更新决策。

任何未完成项都按事实写明，不用“构建成功”替代“已上传”，不用“标签存在”替代“digest/架构/attestation
已验证”，也不用“应用能启动”替代“真实历史会话画面已恢复”。
