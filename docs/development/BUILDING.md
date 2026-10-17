# 开发、构建与测试

本文依据 2026-10-05 仓库配置整理，面向 Windows 开发环境。它给出源码构建入口，不代表本轮已在干净机器上完成全量构建。产品使用见[快速开始](../guides/QUICK_START.md)，正式制品按[发布运行手册](../RELEASE_RUNBOOK.md)和[发布构建规则](../RELEASE.md)执行。

## 工具与环境

应用只需检出 `termlink/`，终端核心以 `native/terminal-core/` 下的 DLL/SO 提供，SHA-256 固定在 `terminal-core.lock.json`；不需要同级 core 源码。核心维护者的编译/导出入口见[双仓库说明](REPOSITORY_LAYOUT.md)。

| 工具 | 仓库要求/依据 |
| --- | --- |
| .NET SDK | `8.0.303`，以 `pc-client/global.json`、`win-agent/global.json` 和各自 rollForward 策略为准 |
| Go | `server/go.mod` 声明 Go 1.26 |
| Rust | 应用的 `network-core/` 固定 `1.97.1`；终端 DLL/SO 已预编译 |
| Node.js/npm | Vite 锁定依赖要求 Node.js `^20.19.0 \|\| >=22.12.0`，选择满足条件的现有 LTS |
| JDK | 本指南使用 JDK 21；Android Java/Kotlin 字节码目标为 17 |
| Android SDK | compile/target SDK 36、min SDK 26，NDK `27.0.12077973` |
| Gradle/插件 | Wrapper 8.13（附 SHA 校验）、AGP 8.11.1、Kotlin 2.1.21，以仓库配置为准 |
| 原生工具 | CMake ≥ 3.22、Ninja；Windows Rust MSVC 目标还需要 MSVC C++ 工具和 Windows SDK |
| 浏览器 | AI Web 夹具测试需要已有的 Chromium/Edge |
| Docker | 仅在容器构建或自部署时需要 Docker Engine/Compose |

维护者工作站的工具清单在 `D:\Dev\DEV_ENV_MANIFEST.json`。该机器构建前按所需工具 ID 调用 `D:\Dev\Scripts\Ensure-DevEnv.ps1 -Tool ...` 核验；这是工作站约定，不是所有贡献者必须拥有的文件。工具缺失时按该机安装策略处理，不把 SDK 下载到仓库或擅自修改系统 PATH。

其他机器从官方来源安装明确版本并验证校验和/签名，按项目配置设置路径。以下示例只影响当前 PowerShell 进程；每段命令失败后先处理错误，再进入下一步。切换到组件目录是为了使其 `global.json` 等配置生效。

## DEV 版本先固定

