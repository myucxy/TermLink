# 许可证状态

2026-10-05，维护者已决定本轮先整理文档，暂不更改许可证。本文件记录现状，不授予新的许可，也不替代任何已有许可证。

仓库尚无覆盖全部自有代码的项目级 LICENSE。代码可见不等于已获得对整个项目修改、分发或商业使用的许可；应分别核对适用的授权条款。

| 范围 | 当前记录 |
| --- | --- |
| terminal-core（同级私有仓库） | Cargo workspace 声明 `Proprietary`，见[仓库边界](docs/development/REPOSITORY_LAYOUT.md) |
| [network-core](network-core/Cargo.toml) | Cargo workspace 声明 `Apache-2.0`；仍需核对完整许可文本与覆盖范围 |
| [Termux 来源归档](terminal-forks/termux-terminal/UPSTREAM_LICENSE.txt) | 上游 GPLv3-only 及组件例外，保留来源和原始署名 |
| [xterm 参考资源](terminal-reference/xterm-harness/THIRD_PARTY_NOTICES.md) | 按该目录的第三方声明 |
| [Android 字体](android/terminal-engine-api/src/main/assets/termlink-fonts/NOTICE.txt) | 保留字体许可与署名 |
| [AI Agent 图标](android/app/src/main/assets/licenses/ai-agent-icons-NOTICE.md) | 按各来源许可及商标边界 |
| [AI Web 依赖](ai-web/THIRD_PARTY.md) | 构建时汇总运行依赖的许可文本 |
| [Server 第三方声明](server/THIRD_PARTY_NOTICES.md) | 组件声明入口，不等于全仓库授权审计完成 |

项目级许可证需要维护者另行决定。届时应核对自有代码权属、贡献来源、第三方代码及资源的兼容性，保留目录例外，再同步根 LICENSE 和组件元数据。不能用一次全局替换覆盖上游许可。

目前不宣称已经满足 SignPath 等开源签名服务的全部申请条件；文档整理、选定许可证、资格审核和签名接入是不同阶段。
