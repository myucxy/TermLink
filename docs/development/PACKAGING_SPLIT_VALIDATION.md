# 双仓库打包验证（2026-10-05）

验证同级仓库拆分后，应用能够从独立核心源码构建，并将核心二进制放入客户端安装载荷。执行本地打包与 Git 同步，不上传 AppShip/Docker Hub、不安装客户端、不部署服务。

## 源码与版本

- 应用构建 revision：`a7ed762d916493aa12374ba0253051ab1ddcef86`。
- 核心 revision：`52e964fff56e1bdc9a9ea33b0209e0315e204c6c`，与应用 `terminal-core.lock.json` 一致。
- 构建开始时两仓库均干净；后续提交补充文档，并修正 OCI 验证器与回归测试，不改变已验证的产品源码、Dockerfile 和构建脚本。
- 现场核对 AppShip 的已发布记录后，Windows 复用 `0.3.2/131`，Android 复用 `0.3.2/130`。Docker Hub `0.3.0` 与 `latest` 指向同一已发布 digest，服务端复用 `0.3.0/128`。
- 全部使用 `Build-Release.ps1 -ReleaseMode Development`，不递增版本；本次产物不是新的正式发布。Android 使用 Release 构建任务及既有签名，包名仍为 `dev.termlink.harness`，不能当作可与正式版并行安装的独立 DEV 包。

## Windows Setup

产物：`artifacts/release/split-dev-windows-0.3.2-131/TermLink-Setup-0.3.2.131-x64.exe`。

SHA-256：`0956597673b2fce3975a1c45841728f605415420b2c658459b4cbc160e696435`。

- `Verify-LocalRelease.ps1` 核验版本、哈希、安装载荷与签名通过；Setup 和 17 个载荷二进制签名有效，沿用既有证书，SHA-1 为 `91ae60bc06d866628b8eb2c62f32bde9dab56466`。
- 将安装载荷安全解包到隔离目录，确认 `termlink_terminal_ffi.dll` 与 `TermLink.TerminalCore.dll` 位于 TermLink 安装目录根部，与 Agent/SessionHost 的加载路径一致。
- 对比安装包中的原生 DLL 与独立核心构建结果，所有 PE 代码/数据节一致；文件整体哈希会受 Authenticode 签名影响，不能直接据此判断核心不同。
- 使用从安装包提取的 DLL 运行 `NativeTerminalCoreValidation`，结果通过。
- DEV 未加 RFC3161 时间戳，未将此结果视作正式签名发布验收。

## Android APK

产物：`artifacts/release/split-dev-android-0.3.2-130/TermLink-Android-0.3.2-130.apk`。

SHA-256：`41803abecf4a01a91f8fe992f5bdd2701a9635ca38ba9a1cdb84b3603fb60602`。

`Verify-LocalRelease.ps1` 核验版本、哈希和签名通过，沿用既有签名证书 SHA-256：`f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b`。

| APK 路径 | 核心库 SHA-256 |
| --- | --- |
| `lib/arm64-v8a/libtermlink_terminal_jni.so` | `18df04a81e8388d8f0c78f3651e5550fed36f7182808f8d6e9db63ca747fbaf4` |
| `lib/armeabi-v7a/libtermlink_terminal_jni.so` | `a516ad59b2fdc40a8927e7c02d793741e174943b8e9f1d9cebb2f1684ec584fa` |
| `lib/x86/libtermlink_terminal_jni.so` | `f6fc357707583c4d8c241d2668d9642bf561ae598dd0ad2fc791675ed7d4f809` |
| `lib/x86_64/libtermlink_terminal_jni.so` | `2a47b603fc8f815b3588d32f09062a694a1dd4afd9cd365730a3ddf921bb553e` |

四种 ABI 均存在，逐项等于 AGP 的最终裁剪产物；使用同版本 NDK `llvm-strip --strip-unneeded` 对原始 JNI 产物独立裁剪后也完全一致。没有打入 `terminal-core` 的 Rust 源码。此次未覆盖模拟器/真机端到端操作。

## 服务端 OCI

产物：`artifacts/release/split-dev-server-0.3.0-128/TermLink-Server-0.3.0-128.oci.tar`。

SHA-256：`1f17be803d5d2b334358e058a346b89d1f9fa6850b111c215ee5dea05858c3f5`。

- 默认 `docker` driver 不支持 OCI exporter；通过进程变量 `BUILDX_BUILDER=termlink-release-117` 使用现有 `docker-container` 构建器后，双架构编译与 OCI 导出完成。没有切换全局配置或重启 Docker。
- 原验证器仅解包最后一层；Dockerfile 在复制可执行文件之后又复制许可证，造成“缺少可执行文件”的误报。修正为从新到旧查找有效文件，尊重覆盖与 whiteout 删除，并检查层摘要与文件类型，只提取目标可执行文件。
- `release/Test-BuildRelease.ps1` 通过，包括新增的后置许可证层、较新程序覆盖、删除、根目录 opaque whiteout、缺少程序五项回归。
- `Verify-LocalRelease.ps1` 对真实 OCI 验证通过：amd64 ELF machine 为 62，arm64 为 183，运行用户均为 `65532:65532`，版本及 revision 与构建基线一致。
- 额外核对 OCI 全部 26 个 blob 的 SHA-256、两个平台 manifest/config 和入口 `/termlink-server`，均通过。
- 服务端只构建允许清单内的 `server/` 内容，不依赖私有终端核心。本次 OCI 未做 Cosign 签名、推送或服务部署，不等于正式镜像发布验收。

## 本地证据与 Git 边界

完整日志与 JSON 在应用仓库被忽略的 `artifacts/validation/`：

- `workspace-packaging-baseline.json`、`split-package-source.json`：版本与源码基线。
- `split-windows-verify.json`、`split-android-verify.json`：制品验证。
- `split-package-contents.json`、`split-package-contents-v2.log`：真实安装载荷、APK 核心库比对与 Windows 核心运行验证。
- `split-server-verify.json`、`split-server-contents.json`、`split-release-regression.log`：双架构 OCI 和验证器回归。
- `split-core-visibility.json`：匿名 GitHub 仓库页/API 为 404、公开对照 API 为 200、授权 SSH 可读的访问核验；没有认证 API 的可见性字段。

Git 同步先上传核心 commit，再上传引用它的应用 main。保留原提交、分支和标签，不强制重写历史；主仓库旧历史仍含核心源码。本轮不创建新的发布标签，不把制品、密钥、构建缓存或迁移备份上传 Git。
