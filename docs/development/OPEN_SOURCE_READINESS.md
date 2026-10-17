# 仓库整理与开源准备

更新日期：2026-10-18。应用与 core 自有代码采用 GPL-3.0-only，代码已上传现有 GitHub 私有仓库。本文记录准备进度，不改变仓库可见性、签名身份或已发布产物。

## 本轮已完成

- 删除被当前指南取代的 28 个旧总体设计、八月会话交接、九月会话知识库及 AI V3 设计文件（含图片）；删除前已在仓库外备份。当前工作树不再包含这些开发对话截图，Git 历史仍保留原内容。
- 4 个本机发布脚本/审核状态文件停止 Git 跟踪，本机副本仍保留并由现有 artifacts 忽略规则保护。
- 更新 [0.3.5 / 135 发布结果](../RELEASE_0.3.5_135.md)与[发布索引](../releases/README.md)，根 README 提供匹配二进制的匿名对应源码下载入口。
- 中英文演示和此前产品短片归入 [feature-demos](../media/feature-demos/README.md)，保留最终视频、字幕、封面及 README 预览。
- 应用 README 补充参考项目、实际组件/资源与来源声明；core 有中英文 README、独立签名依赖包和对应源码 Release。
- 源码归档支持按显式应用/core revision 导出，使用该 commit 的锁文件和原生库 Git 对象；不要求最新文档 HEAD 与旧二进制来源相同。IncludeCoreVendor 可附完整锁定 Rust 依赖和离线配置。
- [依赖清单](../licenses/dependency-inventory.json)记录 Linux 双架构 Server 实际链接模块、AI Web runtime、Android/.NET 声明与 network-core Rust 锁。Server 的 Go runtime 和 71 个链接模块的原始许可文本已汇总到随镜像复制的许可包；AI Web 保留 108 份运行依赖声明。
- 新增文档链接、精确测试向量秘密扫描、Server/Web 和源码打包检查，以及 Issue/PR 模板。旧 Docker workflow 已移除；独立的[GitHub 客户端发布管道](GITHUB_RELEASE.md)核验本地签名产物后发布，不上传签名私钥。
- [0.3.6 / 136](../RELEASE_0.3.6_136.md) 的 APK、Windows Setup 和完整对应源码已发布到 GitHub/AppShip，AppShip 匿名下载及旧/新 build 更新接口核验通过。Docker CI 的双架构构建、amd64 镜像入包、PostgreSQL/OIDC/健康与重启检查已通过；未发布新的 Docker 镜像。

## 保留的边界与剩余事项

| 事项 | 状态与下一步 |
| --- | --- |
| 公开历史范围 | 用户明确保留旧 Git 历史。删除当前文件不会清除旧 blob；公开前仍需确认历史开发对话/工作站信息的可见范围，本轮不重写历史 |
| GitHub 可见性和保护 | 两仓库维持私有。当前 main 未保护；规则接口因私有仓库套餐限制返回 403，私密漏洞报告接口不可核验。公开或升级套餐后再配置分支保护和安全功能，不为开启规则自动公开 |
| 第三方授权审核 | 已提供来源与可追溯清单；Android/.NET 的完整传递依赖以及商标、历史分发仍需逐项人工审核，清单不能当作全部权属审计通过 |
| 干净检出构建 | 复现步骤与本轮实际验证结果见下方；Linux/macOS 客户端及 Android Linux 原生工具适配尚未验收，不作通用平台承诺 |
| 公开签名信任 | Windows 仍使用既有签名者，自签信任、SmartScreen 信誉与签名完整性不同；未自动更换证书 |
| 产品功能门禁 | core RC、设备 JNI 冒烟和源码构建不代替完整图形/OEM/混版本/性能/长稳验收；MI 6 实际发声按维护者要求不测 |

## 扫描与源码交付

2026-10-18 的只读复扫覆盖两仓库当前跟踪文件与可达历史。应用当前 23、历史 27 条 Gitleaks 候选与前次已复核的固定协议向量、测试夹具和公开指纹完全一致，当前候选 Git blob 逐字节不变；core 无候选。应用 11 次 Actions 运行已核对：10 份非空日志无 Gitleaks 命中，另 1 份为有效空 ZIP。未确认新增凭据泄露；不把检测器结果解释为任意秘密都不存在。

