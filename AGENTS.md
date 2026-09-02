# TermLink Project Guidance

## Android and Termux reference policy

- 处理 TermLink Android 端的 Bug、兼容性问题、优化事项或新增需求时，在设计和修改代码前，先查询 Termux 是否已有相关实现、Issue、Pull Request、测试或已知限制。
- 优先检查 `termux/termux-app`，并根据问题扩展到 Termux 组织内相关仓库，例如 `termux-styling`、`termux-api`、`termux-widget`、`termux-tasker`、`termux-boot`、`termux-float`、`termux-x11`、`wcwidth` 和 `termux-packages`。
- 同时检查仓库内已经固定的 Termux 来源、补丁与适配边界：`terminal-forks/termux-terminal/UPSTREAM.md`、`terminal-forks/termux-patches/`、`terminal-forks/Sync-TermuxTerminal.ps1` 和 `android/terminal-engine-termux/`。
- 调研结论应记录参考的仓库、Issue/PR、提交或关键文件，并说明 Termux 方案能否直接复用、需要适配，还是只适合作为行为参考。
- 不要直接照搬 Termux 的本地 PTY、Shell、Bootstrap、APT、插件签名或本地进程架构。必须结合 TermLink 的 Windows ConPTY、远程传输、E2EE、控制租约、ACK、History Gap、Snapshot 和 native `terminal-core` 权威边界综合设计。
- 引入或复制代码、资源、字体、主题、测试向量前必须核对许可证、署名、签名连续性和分发要求。许可证或架构边界不清楚时，只参考行为并自行实现。
- 修复完成后，除 TermLink 自有测试外，应在适用时增加与 Termux 行为的差分测试或回归用例，避免后续升级重新引入同类问题。
