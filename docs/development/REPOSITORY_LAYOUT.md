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

从应用仓库运行[开发说明](BUILDING.md)中的命令。Windows 原生构建、.NET 项目的 DLL 引用以及 Android JNI 构建都指向同级 `../terminal-core`。

核心测试在核心仓库运行，固定 Rust 1.97.1；静态 CRT 配置和十份 Golden Stream 输入已随核心保留，不依赖应用目录内的测试数据。

应用与核心独立提交。客户端正式发布必须同时记录两个 revision、确认各自工作区干净，并保存 native 产物哈希；应用 Git revision 不再独自代表全部源码。Server 构建不依赖终端核心。

`terminal-core.lock.json` 固定本次兼容的核心 commit。核心代码修改并提交后，再在应用仓库更新该锁定值；不按浮动 main 自动升级核心。

```powershell
# 从应用仓库执行，只读核对两个 checkout
.\scripts\Verify-TerminalCoreCheckout.ps1
# 正式客户端构建前还要求两个仓库都干净
.\scripts\Verify-TerminalCoreCheckout.ps1 -RequireClean
```

统一发布脚本及 Windows Setup/MSIX 入口已接入该检查；普通核心构建允许未提交开发修改，但会核对核心 HEAD 与锁定值。绕过统一入口直接调用 Gradle 时仍需先运行该检查。锁定文件不代表核心 commit 已上传到远端；上传状态须单独核对。

这次拆分仍是有权限维护者的源码构建方案。面向无核心源码权限的贡献者，预编译 DLL/SO/AAR、下载校验和二进制许可仍需后续实现，不能声称已经支持。

## 历史、可见性与许可

主仓库旧提交和标签仍能读取 core 源码。本轮没有清除历史，也没有撤回任何既有授权；先拆目录不能达到“旧代码从未公开”的效果。

核心 SSH 远端可访问，但本轮未确认 GitHub private 状态。上传核心前必须核验可见性。若后续决定清理主仓库历史，需要独立规划迁移与远端推送，不能对现有分支/标签直接强制覆盖。

核心维持 Proprietary；其他组件许可证不因目录迁移而改变。此前“禁止商用”的意向尚未转换成新的许可文本。参见[许可证状态](../../LICENSE-STATUS.md)。

## 本轮验证（2026-10-05）

- 迁移前后 1,641 个原有源码/文档文件内容校验一致；之后只调整仓库边界、构建引用、测试数据位置和文档。
- 核心 runtime 源文件与原主仓库内容一致；Rust 核心 462 项、Golden Stream 10 项、FFI 48 项测试通过。
- Windows 原生 DLL 构建和 NativeTerminalCoreValidation 通过；Agent、SessionHost 开发构建均为 0 warning / 0 error。
- Android terminal-engine-native 的四种 ABI 原生构建通过，126 项 JVM/Robolectric 单元测试通过。未将这些结果当作真机或模拟器端到端验收。
- 新增双仓库验证脚本通过干净、未提交开发修改、正式构建拒绝脏目录、revision 不符及核心缺失等检查；已有发布编排验证通过。
- Windows DEV 采用已有成功记录 0.3.2/131，Android 采用 0.3.2/130；未递增版本、签名打包、安装客户端或部署 Server。
- 本机两个仓库独立提交；远端未推送。核心 GitHub 可见性仍待正式上传前核验。