新增检查对合法测试数据采用规则、路径、行列和整个文件 SHA-256 的精确基线。文件内容变化必须重新复核，不整目录豁免、不删除有价值的协议测试向量。扫描与本机备份位于仓库外，不随 Git 上传。

[0.3.5 / 135 完整对应源码](https://appship.kget.cc:8910/api/v1/artifacts/ca7763b8-ae84-4a24-9c78-d0e5698bbca6/download)绑定应用 8503acb、core 1c907c5，不能用另一 core RC 的源码代替。后续带 vendor 的补充源码包必须保留真实制品 revision，不覆盖已有发布附件。详细规则见[发布运行手册](../RELEASE_RUNBOOK.md)。

## 本轮验证记录

以下为 2026-10-18 的实际结果。应用独立检出基于 `95edb46181234e3845316e2be0373ad2d914971b`，没有同级 core 源码；验证产物保存在仓库外，没有安装到设备或覆盖本机客户端。

| 检查 | 实际结果与边界 |
| --- | --- |
| 当前文档入口 | 链接检查和自检通过，覆盖 39 个维护入口，含发布手册和中英文演示索引 |
| 当前源码秘密检查 | 23 个精确复核测试夹具匹配，无未复核候选；扫描 Git blob 字节，避免 Windows 换行干扰 |
| 依赖清单 | 71 个 Go 链接模块和 108 个 Web runtime 包重建比对通过；Rust 同名多版本分别记录，原始 Go 许可包保留 |
| GitHub 应用检查 | [四个任务全部通过](https://github.com/myucxy/TermLink/actions/runs/39056791920)：文档/秘密检查、Server 测试与清单、Web 构建、源码打包自检 |
| GitHub core 检查 | [两个任务全部通过](https://github.com/myucxy/terminal-core/actions/runs/39056794320)：锁定 Rust 格式/Clippy/测试/秘密扫描、19 项发布工具安全自检；对应 `d6522b9` |
| Web 独立检出 | Node 24.14.1，空的任务专用 npm 缓存，从官方 registry 安装并完成 build/verify；版本及 integrity 未变 |
| Android 独立检出 | Wrapper 8.13、JDK 21、版本 0.3.5 / 135、隔离 Debug 包名；关闭 Gradle 构建缓存，262 个任务全部执行，四 ABI 原生网络库构建、导出符号与 16 KiB 对齐检查通过；依赖下载缓存复用 |
| Windows 独立检出 | .NET 8.0.303 和 Rust 1.97.1；原生网络模块重新编译，锁定终端 DLL 核验通过。Agent 使用本轮新 NuGet 包缓存；SessionHost/桌面端空缓存下载停滞后改用现有缓存，仍按锁文件构建 |
| 对应源码工具 | 安全拒绝、旧 revision 选择和 vendor 配置测试全部通过，不修改原发布附件 |
| 实际补充源码包 | 按原 0.3.5 的应用 `8503acb` / core `1c907c5` 生成，2621 个文件及归档哈希全部核验；空 Cargo 缓存执行 `cargo test --workspace --locked --offline`，837 项通过 |

Windows 新包缓存的运行时下载停滞未作为通过记录；复用缓存的成功构建不能证明全新机器网络正常。
Android 起初受到工作站全局 Gradle HTTP 镜像和过长路径影响，使用项目 Wrapper、缩短检出路径后通过，处理方法已写入[构建指南](BUILDING.md)。现有 Gradle/API 弃用提示与产品代码警告未在本次文档整理中扩展修复。

本地带 vendor 的补充源码包 SHA-256 为 `4c943f2da70e48f7be7bea8619cccd7f4280ad48f4cc0657b655f99a4a91395e`。
它仅完成本地验证，未上传、未替换已发布源码附件；离线测试覆盖 core 的 Windows 主机目标，不代表 Android 四 ABI 终端核心已离线重建。

基础检查可在本地运行（Python ≥ 3.10）：

```powershell
python scripts/check_public_docs.py --self-test
python scripts/check_secrets.py --self-test --gitleaks <已有可执行文件>
python scripts/update_dependency_inventory.py --verify
./scripts/Test-CorrespondingSourceArchive.ps1
```

应用普通构建继续只消费已锁定的 DLL/SO；不要求 checkout core，不更改现有二进制或锁文件。维护者本机工具遵循 D:\Dev 清单，其他贡献者通过参数设置已有工具路径，见[构建指南](BUILDING.md)。
