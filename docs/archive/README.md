# 历史与参考材料

这些材料保留历史依据、测试价值及第三方来源。它们不是当前构建或发布的默认入口；从[文档导航](../README.md)进入当前说明。

为保持既有链接、脚本和证据路径稳定，本轮主要调整索引与状态标记，没有批量移动全部文档或源码。Android 和 Windows 的旧组件 README 保存在各自同目录的 HISTORY.md，保留原相对链接。

| 材料 | 用途与状态 |
| --- | --- |
| [DEVELOPMENT_HANDOFF.md](../../DEVELOPMENT_HANDOFF.md) | 早期工作站和阶段交接；本机路径不适用于新贡献者 |
| [COMPLETE_SYSTEM_DESIGN.md](../COMPLETE_SYSTEM_DESIGN.md) | 早期总体设计，包含已退役的依赖与产品边界 |
| [Android HISTORY](../../android/HISTORY.md) | 旧组件说明、历史测试与界面演进 |
| [Windows HISTORY](../../win-agent/HISTORY.md) | 旧 IPC/Tray、安装测试和阶段状态 |
| [AI 初期记录](../AI_AGENTS.md) | 2026-10-03 原型/适配记录；“未发布”等结论只针对当时 |
| [AI Web 实施记录](../AI_WEB_IMPLEMENTATION_STATUS.md) | 按阶段描述的开发、安装和验证结果 |
| [知识库](../knowledge-base/README.md) | 2026-09 的会话分析与审计快照，不是当前未提交改动清单 |
| [设计稿](../design/README.md)与 [UI 原型目录](../../ui-prototypes/) | 交互设计和样例数据，不作为当前产品截图或功能完成证据 |
| [ConPTY 原型](../../prototypes/conpty-probe/README.md) | 技术验证与录制格式来源 |
| [Termux 清理与来源记录](TERMUX_CLEANUP_20261008.md) | 旧 Android 模块、上游源码、补丁、同步工具及专用对照脚本已迁到仓库外；原许可与必要来源继续保留 |
| [xterm 参考 Harness](../../terminal-reference/xterm-harness/README.md) | 参考与兼容性材料，保留自己的第三方声明 |
| [直连验证工具](../../technical-validation/direct-e2e/README.md) | 当前 Relay Only 范围之外，不要求重跑 LAN/NAT |

`testdata/` 和仍被测试引用的参考数据具有回归价值，不因名称带“历史”就可以删除。未来移动或删除档案时应先检查代码、脚本、文档引用和授权要求。

历史记录中的版本、机器、成功/失败和未完成状态保持当时语义。后续成果在新记录中补充，不反向把旧失败改写成通过。
