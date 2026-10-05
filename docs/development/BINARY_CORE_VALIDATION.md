# 终端核心二进制边界验证（2026-10-05）

应用不再编译或读取私有核心源码。Rust、C ABI 头文件、JNI C++、CMake 和 DLL/SO 构建脚本全部由 core 仓库维护；应用只消费五个预编译库，保留 C#/Kotlin 调用适配、帧解码和 UI。

## 构建入口

在 core 仓库执行 `scripts/Build-ToTermLink.ps1`，默认导出到同级 `termlink/native/terminal-core/`。可用 `-TermLinkDirectory` 指定另一份应用源码目录。core 内保存构建缓存；应用端以 `terminal-core.lock.json` 校验来源、大小及 SHA-256，不再要求 core checkout。

应用的 `win-agent/scripts/Build-TerminalCoreNative.ps1`、源码 checkout 检查以及 Android `src/main/cpp` 已移除。原 Android 终端模块的旧核心编译缓存已移至工作区 `.workspace-backups/binary-core-20261005/`，未删除历史证据。

## 实际验证

- 核心 revision：`e6e849172c6fca0de01ae0799eae97ad9483e687`，干净提交；脚本完成 x64 DLL 和 Android 四 ABI SO 编译及导出。
- JNI C++ 移动前后源码一致；四个最终 SO 与前一轮已验证的 APK 内库逐字节一致。
- `scripts/Test-NativeArtifacts.ps1` 七项检查通过：无 core checkout、同尺寸篡改、按平台校验、重复记录、错误路径、dirty core 正式构建拒绝和库缺失。
- 发布编排回归通过。
- 在 `.workspace-validation/binary-only/termlink` 创建独立应用检出；没有同级 core、没有应用构建缓存，Windows Setup 和 Android APK 均成功构建。复用了工作站依赖缓存；不声称验证了全新 SDK/CI 环境。
- Android 原生适配器 126 项 JVM/Robolectric 测试通过，0 failure/error/skipped。
- Windows 原生运行验证通过；从实际 Setup 中提取已签名 DLL 后再次验证通过，PE 代码/数据节与锁定 DLL 一致。
- APK 四种 ABI 的 SO 内容与锁定产物一致；双端版本、哈希、签名及既有签名者连续性检查通过。
- 隔离检出发现 AI Web 清单中三个源文件哈希来自旧 CRLF 工作副本；按仓库已有 LF 规则重新生成清单。Web 源码和编译后的 JS/CSS 没有变化，新检出不再产生这三处清单差异。

## DEV 制品

使用本会话现场核验的最后正式基线，不递增版本。Windows 构建 revision 为 `607d1f97217992c4dc9db12a4bae7d34a3f263f6`；Android 最终构建 revision 为 `44f1064`，较前者只修改 AI Web 源文件校验哈希。后续验证记录提交不改变产品或构建逻辑。

产物位于隔离应用检出的 `artifacts/release/`：

| 平台 | 路径 | SHA-256 |
| --- | --- | --- |
| Windows 0.3.2/131 | `binary-only-windows-0.3.2-131/TermLink-Setup-0.3.2.131-x64.exe` | `6d10ed1f256e8ca8345895506ee2ee00cecf34ce3351c88fc24648c99f515c6a` |
| Android 0.3.2/130 | `binary-only-android-final-0.3.2-130/TermLink-Android-0.3.2-130.apk` | `a3037360cc46b64fec83be0e89b55f4c3088ea25fd4ddbb17e2218c338dbd5ba` |

Windows 使用既有证书，DEV 没有 RFC3161 时间戳。Android 通过 Release 任务验证打包，包名仍是 `dev.termlink.harness`，不是可并行安装的独立 DEV 包。本轮不安装、上传 AppShip 或发布 Docker；Server 无改动。未进行模拟器/真机端到端测试，LAN/NAT 不在当前范围。

## 本地证据

应用仓库忽略的 `artifacts/validation/` 保存 `core-binary-export-clean.log`、`binary-boundary-release-tests.log`、`binary-only-windows-verify.json`、`binary-only-android-final-verify.json`、`binary-only-android-test-count.json`、`binary-only-package-contents.json` 及完整构建/运行日志。

许可证不变。此操作不移除旧 Git 历史，也不代表已授予私有二进制新的公开分发许可。
