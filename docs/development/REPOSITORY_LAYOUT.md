# 应用与核心双仓库

2026-10-05 按维护者要求拆分本机目录，保留原 Git 历史，不重写远端。

```text
TermLink/                  工作区容器，无 .git
├── termlink/              应用仓库，含自身 .git
│   ├── android/
│   ├── win-agent/
│   ├── pc-client/
│   ├── ai-web/
│   ├── server/
│   ├── network-core/
│   └── docs/、scripts/、release/ 等
└── terminal-core/         独立核心仓库，含自身 .git
    ├── crates/
    ├── include/
    └── testdata/
```

| 仓库 | 远端 | 责任 |
| --- | --- | --- |
| 应用 | `git@github.com:myucxy/TermLink.git` | 客户端、服务端、网络核心、协议、集成和发布 |
| 终端核心 | `git@github.com:myucxy/terminal-core.git` | Rust 状态机、FFI、头文件与独立核心测试 |

父目录的 README、AGENTS 和编辑器多根工作区文件仅用于本机导航，不是第三个源码仓库。不要在父目录运行 git init。核心源代码不作为主仓库子模块、junction 或复制目录重新接入。

## 本机迁移

- 迁移前主仓库 HEAD 为 `78c011b`，main 分支及 origin 保留。
- 核心远端已有 `ec9a038` 初始提交，保留为新核心仓库历史起点；核心原有开发历史继续保留在旧主仓库中。
- 原 `TermLink-OpenSource` worktree 仍指向原分支；主 .git 移动后修复其关联路径，不修改旧 worktree 内容。
- 迁移前的 Git bundle、工作区 patch、未跟踪文档及 1,641 个文件哈希保存在工作区父目录的 `.workspace-backups/`。这些备份包含核心历史，不上传。
- 本机缓存、日志、凭据和旧制品随应用目录保留且继续忽略，不把它们加入 Git。旧构建证据中的绝对路径保留其历史含义。

## 构建和版本

实际编译、无核心源码检出打包及 DLL/SO 入包验证见[二进制边界验证记录](BINARY_CORE_VALIDATION.md)。

应用构建只读取本仓库的 `native/terminal-core/` 二进制，不访问同级核心源码。维护者可以继续使用同级目录；普通应用开发者只需检出应用仓库。

```text
terminal-core/                   私有实现与构建
├── crates/、include/             Rust、C ABI 与头文件
├── android/jni/                  JNI C++ 与 CMake
└── scripts/Build-ToTermLink.ps1   编译并导出 DLL/SO
termlink/
├── native/terminal-core/
│   ├── windows-x64/termlink_terminal_ffi.dll
│   └── android/<abi>/libtermlink_terminal_jni.so
└── terminal-core.lock.json       revision、dirty 标记、大小及 SHA-256
```

core 维护者从核心仓库运行 `./scripts/Build-ToTermLink.ps1`，默认向同级 `../termlink` 导出一个 DLL 和四种 ABI 的 SO。可用 `-TermLinkDirectory` 指定另一份应用源码目录。必须先完成全部编译，再复制二进制并更新锁文件；应用中的核心目录只存 DLL/SO，核心构建缓存保留在 core 的 `target/` 和 `dist/`。

core 的 JNI 注册名称、C ABI、Windows 静态 CRT 保持兼容；应用保留 C#/Kotlin 调用绑定、严格帧解码与 View，不包含原生核心实现或编译脚本。network-core 是应用自有的独立网络模块，不属于这次终端核心迁移。

应用维护者将五个库与锁文件一起提交。构建校验仅核对这些文件，无需安装 core 仓库或取得其源码权限：

```powershell
.\scripts\Verify-NativeArtifacts.ps1
.\scripts\Verify-NativeArtifacts.ps1 -Platform Windows
# 正式客户端打包还要求应用干净，且核心产物来自干净提交
.\scripts\Verify-NativeArtifacts.ps1 -RequireClean
```

Windows Setup/MSIX 与统一构建入口调用上述校验；Android Gradle 在 preBuild 验证四种 ABI 的大小和 SHA-256。校验失败即停止，不自动下载、重新编译或悄悄使用旧缓存。客户端发布记录应用 revision、锁文件中的 core revision 和二进制哈希；Server-only 不依赖终端核心。

core 默认拒绝未提交源码导出；明确使用 `-Development` 才允许，锁文件记录 `source_dirty=true`，正式打包拒绝此类产物。此流程只实现已有授权边界内的二进制交付，不授予新的公开分发许可。

## 历史、可见性与许可

主仓库旧提交和标签仍能读取 core 源码。本轮没有清除历史，也没有撤回任何既有授权；先拆目录不能达到“旧代码从未公开”的效果。

2026-10-05 上传前核验：核心仓库通过已授权 SSH 可读取；匿名访问 GitHub 仓库页与 API 均为 404，同网络下公开仓库 API 为 200，符合私有仓库访问行为。未取得认证 API 的 `private` 字段，不将此记录写成 GitHub 配置审计。后续上传仍须核对可见性。若决定清理主仓库历史，需要独立规划迁移与远端推送，不能对现有分支/标签直接强制覆盖。

核心维持 Proprietary；其他组件许可证不因目录迁移而改变。此前“禁止商用”的意向尚未转换成新的许可文本。参见[许可证状态](../../LICENSE-STATUS.md)。

## 初次拆分验证（2026-10-05）

以下为拆分提交前的阶段记录；后续实际签名打包、core 入包验证及 Git 同步范围见[打包验证记录](PACKAGING_SPLIT_VALIDATION.md)。

- 迁移前后 1,641 个原有源码/文档文件内容校验一致；之后只调整仓库边界、构建引用、测试数据位置和文档。
- 核心 runtime 源文件与原主仓库内容一致；Rust 核心 462 项、Golden Stream 10 项、FFI 48 项测试通过。
- Windows 原生 DLL 构建和 NativeTerminalCoreValidation 通过；Agent、SessionHost 开发构建均为 0 warning / 0 error。
- Android terminal-engine-native 的四种 ABI 原生构建通过，126 项 JVM/Robolectric 单元测试通过。未将这些结果当作真机或模拟器端到端验收。
- 新增双仓库验证脚本通过干净、未提交开发修改、正式构建拒绝脏目录、revision 不符及核心缺失等检查；已有发布编排验证通过。
- Windows DEV 采用已有成功记录 0.3.2/131，Android 采用 0.3.2/130；未递增版本、签名打包、安装客户端或部署 Server。
- 本机两个仓库独立提交；远端未推送。核心 GitHub 可见性仍待正式上传前核验。
