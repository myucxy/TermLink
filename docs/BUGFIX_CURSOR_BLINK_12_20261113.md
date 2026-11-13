# 光标闪烁模式12兼容性修复

2026-11-13。共享terminal-core源码已补齐`ESC[?12h/l`和`DECRQM12`，复用既有光标闪烁状态，保持Render、快照及C ABI/JNI兼容；未知模式仍拒绝。843项Rust/FFI回归、Clippy及独立C原生加载验证通过。

**当前Windows/Android安装包尚未包含修复。** 本机缺NDK与Android固定Rust目标、WindowsMSVC/SDK环境，未生成/导入完整五库包。原五库及全部平台锁保持原来源与哈希，不能通过重新标记旧库或隐藏错误提示宣称修复已交付。

精确测试与交付边界见[验证报告](../artifacts/core-cursor-blink12-20261113/REPORT.md)。核心源码和patch保留于独立core仓库，未复制到应用源码。
