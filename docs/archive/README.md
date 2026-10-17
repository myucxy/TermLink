# 历史与参考材料

这些材料保留历史依据、测试价值及第三方来源。它们不是当前构建或发布的默认入口；从[文档导航](../README.md)进入当前说明。

2026-10-18 已删除被当前指南替代的早期总体设计、会话交接、九月会话知识库及 AI V3 静态设计图。它们仍可从 Git 历史查看；删除当前文件不等于清理历史内容。正式发布记录、协议契约、测试输入和第三方来源继续保留。Android 和 Windows 的旧组件说明保存在各自的 HISTORY.md。

| 材料 | 用途与状态 |
| --- | --- |
| [Android HISTORY](../../android/HISTORY.md) | 旧组件说明、历史测试与界面演进 |
| [Windows HISTORY](../../win-agent/HISTORY.md) | 旧 IPC/Tray、安装测试和阶段状态 |
| [AI 初期记录](../AI_AGENTS.md) | 2026-10-03 原型/适配记录；“未发布”等结论只针对当时 |
| [AI Web 实施记录](../AI_WEB_IMPLEMENTATION_STATUS.md) | 按阶段描述的开发、安装和验证结果 |
| [UI 原型目录](../../ui-prototypes/) | 独立交互原型，不作为当前产品截图；实际界面见[功能演示](../media/README.md) |
| [ConPTY 原型](../../prototypes/conpty-probe/README.md) | 技术验证与录制格式来源 |
| [Termux 清理与来源记录](TERMUX_CLEANUP_20261008.md) | 旧 Android 模块、上游源码、补丁、同步工具及专用对照脚本已迁到仓库外；原许可与必要来源继续保留 |
| [xterm 参考 Harness](../../terminal-reference/xterm-harness/README.md) | 参考与兼容性材料，保留自己的第三方声明 |
| [直连验证工具](../../technical-validation/direct-e2e/README.md) | 当前 Relay Only 范围之外，不要求重跑 LAN/NAT |

`testdata/` 和仍被测试引用的参考数据具有回归价值，不因名称带“历史”就可以删除。未来移动或删除档案时应先检查代码、脚本、文档引用和授权要求。

历史记录中的版本、机器、成功/失败和未完成状态保持当时语义。后续成果在新记录中补充，不反向把旧失败改写成通过。