DEV/Debug 沿用**对应平台最后一次成功正式发布**的版本/build，均不递增。基线来自实际发布状态或有核验日期的成功记录，不使用未发布候选、仅有 tag 的记录或另一平台的版本；规则见[运行手册](../RELEASE_RUNBOOK.md#dev-与正式发布版本规则)。

在仓库根目录的 PowerShell 中按需设置：

```powershell
$windowsDevVersion = Read-Host 'Windows 已核验正式版本（三段）'
$windowsDevBuild = Read-Host 'Windows 对应 build'
$windowsAssemblyVersion = "$windowsDevVersion.$windowsDevBuild"
$windowsBuildArgs = @(
    "-p:Version=$windowsDevVersion"
    "-p:AssemblyVersion=$windowsAssemblyVersion"
    "-p:FileVersion=$windowsAssemblyVersion"
    '-p:RestoreLockedMode=true'
)
$androidDevVersion = Read-Host 'Android 已核验正式版本（三段）'
$androidDevBuild = Read-Host 'Android 对应 build'
$androidBuildArgs = @(
    "-PtermlinkVersionName=$androidDevVersion"
    "-PtermlinkVersionCode=$androidDevBuild"
    '-PtermlinkDebugApplicationIdSuffix=.aivalidation'
)
```

只编译某个平台时只需设置该平台变量。build 应符合运行手册的范围约束。`.aivalidation` 是隔离 DEV 包后缀示例；升级既有 DEV 应保留其包名和签名。不要因为签名不匹配而卸载仍有数据的客户端。

Android 当前 Gradle 的版本默认值仍是早期版本，直接运行无参数构建会得到错误的 DEV 基线。正式包也必须显式传参，不能依赖默认值。完整 DEV 制品还需通过打包流程写入 DEV 标记和 revision，不能将以下普通源码输出误当成可分发包。

## AI Web 资源先生成

Android 构建依赖生成资源，**首次检出也要执行**，不是仅在修改 Web 后才执行：

```powershell
Push-Location ai-web
npm ci
npm run build
npm run verify
Pop-Location
```

输出为 `android/app/src/main/assets/ai-web/`，包括哈希清单和第三方许可。Gradle 验证资源存在且与源码相符。不要手工改产物来绕过检查；本机更改源文件或锁文件后重新生成。

锁文件下载 URL 使用 npm 官方 registry，版本与 integrity 保持固定。需要本地镜像时使用 npm 的
进程级 `--registry`/网络代理设置，不顺便升级包或删掉 integrity。依赖缓存可以复用；公开前的
独立检出验证应注明是否使用空缓存，不能把新目录构建描述为全新机器验证。

运行 Web 行为测试时，在上述构建完成后执行：

```powershell
$env:PLAYWRIGHT_CHROMIUM_EXECUTABLE = Read-Host '现有 Chromium/Edge 可执行文件绝对路径'
Push-Location ai-web
npm test
Pop-Location
```

测试默认使用本机 4173 端口和模拟桥，不需要模型账号。默认 Edge 路径不适用于所有机器，可用上述变量覆盖。它不验证 Android WebView、真实 Relay 或真实 Agent 生成。

## Windows 原生库与 .NET

先准备 Rust 1.97.1 和 `x86_64-pc-windows-msvc` 工具链。在仓库根目录选择已安装的工具：

```powershell
$dotnet = Read-Host '.NET SDK dotnet.exe 绝对路径'
$cargo = Read-Host 'Rustup 管理的 cargo.exe 入口绝对路径'
Push-Location win-agent
& $dotnet --version
Pop-Location
& $cargo '+1.97.1' --version
```

Windows 原生脚本采用 `--locked --offline`。新机器依赖缓存为空时，需要先获取锁文件指定依赖，再执行构建：

```powershell
& $cargo '+1.97.1' fetch --locked --manifest-path network-core/Cargo.toml
& .\scripts\Verify-NativeArtifacts.ps1 -Platform Windows
& .\win-agent\scripts\Build-DirectCoreNative.ps1 -CargoPath $cargo
```

这里的 Cargo 入口必须支持 `+1.97.1`，仅用于应用内 network-core。终端 DLL 直接从 `native/terminal-core/windows-x64/` 复制到 .NET 输出和安装载荷，应用不编译终端核心。Relay Only 不等于可以省略网络模块的构建依赖。

按需要构建 Windows 运行时与桌面 UI：

```powershell
Push-Location win-agent
& $dotnet build src/Agent/TermLink.Agent.csproj -c Debug @windowsBuildArgs
& $dotnet build src/SessionHost/TermLink.SessionHost.csproj -c Debug @windowsBuildArgs
Pop-Location
Push-Location pc-client
& $dotnet build src/Desktop/TermLink.Desktop.csproj -f net8.0-windows -c Debug @windowsBuildArgs
Pop-Location
```

这些项目输出在各自的 `bin/`，并不自动组成带 Setup、签名、更新元数据的完整安装目录。不要复制单个 exe 覆盖正在使用的客户端。验证完整安装和升级应走运行手册指定的开发包构建与验证流程。

本机 UI 启动可能附着正在运行的 Agent；仅检查布局时可使用桌面程序的 `--preview-fixture`。真实集成测试应预先确定测试账号、安装目录和受影响会话。

## Android 原生工具与构建

终端 SO 已预编译，Gradle 只验证并打包；下列 CMake/Rust 工具仍用于应用的 network-core，其 Windows 默认路径尚未验证为可在 Linux runner 原样使用。

使用现有工具设置当前进程环境：

```powershell
$env:JAVA_HOME = Read-Host 'JDK 21 根目录'
$env:ANDROID_SDK_ROOT = Read-Host 'Android SDK 根目录'
$env:TERMLINK_CMAKE = Read-Host 'cmake.exe 绝对路径'
$env:TERMLINK_NINJA = Read-Host 'ninja.exe 绝对路径'
$env:TERMLINK_CARGO = Read-Host 'Rust 1.97.1 工具链 bin/cargo.exe 绝对路径（同目录有 rustc.exe）'
$env:TERMLINK_LLVM_READELF = Join-Path $env:ANDROID_SDK_ROOT 'ndk/27.0.12077973/toolchains/llvm/prebuilt/windows-x86_64/bin/llvm-readelf.exe'
```

Android 的 Cargo 配置应指向实际 1.97.1 工具链；CMake 会从同目录定位 `rustc.exe`，与上面 Windows 脚本需要的 rustup 入口有所区别。SDK 平台、Build Tools、NDK 需已安装且许可证已接受；不要提交 `local.properties` 或机器凭据。

预编译终端库覆盖四个 ABI；应用内 network-core 的 Rust 构建仍需要以下目标：`aarch64-linux-android`、`armv7-linux-androideabi`、`i686-linux-android`、`x86_64-linux-android`。缺少目标会导致网络 JNI 构建失败；仅准备模拟器 x86_64 并不足以完成默认全 ABI 构建。

AI Web 资源准备好后，在仓库根目录执行：

```powershell
Push-Location android
.\gradlew.bat --version
.\gradlew.bat testDebugUnitTest assembleDebug lintDebug @androidBuildArgs
Pop-Location
```

APK 位于 `android/app/build/outputs/apk/debug/`。Debug 使用调试签名，不需要正式发布 keystore；正式 APK 必须沿用既有发布签名。需要独立测试服务端时，通过 Debug 属性 `termlinkServerUrl` 显式指定测试地址，不把账号或密码写入构建参数和源码。

旧 Termux 模块、源码和导出工具已迁出仓库；无需获取或生成其任何文件，详见[清理记录](../archive/TERMUX_CLEANUP_20261008.md)。

## 按影响范围验证

以下是入口示例，不能把命令清单当成本轮已通过结果。运行 Windows 示例前使用上文的 `$dotnet` 和 `$windowsBuildArgs`：

```powershell
Push-Location pc-client
& $dotnet run --project tests/ArchitectureValidation -c Debug @windowsBuildArgs
& $dotnet run --project tests/Desktop.HeadlessTests -c Debug @windowsBuildArgs
Pop-Location
Push-Location win-agent
& $dotnet run --project tests/AiAgentValidation -c Debug @windowsBuildArgs
& $dotnet run --project tests/FileOperationsValidation -c Debug @windowsBuildArgs
Pop-Location
```

生产协议、SessionHost、终端输入/快照和更新器另有 `win-agent/tests/` 验证项目。先阅读所选脚本的参数及前置条件；部分旧脚本有固定版本/安装路径，不能不经核对用来生成新的 DEV 或正式制品。

Server 的基础检查在其目录内执行：

```powershell
Push-Location server
go test ./...
go vet ./...
Pop-Location
```

Rust 核心在同级独立仓库中测试，采用 GPLv3、计划后续公开，参见[双仓库说明](REPOSITORY_LAYOUT.md)；协议向量和真实跨端链路分别参见[测试向量](../../testdata/terminal-golden-streams/README.md)、[跨端验证](../../technical-validation/e2e/README.md)。真实 AI 生成可能需要已安装 Agent、登录和可用额度；模拟协议结果与真实生成分开记录。

当前 CLI 与 AI 联测仅使用 Relay Only：按修改范围验证登录、会话、输入、附件、审批、断线恢复和升级等。不安排 LAN/NAT 探测或打洞，不把历史直连项记录为通过。

## 构建限制与对外发布

当前尚未在新的干净环境中验证本文全部步骤。已知可移植性问题、许可审计和 CI 缺口见[开源准备清单](OPEN_SOURCE_READINESS.md)。

普通贡献不需要上传 AppShip、Docker Hub 或接触正式签名私钥。完整发布须另行固定源码 revision、版本/build、签名者、测试及回滚证据，并提供 GPLv3 要求的对应源码和构建资料；所有平台的运行手册门禁仍适用。应用可以不检出 core 而构建，不代表对外分发时可以省略 core 的对应源码。
