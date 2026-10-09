# 许可证与第三方授权

2026-10-09，维护者决定将 TermLink 与独立 terminal-core 仓库的自有代码统一采用 **GNU GPL 第 3 版，仅此版本**（SPDX：`GPL-3.0-only`）。完整条款见根目录 [LICENSE](LICENSE)，授权及无担保声明见 [NOTICE](NOTICE)。此前“暂不选许可证”“禁止商用”和 core 的 `Proprietary` 声明由本次决定替代。

GPLv3 允许个人使用、修改、分发和商业使用。本项目不附加非商业限制，也不默认允许选择 GPL 的后续版本。对外分发受 GPL 覆盖的修改版或组合程序时，须遵循 GPLv3，包括保留许可和署名、提供完整对应源码等适用义务；完整条款以 LICENSE 为准。

适用范围为项目自有的 Android、Windows/PC、Server、AI Web、network-core、terminal-core、脚本、测试和文档。第三方依赖、复制代码、字体、图标和其他资源继续遵循各自许可、署名与商标条款，不能用根 LICENSE 覆盖。对旧版本已合法取得的 Apache-2.0 等许可权利，本次变更不作追溯撤回；历史记录保留当时的事实。

| 范围 | 当前记录 |
| --- | --- |
| TermLink 自有代码 | 根 LICENSE 与 NOTICE：`GPL-3.0-only`；npm 元数据和 Server OCI 标签保持一致 |
| terminal-core（独立仓库及本仓库 DLL/SO） | 自有代码统一为 `GPL-3.0-only`；core 仓库已有完整 LICENSE、NOTICE 和 Cargo 声明，后续公开，见[仓库边界](docs/development/REPOSITORY_LAYOUT.md) |
| [network-core](network-core/Cargo.toml) | 自有代码的 Cargo workspace 声明改为 `GPL-3.0-only`，不修改第三方 Rust crate 的许可 |
| [Termux 历史来源与清理边界](docs/archive/TERMUX_CLEANUP_20261008.md) | 停用源码已迁出当前仓库；保留[原许可声明](docs/archive/TERMUX_UPSTREAM_LICENSE.txt)、历史来源与 Windows V1 兼容代码的授权复核边界 |
| [Windows V1 的 wcwidth 字宽数据](win-agent/src/Agent/WcWidth-NOTICE.txt) | 已与固定上游 Unicode 15.0.0 数据逐区间核对，保留 MIT 许可及 Markus Kuhn 声明，并随 Agent 构建/publish 复制 |
| [xterm 参考资源](terminal-reference/xterm-harness/THIRD_PARTY_NOTICES.md) | 按该目录的第三方声明 |
| [Android 字体](android/terminal-engine-api/src/main/assets/termlink-fonts/NOTICE.txt) | 保留字体许可与署名 |
| [AI Agent 图标](android/app/src/main/assets/licenses/ai-agent-icons-NOTICE.md) | 按各来源许可及商标边界 |
| [AI Web 依赖](ai-web/THIRD_PARTY.md) | 构建时汇总运行依赖的许可文本 |
| [Server 第三方声明](server/THIRD_PARTY_NOTICES.md) | 组件声明入口，不等于全仓库授权审计完成 |

## 源码交付与 core 后续公开

选定许可证和公开 GitHub 仓库是两件事。本轮只修改本地文件，不改变远端可见性、不提交、不推送、不重写历史或重新发布旧制品。core 继续同级独立维护；应用的 `native/terminal-core/` 仍只存 DLL/SO，不回填核心源码或构建脚本。

后续分发 GPLv3 二进制时，必须按许可证提供完整对应源码、构建脚本及适用的安装信息。项目采用随发布提供可下载对应源码的方式：记录应用 commit、`terminal-core.lock.json` 中真实 core commit、第三方来源和源码下载位置，并验证接收者能取得。只有私有 GitHub 地址、“后续会开源”或 DLL/SO 哈希，不满足对应源码交付要求。独立 Server 镜像不需要未链接的 terminal-core，但仍须提供自己的完整对应源码。

现有预编译库的哈希与锁定 revision 不因本次授权调整而重写。交付这批库的源码时，保留其实际构建 revision，并附本次 GPLv3 授权；以后重新构建核心时再按实际提交更新锁文件。具体发布检查见[运行手册](docs/RELEASE_RUNBOOK.md#许可证与对应源码交付2026-10-09)。

完整权属、第三方兼容性与历史分发审计仍需继续，进度见[开源准备清单](docs/development/OPEN_SOURCE_READINESS.md)。本次统一声明不等于所有第三方材料或历史包均已完成审计。

目前不宣称已经满足 SignPath 等开源签名服务的全部申请条件；文档整理、选定许可证、资格审核和签名接入是不同阶段。
